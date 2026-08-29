using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using PicForLater.Infrastructure.Library;
using PicForLater.Infrastructure.Storage;

namespace PicForLater.Infrastructure.LocalSend;

public sealed class LocalSendReceiverService : ILocalSendReceiverService
{
    public const string ReceiverAlias = "PicForLater";
    public const int ReceiverPort = 53317;
    public const int MaximumIncomingItemsPerTransfer = 20;
    public const long MaximumIncomingTransferBytes = 250L * 1024 * 1024;
    public const long MaximumPrepareRequestBytes = 256L * 1024;
    public const long MaximumImageBytes = 50L * 1024 * 1024;
    public static readonly TimeSpan DefaultPairingDuration = TimeSpan.FromMinutes(2);

    private readonly ILocalSendInboxImportService _inboxImporter;
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly ILocalSendReceiverNodeFactory _nodeFactory;
    private readonly TimeSpan _pairingDuration;
    private readonly AppDataPaths _paths;
    private readonly object _snapshotGate = new();
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _trustDecisionGate = new(1, 1);
    private readonly ILocalSendTrustedDeviceStore _trustedDevices;

    private int _activeRequest;
    private int _cleanupFailed;
    private bool _disposed;
    private int _pairingClaimed;
    private NodeRuntime? _runtime;
    private LocalSendReceiverSnapshot _snapshot = new(LocalSendReceiverStatus.Disabled);

