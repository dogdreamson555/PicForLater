using System.Diagnostics;
using PicForLater.App.Models;

namespace PicForLater.App.Services;

public sealed class ScreenshotCaptureService : IScreenshotCaptureService
{
    private const int FirstHotKeyId = 0x5000;
    private const int LastHotKeyId = 0xBFFF;

    private readonly IScreenshotCapturePlatform _platform;
    private readonly IScreenshotCapturePreferenceService _preferences;
    private readonly IScreenshotCaptureImporter _importer;
    private readonly ScreenshotCaptureOptions _options;
    private readonly object _stateGate = new();
    private readonly SemaphoreSlim _settingsGate = new(1, 1);
    private readonly HashSet<int> _unregisterRetryIds = [];

    private ScreenshotCaptureSnapshot _snapshot = ScreenshotCaptureSnapshot.Default;
    private bool _started;
    private bool _acceptHotKeyMessages;
    private int? _activeHotKeyId;
    private int _nextHotKeyId = FirstHotKeyId;
    private CancellationTokenSource? _captureCancellation;
    private Task _captureTask = Task.CompletedTask;
    private bool _restartCaptureRequested;

    public ScreenshotCaptureService(
        IScreenshotCapturePlatform platform,
        IScreenshotCapturePreferenceService preferences,
        IScreenshotCaptureImporter importer,
        ScreenshotCaptureOptions? options = null)
    {
        _platform = platform ?? throw new ArgumentNullException(nameof(platform));
        _preferences = preferences ?? throw new ArgumentNullException(nameof(preferences));
        _importer = importer ?? throw new ArgumentNullException(nameof(importer));
        _options = options ?? ScreenshotCaptureOptions.Default;
        _options.Validate();
    }

    public ScreenshotCaptureSnapshot Snapshot
    {
        get
        {
            lock (_stateGate)
            {
                return _snapshot;
            }
        }
    }

    public event EventHandler<ScreenshotCaptureSnapshotChangedEventArgs>? SnapshotChanged;

