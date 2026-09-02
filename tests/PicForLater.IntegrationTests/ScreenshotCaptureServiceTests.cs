using System.Collections.Concurrent;
using PicForLater.App.Models;
using PicForLater.App.Services;

namespace PicForLater.IntegrationTests;

public sealed class ScreenshotCaptureServiceTests
{
    [Fact]
    public async Task Start_PreferenceReadFailureRollsBackStartedAndDoesNotAllowDefaultKeyRegistration()
    {
        var preferences = new FakePreferences { FailRead = true };
        var platform = new FakePlatform();
        var service = CreateService(platform, preferences);

        await service.StartAsync();
        ScreenshotSettingsOperationResult enable = await service.SetEnabledAsync(true);

        Assert.Equal(RegistrationState.Faulted, service.Snapshot.RegistrationState);
        Assert.False(enable.Succeeded);
        Assert.Equal(ScreenshotSettingsFailureKind.NotStarted, enable.FailureKind);
        Assert.Empty(platform.Registered);
    }

    [Fact]
    public async Task Start_PersistedEnabledConflictKeepsRequestedSeparateFromRuntimeRegistration()
    {
        var preferences = new FakePreferences(isEnabledRequested: true);
        var platform = new FakePlatform();
        platform.RegistrationResults.Enqueue(ScreenshotHotKeyRegistrationStatus.Conflict);
        var service = CreateService(platform, preferences);

        await service.StartAsync();

        Assert.True(service.Snapshot.IsEnabledRequested);
        Assert.Equal(RegistrationState.Conflict, service.Snapshot.RegistrationState);
        Assert.Empty(platform.Registered);
        Assert.True(preferences.Current.IsEnabledRequested);
        await service.StopAsync();
    }

    [Fact]
    public async Task Enable_RegistersBeforePersistingAndPublishesReadyOnlyAfterBothSucceed()
    {
        var preferences = new FakePreferences();
        var platform = new FakePlatform();
        var service = CreateService(platform, preferences);
        await service.StartAsync();
        preferences.BeforeEnabledSave = () => Assert.Single(platform.Registered);

        ScreenshotSettingsOperationResult result = await service.SetEnabledAsync(true);

        Assert.True(result.Succeeded);
        Assert.True(preferences.Current.IsEnabledRequested);
        Assert.True(service.Snapshot.IsEnabledRequested);
        Assert.Equal(RegistrationState.Ready, service.Snapshot.RegistrationState);
        Assert.Single(platform.Registered);
        Assert.Equal(["SaveEnabled:True"], preferences.Calls);
        await service.StopAsync();
    }

    [Fact]
    public async Task Enable_ConflictKeepsFeatureClosedAndDoesNotPersistEnabled()
    {
        var preferences = new FakePreferences();
        var platform = new FakePlatform();
        platform.RegistrationResults.Enqueue(ScreenshotHotKeyRegistrationStatus.Conflict);
        var service = CreateService(platform, preferences);
        await service.StartAsync();

        ScreenshotSettingsOperationResult result = await service.SetEnabledAsync(true);

        Assert.False(result.Succeeded);
        Assert.Equal(ScreenshotSettingsFailureKind.HotKeyConflict, result.FailureKind);
        Assert.False(service.Snapshot.IsEnabledRequested);
        Assert.Equal(RegistrationState.Conflict, service.Snapshot.RegistrationState);
        Assert.False(preferences.Current.IsEnabledRequested);
        Assert.Empty(platform.Registered);
        await service.StopAsync();
    }

    [Fact]
    public async Task Enable_PreferenceFailureUnregistersCandidateAndStaysClosed()
    {
        var preferences = new FakePreferences { FailEnabledSave = true };
        var platform = new FakePlatform();
        var service = CreateService(platform, preferences);
        await service.StartAsync();

        ScreenshotSettingsOperationResult result = await service.SetEnabledAsync(true);

        Assert.False(result.Succeeded);
        Assert.Equal(ScreenshotSettingsFailureKind.Preference, result.FailureKind);
        Assert.False(service.Snapshot.IsEnabledRequested);
        Assert.Equal(RegistrationState.Faulted, service.Snapshot.RegistrationState);
        Assert.Empty(platform.Registered);
        Assert.Single(platform.UnregisterCalls);
        await service.StopAsync();
    }

