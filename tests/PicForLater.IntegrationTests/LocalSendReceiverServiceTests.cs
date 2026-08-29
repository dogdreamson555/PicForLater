using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using PicForLater.Infrastructure.Library;
using PicForLater.Infrastructure.LocalSend;
using PicForLater.Infrastructure.Storage;

namespace PicForLater.IntegrationTests;

public sealed class LocalSendReceiverServiceTests
{
    private static readonly string TrustedFingerprint = Fingerprint('A');
    private static readonly string OtherFingerprint = Fingerprint('B');

    [Fact]
    public async Task Start_SubscribesBeforeStartingAndUsesHardenedNormalOptions()
    {
        using var root = new TemporaryAppDataRoot();
        var factory = new FakeNodeFactory
        {
            ConfigureNode = static (_, node) => node.DiscoveryLimited = true,
        };
        await using var receiver = CreateReceiver(root, factory);

        await receiver.StartAsync();
        await receiver.StartAsync();

        var node = Assert.Single(factory.Nodes);
        Assert.True(node.WatcherSubscribed.Task.IsCompletedSuccessfully);
        Assert.True(node.StartedAfterWatcherSubscription);
        Assert.Equal(LocalSendReceiverStatus.Listening, receiver.Snapshot.Status);
        Assert.True(receiver.Snapshot.DiscoveryLimited);
        Assert.Null(node.Options.ReceivePin);
        Assert.Equal(root.Paths.LocalSendIdentityDirectoryPath, node.Options.IdentityDirectoryPath);
        Assert.Equal(root.Paths.LocalSendInboxDirectoryPath, node.Options.InboxDirectoryPath);
        Assert.Equal(LocalSendReceiverService.ReceiverPort, node.Options.Port);
        Assert.Equal(1, node.Options.MaximumConcurrentTransfers);
        Assert.Equal(2, node.Options.MaximumConcurrentFileUploads);
        Assert.Equal(20, node.Options.MaximumIncomingItemsPerTransfer);
        Assert.Equal(250L * 1024 * 1024, node.Options.MaximumIncomingTransferBytes);
        Assert.Equal(256L * 1024, node.Options.MaximumPrepareRequestBytes);

        await receiver.StopAsync();

        Assert.Equal(LocalSendReceiverStatus.Disabled, receiver.Snapshot.Status);
        Assert.True(node.StopCalled);
        Assert.True(node.DisposeCalled);
    }

    [Fact]
    public async Task TrustedMode_DeclinesUnknownAndUnsupportedOffersThenImportsSupportedImage()
    {
        using var root = new TemporaryAppDataRoot();
        var trustedDevices = new LocalSendTrustedDeviceStore(root.Paths);
        await trustedDevices.AddAsync(TrustedFingerprint, "Original phone");
        var inbox = new FakeInboxImporter();
        var factory = new FakeNodeFactory();
        await using var receiver = CreateReceiver(root, factory, trustedDevices, inbox);
        var completed = NewCompletion<LocalSendReceiveSummary>();
        receiver.TransferCompleted += summary => completed.TrySetResult(summary);
        await receiver.StartAsync();
        var node = Assert.Single(factory.Nodes);
        var unknownRequest = Request(
            OtherFingerprint,
            "Original phone",
            [Item("unknown", "unknown.png", 1)]);
        var unsupportedRequest = Request(
            TrustedFingerprint,
            "Renamed phone",
            [Item("document", "document.pdf", 1)]);
        var acceptedRequest = Request(
            TrustedFingerprint.ToLowerInvariant(),
            "Renamed phone",
            [Item("image", "folder/photo.PNG", 128)]);
        node.AcceptBehavior = (requestId, options, _) =>
        {
            Assert.Equal(acceptedRequest.RequestId, requestId);
            var acceptedId = Assert.Single(options.AcceptedItemIds);
            Assert.Equal("image", acceptedId);
            var targetName = Assert.Single(options.TargetFileNames).Value;
            Assert.True(targetName.Length <= LocalSendInboxImportService.MaximumInboxFileNameLength);
            Assert.EndsWith("-photo.png", targetName, StringComparison.Ordinal);
            return Task.FromResult(new LocalSendReceiveResult(
                acceptedRequest.TransferId,
                LocalSendReceiveTransferState.Completed,
                [new("image", "folder/photo.PNG", 128, "C:\\safe\\photo.png")]));
        };

        await node.EnqueueAsync(unknownRequest);
        await WaitUntilAsync(() => node.DeclinedRequestIds.Contains(unknownRequest.RequestId));
        await node.EnqueueAsync(unsupportedRequest);
        await WaitUntilAsync(() => node.DeclinedRequestIds.Contains(unsupportedRequest.RequestId));
        await node.EnqueueAsync(acceptedRequest);
        var summary = await completed.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(LocalSendReceiveTransferState.Completed, summary.TransferState);
        Assert.False(summary.PairedDuringTransfer);
        Assert.Equal(1, summary.ImportedCount);
        Assert.Equal(0, summary.FailedCount);
        Assert.Equal(["C:\\safe\\photo.png"], inbox.Paths);
        var updated = await trustedDevices.FindAsync(TrustedFingerprint);
        Assert.NotNull(updated);
        Assert.Equal("Renamed phone", updated.DisplayName);
        Assert.NotNull(updated.LastReceivedAtUtc);
        await WaitUntilAsync(() => receiver.Snapshot.Status == LocalSendReceiverStatus.Listening);
        var trustedSummary = Assert.Single(await receiver.GetTrustedDevicesAsync());
        Assert.Equal(32, trustedSummary.DeviceId.Length);
        Assert.DoesNotContain(TrustedFingerprint, trustedSummary.DeviceId, StringComparison.Ordinal);
        Assert.True(await receiver.RemoveTrustedDeviceAsync(trustedSummary.DeviceId));
        Assert.Null(await trustedDevices.FindAsync(TrustedFingerprint));
    }

