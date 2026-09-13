using System.Diagnostics;
using Microsoft.UI.Dispatching;
using PicForLater.App.Models;
using PicForLater.Core.Analysis;
using PicForLater.Infrastructure.LocalSend;

namespace PicForLater.App.Services;

internal sealed record TrayBusinessState(
    bool StorageReady,
    bool LocalAnalysisAvailable,
    bool IsLocalSendRequested,
    LocalSendReceiverSnapshot? LocalSendSnapshot,
    bool IsLocalSendOperationInProgress,
    AnalysisExecutionBackend? AnalysisBackend,
    RemoteAnalysisExecutionState? AnalysisExecutionState,
    bool IsRemoteAnalysisVisible,
    bool IsRemoteAnalysisSelectable,
    bool IsAnalysisOperationInProgress,
    ScreenshotCaptureSnapshot? ScreenshotSnapshot,
    bool IsScreenshotOperationInProgress)
{
    internal static TrayBusinessState Initial { get; } = new(
        StorageReady: false,
        LocalAnalysisAvailable: false,
        IsLocalSendRequested: false,
        LocalSendSnapshot: null,
        IsLocalSendOperationInProgress: false,
        AnalysisBackend: null,
        AnalysisExecutionState: null,
        IsRemoteAnalysisVisible: false,
        IsRemoteAnalysisSelectable: false,
        IsAnalysisOperationInProgress: false,
        ScreenshotSnapshot: null,
        IsScreenshotOperationInProgress: false);
}

internal sealed record AnalysisSelectionResult(
    bool Applied,
    RemoteAnalysisExecutionState? ExecutionState,
    bool LocalAnalysisAvailable,
    bool RemoteAnalysisVisible,
    bool RemoteAnalysisSelectable)
{
    internal static AnalysisSelectionResult Unavailable(bool localAnalysisAvailable) => new(
        Applied: false,
        ExecutionState: null,
        LocalAnalysisAvailable: localAnalysisAvailable,
        RemoteAnalysisVisible: false,
        RemoteAnalysisSelectable: false);
}

/// <summary>
/// Coordinates the small set of background features exposed both by the settings
/// page and by the system tray. It owns operation gates so a page and the tray
/// cannot issue competing preference/service transitions.
/// </summary>
internal sealed class BusinessFeatureCoordinator : IDisposable
{
    private readonly DispatcherQueue _dispatcherQueue;
    private readonly Func<ILocalSendReceiverService?> _localSendReceiverAccessor;
    private readonly Func<IScreenshotCaptureService?> _screenshotCaptureAccessor;
    private readonly Func<IRemoteApiProfileService?> _profileServiceAccessor;
    private readonly Func<IRemoteApiCredentialService?> _credentialServiceAccessor;
    private readonly Func<bool> _localAnalysisAvailabilityAccessor;
    private readonly ILocalSendReceivePreferenceService _localSendPreference;
    private readonly SemaphoreSlim _localSendOperationGate = new(1, 1);
    private readonly SemaphoreSlim _analysisOperationGate = new(1, 1);
    private readonly SemaphoreSlim _screenshotOperationGate = new(1, 1);
    private readonly object _stateGate = new();
    private readonly object _operationStateGate = new();
    private ILocalSendReceiverService? _localSendReceiver;
    private Action<LocalSendReceiverSnapshot>? _localSendSnapshotChangedHandler;
    private IScreenshotCaptureService? _screenshotCapture;
    private long _analysisRefreshGeneration;
    private TrayBusinessState _state = TrayBusinessState.Initial;
    private TaskCompletionSource? _operationsIdleSource;
    private int _activeOperationCount;
    private TaskCompletionSource? _analysisRefreshIdleSource;
    private int _activeAnalysisRefreshCount;
    private int _shutdownRequested;
    private int _disposed;