    [Fact]
    public async Task UpdateHotKey_EnabledRegistersCandidatePersistsSwitchesAcceptedIdThenUnregistersOld()
    {
        var preferences = new FakePreferences(isEnabledRequested: true);
        var platform = new FakePlatform();
        var service = CreateService(platform, preferences);
        await service.StartAsync();
        int oldId = Assert.Single(platform.Registered).Key;
        var replacement = new ScreenshotHotKey(
            ScreenshotHotKeyModifiers.Control | ScreenshotHotKeyModifiers.Alt,
            ScreenshotHotKeyKey.D7);

        ScreenshotSettingsOperationResult result = await service.UpdateHotKeyAsync(replacement);

        Assert.True(result.Succeeded);
        KeyValuePair<int, ScreenshotHotKey> current = Assert.Single(platform.Registered);
        Assert.NotEqual(oldId, current.Key);
        Assert.Equal(replacement, current.Value);
        Assert.Equal(replacement, service.Snapshot.HotKey);
        Assert.Equal(RegistrationState.Ready, service.Snapshot.RegistrationState);
        Assert.Contains(oldId, platform.UnregisterCalls);
        await service.StopAsync();
    }

    [Fact]
    public async Task UpdateHotKey_CandidateConflictLeavesOldRegistrationAndPreferenceUntouched()
    {
        var preferences = new FakePreferences(isEnabledRequested: true);
        var platform = new FakePlatform();
        var service = CreateService(platform, preferences);
        await service.StartAsync();
        KeyValuePair<int, ScreenshotHotKey> old = Assert.Single(platform.Registered);
        platform.RegistrationResults.Enqueue(ScreenshotHotKeyRegistrationStatus.Conflict);
        var replacement = new ScreenshotHotKey(
            ScreenshotHotKeyModifiers.Win | ScreenshotHotKeyModifiers.Control,
            ScreenshotHotKeyKey.Q);

        ScreenshotSettingsOperationResult result = await service.UpdateHotKeyAsync(replacement);

        Assert.False(result.Succeeded);
        Assert.Equal(ScreenshotSettingsFailureKind.HotKeyConflict, result.FailureKind);
        Assert.Equal(old, Assert.Single(platform.Registered));
        Assert.Equal(ScreenshotHotKey.Default, preferences.Current.HotKey);
        Assert.Equal(ScreenshotHotKey.Default, service.Snapshot.HotKey);
        await service.StopAsync();
    }

    [Fact]
    public async Task UpdateHotKey_PreferenceFailureRollsBackCandidateAndKeepsOldRegistration()
    {
        var preferences = new FakePreferences(isEnabledRequested: true) { FailHotKeySave = true };
        var platform = new FakePlatform();
        var service = CreateService(platform, preferences);
        await service.StartAsync();
        KeyValuePair<int, ScreenshotHotKey> old = Assert.Single(platform.Registered);
        var replacement = new ScreenshotHotKey(
            ScreenshotHotKeyModifiers.Win | ScreenshotHotKeyModifiers.Shift,
            ScreenshotHotKeyKey.B);

        ScreenshotSettingsOperationResult result = await service.UpdateHotKeyAsync(replacement);

        Assert.False(result.Succeeded);
        Assert.Equal(ScreenshotSettingsFailureKind.Preference, result.FailureKind);
        Assert.Equal(old, Assert.Single(platform.Registered));
        Assert.Equal(ScreenshotHotKey.Default, service.Snapshot.HotKey);
        Assert.Contains(platform.UnregisterCalls, id => id != old.Key);
        await service.StopAsync();
    }