    [Fact]
    public async Task TrustedMode_ImportsCompletedItemsFromAPartiallyFailedTransfer()
    {
        using var root = new TemporaryAppDataRoot();
        var trustedDevices = new LocalSendTrustedDeviceStore(root.Paths);
        await trustedDevices.AddAsync(TrustedFingerprint, "Phone");
        var inbox = new FakeInboxImporter(path => Task.FromResult(
            new LocalSendInboxImportResult(
                path.EndsWith("first.png", StringComparison.Ordinal)
                    ? LocalSendInboxImportStatus.Imported
                    : LocalSendInboxImportStatus.Duplicate,
                Guid.NewGuid(),
                InboxFileRemoved: true)));
        var factory = new FakeNodeFactory();
        await using var receiver = CreateReceiver(root, factory, trustedDevices, inbox);
        var completed = NewCompletion<LocalSendReceiveSummary>();
        receiver.TransferCompleted += summary => completed.TrySetResult(summary);
        await receiver.StartAsync();
        var node = Assert.Single(factory.Nodes);
        var request = Request(
            TrustedFingerprint,
            "Phone",
            [
                Item("first", "first.png", 100),
                Item("second", "second.jpeg", LocalSendReceiverService.MaximumImageBytes),
                Item("empty", "empty.webp", 0),
                Item("large", "large.jpg", LocalSendReceiverService.MaximumImageBytes + 1),
                Item("text", "note.txt", 12),
            ]);
        node.AcceptBehavior = (_, options, _) =>
        {
            Assert.Equal(["first", "second"], options.AcceptedItemIds.Order());
            Assert.EndsWith(".png", options.TargetFileNames["first"], StringComparison.Ordinal);
            Assert.EndsWith(".jpg", options.TargetFileNames["second"], StringComparison.Ordinal);
            return Task.FromResult(new LocalSendReceiveResult(
                request.TransferId,
                LocalSendReceiveTransferState.Failed,
                [
                    new("first", "first.png", 100, "C:\\inbox\\first.png"),
                    new("second", "second.jpeg", 200, "C:\\inbox\\second.jpg"),
                    new("unknown", "unknown.png", 1, "C:\\outside\\unknown.png"),
                ],
                "receive_failed"));
        };

        await node.EnqueueAsync(request);
        var summary = await completed.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(LocalSendReceiveTransferState.Failed, summary.TransferState);
        Assert.Equal(5, summary.OfferedCount);
        Assert.Equal(2, summary.AcceptedCount);
        Assert.Equal(1, summary.ImportedCount);
        Assert.Equal(1, summary.DuplicateCount);
        Assert.Equal(0, summary.FailedCount);
        Assert.Equal("receive_failed", summary.ErrorCode);
        Assert.Equal(
            ["C:\\inbox\\first.png", "C:\\inbox\\second.jpg"],
            inbox.Paths);
        Assert.Null((await trustedDevices.FindAsync(TrustedFingerprint))!.LastReceivedAtUtc);
    }