    internal BusinessFeatureCoordinator(
        DispatcherQueue dispatcherQueue,
        Func<ILocalSendReceiverService?> localSendReceiverAccessor,
        Func<IScreenshotCaptureService?> screenshotCaptureAccessor,
        Func<IRemoteApiProfileService?> profileServiceAccessor,
        Func<IRemoteApiCredentialService?> credentialServiceAccessor,
        ILocalSendReceivePreferenceService localSendPreference,
        Func<bool> localAnalysisAvailabilityAccessor)
    {
        _dispatcherQueue = dispatcherQueue
            ?? throw new ArgumentNullException(nameof(dispatcherQueue));
        _localSendReceiverAccessor = localSendReceiverAccessor
            ?? throw new ArgumentNullException(nameof(localSendReceiverAccessor));
        _screenshotCaptureAccessor = screenshotCaptureAccessor
            ?? throw new ArgumentNullException(nameof(screenshotCaptureAccessor));
        _profileServiceAccessor = profileServiceAccessor
            ?? throw new ArgumentNullException(nameof(profileServiceAccessor));
        _credentialServiceAccessor = credentialServiceAccessor
            ?? throw new ArgumentNullException(nameof(credentialServiceAccessor));
        _localSendPreference = localSendPreference
            ?? throw new ArgumentNullException(nameof(localSendPreference));
        _localAnalysisAvailabilityAccessor = localAnalysisAvailabilityAccessor
            ?? throw new ArgumentNullException(nameof(localAnalysisAvailabilityAccessor));
    }

    internal event Action<TrayBusinessState>? StateChanged;

    internal TrayBusinessState CurrentState
    {
        get
        {
            lock (_stateGate)
            {
                return _state;
            }
        }
    }

    internal void SetStorageReady(bool isReady)
    {
        if (IsClosed())
        {
            return;
        }

        UpdateState(state => state with
        {
            StorageReady = isReady,
            LocalAnalysisAvailable = isReady && _localAnalysisAvailabilityAccessor(),
            AnalysisBackend = isReady ? state.AnalysisBackend : null,
            AnalysisExecutionState = isReady ? state.AnalysisExecutionState : null,
            IsRemoteAnalysisVisible = isReady && state.IsRemoteAnalysisVisible,
            IsRemoteAnalysisSelectable = isReady && state.IsRemoteAnalysisSelectable,
        });
        if (isReady)
        {
            _ = RefreshAnalysisAsync();
        }
    }

    internal void AttachLocalSendReceiver(ILocalSendReceiverService? receiver)
    {
        ILocalSendReceiverService? previousReceiver;
        Action<LocalSendReceiverSnapshot>? previousHandler;
        Action<LocalSendReceiverSnapshot>? newHandler = null;
        lock (_stateGate)
        {
            if (IsClosed() || ReferenceEquals(receiver, _localSendReceiver))
            {
                return;
            }

            previousReceiver = _localSendReceiver;
            previousHandler = _localSendSnapshotChangedHandler;
            if (previousReceiver is not null && previousHandler is not null)
            {
                previousReceiver.SnapshotChanged -= previousHandler;
            }

            _localSendReceiver = receiver;
            if (receiver is not null)
            {
                newHandler = snapshot => LocalSendReceiver_SnapshotChanged(receiver, snapshot);
                _localSendSnapshotChangedHandler = newHandler;
                receiver.SnapshotChanged += newHandler;
            }
            else
            {
                _localSendSnapshotChangedHandler = null;
            }
        }

        UpdateState(state => ReferenceEquals(_localSendReceiver, receiver)
            ? state with
            {
                IsLocalSendRequested = _localSendPreference.IsEnabled,
                LocalSendSnapshot = receiver?.Snapshot,
            }
            : state);
    }