    public event EventHandler<ScreenshotCaptureCompletedEventArgs>? CaptureCompleted;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        await _settingsGate.WaitAsync(cancellationToken).ConfigureAwait(true);
        try
        {
            lock (_stateGate)
            {
                if (_started)
                {
                    return;
                }

                _started = true;
            }

            _platform.HotKeyPressed += Platform_HotKeyPressed;

            ScreenshotCapturePreferences preferences;
            try
            {
                preferences = _preferences.Read();
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                lock (_stateGate)
                {
                    _started = false;
                }

                _platform.HotKeyPressed -= Platform_HotKeyPressed;
                UpdateSnapshot(ScreenshotCaptureSnapshot.Default with
                {
                    RegistrationState = RegistrationState.Faulted,
                });
                return;
            }

            UpdateSnapshot(new ScreenshotCaptureSnapshot(
                preferences.IsEnabledRequested,
                preferences.HotKey,
                RegistrationState.Disabled,
                CaptureState.Idle));

            if (!preferences.IsEnabledRequested)
            {
                return;
            }

            int hotKeyId = AllocateHotKeyId();
            ScreenshotHotKeyRegistrationStatus registration =
                _platform.RegisterHotKey(hotKeyId, preferences.HotKey);
            switch (registration)
            {
                case ScreenshotHotKeyRegistrationStatus.Registered:
                    lock (_stateGate)
                    {
                        _activeHotKeyId = hotKeyId;
                        _acceptHotKeyMessages = true;
                    }

                    UpdateSnapshot(Snapshot with { RegistrationState = RegistrationState.Ready });
                    break;
                case ScreenshotHotKeyRegistrationStatus.Conflict:
                    UpdateSnapshot(Snapshot with { RegistrationState = RegistrationState.Conflict });
                    break;
                default:
                    UpdateSnapshot(Snapshot with { RegistrationState = RegistrationState.Faulted });
                    break;
            }
        }
        finally
        {
            _settingsGate.Release();
        }
    }

    public async Task<ScreenshotSettingsOperationResult> SetEnabledAsync(
        bool isEnabled,
        CancellationToken cancellationToken = default)
    {
        await _settingsGate.WaitAsync(cancellationToken).ConfigureAwait(true);
        try
        {
            if (!IsStarted())
            {
                return ScreenshotSettingsOperationResult.Failed(ScreenshotSettingsFailureKind.NotStarted);
            }

            return isEnabled
                ? EnableCore()
                : DisableCore();
        }
        finally
        {
            _settingsGate.Release();
        }
    }

    public async Task<ScreenshotSettingsOperationResult> UpdateHotKeyAsync(
        ScreenshotHotKey hotKey,
        CancellationToken cancellationToken = default)
    {
        if (!ScreenshotHotKey.IsValid(hotKey.Modifiers, hotKey.Key))
        {
            throw new ArgumentOutOfRangeException(nameof(hotKey));
        }

        await _settingsGate.WaitAsync(cancellationToken).ConfigureAwait(true);
        try
        {
            if (!IsStarted())
            {
                return ScreenshotSettingsOperationResult.Failed(ScreenshotSettingsFailureKind.NotStarted);
            }

            ScreenshotCaptureSnapshot before = Snapshot;
            if (before.HotKey == hotKey)
            {
                return ScreenshotSettingsOperationResult.Success;
            }

            int candidateId = AllocateHotKeyId();
            ScreenshotHotKeyRegistrationStatus registration =
                _platform.RegisterHotKey(candidateId, hotKey);
            if (registration != ScreenshotHotKeyRegistrationStatus.Registered)
            {
                return ScreenshotSettingsOperationResult.Failed(
                    registration == ScreenshotHotKeyRegistrationStatus.Conflict
                        ? ScreenshotSettingsFailureKind.HotKeyConflict
                        : ScreenshotSettingsFailureKind.Registration);
            }

            try
            {
                _preferences.SetHotKey(hotKey);
            }
            catch (Exception exception) when (IsPreferenceException(exception))
            {
                TryUnregisterOrRemember(candidateId);
                return ScreenshotSettingsOperationResult.Failed(ScreenshotSettingsFailureKind.Preference);
            }

            int? previousId;
            if (before.IsEnabledRequested)
            {
                lock (_stateGate)
                {
                    previousId = _activeHotKeyId;
                    _activeHotKeyId = candidateId;
                    _acceptHotKeyMessages = true;
                }

                UpdateSnapshot(before with
                {
                    HotKey = hotKey,
                    RegistrationState = RegistrationState.Ready,
                });
            }
            else
            {
                previousId = candidateId;
                UpdateSnapshot(before with
                {
                    HotKey = hotKey,
                    RegistrationState = RegistrationState.Disabled,
                });
            }

            if (previousId is int id && id != _activeHotKeyId)
            {
                TryUnregisterOrRemember(id);
            }

            return ScreenshotSettingsOperationResult.Success;
        }
        finally
        {
            _settingsGate.Release();
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await _settingsGate.WaitAsync(cancellationToken).ConfigureAwait(true);
        Task captureTask;
        int[] registrations;
        bool wasStarted;
        try
        {
            lock (_stateGate)
            {
                wasStarted = _started;
                if (!wasStarted && _unregisterRetryIds.Count == 0)
                {
                    return;
                }

                _started = false;
                _acceptHotKeyMessages = false;
                _restartCaptureRequested = false;
                _captureCancellation?.Cancel();
                captureTask = wasStarted ? _captureTask : Task.CompletedTask;
                var registrationIds = new HashSet<int>(_unregisterRetryIds);
                if (_activeHotKeyId is int activeHotKeyId)
                {
                    registrationIds.Add(activeHotKeyId);
                }

                registrations = registrationIds.ToArray();
                _activeHotKeyId = null;
                _unregisterRetryIds.Clear();
            }

            if (wasStarted)
            {
                _platform.HotKeyPressed -= Platform_HotKeyPressed;
                UpdateSnapshot(Snapshot with
                {
                    RegistrationState = RegistrationState.Disabled,
                    CaptureState = CaptureState.Idle,
                });
            }

            foreach (int hotKeyId in registrations)
            {
                bool unregistered;
                try
                {
                    unregistered = _platform.UnregisterHotKey(hotKeyId);
                }
                catch (ObjectDisposedException)
                {
                    // WM_NCDESTROY is the final native cleanup boundary. A Stop
                    // that arrived through Window.Closed must still cancel and
                    // await the business session without retrying a dead HWND.
                    unregistered = true;
                }

                if (!unregistered)
                {
                    lock (_stateGate)
                    {
                        _unregisterRetryIds.Add(hotKeyId);
                    }
                }
            }
        }
        finally
        {
            _settingsGate.Release();
        }

        try
        {
            await captureTask.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
        }
    }

    private ScreenshotSettingsOperationResult EnableCore()
    {
        ScreenshotCaptureSnapshot before = Snapshot;
        if (before.IsEnabledRequested && before.RegistrationState == RegistrationState.Ready)
        {
            return ScreenshotSettingsOperationResult.Success;
        }

        int candidateId = AllocateHotKeyId();
        ScreenshotHotKeyRegistrationStatus registration =
            _platform.RegisterHotKey(candidateId, before.HotKey);
        if (registration != ScreenshotHotKeyRegistrationStatus.Registered)
        {
            UpdateSnapshot(before with
            {
                RegistrationState = registration == ScreenshotHotKeyRegistrationStatus.Conflict
                    ? RegistrationState.Conflict
                    : RegistrationState.Faulted,
            });
            return ScreenshotSettingsOperationResult.Failed(
                registration == ScreenshotHotKeyRegistrationStatus.Conflict
                    ? ScreenshotSettingsFailureKind.HotKeyConflict
                    : ScreenshotSettingsFailureKind.Registration);
        }

        try
        {
            _preferences.SetEnabled(true);
        }
        catch (Exception exception) when (IsPreferenceException(exception))
        {
            TryUnregisterOrRemember(candidateId);
            UpdateSnapshot(before with { RegistrationState = RegistrationState.Faulted });
            return ScreenshotSettingsOperationResult.Failed(ScreenshotSettingsFailureKind.Preference);
        }

        lock (_stateGate)
        {
            _activeHotKeyId = candidateId;
            _acceptHotKeyMessages = true;
        }

        UpdateSnapshot(before with
        {
            IsEnabledRequested = true,
            RegistrationState = RegistrationState.Ready,
        });
        return ScreenshotSettingsOperationResult.Success;
    }

    private ScreenshotSettingsOperationResult DisableCore()
    {
        ScreenshotCaptureSnapshot before = Snapshot;
        lock (_stateGate)
        {
            _acceptHotKeyMessages = false;
            _restartCaptureRequested = false;
            _captureCancellation?.Cancel();
        }

        try
        {
            _preferences.SetEnabled(false);
        }
        catch (Exception exception) when (IsPreferenceException(exception))
        {
            lock (_stateGate)
            {
                _acceptHotKeyMessages = _activeHotKeyId.HasValue &&
                    before.RegistrationState == RegistrationState.Ready;
            }

            return ScreenshotSettingsOperationResult.Failed(ScreenshotSettingsFailureKind.Preference);
        }

        int? previousId;
        lock (_stateGate)
        {
            previousId = _activeHotKeyId;
            _activeHotKeyId = null;
        }

        UpdateSnapshot(before with
        {
            IsEnabledRequested = false,
            RegistrationState = RegistrationState.Disabled,
        });
        // CaptureState intentionally remains factual until the cancelled session
        // exits. A rapid re-enable must not admit a second importer concurrently.
        if (previousId is int id)
        {
            TryUnregisterOrRemember(id);
        }

        return ScreenshotSettingsOperationResult.Success;
    }

    private void Platform_HotKeyPressed(object? sender, ScreenshotHotKeyPressedEventArgs e)
    {
        CancellationTokenSource captureCancellation;
        ScreenshotCaptureSnapshot? changedSnapshot;
        lock (_stateGate)
        {
            if (!_started ||
                !_acceptHotKeyMessages ||
                _activeHotKeyId != e.HotKeyId ||
                _snapshot.RegistrationState != RegistrationState.Ready ||
                _snapshot.CaptureState == CaptureState.Importing)
            {
                return;
            }

            if (_snapshot.CaptureState == CaptureState.Capturing)
            {
                // An unpackaged caller cannot receive the Snipping Tool's cancel
                // callback. A fresh hotkey press is therefore an explicit retry:
                // cancel the stale wait and let its finally block start one new
                // session after the old one has fully exited.
                _restartCaptureRequested = true;
                _captureCancellation?.Cancel();
                return;
            }

            _restartCaptureRequested = false;
            captureCancellation = new CancellationTokenSource();
            _captureCancellation = captureCancellation;
            changedSnapshot = SetSnapshotLocked(_snapshot with { CaptureState = CaptureState.Capturing });
            _captureTask = RunCaptureAsync(captureCancellation, _snapshot.HotKey);
        }

        RaiseSnapshotChanged(changedSnapshot);
    }

    private async Task RunCaptureAsync(
        CancellationTokenSource sessionCancellation,
        ScreenshotHotKey sessionHotKey)
    {
        // WM_HOTKEY delivery and state publication must finish before a session
        // can synchronously fail and return to Idle.
        await Task.Yield();

        ScreenshotCaptureResult? completion = null;
        using var timeout = new CancellationTokenSource(_options.CaptureTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            sessionCancellation.Token,
            timeout.Token);
        CancellationToken cancellationToken = linked.Token;

        try
        {
            if (!await WaitForCaptureKeysReleasedAsync(sessionHotKey, cancellationToken)
                    .ConfigureAwait(false))
            {
                completion = ScreenshotCaptureResult.Failed(
                    ScreenshotCaptureFailureKind.InputInjection);
                return;
            }

            cancellationToken.ThrowIfCancellationRequested();
            uint observedSequence = _platform.GetClipboardSequenceNumber();
            if (observedSequence == 0)
            {
                ScreenshotClipboardAccessResult baselineProbe =
                    await _platform.ProbeClipboardAccessAsync(cancellationToken)
                        .ConfigureAwait(false);
                if (!baselineProbe.IsAvailable)
                {
                    completion = ScreenshotCaptureResult.Failed(
                        ScreenshotCaptureFailureKind.ClipboardUnavailable);
                    return;
                }

                observedSequence = baselineProbe.SequenceNumber;
            }

            cancellationToken.ThrowIfCancellationRequested();
            ScreenshotForegroundWindowSnapshot invokingForeground =
                _platform.GetForegroundWindowSnapshot();
            if (!_platform.SendScreenshotShortcut())
            {
                completion = ScreenshotCaptureResult.Failed(
                    ScreenshotCaptureFailureKind.InputInjection);
                return;
            }

            cancellationToken.ThrowIfCancellationRequested();
            ScreenshotForegroundWindowSnapshot captureUiForeground = default;
            long? captureUiExitObservedAt = null;
            while (true)
            {
                await Task.Delay(_options.ClipboardPollingInterval, cancellationToken)
                    .ConfigureAwait(false);
                bool captureUiExitGraceElapsed = HasCaptureUiExitGraceElapsed(
                    invokingForeground,
                    _platform.GetForegroundWindowSnapshot(),
                    ref captureUiForeground,
                    ref captureUiExitObservedAt);
                uint currentSequence = _platform.GetClipboardSequenceNumber();
                if (currentSequence == observedSequence)
                {
                    if (currentSequence != 0)
                    {
                        if (captureUiExitGraceElapsed)
                        {
                            return;
                        }

                        continue;
                    }

                    ScreenshotClipboardAccessResult zeroProbe =
                        await _platform.ProbeClipboardAccessAsync(cancellationToken)
                            .ConfigureAwait(false);
                    if (!zeroProbe.IsAvailable)
                    {
                        completion = ScreenshotCaptureResult.Failed(
                            ScreenshotCaptureFailureKind.ClipboardUnavailable);
                        return;
                    }

                    currentSequence = zeroProbe.SequenceNumber;
                    if (currentSequence == observedSequence)
                    {
                        if (captureUiExitGraceElapsed)
                        {
                            return;
                        }

                        continue;
                    }

                    // The clipboard changed while the access probe held it.
                    // Reopen it through the normal detached read path below.
                }

                ScreenshotClipboardReadResult read;
                try
                {
                    read = await _platform.ReadClipboardImageAsync(cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception)
                {
                    completion = ScreenshotCaptureResult.Failed(
                        ScreenshotCaptureFailureKind.ClipboardUnavailable);
                    return;
                }

                if (read.Status != ScreenshotClipboardReadStatus.ClipboardUnavailable &&
                    read.SequenceNumber == observedSequence)
                {
                    // A zero polling value can mean temporary window-station
                    // inaccessibility rather than a wrapped sequence. Never
                    // claim content that the opened Clipboard identifies as old.
                    if (captureUiExitGraceElapsed)
                    {
                        return;
                    }

                    continue;
                }

                switch (read.Status)
                {
                    case ScreenshotClipboardReadStatus.NoImage:
                        observedSequence = read.SequenceNumber;
                        if (captureUiExitGraceElapsed)
                        {
                            return;
                        }

                        continue;
                    case ScreenshotClipboardReadStatus.ClipboardUnavailable:
                        completion = ScreenshotCaptureResult.Failed(
                            ScreenshotCaptureFailureKind.ClipboardUnavailable);
                        return;
                    case ScreenshotClipboardReadStatus.UnsupportedImage:
                        completion = ScreenshotCaptureResult.Failed(
                            ScreenshotCaptureFailureKind.UnsupportedClipboardImage);
                        return;
                    case ScreenshotClipboardReadStatus.InvalidImage:
                        completion = ScreenshotCaptureResult.Failed(
                            ScreenshotCaptureFailureKind.InvalidImage);
                        return;
                    case ScreenshotClipboardReadStatus.Image when read.Image is not null:
                        break;
                    default:
                        completion = ScreenshotCaptureResult.Failed(
                            ScreenshotCaptureFailureKind.InvalidImage);
                        return;
                }

                if (!TryEnterImporting(sessionCancellation))
                {
                    return;
                }

                ScreenshotImportResult imported =
                    await _importer.ImportAsync(read.Image, cancellationToken).ConfigureAwait(false);
                completion = imported.Status == ScreenshotImportStatus.Imported
                    ? ScreenshotCaptureResult.Imported(imported.ImageItemId)
                    : ScreenshotCaptureResult.Duplicate(imported.ImageItemId);
                return;
            }
        }
        catch (OperationCanceledException) when (
            timeout.IsCancellationRequested && !sessionCancellation.IsCancellationRequested)
        {
            completion = ScreenshotCaptureResult.TimedOut();
        }
        catch (OperationCanceledException) when (sessionCancellation.IsCancellationRequested)
        {
        }
        catch (ScreenshotCaptureImportException exception)
        {
            completion = ScreenshotCaptureResult.Failed(exception.FailureKind);
        }
        catch (InvalidDataException)
        {
            completion = ScreenshotCaptureResult.Failed(ScreenshotCaptureFailureKind.InvalidImage);
        }
        catch (Exception)
        {
            completion = ScreenshotCaptureResult.Failed(ScreenshotCaptureFailureKind.Import);
        }
        finally
        {
            ScreenshotCaptureSnapshot? changedSnapshot = null;
            lock (_stateGate)
            {
                if (ReferenceEquals(_captureCancellation, sessionCancellation))
                {
                    if (_restartCaptureRequested &&
                        _started &&
                        _acceptHotKeyMessages &&
                        _activeHotKeyId.HasValue &&
                        _snapshot.RegistrationState == RegistrationState.Ready &&
                        _snapshot.CaptureState == CaptureState.Capturing)
                    {
                        _restartCaptureRequested = false;
                        var restartCancellation = new CancellationTokenSource();
                        _captureCancellation = restartCancellation;
                        _captureTask = RunCaptureAsync(
                            restartCancellation,
                            _snapshot.HotKey);
                    }
                    else
                    {
                        _restartCaptureRequested = false;
                        _captureCancellation = null;
                        changedSnapshot = SetSnapshotLocked(
                            _snapshot with { CaptureState = CaptureState.Idle });
                    }
                }
            }

            bool publishCompletion = completion is not null &&
                !sessionCancellation.IsCancellationRequested;
            sessionCancellation.Dispose();
            RaiseSnapshotChanged(changedSnapshot);
            if (publishCompletion)
            {
                CaptureCompleted?.Invoke(this, new ScreenshotCaptureCompletedEventArgs(completion!));
            }
        }
    }

    private async Task<bool> WaitForCaptureKeysReleasedAsync(
        ScreenshotHotKey sessionHotKey,
        CancellationToken cancellationToken)
    {
        var timer = Stopwatch.StartNew();
        while (timer.Elapsed < _options.KeyReleaseTimeout)
        {
            if (_platform.AreCaptureKeysReleased(sessionHotKey))
            {
                return true;
            }

            await Task.Delay(_options.KeyReleasePollingInterval, cancellationToken)
                .ConfigureAwait(false);
        }

        return false;
    }

    private bool HasCaptureUiExitGraceElapsed(
        ScreenshotForegroundWindowSnapshot invokingForeground,
        ScreenshotForegroundWindowSnapshot currentForeground,
        ref ScreenshotForegroundWindowSnapshot captureUiForeground,
        ref long? captureUiExitObservedAt)
    {
        if (!captureUiForeground.IsAvailable)
        {
            if (currentForeground.IsAvailable &&
                currentForeground.WindowHandle != invokingForeground.WindowHandle)
            {
                captureUiForeground = currentForeground;
            }

            return false;
        }

        bool captureUiStillForeground =
            currentForeground.WindowHandle == captureUiForeground.WindowHandle ||
            (captureUiForeground.ProcessId != 0 &&
                currentForeground.ProcessId == captureUiForeground.ProcessId);
        if (captureUiStillForeground)
        {
            captureUiExitObservedAt = null;
            return false;
        }

        captureUiExitObservedAt ??= Stopwatch.GetTimestamp();
        return Stopwatch.GetElapsedTime(captureUiExitObservedAt.Value) >=
            _options.CaptureUiExitGracePeriod;
    }

    private bool TryEnterImporting(CancellationTokenSource sessionCancellation)
    {
        ScreenshotCaptureSnapshot? changedSnapshot;
        lock (_stateGate)
        {
            if (!ReferenceEquals(_captureCancellation, sessionCancellation) ||
                sessionCancellation.IsCancellationRequested ||
                !_started ||
                !_acceptHotKeyMessages)
            {
                return false;
            }

            changedSnapshot = SetSnapshotLocked(_snapshot with { CaptureState = CaptureState.Importing });
        }

        RaiseSnapshotChanged(changedSnapshot);
        return true;
    }

    private int AllocateHotKeyId()
    {
        lock (_stateGate)
        {
            int range = LastHotKeyId - FirstHotKeyId + 1;
            for (int attempt = 0; attempt < range; attempt++)
            {
                int candidate = _nextHotKeyId;
                _nextHotKeyId = _nextHotKeyId == LastHotKeyId
                    ? FirstHotKeyId
                    : _nextHotKeyId + 1;
                if (_activeHotKeyId != candidate && !_unregisterRetryIds.Contains(candidate))
                {
                    return candidate;
                }
            }
        }

        throw new InvalidOperationException("No screenshot hotkey identifier is available.");
    }

    private void TryUnregisterOrRemember(int hotKeyId)
    {
        if (_platform.UnregisterHotKey(hotKeyId))
        {
            return;
        }

        lock (_stateGate)
        {
            _unregisterRetryIds.Add(hotKeyId);
        }
    }

    private bool IsStarted()
    {
        lock (_stateGate)
        {
            return _started;
        }
    }

    private void UpdateSnapshot(ScreenshotCaptureSnapshot snapshot)
    {
        ScreenshotCaptureSnapshot? changedSnapshot;
        lock (_stateGate)
        {
            changedSnapshot = SetSnapshotLocked(snapshot);
        }

        RaiseSnapshotChanged(changedSnapshot);
    }

    private ScreenshotCaptureSnapshot? SetSnapshotLocked(ScreenshotCaptureSnapshot snapshot)
    {
        Debug.Assert(Monitor.IsEntered(_stateGate));
        if (_snapshot == snapshot)
        {
            return null;
        }

        _snapshot = snapshot;
        return snapshot;
    }

    private void RaiseSnapshotChanged(ScreenshotCaptureSnapshot? snapshot)
    {
        if (snapshot is not null)
        {
            SnapshotChanged?.Invoke(this, new ScreenshotCaptureSnapshotChangedEventArgs(snapshot));
        }
    }

    private static bool IsPreferenceException(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or InvalidOperationException;
}