    [Fact]
    public async Task Pairing_PersistsTrustBeforeAcceptAndRebuildsNormalNodeAfterTransfer()
    {
        using var root = new TemporaryAppDataRoot();
        var trustedDevices = new LocalSendTrustedDeviceStore(root.Paths);
        var inbox = new FakeInboxImporter();
        var factory = new FakeNodeFactory();
        await using var receiver = CreateReceiver(root, factory, trustedDevices, inbox);
        var completed = NewCompletion<LocalSendReceiveSummary>();
        receiver.TransferCompleted += summary => completed.TrySetResult(summary);
        await receiver.StartAsync();

        var pairing = await receiver.BeginPairingAsync();

        Assert.Matches("^[0-9]{6}$", pairing.Pin);
        Assert.Equal(LocalSendReceiverStatus.Pairing, receiver.Snapshot.Status);
        Assert.Equal(pairing.Pin, receiver.Snapshot.PairingPin);
        Assert.Equal(2, factory.Nodes.Count);
        Assert.Null(factory.Nodes[0].Options.ReceivePin);
        Assert.Equal(pairing.Pin, factory.Nodes[1].Options.ReceivePin);
        Assert.True(factory.Nodes[0].DisposeCalled);
        var request = Request(
            OtherFingerprint,
            "New phone",
            [Item("image", "new.webp", 256)]);
        factory.Nodes[1].AcceptBehavior = async (_, _, _) =>
        {
            Assert.NotNull(await trustedDevices.FindAsync(OtherFingerprint));
            return new(
                request.TransferId,
                LocalSendReceiveTransferState.Completed,
                [new("image", "new.webp", 256, "C:\\inbox\\new.webp")]);
        };

        await factory.Nodes[1].EnqueueAsync(request);
        var summary = await completed.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await WaitUntilAsync(() =>
            factory.Nodes.Count == 3
            && receiver.Snapshot.Status == LocalSendReceiverStatus.Listening);

        Assert.True(summary.PairedDuringTransfer);
        Assert.Equal(1, summary.ImportedCount);
        Assert.NotNull(await trustedDevices.FindAsync(OtherFingerprint));
        Assert.Null(factory.Nodes[2].Options.ReceivePin);
        Assert.Null(receiver.Snapshot.PairingPin);
        Assert.Equal(
            factory.Nodes[0].Options.IdentityDirectoryPath,
            factory.Nodes[2].Options.IdentityDirectoryPath);
    }

    [Fact]
    public async Task Pairing_DeclinesWhenTrustCannotBeSavedAndStaysInPairingMode()
    {
        using var root = new TemporaryAppDataRoot();
        var trustedDevices = new FakeTrustedDeviceStore
        {
            AddBehavior = (_, _, _) => throw new IOException("Simulated trust-store failure."),
        };
        var factory = new FakeNodeFactory();
        await using var receiver = CreateReceiver(root, factory, trustedDevices);
        await receiver.StartAsync();
        await receiver.BeginPairingAsync();
        var pairingNode = factory.Nodes[1];
        var request = Request(
            OtherFingerprint,
            "Phone",
            [Item("image", "photo.png", 1)]);

        await pairingNode.EnqueueAsync(request);
        await WaitUntilAsync(() => pairingNode.DeclinedRequestIds.Contains(request.RequestId));

        Assert.Empty(pairingNode.AcceptedRequests);
        Assert.Equal(LocalSendReceiverStatus.Pairing, receiver.Snapshot.Status);
        Assert.Null(await trustedDevices.FindAsync(OtherFingerprint));
        await receiver.CancelPairingAsync();
        Assert.Equal(LocalSendReceiverStatus.Listening, receiver.Snapshot.Status);
    }

    [Fact]
    public async Task Pairing_TimeoutAndCancellationEachRebuildANormalNode()
    {
        using var root = new TemporaryAppDataRoot();
        var factory = new FakeNodeFactory();
        await using var receiver = CreateReceiver(
            root,
            factory,
            pairingDuration: TimeSpan.FromMilliseconds(40));
        await receiver.StartAsync();

        await receiver.BeginPairingAsync();
        await WaitUntilAsync(() =>
            factory.Nodes.Count == 3
            && receiver.Snapshot.Status == LocalSendReceiverStatus.Listening);

        Assert.Null(factory.Nodes[2].Options.ReceivePin);
        await receiver.BeginPairingAsync();
        Assert.Equal(4, factory.Nodes.Count);
        await receiver.CancelPairingAsync();

        Assert.Equal(5, factory.Nodes.Count);
        Assert.Null(factory.Nodes[4].Options.ReceivePin);
        Assert.Equal(LocalSendReceiverStatus.Listening, receiver.Snapshot.Status);
    }