    internal void AttachScreenshotCapture(IScreenshotCaptureService? service)
    {
        IScreenshotCaptureService? previousService;
        lock (_stateGate)
        {
            if (IsClosed() || ReferenceEquals(service, _screenshotCapture))
            {
                return;
            }

            previousService = _screenshotCapture;
            if (previousService is not null)
            {
                previousService.SnapshotChanged -= ScreenshotCapture_SnapshotChanged;
            }

            _screenshotCapture = service;
            if (service is not null)
            {
                service.SnapshotChanged += ScreenshotCapture_SnapshotChanged;
            }
        }

        UpdateState(state => ReferenceEquals(_screenshotCapture, service)
            ? state with { ScreenshotSnapshot = service?.Snapshot }
            : state);
    }

    internal void SetLocalAnalysisAvailability(bool isAvailable)
    {
        if (IsClosed())
        {
            return;
        }

        UpdateState(state => state with
        {
            LocalAnalysisAvailable = state.StorageReady && isAvailable,
        });
    }

    internal Task RefreshAnalysisAsync()
    {
        if (IsClosed())
        {
            return Task.CompletedTask;
        }

        lock (_operationStateGate)
        {
            if (_disposed != 0 || _shutdownRequested != 0)
            {
                return Task.CompletedTask;
            }

            if (_activeAnalysisRefreshCount++ == 0)
            {
                _analysisRefreshIdleSource = new TaskCompletionSource(
                    TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }

        return RefreshAnalysisCoreAsync();
    }

    private async Task RefreshAnalysisCoreAsync()
    {
        try
        {
            var generation = Interlocked.Increment(ref _analysisRefreshGeneration);
            var localAnalysisAvailable = GetLocalAnalysisAvailability();
            if (!CurrentState.StorageReady)
            {
                UpdateAnalysisStateIfCurrent(
                    generation,
                    backend: null,
                    executionState: null,
                    localAnalysisAvailable: false,
                    remoteVisible: false,
                    remoteSelectable: false);
                return;
            }

            var profiles = _profileServiceAccessor();
            if (profiles is null)
            {
                UpdateAnalysisStateIfCurrent(
                    generation,
                    backend: null,
                    executionState: null,
                    localAnalysisAvailable,
                    remoteVisible: false,
                    remoteSelectable: false);
                return;
            }

            try
            {
                var executionState = await profiles.GetExecutionStateAsync()
                    .ConfigureAwait(false);
                var eligibleProfiles = await AnalysisEligibility.GetEligibleProfilesAsync(
                        profiles,
                        _credentialServiceAccessor(),
                        localAnalysisAvailable)
                    .ConfigureAwait(false);
                var remoteSelection = AnalysisEligibility.ResolveEligibleSelection(
                    executionState,
                    eligibleProfiles);
                var remoteSelectable = remoteSelection is not null;
                var remoteVisible = remoteSelectable
                    || executionState.Settings.Backend == AnalysisExecutionBackend.RemoteApi;

                UpdateAnalysisStateIfCurrent(
                    generation,
                    executionState.Settings.Backend,
                    executionState,
                    localAnalysisAvailable,
                    remoteVisible,
                    remoteSelectable);
            }
            catch
            {
                // Keep a previously published capability snapshot on transient refresh
                // failures. Menu opening must not erase a known-good local cache merely
                // because a settings read was temporarily unavailable.
                if (IsAnalysisRefreshCurrent(generation)
                    && CurrentState.AnalysisBackend is null)
                {
                    UpdateAnalysisStateIfCurrent(
                        generation,
                        backend: null,
                        executionState: null,
                        localAnalysisAvailable,
                        remoteVisible: false,
                        remoteSelectable: false);
                }
            }
        }
        finally
        {
            AnalysisRefreshFinished();
        }
    }

    internal async Task<bool> SetLocalSendEnabledAsync(bool isEnabled)
    {
        var lease = await TryAcquireAsync(
                _localSendOperationGate,
                OperationKind.LocalSend)
            .ConfigureAwait(true);
        if (lease is null)
        {
            return false;
        }

        ILocalSendReceiverService? receiver = null;
        try
        {
            receiver = _localSendReceiverAccessor();
            if (!CurrentState.StorageReady || receiver is null)
            {
                return false;
            }

            // Keep the request persisted even when the receiver later reports a
            // startup failure. The setting represents the user's requested state,
            // while the snapshot represents the actual listener state.
            _localSendPreference.SetEnabled(isEnabled);
            UpdateState(state => state with
            {
                IsLocalSendRequested = isEnabled,
                LocalSendSnapshot = receiver.Snapshot,
            });

            if (isEnabled)
            {
                await receiver.StartAsync().ConfigureAwait(true);
            }
            else
            {
                await receiver.StopAsync().ConfigureAwait(true);
            }

            UpdateState(state => state with
            {
                IsLocalSendRequested = _localSendPreference.IsEnabled,
                LocalSendSnapshot = receiver.Snapshot,
            });
            return true;
        }
        catch
        {
            UpdateState(state => state with
            {
                IsLocalSendRequested = _localSendPreference.IsEnabled,
                LocalSendSnapshot = receiver?.Snapshot,
            });
            return false;
        }
        finally
        {
            await lease.DisposeAsync().ConfigureAwait(true);
        }
    }

    /// <summary>
    /// Starts the receiver only when the persisted request is still enabled.
    /// This is used by startup after storage becomes ready so a stale startup
    /// continuation cannot overwrite a user's intervening disable action.
    /// </summary>
    internal async Task<bool> StartLocalSendIfRequestedAsync(
        CancellationToken cancellationToken = default)
    {
        // Startup must not be dropped merely because a page/tray operation
        // reached the gate at the same instant. Once admitted, the preference
        // is checked again so a completed disable still wins.
        var lease = await AcquireAsync(
                _localSendOperationGate,
                OperationKind.LocalSend,
                waitForGate: true,
                cancellationToken: cancellationToken)
            .ConfigureAwait(true);
        if (lease is null)
        {
            return false;
        }

        ILocalSendReceiverService? receiver = null;
        try
        {
            receiver = _localSendReceiverAccessor();
            if (!CurrentState.StorageReady
                || receiver is null
                || !_localSendPreference.IsEnabled)
            {
                return false;
            }

            UpdateState(state => state with
            {
                IsLocalSendRequested = _localSendPreference.IsEnabled,
                LocalSendSnapshot = receiver.Snapshot,
            });
            await receiver.StartAsync(cancellationToken).ConfigureAwait(true);
            UpdateState(state => state with
            {
                IsLocalSendRequested = _localSendPreference.IsEnabled,
                LocalSendSnapshot = receiver.Snapshot,
            });
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            UpdateState(state => state with
            {
                IsLocalSendRequested = _localSendPreference.IsEnabled,
                LocalSendSnapshot = receiver?.Snapshot,
            });
            return false;
        }
        finally
        {
            await lease.DisposeAsync().ConfigureAwait(true);
        }
    }

    /// <summary>
    /// Acquires the same analysis operation gate used by tray and settings-home
    /// selection. API settings mutations use this lease for their multi-step
    /// profile/credential transitions.
    /// </summary>
    internal Task<IAsyncDisposable?> TryAcquireAnalysisOperationAsync() =>
        TryAcquireAsync(_analysisOperationGate, OperationKind.Analysis);

    internal Task<IAsyncDisposable?> AcquireAnalysisOperationAsync() =>
        AcquireAsync(
            _analysisOperationGate,
            OperationKind.Analysis,
            waitForGate: true,
            cancellationToken: CancellationToken.None);

    internal void ClearAnalysisStateAfterFailedSelection()
    {
        ClearAnalysisState(GetLocalAnalysisAvailability());
    }

    internal async Task<AnalysisSelectionResult> SelectAnalysisBackendAsync(
        AnalysisExecutionBackend backend)
    {
        // Any menu-opening refresh that started before this user action is now
        // stale and must not restore the pre-selection checkmark afterwards.
        Interlocked.Increment(ref _analysisRefreshGeneration);
        var lease = await TryAcquireAsync(
                _analysisOperationGate,
                OperationKind.Analysis)
            .ConfigureAwait(true);
        if (lease is null)
        {
            return AnalysisSelectionResult.Unavailable(GetLocalAnalysisAvailability());
        }

        try
        {
            var localAnalysisAvailable = GetLocalAnalysisAvailability();
            var profiles = _profileServiceAccessor();
            if (!CurrentState.StorageReady || profiles is null)
            {
                ClearAnalysisState(localAnalysisAvailable);
                return AnalysisSelectionResult.Unavailable(localAnalysisAvailable);
            }

            RemoteAnalysisExecutionState executionState;
            try
            {
                executionState = await profiles.GetExecutionStateAsync()
                    .ConfigureAwait(true);
                var eligibleProfiles = await AnalysisEligibility.GetEligibleProfilesAsync(
                        profiles,
                        _credentialServiceAccessor(),
                        localAnalysisAvailable)
                    .ConfigureAwait(true);
                if (backend == AnalysisExecutionBackend.Local)
                {
                    if (!localAnalysisAvailable)
                    {
                        var unavailableResult = CreateAnalysisSelectionResult(
                            false,
                            executionState,
                            localAnalysisAvailable,
                            eligibleProfiles);
                        ApplyAnalysisState(unavailableResult);
                        return unavailableResult;
                    }

                    await profiles.SelectLocalAsync().ConfigureAwait(true);
                }
                else
                {
                    var selection = AnalysisEligibility.ResolveEligibleSelection(
                        executionState,
                        eligibleProfiles);
                    if (selection is null)
                    {
                        var unavailableResult = CreateAnalysisSelectionResult(
                            false,
                            executionState,
                            localAnalysisAvailable,
                            eligibleProfiles);
                        ApplyAnalysisState(unavailableResult);
                        return unavailableResult;
                    }

                    await profiles.SelectRemoteAsync(
                            selection.Value.Profile.ProfileId,
                            selection.Value.Mode)
                        .ConfigureAwait(true);
                }

                executionState = await profiles.GetExecutionStateAsync()
                    .ConfigureAwait(true);
                var refreshedEligibleProfiles = await AnalysisEligibility.GetEligibleProfilesAsync(
                        profiles,
                        _credentialServiceAccessor(),
                        localAnalysisAvailable)
                    .ConfigureAwait(true);
                var result = CreateAnalysisSelectionResult(
                    true,
                    executionState,
                    localAnalysisAvailable,
                    refreshedEligibleProfiles);
                ApplyAnalysisState(result);
                return result;
            }
            catch
            {
                // The mutation may already have committed before a follow-up
                // read/qualification failed. Re-read the store independently so
                // the tray reflects the database fact rather than the old
                // optimistic snapshot. If that reconciliation also fails, clear
                // the selection instead of claiming the old backend is factual.
                var reconciled = await ReconcileAnalysisStateAsync(
                        localAnalysisAvailable)
                    .ConfigureAwait(true);
                if (reconciled is not null)
                {
                    return reconciled;
                }

                return AnalysisSelectionResult.Unavailable(localAnalysisAvailable);
            }
        }
        finally
        {
            await lease.DisposeAsync().ConfigureAwait(true);
        }
    }

    internal async Task<ScreenshotSettingsOperationResult> SetScreenshotEnabledAsync(
        bool isEnabled)
    {
        var lease = await TryAcquireAsync(
                _screenshotOperationGate,
                OperationKind.Screenshot)
            .ConfigureAwait(true);
        if (lease is null)
        {
            return ScreenshotSettingsOperationResult.Failed(
                ScreenshotSettingsFailureKind.NotStarted);
        }

        IScreenshotCaptureService? service = null;
        try
        {
            service = _screenshotCaptureAccessor();
            if (!CurrentState.StorageReady || service is null)
            {
                return ScreenshotSettingsOperationResult.Failed(
                    ScreenshotSettingsFailureKind.NotStarted);
            }

            return await service.SetEnabledAsync(isEnabled).ConfigureAwait(true);
        }
        catch
        {
            return ScreenshotSettingsOperationResult.Failed(
                ScreenshotSettingsFailureKind.Registration);
        }
        finally
        {
            if (service is not null && ReferenceEquals(service, _screenshotCapture))
            {
                UpdateState(state => state with { ScreenshotSnapshot = service.Snapshot });
            }

            await lease.DisposeAsync().ConfigureAwait(true);
        }
    }

    internal Task WaitForOperationsAsync()
    {
        // Close operation admission before taking the idle-task snapshot. This
        // makes the wait a shutdown barrier instead of a point-in-time query.
        BeginShutdown();

        Task operationTask;
        Task refreshTask;
        lock (_operationStateGate)
        {
            operationTask = _activeOperationCount == 0
                ? Task.CompletedTask
                : (_operationsIdleSource ??= new TaskCompletionSource(
                    TaskCreationOptions.RunContinuationsAsynchronously)).Task;
            refreshTask = _activeAnalysisRefreshCount == 0
                ? Task.CompletedTask
                : (_analysisRefreshIdleSource ??= new TaskCompletionSource(
                    TaskCreationOptions.RunContinuationsAsynchronously)).Task;
        }

        return Task.WhenAll(operationTask, refreshTask);
    }

    internal void BeginShutdown()
    {
        if (Interlocked.Exchange(ref _shutdownRequested, 1) == 0)
        {
            Interlocked.Increment(ref _analysisRefreshGeneration);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        BeginShutdown();
        ILocalSendReceiverService? localSendReceiver;
        Action<LocalSendReceiverSnapshot>? localSendHandler;
        IScreenshotCaptureService? screenshotCapture;
        lock (_stateGate)
        {
            localSendReceiver = _localSendReceiver;
            localSendHandler = _localSendSnapshotChangedHandler;
            _localSendReceiver = null;
            _localSendSnapshotChangedHandler = null;
            screenshotCapture = _screenshotCapture;
            _screenshotCapture = null;
        }

        if (localSendReceiver is not null && localSendHandler is not null)
        {
            localSendReceiver.SnapshotChanged -= localSendHandler;
        }

        if (screenshotCapture is not null)
        {
            screenshotCapture.SnapshotChanged -= ScreenshotCapture_SnapshotChanged;
        }

        StateChanged = null;
    }

    private async Task<IAsyncDisposable?> TryAcquireAsync(
        SemaphoreSlim gate,
        OperationKind operation)
        => await AcquireAsync(
                gate,
                operation,
                waitForGate: false,
                CancellationToken.None)
            .ConfigureAwait(false);

    private async Task<IAsyncDisposable?> AcquireAsync(
        SemaphoreSlim gate,
        OperationKind operation,
        bool waitForGate,
        CancellationToken cancellationToken)
    {
        if (IsClosed())
        {
            return null;
        }

        bool acquired;
        if (waitForGate)
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            acquired = true;
        }
        else
        {
            acquired = await gate.WaitAsync(0, cancellationToken)
                .ConfigureAwait(false);
        }
        if (!acquired)
        {
            return null;
        }

        if (IsClosed())
        {
            gate.Release();
            return null;
        }

        lock (_operationStateGate)
        {
            if (_disposed != 0 || _shutdownRequested != 0)
            {
                gate.Release();
                return null;
            }

            if (_activeOperationCount++ == 0)
            {
                _operationsIdleSource = new TaskCompletionSource(
                    TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }

        SetOperationInProgress(operation, isInProgress: true);
        return new OperationLease(() =>
        {
            SetOperationInProgress(operation, isInProgress: false);
            gate.Release();
            OperationFinished();
        });
    }

    private void OperationFinished()
    {
        TaskCompletionSource? idleSource = null;
        lock (_operationStateGate)
        {
            if (_activeOperationCount > 0 && --_activeOperationCount == 0)
            {
                idleSource = _operationsIdleSource;
                _operationsIdleSource = null;
            }
        }

        idleSource?.TrySetResult();
    }

    private void AnalysisRefreshFinished()
    {
        TaskCompletionSource? idleSource = null;
        lock (_operationStateGate)
        {
            if (_activeAnalysisRefreshCount > 0 && --_activeAnalysisRefreshCount == 0)
            {
                idleSource = _analysisRefreshIdleSource;
                _analysisRefreshIdleSource = null;
            }
        }

        idleSource?.TrySetResult();
    }

    private void LocalSendReceiver_SnapshotChanged(
        ILocalSendReceiverService source,
        LocalSendReceiverSnapshot snapshot)
    {
        lock (_stateGate)
        {
            if (_disposed != 0 || !ReferenceEquals(source, _localSendReceiver))
            {
                return;
            }
        }

        UpdateState(state => ReferenceEquals(_localSendReceiver, source)
            ? state with
            {
                IsLocalSendRequested = _localSendPreference.IsEnabled,
                LocalSendSnapshot = snapshot,
            }
            : state);
    }

    private void ScreenshotCapture_SnapshotChanged(
        object? sender,
        ScreenshotCaptureSnapshotChangedEventArgs e)
    {
        if (IsClosed() || sender is not IScreenshotCaptureService service
            || !ReferenceEquals(service, _screenshotCapture))
        {
            return;
        }

        UpdateState(state => ReferenceEquals(_screenshotCapture, service)
            ? state with { ScreenshotSnapshot = e.Snapshot }
            : state);
    }

    private AnalysisSelectionResult CreateAnalysisSelectionResult(
        bool applied,
        RemoteAnalysisExecutionState executionState,
        bool localAnalysisAvailable,
        IReadOnlyList<(RemoteApiProfile Profile, RemoteInputMode Mode)> eligibleProfiles)
    {
        var remoteSelection = AnalysisEligibility.ResolveEligibleSelection(
            executionState,
            eligibleProfiles);
        return new(
            applied,
            executionState,
            localAnalysisAvailable,
            remoteSelection is not null
                || executionState.Settings.Backend == AnalysisExecutionBackend.RemoteApi,
            remoteSelection is not null);
    }

    private void ApplyAnalysisState(AnalysisSelectionResult result)
    {
        if (result.ExecutionState is null)
        {
            return;
        }

        UpdateState(state => state with
        {
            AnalysisBackend = result.ExecutionState.Settings.Backend,
            AnalysisExecutionState = result.ExecutionState,
            LocalAnalysisAvailable = state.StorageReady && result.LocalAnalysisAvailable,
            IsRemoteAnalysisVisible = result.RemoteAnalysisVisible,
            IsRemoteAnalysisSelectable = result.RemoteAnalysisSelectable,
        });
    }

    private async Task<AnalysisSelectionResult?> ReconcileAnalysisStateAsync(
        bool localAnalysisAvailable)
    {
        try
        {
            var profiles = _profileServiceAccessor();
            if (!CurrentState.StorageReady || profiles is null)
            {
                ClearAnalysisState(localAnalysisAvailable);
                return null;
            }

            var executionState = await profiles.GetExecutionStateAsync()
                .ConfigureAwait(true);
            var eligibleProfiles = await AnalysisEligibility.GetEligibleProfilesAsync(
                    profiles,
                    _credentialServiceAccessor(),
                    localAnalysisAvailable)
                .ConfigureAwait(true);
            var result = CreateAnalysisSelectionResult(
                applied: false,
                executionState,
                localAnalysisAvailable,
                eligibleProfiles);
            ApplyAnalysisState(result);
            return result;
        }
        catch
        {
            ClearAnalysisState(localAnalysisAvailable);
            return null;
        }
    }

    private void ClearAnalysisState(bool localAnalysisAvailable)
    {
        UpdateState(state => state with
        {
            AnalysisBackend = null,
            AnalysisExecutionState = null,
            LocalAnalysisAvailable = state.StorageReady && localAnalysisAvailable,
            IsRemoteAnalysisVisible = false,
            IsRemoteAnalysisSelectable = false,
        });
    }

    private void UpdateAnalysisStateIfCurrent(
        long generation,
        AnalysisExecutionBackend? backend,
        RemoteAnalysisExecutionState? executionState,
        bool localAnalysisAvailable,
        bool remoteVisible,
        bool remoteSelectable)
    {
        if (!IsAnalysisRefreshCurrent(generation))
        {
            return;
        }

        UpdateState(state => state with
        {
            AnalysisBackend = backend,
            AnalysisExecutionState = executionState,
            LocalAnalysisAvailable = state.StorageReady && localAnalysisAvailable,
            IsRemoteAnalysisVisible = remoteVisible,
            IsRemoteAnalysisSelectable = remoteSelectable,
        });
    }

    private void SetOperationInProgress(OperationKind operation, bool isInProgress)
    {
        UpdateState(state => operation switch
        {
            OperationKind.LocalSend => state with
            {
                IsLocalSendOperationInProgress = isInProgress,
            },
            OperationKind.Analysis => state with
            {
                IsAnalysisOperationInProgress = isInProgress,
            },
            OperationKind.Screenshot => state with
            {
                IsScreenshotOperationInProgress = isInProgress,
            },
            _ => state,
        });
    }

    private bool GetLocalAnalysisAvailability() =>
        CurrentState.StorageReady && _localAnalysisAvailabilityAccessor();

    private bool IsAnalysisRefreshCurrent(long generation) =>
        !IsClosed() && generation == Volatile.Read(ref _analysisRefreshGeneration);

    private bool IsClosed() =>
        Volatile.Read(ref _disposed) != 0
        || Volatile.Read(ref _shutdownRequested) != 0;

    private void UpdateState(Func<TrayBusinessState, TrayBusinessState> update)
    {
        TrayBusinessState state;
        lock (_stateGate)
        {
            if (IsClosed())
            {
                return;
            }

            _state = update(_state);
            state = _state;
        }

        RaiseStateChanged(state);
    }

    private void RaiseStateChanged(TrayBusinessState state)
    {
        if (IsClosed())
        {
            return;
        }

        if (_dispatcherQueue.HasThreadAccess)
        {
            InvokeStateChanged(state);
            return;
        }

        _ = _dispatcherQueue.TryEnqueue(() =>
        {
            if (!IsClosed() && IsCurrentState(state))
            {
                InvokeStateChanged(state);
            }
        });
    }

    private bool IsCurrentState(TrayBusinessState expected)
    {
        lock (_stateGate)
        {
            return ReferenceEquals(_state, expected);
        }
    }

    private void InvokeStateChanged(TrayBusinessState state)
    {
        Delegate[] handlers = StateChanged?.GetInvocationList() ?? [];
        foreach (Action<TrayBusinessState> handler in handlers.Cast<
                     Action<TrayBusinessState>>())
        {
            try
            {
                handler(state);
            }
            catch (Exception exception)
            {
                Debug.WriteLine(
                    $"Business feature state observer failed: {exception.GetType().Name}.");
            }
        }
    }

    private enum OperationKind
    {
        LocalSend,
        Analysis,
        Screenshot,
    }

    private sealed class OperationLease(Action release) : IAsyncDisposable
    {
        private Action? _release = release ?? throw new ArgumentNullException(nameof(release));

        public ValueTask DisposeAsync()
        {
            Interlocked.Exchange(ref _release, null)?.Invoke();
            return ValueTask.CompletedTask;
        }
    }
}
