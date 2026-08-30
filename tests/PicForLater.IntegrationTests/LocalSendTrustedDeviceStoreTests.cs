using System.Text;
using System.Text.Json;
using PicForLater.Infrastructure.Storage;

namespace PicForLater.IntegrationTests;

public sealed class LocalSendTrustedDeviceStoreTests
{
    private static readonly DateTimeOffset InitialTime =
        new(2026, 8, 28, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task MissingFile_ReturnsAnEmptyTrustedDeviceList()
    {
        using var root = new TemporaryAppDataRoot();
        var store = new LocalSendTrustedDeviceStore(root.Paths);

        var devices = await store.GetAllAsync();

        Assert.Empty(devices);
        Assert.False(File.Exists(root.Paths.LocalSendTrustedDevicesFilePath));
    }

    [Fact]
    public async Task AddFindMarkReceivedAndRemove_RoundTripsAcrossStoreInstances()
    {
        using var root = new TemporaryAppDataRoot();
        var time = new MutableTimeProvider(InitialTime);
        var store = new LocalSendTrustedDeviceStore(root.Paths, time);
        var fingerprint = Fingerprint('a');

        var added = await store.AddAsync(fingerprint, "Phone");

        Assert.Equal(fingerprint, added.Fingerprint);
        Assert.Equal(InitialTime, added.FirstPairedAtUtc);
        Assert.Null(added.LastReceivedAtUtc);
        Assert.Equal(added, await store.FindAsync(fingerprint.ToUpperInvariant()));

        time.UtcNow = InitialTime.AddMinutes(5);
        Assert.True(await store.MarkReceivedAsync(fingerprint, "Renamed phone"));

        var reopened = new LocalSendTrustedDeviceStore(root.Paths);
        var received = await reopened.FindAsync(fingerprint);
        Assert.NotNull(received);
        Assert.Equal("Renamed phone", received.DisplayName);
        Assert.Equal(InitialTime, received.FirstPairedAtUtc);
        Assert.Equal(time.UtcNow, received.LastReceivedAtUtc);

        Assert.True(await reopened.RemoveAsync(fingerprint.ToUpperInvariant()));
        Assert.Null(await reopened.FindAsync(fingerprint));
        Assert.False(await reopened.RemoveAsync(fingerprint));
    }

    [Fact]
    public async Task Add_DeduplicatesNormalizedFingerprintAndPreservesTimestamps()
    {
        using var root = new TemporaryAppDataRoot();
        var time = new MutableTimeProvider(InitialTime);
        var store = new LocalSendTrustedDeviceStore(root.Paths, time);
        var fingerprint = Fingerprint('b');

        await store.AddAsync(fingerprint, "First name");
        time.UtcNow = InitialTime.AddMinutes(1);
        await store.MarkReceivedAsync(fingerprint, "Received name");
        var before = await store.FindAsync(fingerprint);

        time.UtcNow = InitialTime.AddDays(1);
        var after = await store.AddAsync(fingerprint.ToUpperInvariant(), "Paired again");
        var devices = await store.GetAllAsync();

        Assert.Single(devices);
        Assert.Equal("Paired again", after.DisplayName);
        Assert.Equal(before!.FirstPairedAtUtc, after.FirstPairedAtUtc);
        Assert.Equal(before.LastReceivedAtUtc, after.LastReceivedAtUtc);
    }

    [Fact]
    public async Task SameDisplayNameWithDifferentFingerprints_RemainsTwoDevices()
    {
        using var root = new TemporaryAppDataRoot();
        var store = new LocalSendTrustedDeviceStore(root.Paths);

        await store.AddAsync(Fingerprint('c'), "Shared name");
        await store.AddAsync(Fingerprint('d'), "Shared name");

        Assert.Equal(2, (await store.GetAllAsync()).Count);
    }

    [Fact]
    public async Task MarkReceived_DoesNotTrustAnUnknownFingerprint()
    {
        using var root = new TemporaryAppDataRoot();
        var store = new LocalSendTrustedDeviceStore(root.Paths);

        Assert.False(await store.MarkReceivedAsync(Fingerprint('e'), "Unknown"));
        Assert.Empty(await store.GetAllAsync());
        Assert.False(File.Exists(root.Paths.LocalSendTrustedDevicesFilePath));
    }

    [Fact]
    public async Task DisplayName_IsNormalizedAndTruncatedByUnicodeScalar()
    {
        using var root = new TemporaryAppDataRoot();
        var store = new LocalSendTrustedDeviceStore(root.Paths);
        var formatted = await store.AddAsync(Fingerprint('1'), "  Pixel\t\u202e  Phone\r\n ");
        var empty = await store.AddAsync(Fingerprint('2'), "\u202e\r\n\t");
        var emoji = await store.AddAsync(
            Fingerprint('3'),
            string.Concat(Enumerable.Repeat("😀", 90)));

        Assert.Equal("Pixel Phone", formatted.DisplayName);
        Assert.Equal("LocalSend device", empty.DisplayName);
        Assert.Equal(
            LocalSendTrustedDeviceStore.MaximumDisplayNameLength,
            emoji.DisplayName.EnumerateRunes().Count());
        Assert.DoesNotContain('\uFFFD', emoji.DisplayName);
    }

    [Fact]
    public async Task InvalidFingerprintArguments_AreRejectedOrFailClosed()
    {
        using var root = new TemporaryAppDataRoot();
        var store = new LocalSendTrustedDeviceStore(root.Paths);

        Assert.Null(await store.FindAsync("not-a-fingerprint"));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            store.AddAsync("not-a-fingerprint", "Phone"));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            store.MarkReceivedAsync("not-a-fingerprint", "Phone"));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            store.RemoveAsync("not-a-fingerprint"));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            store.AddAsync(Fingerprint('a') + "\n", "Phone"));
    }

    [Theory]
    [MemberData(nameof(InvalidDocuments))]
    public async Task InvalidDocument_FailsClosed(string json)
    {
        using var root = new TemporaryAppDataRoot();
        root.Paths.EnsureCreated();
        await File.WriteAllTextAsync(root.Paths.LocalSendTrustedDevicesFilePath, json);
        var store = new LocalSendTrustedDeviceStore(root.Paths);

        await Assert.ThrowsAsync<InvalidDataException>(() => store.GetAllAsync());
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            store.FindAsync(Fingerprint('f')));
    }

    [Fact]
    public async Task OversizedDocument_FailsClosed()
    {
        using var root = new TemporaryAppDataRoot();
        root.Paths.EnsureCreated();
        var bytes = new byte[LocalSendTrustedDeviceStore.MaximumFileLength + 1];
        await File.WriteAllBytesAsync(root.Paths.LocalSendTrustedDevicesFilePath, bytes);
        var store = new LocalSendTrustedDeviceStore(root.Paths);

        await Assert.ThrowsAsync<InvalidDataException>(() => store.GetAllAsync());
    }

    [Fact]
    public async Task SuccessfulReplacement_LeavesNoTemporaryFile()
    {
        using var root = new TemporaryAppDataRoot();
        var store = new LocalSendTrustedDeviceStore(root.Paths);

        await store.AddAsync(Fingerprint('1'), "First");
        await store.AddAsync(Fingerprint('2'), "Second");
        await store.MarkReceivedAsync(Fingerprint('1'), "First renamed");

        Assert.Empty(Directory.EnumerateFiles(
            root.Paths.DatabaseDirectoryPath,
            ".localsend-trusted-devices.json.*.tmp"));
        Assert.Equal(2, (await new LocalSendTrustedDeviceStore(root.Paths).GetAllAsync()).Count);
    }

    [Fact]
    public async Task FailedAtomicReplacement_PreservesThePreviousDocumentAndCleansTemporaryFile()
    {
        using var root = new TemporaryAppDataRoot();
        var store = new LocalSendTrustedDeviceStore(root.Paths);
        var originalFingerprint = Fingerprint('3');
        await store.AddAsync(originalFingerprint, "Original");

        await using (new FileStream(
                         root.Paths.LocalSendTrustedDevicesFilePath,
                         FileMode.Open,
                         FileAccess.Read,
                         FileShare.Read))
        {
            var exception = await Record.ExceptionAsync(() =>
                store.AddAsync(Fingerprint('4'), "Must not persist"));
            Assert.True(exception is IOException or UnauthorizedAccessException);
        }

        var reopened = new LocalSendTrustedDeviceStore(root.Paths);
        var devices = await reopened.GetAllAsync();
        Assert.Single(devices);
        Assert.Equal(originalFingerprint, devices[0].Fingerprint);
        Assert.Empty(Directory.EnumerateFiles(
            root.Paths.DatabaseDirectoryPath,
            ".localsend-trusted-devices.json.*.tmp"));
    }

    [Fact]
    public async Task TrustedDeviceFileReparsePoint_IsRejectedWithoutChangingTheTarget()
    {
        using var root = new TemporaryAppDataRoot();
        root.Paths.EnsureCreated();
        var outsideDirectory = CreateOutsideDirectory();
        var outsideFile = Path.Combine(outsideDirectory, "outside.json");
        await File.WriteAllTextAsync(outsideFile, "must remain");
        File.CreateSymbolicLink(root.Paths.LocalSendTrustedDevicesFilePath, outsideFile);

        try
        {
            var store = new LocalSendTrustedDeviceStore(root.Paths);
            await Assert.ThrowsAsync<InvalidOperationException>(() => store.GetAllAsync());
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                store.AddAsync(Fingerprint('5'), "Blocked"));
            Assert.Equal("must remain", await File.ReadAllTextAsync(outsideFile));
        }
        finally
        {
            File.Delete(root.Paths.LocalSendTrustedDevicesFilePath);
            Directory.Delete(outsideDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task DataDirectoryReparsePoint_IsRejectedWithoutWritingOutsideTheRoot()
    {
        using var root = new TemporaryAppDataRoot();
        root.Paths.EnsureCreated();
        var outsideDirectory = CreateOutsideDirectory();
        Directory.Delete(root.Paths.DatabaseDirectoryPath, recursive: true);
        Directory.CreateSymbolicLink(root.Paths.DatabaseDirectoryPath, outsideDirectory);

        try
        {
            var store = new LocalSendTrustedDeviceStore(root.Paths);
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                store.AddAsync(Fingerprint('6'), "Blocked"));
            Assert.Empty(Directory.EnumerateFileSystemEntries(outsideDirectory));
        }
        finally
        {
            Directory.Delete(root.Paths.DatabaseDirectoryPath);
            Directory.Delete(outsideDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task ConcurrentAdds_AreSerializedWithoutLosingDevices()
    {
        using var root = new TemporaryAppDataRoot();
        var store = new LocalSendTrustedDeviceStore(root.Paths);
        var fingerprints = Enumerable.Range(0, 16)
            .Select(index => index.ToString("x64"))
            .ToArray();

        await Task.WhenAll(fingerprints.Select((fingerprint, index) =>
            store.AddAsync(fingerprint, $"Phone {index}")));

        Assert.Equal(
            fingerprints.Order(StringComparer.Ordinal),
            (await store.GetAllAsync())
            .Select(device => device.Fingerprint)
            .Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task ConcurrentAddsAcrossStoreInstances_AreSerializedWithoutLosingDevices()
    {
        using var root = new TemporaryAppDataRoot();
        var firstStore = new LocalSendTrustedDeviceStore(root.Paths);
        var secondStore = new LocalSendTrustedDeviceStore(root.Paths);
        var fingerprints = Enumerable.Range(16, 16)
            .Select(index => index.ToString("x64"))
            .ToArray();

        await Task.WhenAll(fingerprints.Select((fingerprint, index) =>
            (index % 2 == 0 ? firstStore : secondStore).AddAsync(fingerprint, $"Phone {index}")));

        Assert.Equal(
            fingerprints.Order(StringComparer.Ordinal),
            (await firstStore.GetAllAsync())
            .Select(device => device.Fingerprint)
            .Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Cancellation_IsObservedBeforeReadingTheStore()
    {
        using var root = new TemporaryAppDataRoot();
        var store = new LocalSendTrustedDeviceStore(root.Paths);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            store.GetAllAsync(cancellation.Token));
        Assert.False(Directory.Exists(root.RootPath));
    }

    public static IEnumerable<object[]> InvalidDocuments()
    {
        yield return [string.Empty];
        yield return ["{ not-json"];
        yield return ["""
            { "schemaVersion": 1, "devices": [null] }
            """];
        yield return ["""
            { "schemaVersion": 2, "devices": [] }
            """];
        yield return [CreateDocument(
            new DeviceJson(Fingerprint('7'), "First", InitialTime, null),
            new DeviceJson(Fingerprint('7').ToUpperInvariant(), "Duplicate", InitialTime, null))];
        yield return [CreateDocument(
            new DeviceJson(Fingerprint('8'), "Bad offset", InitialTime.ToOffset(TimeSpan.FromHours(8)), null))];
        yield return [CreateDocument(
            new DeviceJson("not-a-fingerprint", "Invalid", InitialTime, null))];
        yield return [CreateDocument(
            new DeviceJson(
                Fingerprint('9'),
                "Backwards time",
                InitialTime,
                InitialTime.AddMinutes(-1)))];
        yield return [CreateDocument(Enumerable.Range(0, LocalSendTrustedDeviceStore.MaximumDeviceCount + 1)
            .Select(index => new DeviceJson(index.ToString("x64"), $"Phone {index}", InitialTime, null))
            .ToArray())];
    }

    private static string CreateDocument(params DeviceJson[] devices) =>
        JsonSerializer.Serialize(new { schemaVersion = 1, devices });

    private static string Fingerprint(char value) => new(value, 64);

    private static string CreateOutsideDirectory()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            "PicForLater.Tests.Outside",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed record DeviceJson(
        string Fingerprint,
        string DisplayName,
        DateTimeOffset FirstPairedAtUtc,
        DateTimeOffset? LastReceivedAtUtc);

    private sealed class MutableTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public DateTimeOffset UtcNow { get; set; } = utcNow;

        public override DateTimeOffset GetUtcNow() => UtcNow;
    }
}