    [Fact]
    public async Task PairingTimeout_CancelsAnInProgressTrustSaveBeforeAccepting()
    {
        using var root = new TemporaryAppDataRoot();
        var addStarted = NewCompletion();
        var trustedDevices = new FakeTrustedDeviceStore
        {
            AddBehavior = async (_, _, cancellationToken) =>
            {
                addStarted.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                throw new InvalidOperationException("The trust save unexpectedly completed.");
            },
        };
        var factory = new FakeNodeFactory();
        await using var receiver = CreateReceiver(
            root,
            factory,
            trustedDevices,
            pairingDuration: TimeSpan.FromMilliseconds(40));
        await receiver.StartAsync();
        await receiver.BeginPairingAsync();
        var pairingNode = factory.Nodes[1];
        var request = Request(
            OtherFingerprint,
            "Phone",
            [Item("image", "photo.png", 1)]);

        await pairingNode.EnqueueAsync(request);
        await addStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await WaitUntilAsync(() =>
            factory.Nodes.Count == 3
            && receiver.Snapshot.Status == LocalSendReceiverStatus.Listening);

        Assert.Contains(request.RequestId, pairingNode.DeclinedRequestIds);
        Assert.Empty(pairingNode.AcceptedRequests);
        Assert.Null(await trustedDevices.FindAsync(OtherFingerprint));
    }

    [Fact]
    public async Task PairingStopFailure_DoesNotCreateAReplacementNode()
    {
        using var root = new TemporaryAppDataRoot();
        var factory = new FakeNodeFactory();
        await using var receiver = CreateReceiver(root, factory);
        await receiver.StartAsync();
        await receiver.BeginPairingAsync();
        factory.Nodes[1].StopException = new IOException("Simulated listener cleanup failure.");

        await receiver.CancelPairingAsync();

        Assert.Equal(2, factory.Nodes.Count);
        Assert.Equal(LocalSendReceiverStatus.Faulted, receiver.Snapshot.Status);
        Assert.Equal("ReceiverStopFailed", receiver.Snapshot.LastErrorCode);
        await Assert.ThrowsAsync<InvalidOperationException>(() => receiver.StartAsync());
        Assert.Equal(2, factory.Nodes.Count);
    }

    [Fact]
    public async Task PairingStartFailure_RestoresTheNormalReceiver()
    {
        using var root = new TemporaryAppDataRoot();
        var factory = new FakeNodeFactory
        {
            ConfigureNode = static (index, node) =>
            {
                if (index == 1)
                {
                    node.StartException = new IOException("Simulated pairing bind failure.");
                }
            },
        };
        await using var receiver = CreateReceiver(root, factory);
        await receiver.StartAsync();

        await Assert.ThrowsAsync<IOException>(() => receiver.BeginPairingAsync());

        Assert.Equal(3, factory.Nodes.Count);
        Assert.True(factory.Nodes[1].DisposeCalled);
        Assert.Null(factory.Nodes[2].Options.ReceivePin);
        Assert.Equal(LocalSendReceiverStatus.Listening, receiver.Snapshot.Status);
    }

    [Fact]
    public async Task InitialStartFailure_CleansTheNodeAndLeavesAFaultedSnapshot()
    {
        using var root = new TemporaryAppDataRoot();
        var factory = new FakeNodeFactory
        {
            ConfigureNode = static (_, node) =>
                node.StartException = new IOException("Simulated port conflict."),
        };
        await using var receiver = CreateReceiver(root, factory);

        await Assert.ThrowsAsync<IOException>(() => receiver.StartAsync());

        var node = Assert.Single(factory.Nodes);
        Assert.True(node.StopCalled);
        Assert.True(node.DisposeCalled);
        Assert.Equal(LocalSendReceiverStatus.Faulted, receiver.Snapshot.Status);
        Assert.Equal("ReceiverStartFailed", receiver.Snapshot.LastErrorCode);
    }