    [Fact]
    public async Task UpdateHotKey_WhileDisabledChecksAvailabilityPersistsAndLeavesNoRegistration()
    {
        var preferences = new FakePreferences();
        var platform = new FakePlatform();
        var service = CreateService(platform, preferences);
        await service.StartAsync();
        var replacement = new ScreenshotHotKey(
            ScreenshotHotKeyModifiers.Win | ScreenshotHotKeyModifiers.Alt,
            ScreenshotHotKeyKey.C);

        ScreenshotSettingsOperationResult result = await service.UpdateHotKeyAsync(replacement);

        Assert.True(result.Succeeded);
        Assert.Equal(replacement, preferences.Current.HotKey);
        Assert.Equal(replacement, service.Snapshot.HotKey);
        Assert.Equal(RegistrationState.Disabled, service.Snapshot.RegistrationState);
        Assert.Empty(platform.Registered);
        Assert.Single(platform.UnregisterCalls);
        await service.StopAsync();
    }

    [Fact]
    public async Task UpdateHotKey_WhileRequestedOffClearsAStaleEnableConflictAfterAvailabilitySucceeds()
    {
        var preferences = new FakePreferences();
        var platform = new FakePlatform();
        var service = CreateService(platform, preferences);
        await service.StartAsync();
        platform.RegistrationResults.Enqueue(ScreenshotHotKeyRegistrationStatus.Conflict);
        Assert.False((await service.SetEnabledAsync(true)).Succeeded);
        Assert.Equal(RegistrationState.Conflict, service.Snapshot.RegistrationState);
        var replacement = new ScreenshotHotKey(
            ScreenshotHotKeyModifiers.Control | ScreenshotHotKeyModifiers.Alt,
            ScreenshotHotKeyKey.N);

        Assert.True((await service.UpdateHotKeyAsync(replacement)).Succeeded);

        Assert.False(service.Snapshot.IsEnabledRequested);
        Assert.Equal(RegistrationState.Disabled, service.Snapshot.RegistrationState);
        Assert.Equal(replacement, service.Snapshot.HotKey);
        await service.StopAsync();
    }

    [Fact]
    public async Task UpdateHotKey_OldUnregisterFailureKeepsOldIdIgnoredAndRetriesItOnStop()
    {
        var preferences = new FakePreferences(isEnabledRequested: true);
        var platform = new FakePlatform();
        var service = CreateService(platform, preferences);
        await service.StartAsync();
        int oldId = Assert.Single(platform.Registered).Key;
        platform.UnregisterFailures.Add(oldId);
        var replacement = new ScreenshotHotKey(
            ScreenshotHotKeyModifiers.Control | ScreenshotHotKeyModifiers.Alt,
            ScreenshotHotKeyKey.R);

        Assert.True((await service.UpdateHotKeyAsync(replacement)).Succeeded);
        int newId = platform.Registered.Single(pair => pair.Key != oldId).Key;
        platform.RaiseHotKey(oldId);
        await Task.Delay(10);
        Assert.Equal(0, platform.SendInputCalls);

        platform.RaiseHotKey(newId);
        await EventuallyAsync(() => platform.SendInputCalls == 1);
        await service.StopAsync();
        await service.StopAsync();

        Assert.True(platform.UnregisterCalls.Count(id => id == oldId) >= 3);
    }

    [Fact]
    public async Task Disabled_IgnoresPlatformHotKeyEvents()
    {
        var platform = new FakePlatform();
        var service = CreateService(platform, new FakePreferences());
        await service.StartAsync();

        platform.RaiseHotKey(0x5000);
        await Task.Delay(20);

        Assert.Equal(CaptureState.Idle, service.Snapshot.CaptureState);
        Assert.Equal(0, platform.SendInputCalls);
        await service.StopAsync();
    }

    [Fact]
    public async Task Capture_ClaimsOneSessionIgnoresRepeatedTriggerAndPublishesImportedResult()
    {
        var platform = new FakePlatform { AdvanceSequence = true };
        var importer = new BlockingImporter();
        var service = CreateService(
            platform,
            new FakePreferences(isEnabledRequested: true),
            importer);
        ScreenshotCaptureResult? completion = null;
        service.CaptureCompleted += (_, args) => completion = args.Result;
        await service.StartAsync();
        int activeId = Assert.Single(platform.Registered).Key;

        platform.RaiseHotKey(activeId);
        await importer.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(CaptureState.Importing, service.Snapshot.CaptureState);
        platform.RaiseHotKey(activeId);
        Assert.Equal(1, importer.CallCount);
        importer.Complete(new ScreenshotImportResult(ScreenshotImportStatus.Imported, importer.ImageItemId));
        await EventuallyAsync(() => completion is not null);

        Assert.Equal(CaptureOutcome.Imported, completion!.Outcome);
        Assert.Equal(importer.ImageItemId, completion.ImageItemId);
        Assert.Equal(CaptureState.Idle, service.Snapshot.CaptureState);
        Assert.Equal(1, platform.SendInputCalls);
        Assert.True(platform.Calls.IndexOf("GetSequence") < platform.Calls.IndexOf("SendInput"));
        await service.StopAsync();
    }