    public LocalSendReceiverService(
        AppDataPaths paths,
        ILocalSendReceiverNodeFactory nodeFactory,
        ILocalSendTrustedDeviceStore trustedDevices,
        ILocalSendInboxImportService inboxImporter,
        TimeProvider? timeProvider = null,
        TimeSpan? pairingDuration = null)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _nodeFactory = nodeFactory ?? throw new ArgumentNullException(nameof(nodeFactory));
        _trustedDevices = trustedDevices ?? throw new ArgumentNullException(nameof(trustedDevices));
        _inboxImporter = inboxImporter ?? throw new ArgumentNullException(nameof(inboxImporter));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _pairingDuration = pairingDuration ?? DefaultPairingDuration;
        if (_pairingDuration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(pairingDuration),
                "The LocalSend pairing duration must be positive.");
        }
    }

    public LocalSendReceiverSnapshot Snapshot
    {
        get
        {
            lock (_snapshotGate)
            {
                return _snapshot;
            }
        }
    }

    public event Action<LocalSendReceiverSnapshot>? SnapshotChanged;

    public event Action<LocalSendReceiveSummary>? TransferCompleted;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            if (_runtime is not null)
            {
                return;
            }

            if (Volatile.Read(ref _cleanupFailed) != 0)
            {
                SetSnapshot(new(
                    LocalSendReceiverStatus.Faulted,
                    LastErrorCode: "ReceiverStopFailed"));
                throw new InvalidOperationException(
                    "A previous LocalSend node did not stop cleanly.");
            }

            SetSnapshot(new(LocalSendReceiverStatus.Starting));
            try
            {
                EnsureReceiverPathsAreSafe();
                var runtime = await CreateAndStartRuntimeAsync(
                    pairingPin: null,
                    pairingExpiresAtUtc: null,
                    cancellationToken).ConfigureAwait(false);
                SetListeningSnapshot(runtime);
            }
            catch
            {
                SetSnapshot(new(
                    LocalSendReceiverStatus.Faulted,
                    LastErrorCode: "ReceiverStartFailed"));
                throw;
            }
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            await StopCoreAsync().ConfigureAwait(false);
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    public async Task<LocalSendPairingSession> BeginPairingAsync(
        CancellationToken cancellationToken = default)
    {
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            var normalRuntime = _runtime;
            if (normalRuntime is null
                || normalRuntime.IsPairing
                || Snapshot.Status != LocalSendReceiverStatus.Listening
                || Volatile.Read(ref _activeRequest) != 0)
            {
                throw new InvalidOperationException(
                    "LocalSend pairing requires an idle, listening receiver.");
            }

            SetSnapshot(new(LocalSendReceiverStatus.Starting));
            _runtime = null;
            if (!await StopRuntimeAsync(normalRuntime, waitForWatcher: true).ConfigureAwait(false))
            {
                SetSnapshot(new(
                    LocalSendReceiverStatus.Faulted,
                    LastErrorCode: "ReceiverStopFailed"));
                throw new InvalidOperationException(
                    "The normal LocalSend receiver could not be stopped safely.");
            }

            var pin = CreatePairingPin();
            var expiresAtUtc = _timeProvider.GetUtcNow().ToUniversalTime() + _pairingDuration;
            try
            {
                var pairingRuntime = await CreateAndStartRuntimeAsync(
                    pin,
                    expiresAtUtc,
                    cancellationToken).ConfigureAwait(false);
                Interlocked.Exchange(ref _pairingClaimed, 0);
                SetSnapshot(new(
                    LocalSendReceiverStatus.Pairing,
                    pairingRuntime.Node.DiscoveryLimited,
                    pin,
                    expiresAtUtc));
                pairingRuntime.PairingTimeoutTask = RunPairingTimeoutAsync(pairingRuntime);
                return new(pin, expiresAtUtc);
            }
            catch
            {
                await TryRestoreNormalRuntimeAsync().ConfigureAwait(false);
                throw;
            }
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    public async Task CancelPairingAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            var runtime = _runtime;
            if (runtime is null || !runtime.IsPairing)
            {
                return;
            }

            if (Volatile.Read(ref _pairingClaimed) != 0
                || Snapshot.Status == LocalSendReceiverStatus.Receiving)
            {
                throw new InvalidOperationException(
                    "A LocalSend pairing transfer is already in progress.");
            }

            SetSnapshot(new(LocalSendReceiverStatus.Starting));
            _runtime = null;
            CancelPairingTimeout(runtime);
            if (!await StopRuntimeAsync(runtime, waitForWatcher: true).ConfigureAwait(false))
            {
                SetSnapshot(new(
                    LocalSendReceiverStatus.Faulted,
                    LastErrorCode: "ReceiverStopFailed"));
                return;
            }

            await RestoreNormalRuntimeOrFaultAsync().ConfigureAwait(false);
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    public async Task<IReadOnlyList<LocalSendTrustedDeviceSummary>> GetTrustedDevicesAsync(
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var devices = await _trustedDevices.GetAllAsync(cancellationToken).ConfigureAwait(false);
        return devices
            .Select(static device => new LocalSendTrustedDeviceSummary(
                CreateTrustedDeviceId(device.Fingerprint),
                device.DisplayName,
                device.FirstPairedAtUtc,
                device.LastReceivedAtUtc))
            .ToArray();
    }

    public async Task<bool> RemoveTrustedDeviceAsync(
        string deviceId,
        CancellationToken cancellationToken = default)
    {
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            if (!Snapshot.CanManageTrustedDevices
                || Volatile.Read(ref _activeRequest) != 0)
            {
                throw new InvalidOperationException(
                    "Trusted LocalSend devices cannot be changed while the receiver is busy.");
            }

            if (string.IsNullOrWhiteSpace(deviceId))
            {
                throw new ArgumentException(
                    "A trusted LocalSend device ID is required.",
                    nameof(deviceId));
            }

            await _trustDecisionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var devices = await _trustedDevices.GetAllAsync(cancellationToken).ConfigureAwait(false);
                var device = devices.FirstOrDefault(candidate =>
                    StringComparer.Ordinal.Equals(
                        CreateTrustedDeviceId(candidate.Fingerprint),
                        deviceId));
                return device is not null
                       && await _trustedDevices.RemoveAsync(
                           device.Fingerprint,
                           cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _trustDecisionGate.Release();
            }
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed)
            {
                return;
            }

            await StopCoreAsync().ConfigureAwait(false);
            _disposed = true;
        }
        finally
        {
            _lifecycle.Release();
        }

        _lifecycle.Dispose();
        _trustDecisionGate.Dispose();
    }

    private async Task StopCoreAsync()
    {
        var runtime = _runtime;
        if (runtime is null)
        {
            SetSnapshot(new(LocalSendReceiverStatus.Disabled));
            return;
        }

        SetSnapshot(new(LocalSendReceiverStatus.Stopping));
        _runtime = null;
        CancelPairingTimeout(runtime);
        Interlocked.Exchange(ref _pairingClaimed, 0);
        var stoppedCleanly = await StopRuntimeAsync(
            runtime,
            waitForWatcher: true).ConfigureAwait(false);
        SetSnapshot(stoppedCleanly
            ? new(LocalSendReceiverStatus.Disabled)
            : new(
                LocalSendReceiverStatus.Faulted,
                LastErrorCode: "ReceiverStopFailed"));
    }

    private async Task<NodeRuntime> CreateAndStartRuntimeAsync(
        string? pairingPin,
        DateTimeOffset? pairingExpiresAtUtc,
        CancellationToken cancellationToken)
    {
        var options = new LocalSendReceiverNodeOptions(
            ReceiverAlias,
            _paths.LocalSendIdentityDirectoryPath,
            _paths.LocalSendInboxDirectoryPath,
            pairingPin,
            ReceiverPort,
            MaximumConcurrentTransfers: 1,
            MaximumConcurrentFileUploads: 2,
            MaximumIncomingItemsPerTransfer,
            MaximumIncomingTransferBytes,
            MaximumPrepareRequestBytes);
        var node = _nodeFactory.Create(options)
            ?? throw new InvalidOperationException("The LocalSend node factory returned null.");
        var runtime = new NodeRuntime(node, pairingPin, pairingExpiresAtUtc);
        _runtime = runtime;
        var subscriptionReady = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        runtime.WatcherTask = WatchIncomingTransfersAsync(runtime, subscriptionReady);

        try
        {
            await subscriptionReady.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            await node.StartAsync(cancellationToken).ConfigureAwait(false);
            return runtime;
        }
        catch
        {
            if (ReferenceEquals(_runtime, runtime))
            {
                _runtime = null;
            }

            await StopRuntimeAsync(runtime, waitForWatcher: true).ConfigureAwait(false);
            throw;
        }
    }

    private async Task WatchIncomingTransfersAsync(
        NodeRuntime runtime,
        TaskCompletionSource subscriptionReady)
    {
        try
        {
            await using var requests = runtime.Node
                .WatchIncomingTransfersAsync(runtime.Cancellation.Token)
                .GetAsyncEnumerator(runtime.Cancellation.Token);
            var hasNext = requests.MoveNextAsync();
            subscriptionReady.TrySetResult();
            while (await hasNext.ConfigureAwait(false))
            {
                if (!IsCurrentRuntime(runtime))
                {
                    break;
                }

                await HandleIncomingRequestAsync(
                    runtime,
                    requests.Current).ConfigureAwait(false);
                hasNext = requests.MoveNextAsync();
            }
        }
        catch (OperationCanceledException) when (runtime.Cancellation.IsCancellationRequested)
        {
            subscriptionReady.TrySetCanceled(runtime.Cancellation.Token);
        }
        catch (Exception)
        {
            subscriptionReady.TrySetException(
                new InvalidOperationException("The LocalSend request watcher failed."));
            if (IsCurrentRuntime(runtime))
            {
                _ = FailRuntimeFromWatcherAsync(runtime);
            }
        }
    }

    private async Task HandleIncomingRequestAsync(
        NodeRuntime runtime,
        LocalSendIncomingRequest request)
    {
        if (Interlocked.Exchange(ref _activeRequest, 1) != 0)
        {
            await TryDeclineAsync(runtime, request.RequestId).ConfigureAwait(false);
            return;
        }

        try
        {
            if (runtime.IsPairing)
            {
                await HandlePairingRequestAsync(runtime, request).ConfigureAwait(false);
            }
            else
            {
                await HandleTrustedRequestAsync(runtime, request).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (runtime.Cancellation.IsCancellationRequested)
        {
        }
        catch (Exception)
        {
            await TryDeclineAsync(runtime, request.RequestId).ConfigureAwait(false);
        }
        finally
        {
            Interlocked.Exchange(ref _activeRequest, 0);
        }
    }

    private async Task HandleTrustedRequestAsync(
        NodeRuntime runtime,
        LocalSendIncomingRequest request)
    {
        LocalSendTrustedDevice? trustedDevice;
        await _trustDecisionGate.WaitAsync(runtime.Cancellation.Token).ConfigureAwait(false);
        try
        {
            trustedDevice = await _trustedDevices.FindAsync(
                request.SenderFingerprint,
                runtime.Cancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (runtime.Cancellation.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            await TryDeclineAsync(runtime, request.RequestId).ConfigureAwait(false);
            return;
        }
        finally
        {
            _trustDecisionGate.Release();
        }

        var acceptedItems = SelectSupportedImages(request.Items);
        if (trustedDevice is null || acceptedItems.Count == 0)
        {
            await TryDeclineAsync(runtime, request.RequestId).ConfigureAwait(false);
            return;
        }

        SetReceivingSnapshot(runtime);
        var summary = await ReceiveAndImportAsync(
            runtime,
            request,
            acceptedItems,
            trustedDevice.DisplayName,
            pairedDuringTransfer: false).ConfigureAwait(false);
        if (summary.TransferState == LocalSendReceiveTransferState.Completed)
        {
            try
            {
                await _trustedDevices.MarkReceivedAsync(
                    request.SenderFingerprint,
                    request.SenderDisplayName,
                    runtime.Cancellation.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (runtime.Cancellation.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception)
            {
                summary = summary with { ErrorCode = "TrustedDeviceUpdateFailed" };
            }
        }

        PublishTransferCompleted(summary);
        if (IsCurrentRuntime(runtime))
        {
            SetListeningSnapshot(runtime);
        }
    }

    private async Task HandlePairingRequestAsync(
        NodeRuntime runtime,
        LocalSendIncomingRequest request)
    {
        var acceptedItems = SelectSupportedImages(request.Items);
        if (acceptedItems.Count == 0
            || Interlocked.CompareExchange(ref _pairingClaimed, 1, 0) != 0)
        {
            await TryDeclineAsync(runtime, request.RequestId).ConfigureAwait(false);
            return;
        }

        LocalSendTrustedDevice? previousDevice;
        LocalSendTrustedDevice pairedDevice;
        await _trustDecisionGate.WaitAsync(runtime.Cancellation.Token).ConfigureAwait(false);
        try
        {
            using var trustSaveCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                runtime.Cancellation.Token,
                runtime.PairingTrustCancellation.Token);
            previousDevice = await _trustedDevices.FindAsync(
                request.SenderFingerprint,
                trustSaveCancellation.Token).ConfigureAwait(false);
            pairedDevice = await _trustedDevices.AddAsync(
                request.SenderFingerprint,
                request.SenderDisplayName,
                trustSaveCancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (runtime.Cancellation.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            Interlocked.Exchange(ref _pairingClaimed, 0);
            await TryDeclineAsync(runtime, request.RequestId).ConfigureAwait(false);
            if (IsPairingExpired(runtime))
            {
                SchedulePairingRestore(runtime);
            }

            return;
        }
        finally
        {
            _trustDecisionGate.Release();
        }

        bool pairingExpired;
        lock (runtime.PairingStateGate)
        {
            pairingExpired = runtime.PairingExpired;
            if (!pairingExpired)
            {
                runtime.PairingTransferStarted = true;
            }
        }

        if (pairingExpired)
        {
            var rolledBack = await TryRollbackExpiredPairingTrustAsync(
                runtime,
                previousDevice,
                pairedDevice).ConfigureAwait(false);
            Interlocked.Exchange(ref _pairingClaimed, 0);
            await TryDeclineAsync(runtime, request.RequestId).ConfigureAwait(false);
            if (rolledBack)
            {
                SchedulePairingRestore(runtime);
            }
            else
            {
                SchedulePairingFault(runtime, "PairingTrustRollbackFailed");
            }

            return;
        }

        CancelPairingTimeout(runtime);
        SetReceivingSnapshot(runtime);
        LocalSendReceiveSummary summary;
        try
        {
            summary = await ReceiveAndImportAsync(
                runtime,
                request,
                acceptedItems,
                pairedDevice.DisplayName,
                pairedDuringTransfer: true).ConfigureAwait(false);
            if (summary.TransferState == LocalSendReceiveTransferState.Completed)
            {
                try
                {
                    await _trustedDevices.MarkReceivedAsync(
                        request.SenderFingerprint,
                        request.SenderDisplayName,
                        runtime.Cancellation.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (runtime.Cancellation.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception)
                {
                    summary = summary with { ErrorCode = "TrustedDeviceUpdateFailed" };
                }
            }

            PublishTransferCompleted(summary);
        }
        finally
        {
            SchedulePairingRestore(runtime);
        }
    }

    private async Task<LocalSendReceiveSummary> ReceiveAndImportAsync(
        NodeRuntime runtime,
        LocalSendIncomingRequest request,
        IReadOnlyList<LocalSendIncomingItem> acceptedItems,
        string deviceDisplayName,
        bool pairedDuringTransfer)
    {
        var acceptedIds = acceptedItems
            .Select(static item => item.Id)
            .ToHashSet(StringComparer.Ordinal);
        var targetNames = acceptedItems.ToDictionary(
            static item => item.Id,
            static item => LocalSendInboxImportService.CreateTargetFileName(item.FileName),
            StringComparer.Ordinal);
        LocalSendReceiveResult receiveResult;
        try
        {
            receiveResult = await runtime.Node.AcceptAsync(
                request.RequestId,
                new(
                    _paths.LocalSendInboxDirectoryPath,
                    acceptedIds.ToArray(),
                    targetNames),
                runtime.Cancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (runtime.Cancellation.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return new(
                request.TransferId,
                deviceDisplayName,
                pairedDuringTransfer,
                LocalSendReceiveTransferState.Failed,
                request.Items.Count,
                acceptedItems.Count,
                ImportedCount: 0,
                DuplicateCount: 0,
                FailedCount: acceptedItems.Count,
                ErrorCode: "ReceiveFailed");
        }

        var importedCount = 0;
        var duplicateCount = 0;
        string? importErrorCode = null;
        var processedIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var transferredItem in receiveResult.Items)
        {
            if (string.IsNullOrWhiteSpace(transferredItem.SavedPath)
                || !acceptedIds.Contains(transferredItem.ItemId)
                || !processedIds.Add(transferredItem.ItemId))
            {
                continue;
            }

            try
            {
                var importResult = await _inboxImporter.ImportAsync(
                    transferredItem.SavedPath,
                    runtime.Cancellation.Token).ConfigureAwait(false);
                if (importResult.Status == LocalSendInboxImportStatus.Imported)
                {
                    importedCount++;
                }
                else if (importResult.Status == LocalSendInboxImportStatus.Duplicate)
                {
                    duplicateCount++;
                }
                else
                {
                    importErrorCode ??= SanitizeApplicationErrorCode(importResult.ErrorCode)
                        ?? "InboxImportFailed";
                }
            }
            catch (OperationCanceledException) when (runtime.Cancellation.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception)
            {
                importErrorCode ??= "InboxImportFailed";
            }
        }

        var failedCount = Math.Max(
            0,
            acceptedItems.Count - importedCount - duplicateCount);
        var errorCode = receiveResult.State switch
        {
            LocalSendReceiveTransferState.Failed =>
                SanitizeFailureCode(receiveResult.FailureCode) ?? "ReceiveFailed",
            LocalSendReceiveTransferState.Completed when failedCount > 0 =>
                importErrorCode ?? "ReceiveIncomplete",
            _ => null,
        };
        return new(
            receiveResult.TransferId,
            deviceDisplayName,
            pairedDuringTransfer,
            receiveResult.State,
            request.Items.Count,
            acceptedItems.Count,
            importedCount,
            duplicateCount,
            failedCount,
            errorCode);
    }

    private void SchedulePairingRestore(NodeRuntime pairingRuntime)
    {
        _ = RestoreNormalAfterPairingAsync(pairingRuntime);
    }

    private void SchedulePairingFault(NodeRuntime pairingRuntime, string errorCode)
    {
        _ = StopPairingAsFaultedAsync(pairingRuntime, errorCode);
    }

    private async Task<bool> TryRollbackExpiredPairingTrustAsync(
        NodeRuntime runtime,
        LocalSendTrustedDevice? previousDevice,
        LocalSendTrustedDevice pairedDevice)
    {
        try
        {
            await _trustDecisionGate.WaitAsync(runtime.Cancellation.Token).ConfigureAwait(false);
            try
            {
                if (previousDevice is not null)
                {
                    await _trustedDevices.AddAsync(
                        previousDevice.Fingerprint,
                        previousDevice.DisplayName,
                        runtime.Cancellation.Token).ConfigureAwait(false);
                    return true;
                }

                var current = await _trustedDevices.FindAsync(
                    pairedDevice.Fingerprint,
                    runtime.Cancellation.Token).ConfigureAwait(false);
                return current is null
                       || await _trustedDevices.RemoveAsync(
                           pairedDevice.Fingerprint,
                           runtime.Cancellation.Token).ConfigureAwait(false);
            }
            finally
            {
                _trustDecisionGate.Release();
            }
        }
        catch (Exception)
        {
            return false;
        }
    }

    private async Task StopPairingAsFaultedAsync(
        NodeRuntime pairingRuntime,
        string errorCode)
    {
        try
        {
            await _lifecycle.WaitAsync().ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            return;
        }

        try
        {
            if (_disposed || !ReferenceEquals(_runtime, pairingRuntime))
            {
                return;
            }

            _runtime = null;
            CancelPairingTimeout(pairingRuntime);
            Interlocked.Exchange(ref _pairingClaimed, 0);
            var stoppedCleanly = await StopRuntimeAsync(
                pairingRuntime,
                waitForWatcher: true).ConfigureAwait(false);
            SetSnapshot(new(
                LocalSendReceiverStatus.Faulted,
                LastErrorCode: stoppedCleanly ? errorCode : "ReceiverStopFailed"));
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    private async Task RestoreNormalAfterPairingAsync(NodeRuntime pairingRuntime)
    {
        try
        {
            await _lifecycle.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_disposed || !ReferenceEquals(_runtime, pairingRuntime))
                {
                    return;
                }

                SetSnapshot(new(LocalSendReceiverStatus.Starting));
                _runtime = null;
                CancelPairingTimeout(pairingRuntime);
                Interlocked.Exchange(ref _pairingClaimed, 0);
                if (!await StopRuntimeAsync(
                        pairingRuntime,
                        waitForWatcher: true).ConfigureAwait(false))
                {
                    SetSnapshot(new(
                        LocalSendReceiverStatus.Faulted,
                        LastErrorCode: "ReceiverStopFailed"));
                    return;
                }

                await RestoreNormalRuntimeOrFaultAsync().ConfigureAwait(false);
            }
            finally
            {
                _lifecycle.Release();
            }
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private async Task RunPairingTimeoutAsync(NodeRuntime pairingRuntime)
    {
        try
        {
            await Task.Delay(
                _pairingDuration,
                _timeProvider,
                pairingRuntime.PairingTimeoutCancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
            when (pairingRuntime.PairingTimeoutCancellation.IsCancellationRequested)
        {
            return;
        }

        bool pairingClaimed;
        lock (pairingRuntime.PairingStateGate)
        {
            if (pairingRuntime.PairingTransferStarted)
            {
                return;
            }

            pairingRuntime.PairingExpired = true;
            pairingClaimed = Volatile.Read(ref _pairingClaimed) != 0;
            if (pairingClaimed)
            {
                pairingRuntime.PairingTrustCancellation.Cancel();
            }
        }

        if (pairingClaimed)
        {
            return;
        }

        try
        {
            await _lifecycle.WaitAsync().ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            return;
        }

        try
        {
            if (_disposed
                || !ReferenceEquals(_runtime, pairingRuntime))
            {
                return;
            }

            SetSnapshot(new(LocalSendReceiverStatus.Starting));
            _runtime = null;
            if (!await StopRuntimeAsync(
                    pairingRuntime,
                    waitForWatcher: true).ConfigureAwait(false))
            {
                SetSnapshot(new(
                    LocalSendReceiverStatus.Faulted,
                    LastErrorCode: "ReceiverStopFailed"));
                return;
            }

            await RestoreNormalRuntimeOrFaultAsync().ConfigureAwait(false);
        }
        catch (Exception)
        {
            SetSnapshot(new(
                LocalSendReceiverStatus.Faulted,
                LastErrorCode: "ReceiverRestartFailed"));
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    private async Task FailRuntimeFromWatcherAsync(NodeRuntime runtime)
    {
        try
        {
            await _lifecycle.WaitAsync().ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            return;
        }

        try
        {
            if (!ReferenceEquals(_runtime, runtime))
            {
                return;
            }

            _runtime = null;
            CancelPairingTimeout(runtime);
            await StopRuntimeAsync(runtime, waitForWatcher: false).ConfigureAwait(false);
            SetSnapshot(new(
                LocalSendReceiverStatus.Faulted,
                LastErrorCode: "ReceiverWatchFailed"));
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    private async Task<bool> StopRuntimeAsync(
        NodeRuntime runtime,
        bool waitForWatcher)
    {
        var stoppedCleanly = true;
        await runtime.Cancellation.CancelAsync().ConfigureAwait(false);
        await runtime.PairingTrustCancellation.CancelAsync().ConfigureAwait(false);
        try
        {
            await runtime.Node.StopAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception)
        {
            stoppedCleanly = false;
        }

        try
        {
            await runtime.Node.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception)
        {
            stoppedCleanly = false;
        }

        if (waitForWatcher)
        {
            try
            {
                await runtime.WatcherTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception)
            {
                stoppedCleanly = false;
            }
        }

        runtime.Cancellation.Dispose();
        runtime.PairingTimeoutCancellation.Dispose();
        runtime.PairingTrustCancellation.Dispose();
        if (!stoppedCleanly)
        {
            Interlocked.Exchange(ref _cleanupFailed, 1);
        }

        return stoppedCleanly;
    }

    private async Task RestoreNormalRuntimeOrFaultAsync()
    {
        if (Volatile.Read(ref _cleanupFailed) != 0)
        {
            SetSnapshot(new(
                LocalSendReceiverStatus.Faulted,
                LastErrorCode: "ReceiverStopFailed"));
            return;
        }

        try
        {
            var runtime = await CreateAndStartRuntimeAsync(
                pairingPin: null,
                pairingExpiresAtUtc: null,
                CancellationToken.None).ConfigureAwait(false);
            SetListeningSnapshot(runtime);
        }
        catch
        {
            SetSnapshot(new(
                LocalSendReceiverStatus.Faulted,
                LastErrorCode: "ReceiverRestartFailed"));
        }
    }

    private async Task TryRestoreNormalRuntimeAsync()
    {
        if (_runtime is not null)
        {
            var failedRuntime = _runtime;
            _runtime = null;
            if (!await StopRuntimeAsync(
                    failedRuntime,
                    waitForWatcher: true).ConfigureAwait(false))
            {
                SetSnapshot(new(
                    LocalSendReceiverStatus.Faulted,
                    LastErrorCode: "ReceiverStopFailed"));
                return;
            }
        }

        await RestoreNormalRuntimeOrFaultAsync().ConfigureAwait(false);
    }

    private async Task TryDeclineAsync(NodeRuntime runtime, Guid requestId)
    {
        try
        {
            await runtime.Node.DeclineAsync(
                requestId,
                runtime.Cancellation.Token).ConfigureAwait(false);
        }
        catch (Exception)
        {
        }
    }

    private static IReadOnlyList<LocalSendIncomingItem> SelectSupportedImages(
        IReadOnlyList<LocalSendIncomingItem> items)
    {
        return items
            .Where(static item =>
                !string.IsNullOrWhiteSpace(item.Id)
                && item.Size is > 0 and <= MaximumImageBytes
                && NormalizeImageExtension(item.FileName) is not null)
            .GroupBy(static item => item.Id, StringComparer.Ordinal)
            .Select(static group => group.First())
            .ToArray();
    }

    private static string? NormalizeImageExtension(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return null;
        }

        return Path.GetExtension(fileName).ToLowerInvariant() switch
        {
            ".png" => ".png",
            ".jpg" or ".jpeg" => ".jpg",
            ".webp" => ".webp",
            _ => null,
        };
    }

    private void EnsureReceiverPathsAreSafe()
    {
        _paths.EnsureCreated();
        EnsureDirectoryIsSafe(_paths.LocalSendIdentityDirectoryPath);
        EnsureDirectoryIsSafe(_paths.LocalSendInboxDirectoryPath);
    }

    private void EnsureDirectoryIsSafe(string path)
    {
        _paths.EnsureSafePath(path);
        var attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.Directory) == 0
            || (attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidOperationException(
                "A required LocalSend directory is not safe to use.");
        }
    }

    private void SetListeningSnapshot(NodeRuntime runtime)
    {
        SetSnapshot(new(
            LocalSendReceiverStatus.Listening,
            runtime.Node.DiscoveryLimited));
    }

    private void SetReceivingSnapshot(NodeRuntime runtime)
    {
        if (!IsCurrentRuntime(runtime))
        {
            return;
        }

        SetSnapshot(new(
            LocalSendReceiverStatus.Receiving,
            runtime.Node.DiscoveryLimited));
    }

    private void SetSnapshot(LocalSendReceiverSnapshot snapshot)
    {
        lock (_snapshotGate)
        {
            _snapshot = snapshot;
        }

        var handlers = SnapshotChanged;
        if (handlers is null)
        {
            return;
        }

        foreach (Action<LocalSendReceiverSnapshot> handler in handlers.GetInvocationList())
        {
            try
            {
                handler(snapshot);
            }
            catch (Exception)
            {
            }
        }
    }

    private void PublishTransferCompleted(LocalSendReceiveSummary summary)
    {
        var handlers = TransferCompleted;
        if (handlers is null)
        {
            return;
        }

        foreach (Action<LocalSendReceiveSummary> handler in handlers.GetInvocationList())
        {
            try
            {
                handler(summary);
            }
            catch (Exception)
            {
            }
        }
    }

    private bool IsCurrentRuntime(NodeRuntime runtime) =>
        ReferenceEquals(Volatile.Read(ref _runtime), runtime);

    private static bool IsPairingExpired(NodeRuntime runtime)
    {
        lock (runtime.PairingStateGate)
        {
            return runtime.PairingExpired;
        }
    }

    private static string CreatePairingPin() =>
        RandomNumberGenerator.GetInt32(1_000_000).ToString("D6", CultureInfo.InvariantCulture);

    private static string CreateTrustedDeviceId(string fingerprint)
    {
        var input = Encoding.UTF8.GetBytes(
            "PicForLater.LocalSend.TrustedDevice.v1\0" + fingerprint);
        return Convert.ToHexString(SHA256.HashData(input).AsSpan(0, 16));
    }

    private static string? SanitizeFailureCode(string? failureCode)
    {
        if (string.IsNullOrWhiteSpace(failureCode))
        {
            return null;
        }

        return failureCode.Length <= 64
               && failureCode.All(static character =>
                   character is >= 'a' and <= 'z' or >= '0' and <= '9' or '_' or '-')
            ? failureCode
            : "ReceiveFailed";
    }

    private static string? SanitizeApplicationErrorCode(string? errorCode)
    {
        if (string.IsNullOrWhiteSpace(errorCode))
        {
            return null;
        }

        return errorCode.Length <= 64
               && errorCode.All(static character =>
                   character is >= 'a' and <= 'z'
                       or >= 'A' and <= 'Z'
                       or >= '0' and <= '9'
                       or '_'
                       or '-')
            ? errorCode
            : null;
    }

    private static void CancelPairingTimeout(NodeRuntime runtime)
    {
        if (runtime.IsPairing)
        {
            runtime.PairingTimeoutCancellation.Cancel();
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    private sealed class NodeRuntime(
        ILocalSendReceiverNode node,
        string? pairingPin,
        DateTimeOffset? pairingExpiresAtUtc)
    {
        public CancellationTokenSource Cancellation { get; } = new();

        public bool IsPairing => PairingPin is not null;

        public ILocalSendReceiverNode Node { get; } = node;

        public string? PairingPin { get; } = pairingPin;

        public DateTimeOffset? PairingExpiresAtUtc { get; } = pairingExpiresAtUtc;

        public CancellationTokenSource PairingTimeoutCancellation { get; } = new();

        public bool PairingExpired { get; set; }

        public object PairingStateGate { get; } = new();

        public CancellationTokenSource PairingTrustCancellation { get; } = new();

        public bool PairingTransferStarted { get; set; }

        public Task PairingTimeoutTask { get; set; } = Task.CompletedTask;

        public Task WatcherTask { get; set; } = Task.CompletedTask;
    }
}