    [Fact]
    public async Task Stop_CancelsAnActiveReceiveAndWaitsForTheWatcher()
    {
        using var root = new TemporaryAppDataRoot();
        var trustedDevices = new LocalSendTrustedDeviceStore(root.Paths);
        await trustedDevices.AddAsync(TrustedFingerprint, "Phone");
        var factory = new FakeNodeFactory();
        await using var receiver = CreateReceiver(root, factory, trustedDevices);
        await receiver.StartAsync();
        var node = Assert.Single(factory.Nodes);
        var acceptStarted = NewCompletion();
        node.AcceptBehavior = async (_, _, cancellationToken) =>
        {
            acceptStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("The cancellation wait unexpectedly completed.");
        };
        var request = Request(
            TrustedFingerprint,
            "Phone",
            [Item("image", "photo.png", 1)]);
        await node.EnqueueAsync(request);
        await acceptStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        await receiver.StopAsync().WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(LocalSendReceiverStatus.Disabled, receiver.Snapshot.Status);
        Assert.True(node.DisposeCalled);
    }

    private static LocalSendReceiverService CreateReceiver(
        TemporaryAppDataRoot root,
        FakeNodeFactory factory,
        ILocalSendTrustedDeviceStore? trustedDevices = null,
        ILocalSendInboxImportService? inbox = null,
        TimeSpan? pairingDuration = null)
    {
        return new(
            root.Paths,
            factory,
            trustedDevices ?? new FakeTrustedDeviceStore(),
            inbox ?? new FakeInboxImporter(),
            pairingDuration: pairingDuration);
    }

    private static LocalSendIncomingRequest Request(
        string fingerprint,
        string displayName,
        IReadOnlyList<LocalSendIncomingItem> items) =>
        new(Guid.NewGuid(), Guid.NewGuid(), fingerprint, displayName, items);

    private static LocalSendIncomingItem Item(
        string id,
        string fileName,
        long size) =>
        new(id, fileName, size, "application/octet-stream");

    private static string Fingerprint(char character) => new(character, 64);