    [Fact]
    public async Task Capture_ClipboardChangeDuringSendInputIsDetectedFromPreInjectionBaseline()
    {
        var platform = new FakePlatform { SequenceChangesDuringSend = true };
        var service = CreateService(platform, new FakePreferences(isEnabledRequested: true));
        ScreenshotCaptureResult? completion = null;
        service.CaptureCompleted += (_, args) => completion = args.Result;
        await service.StartAsync();

        platform.RaiseHotKey(Assert.Single(platform.Registered).Key);
        await EventuallyAsync(() => completion is not null);

        Assert.Equal(CaptureOutcome.Imported, completion!.Outcome);
        Assert.True(platform.Calls.IndexOf("GetSequence") < platform.Calls.IndexOf("SendInput"));
        await service.StopAsync();
    }

    [Fact]
    public async Task Capture_NoSequenceChangePublishesTimedOutAndReturnsIdle()
    {
        var platform = new FakePlatform { AdvanceSequence = false };
        var service = CreateService(
            platform,
            new FakePreferences(isEnabledRequested: true),
            options: new ScreenshotCaptureOptions
            {
                KeyReleaseTimeout = TimeSpan.FromMilliseconds(20),
                KeyReleasePollingInterval = TimeSpan.FromMilliseconds(1),
                ClipboardPollingInterval = TimeSpan.FromMilliseconds(2),
                CaptureTimeout = TimeSpan.FromMilliseconds(20),
            });
        ScreenshotCaptureResult? completion = null;
        service.CaptureCompleted += (_, args) => completion = args.Result;
        await service.StartAsync();

        platform.RaiseHotKey(Assert.Single(platform.Registered).Key);
        await EventuallyAsync(() => completion is not null);

        Assert.Equal(CaptureOutcome.TimedOut, completion!.Outcome);
        Assert.Equal(CaptureState.Idle, service.Snapshot.CaptureState);
        await service.StopAsync();
    }

    [Fact]
    public async Task Capture_KeyReleaseTimeoutDoesNotCallSendInputAndReturnsIdle()
    {
        var platform = new FakePlatform { KeysReleased = false };
        var service = CreateService(platform, new FakePreferences(isEnabledRequested: true));
        ScreenshotCaptureResult? completion = null;
        service.CaptureCompleted += (_, args) => completion = args.Result;
        await service.StartAsync();

        platform.RaiseHotKey(Assert.Single(platform.Registered).Key);
        await EventuallyAsync(() => completion is not null);

        Assert.Equal(CaptureOutcome.Failed, completion!.Outcome);
        Assert.Equal(ScreenshotCaptureFailureKind.InputInjection, completion.FailureKind);
        Assert.Equal(0, platform.SendInputCalls);
        Assert.Equal(CaptureState.Idle, service.Snapshot.CaptureState);
        await service.StopAsync();
    }

    [Fact]
    public async Task Capture_PartialSendInputFailsImmediatelyWithoutWaitingForClipboard()
    {
        var platform = new FakePlatform { SendInputSucceeds = false };
        var service = CreateService(platform, new FakePreferences(isEnabledRequested: true));
        ScreenshotCaptureResult? completion = null;
        service.CaptureCompleted += (_, args) => completion = args.Result;
        await service.StartAsync();

        platform.RaiseHotKey(Assert.Single(platform.Registered).Key);
        await EventuallyAsync(() => completion is not null);

        Assert.Equal(CaptureOutcome.Failed, completion!.Outcome);
        Assert.Equal(ScreenshotCaptureFailureKind.InputInjection, completion.FailureKind);
        Assert.Equal(1, platform.SendInputCalls);
        Assert.Equal(0, platform.ReadClipboardCalls);
        Assert.Equal(CaptureState.Idle, service.Snapshot.CaptureState);
        await service.StopAsync();
    }

    [Fact]
    public async Task Stop_CancelsClaimedImportWithoutPublishingALateCompletion()
    {
        var platform = new FakePlatform { AdvanceSequence = true };
        var importer = new BlockingImporter();
        var service = CreateService(
            platform,
            new FakePreferences(isEnabledRequested: true),
            importer);
        var completions = new List<ScreenshotCaptureResult>();
        service.CaptureCompleted += (_, args) => completions.Add(args.Result);
        await service.StartAsync();
        platform.RaiseHotKey(Assert.Single(platform.Registered).Key);
        await importer.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));

        await service.StopAsync();

        Assert.Empty(completions);
        Assert.Equal(CaptureState.Idle, service.Snapshot.CaptureState);
        Assert.Equal(RegistrationState.Disabled, service.Snapshot.RegistrationState);
    }

    [Fact]
    public async Task DisableThenReenable_DoesNotAdmitSecondSessionUntilOldImporterActuallyEnds()
    {
        var platform = new FakePlatform { AdvanceSequence = true };
        var importer = new BlockingImporter(ignoreCancellation: true);
        var service = CreateService(
            platform,
            new FakePreferences(isEnabledRequested: true),
            importer);
        await service.StartAsync();
        platform.RaiseHotKey(Assert.Single(platform.Registered).Key);
        await importer.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(CaptureState.Importing, service.Snapshot.CaptureState);

        Assert.True((await service.SetEnabledAsync(false)).Succeeded);
        Assert.True((await service.SetEnabledAsync(true)).Succeeded);
        Assert.Equal(RegistrationState.Ready, service.Snapshot.RegistrationState);
        Assert.Equal(CaptureState.Importing, service.Snapshot.CaptureState);
        platform.RaiseHotKey(Assert.Single(platform.Registered).Key);
        await Task.Delay(10);
        Assert.Equal(1, importer.CallCount);

        importer.Complete(new ScreenshotImportResult(ScreenshotImportStatus.Imported, importer.ImageItemId));
        await EventuallyAsync(() => service.Snapshot.CaptureState == CaptureState.Idle);
        await service.StopAsync();
    }

    private static ScreenshotCaptureService CreateService(
        FakePlatform platform,
        FakePreferences preferences,
        IScreenshotCaptureImporter? importer = null,
        ScreenshotCaptureOptions? options = null) =>
        new(
            platform,
            preferences,
            importer ?? new ImmediateImporter(),
            options ?? new ScreenshotCaptureOptions
            {
                KeyReleaseTimeout = TimeSpan.FromMilliseconds(50),
                KeyReleasePollingInterval = TimeSpan.FromMilliseconds(1),
                ClipboardPollingInterval = TimeSpan.FromMilliseconds(2),
                CaptureTimeout = TimeSpan.FromMilliseconds(100),
            });

    private static async Task EventuallyAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (!condition())
        {
            await Task.Delay(5, timeout.Token);
        }
    }

    private sealed class FakePreferences : IScreenshotCapturePreferenceService
    {
        internal FakePreferences(bool isEnabledRequested = false)
        {
            Current = new ScreenshotCapturePreferences(isEnabledRequested, ScreenshotHotKey.Default);
        }

        internal ScreenshotCapturePreferences Current { get; private set; }
        internal bool FailEnabledSave { get; init; }
        internal bool FailHotKeySave { get; init; }
        internal bool FailRead { get; init; }
        internal Action? BeforeEnabledSave { get; set; }
        internal List<string> Calls { get; } = [];

        public ScreenshotCapturePreferences Read() =>
            FailRead ? throw new IOException("fixture") : Current;

        public void SetEnabled(bool isEnabled)
        {
            Calls.Add($"SaveEnabled:{isEnabled}");
            if (FailEnabledSave)
            {
                throw new IOException("fixture");
            }

            BeforeEnabledSave?.Invoke();
            Current = Current with { IsEnabledRequested = isEnabled };
        }

        public void SetHotKey(ScreenshotHotKey hotKey)
        {
            Calls.Add("SaveHotKey");
            if (FailHotKeySave)
            {
                throw new IOException("fixture");
            }

            Current = Current with { HotKey = hotKey };
        }
    }

    private sealed class FakePlatform : IScreenshotCapturePlatform
    {
        private uint _sequence = 1;

        internal Queue<ScreenshotHotKeyRegistrationStatus> RegistrationResults { get; } = new();
        internal Dictionary<int, ScreenshotHotKey> Registered { get; } = [];
        internal List<int> UnregisterCalls { get; } = [];
        internal HashSet<int> UnregisterFailures { get; } = [];
        internal List<string> Calls { get; } = [];
        internal bool AdvanceSequence { get; init; }
        internal bool SequenceChangesDuringSend { get; init; }
        internal bool KeysReleased { get; init; } = true;
        internal bool SendInputSucceeds { get; init; } = true;
        internal int SendInputCalls { get; private set; }
        internal int ReadClipboardCalls { get; private set; }

        public event EventHandler<ScreenshotHotKeyPressedEventArgs>? HotKeyPressed;

        public ScreenshotHotKeyRegistrationStatus RegisterHotKey(int hotKeyId, ScreenshotHotKey hotKey)
        {
            Calls.Add("Register");
            ScreenshotHotKeyRegistrationStatus result = RegistrationResults.Count == 0
                ? ScreenshotHotKeyRegistrationStatus.Registered
                : RegistrationResults.Dequeue();
            if (result == ScreenshotHotKeyRegistrationStatus.Registered)
            {
                Registered.Add(hotKeyId, hotKey);
            }

            return result;
        }

        public bool UnregisterHotKey(int hotKeyId)
        {
            UnregisterCalls.Add(hotKeyId);
            if (UnregisterFailures.Contains(hotKeyId))
            {
                return false;
            }

            Registered.Remove(hotKeyId);
            return true;
        }

        public bool AreCaptureKeysReleased(ScreenshotHotKey hotKey) => KeysReleased;

        public bool SendScreenshotShortcut()
        {
            Calls.Add("SendInput");
            SendInputCalls++;
            if (SequenceChangesDuringSend)
            {
                _sequence++;
            }

            return SendInputSucceeds;
        }

        public uint GetClipboardSequenceNumber()
        {
            Calls.Add("GetSequence");
            if (AdvanceSequence && SendInputCalls > 0)
            {
                return _sequence++;
            }

            return _sequence;
        }

        public ValueTask<ScreenshotClipboardReadResult> ReadClipboardImageAsync(
            CancellationToken cancellationToken = default)
        {
            ReadClipboardCalls++;
            return ValueTask.FromResult(ScreenshotClipboardReadResult.FromImage(
                new ScreenshotClipboardImage(ScreenshotClipboardImageFormat.Png, new byte[] { 1, 2, 3 })));
        }

        internal void RaiseHotKey(int hotKeyId) =>
            HotKeyPressed?.Invoke(this, new ScreenshotHotKeyPressedEventArgs(hotKeyId));

    }

    private sealed class ImmediateImporter : IScreenshotCaptureImporter
    {
        public Task<ScreenshotImportResult> ImportAsync(
            ScreenshotClipboardImage image,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ScreenshotImportResult(ScreenshotImportStatus.Imported, Guid.NewGuid()));
    }

    private sealed class BlockingImporter : IScreenshotCaptureImporter
    {
        private readonly TaskCompletionSource<ScreenshotImportResult> _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly bool _ignoreCancellation;

        internal BlockingImporter(bool ignoreCancellation = false)
        {
            _ignoreCancellation = ignoreCancellation;
        }

        internal TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Guid ImageItemId { get; } = Guid.NewGuid();
        internal int CallCount { get; private set; }

        public Task<ScreenshotImportResult> ImportAsync(
            ScreenshotClipboardImage image,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            Started.TrySetResult();
            return _ignoreCancellation
                ? _completion.Task
                : _completion.Task.WaitAsync(cancellationToken);
        }

        internal void Complete(ScreenshotImportResult result) => _completion.TrySetResult(result);
    }
}