    private static TaskCompletionSource NewCompletion() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static TaskCompletionSource<T> NewCompletion<T>() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (!condition())
        {
            await Task.Delay(10, timeout.Token);
        }
    }

    private sealed class FakeNodeFactory : ILocalSendReceiverNodeFactory
    {
        public Action<int, FakeNode>? ConfigureNode { get; init; }

        public List<FakeNode> Nodes { get; } = [];

        public ILocalSendReceiverNode Create(LocalSendReceiverNodeOptions options)
        {
            var node = new FakeNode(options);
            ConfigureNode?.Invoke(Nodes.Count, node);
            Nodes.Add(node);
            return node;
        }
    }

    private sealed class FakeNode(LocalSendReceiverNodeOptions options) : ILocalSendReceiverNode
    {
        private readonly Channel<LocalSendIncomingRequest> _requests =
            Channel.CreateUnbounded<LocalSendIncomingRequest>();

        public Func<Guid, LocalSendAcceptOptions, CancellationToken, Task<LocalSendReceiveResult>>
            AcceptBehavior { get; set; } = (requestId, _, _) => Task.FromResult(
                new LocalSendReceiveResult(
                    requestId,
                    LocalSendReceiveTransferState.Completed,
                    []));

        public ConcurrentQueue<(Guid RequestId, LocalSendAcceptOptions Options)> AcceptedRequests
        {
            get;
        } = new();

        public ConcurrentBag<Guid> DeclinedRequestIds { get; } = [];

        public bool DiscoveryLimited { get; set; }

        public bool DisposeCalled { get; private set; }

        public LocalSendReceiverNodeOptions Options { get; } = options;

        public Exception? StartException { get; set; }

        public Exception? StopException { get; set; }

        public bool StartedAfterWatcherSubscription { get; private set; }

        public bool StopCalled { get; private set; }

        public TaskCompletionSource WatcherSubscribed { get; } = NewCompletion();

        public async IAsyncEnumerable<LocalSendIncomingRequest> WatchIncomingTransfersAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            WatcherSubscribed.TrySetResult();
            await foreach (var request in _requests.Reader
                               .ReadAllAsync(cancellationToken)
                               .ConfigureAwait(false))
            {
                yield return request;
            }
        }

        public Task StartAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            StartedAfterWatcherSubscription = WatcherSubscribed.Task.IsCompletedSuccessfully;
            if (!StartedAfterWatcherSubscription)
            {
                throw new InvalidOperationException("The node started before its watcher subscribed.");
            }

            return StartException is null
                ? Task.CompletedTask
                : Task.FromException(StartException);
        }

        public Task StopAsync(CancellationToken cancellationToken = default)
        {
            StopCalled = true;
            _requests.Writer.TryComplete();
            return StopException is null
                ? Task.CompletedTask
                : Task.FromException(StopException);
        }

        public Task<LocalSendReceiveResult> AcceptAsync(
            Guid requestId,
            LocalSendAcceptOptions options,
            CancellationToken cancellationToken = default)
        {
            AcceptedRequests.Enqueue((requestId, options));
            return AcceptBehavior(requestId, options, cancellationToken);
        }

        public Task DeclineAsync(
            Guid requestId,
            CancellationToken cancellationToken = default)
        {
            DeclinedRequestIds.Add(requestId);
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            DisposeCalled = true;
            _requests.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }

        public ValueTask EnqueueAsync(LocalSendIncomingRequest request) =>
            _requests.Writer.WriteAsync(request);
    }

    private sealed class FakeTrustedDeviceStore : ILocalSendTrustedDeviceStore
    {
        private readonly ConcurrentDictionary<string, LocalSendTrustedDevice> _devices =
            new(StringComparer.Ordinal);

        public Func<string, string, CancellationToken, Task<LocalSendTrustedDevice>>?
            AddBehavior { get; init; }

        public Task<IReadOnlyList<LocalSendTrustedDevice>> GetAllAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<LocalSendTrustedDevice>>(_devices.Values.ToArray());

        public Task<LocalSendTrustedDevice?> FindAsync(
            string? fingerprint,
            CancellationToken cancellationToken = default)
        {
            if (!TryNormalizeFingerprint(fingerprint, out var normalized))
            {
                return Task.FromResult<LocalSendTrustedDevice?>(null);
            }

            _devices.TryGetValue(normalized, out var device);
            return Task.FromResult(device);
        }

        public async Task<LocalSendTrustedDevice> AddAsync(
            string fingerprint,
            string displayName,
            CancellationToken cancellationToken = default)
        {
            if (AddBehavior is not null)
            {
                return await AddBehavior(fingerprint, displayName, cancellationToken);
            }

            if (!TryNormalizeFingerprint(fingerprint, out var normalized))
            {
                throw new ArgumentException("Invalid fingerprint.", nameof(fingerprint));
            }

            var device = new LocalSendTrustedDevice(
                normalized,
                displayName,
                DateTimeOffset.UtcNow,
                null);
            _devices[normalized] = device;
            return device;
        }

        public Task<bool> MarkReceivedAsync(
            string fingerprint,
            string displayName,
            CancellationToken cancellationToken = default)
        {
            if (!TryNormalizeFingerprint(fingerprint, out var normalized)
                || !_devices.TryGetValue(normalized, out var device))
            {
                return Task.FromResult(false);
            }

            _devices[normalized] = device with
            {
                DisplayName = displayName,
                LastReceivedAtUtc = DateTimeOffset.UtcNow,
            };
            return Task.FromResult(true);
        }

        public Task<bool> RemoveAsync(
            string fingerprint,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(
                TryNormalizeFingerprint(fingerprint, out var normalized)
                && _devices.TryRemove(normalized, out _));
        }

        private static bool TryNormalizeFingerprint(
            string? fingerprint,
            out string normalized)
        {
            normalized = string.Empty;
            if (fingerprint is null
                || fingerprint.Length != 64
                || fingerprint.Any(static character => !char.IsAsciiHexDigit(character)))
            {
                return false;
            }

            normalized = fingerprint.ToUpperInvariant();
            return true;
        }
    }

    private sealed class FakeInboxImporter(
        Func<string, Task<LocalSendInboxImportResult>>? behavior = null)
        : ILocalSendInboxImportService
    {
        private readonly Func<string, Task<LocalSendInboxImportResult>> _behavior =
            behavior ?? (_ => Task.FromResult(new LocalSendInboxImportResult(
                LocalSendInboxImportStatus.Imported,
                Guid.NewGuid(),
                InboxFileRemoved: true)));

        public List<string> Paths { get; } = [];

        public async Task<LocalSendInboxImportResult> ImportAsync(
            string absolutePath,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Paths.Add(absolutePath);
            return await _behavior(absolutePath);
        }

        public Task<LocalSendInboxRecoveryResult> RecoverAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new LocalSendInboxRecoveryResult([]));
    }
}
