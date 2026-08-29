namespace PicForLater.Infrastructure.LocalSend;

public enum LocalSendReceiverStatus
{
    Disabled = 0,
    Starting = 1,
    Listening = 2,
    Pairing = 3,
    Receiving = 4,
    Stopping = 5,
    Faulted = 6,
}

public enum LocalSendReceiveTransferState
{
    Completed = 1,
    Cancelled = 2,
    Failed = 3,
}

public sealed record LocalSendReceiverSnapshot(
    LocalSendReceiverStatus Status,
    bool DiscoveryLimited = false,
    string? PairingPin = null,
    DateTimeOffset? PairingExpiresAtUtc = null,
    string? LastErrorCode = null)
{
    public bool IsEnabled => Status != LocalSendReceiverStatus.Disabled;

    public bool CanPair => Status == LocalSendReceiverStatus.Listening;

    public bool CanManageTrustedDevices => Status is
        LocalSendReceiverStatus.Disabled or LocalSendReceiverStatus.Listening;
}

public sealed record LocalSendPairingSession(
    string Pin,
    DateTimeOffset ExpiresAtUtc);

public sealed record LocalSendTrustedDeviceSummary(
    string DeviceId,
    string DisplayName,
    DateTimeOffset FirstPairedAtUtc,
    DateTimeOffset? LastReceivedAtUtc);

public sealed record LocalSendReceiveSummary(
    Guid TransferId,
    string DeviceDisplayName,
    bool PairedDuringTransfer,
    LocalSendReceiveTransferState TransferState,
    int OfferedCount,
    int AcceptedCount,
    int ImportedCount,
    int DuplicateCount,
    int FailedCount,
    string? ErrorCode = null);

public sealed record LocalSendReceiverNodeOptions(
    string Alias,
    string IdentityDirectoryPath,
    string InboxDirectoryPath,
    string? ReceivePin,
    int Port,
    int MaximumConcurrentTransfers,
    int MaximumConcurrentFileUploads,
    int MaximumIncomingItemsPerTransfer,
    long MaximumIncomingTransferBytes,
    long MaximumPrepareRequestBytes);

public sealed record LocalSendIncomingItem(
    string Id,
    string FileName,
    long Size,
    string ContentType);

public sealed record LocalSendIncomingRequest(
    Guid RequestId,
    Guid TransferId,
    string SenderFingerprint,
    string SenderDisplayName,
    IReadOnlyList<LocalSendIncomingItem> Items);

public sealed record LocalSendAcceptOptions(
    string DestinationDirectory,
    IReadOnlyCollection<string> AcceptedItemIds,
    IReadOnlyDictionary<string, string> TargetFileNames);

public sealed record LocalSendTransferredItem(
    string ItemId,
    string FileName,
    long BytesTransferred,
    string? SavedPath);

public sealed record LocalSendReceiveResult(
    Guid TransferId,
    LocalSendReceiveTransferState State,
    IReadOnlyList<LocalSendTransferredItem> Items,
    string? FailureCode = null);

public interface ILocalSendReceiverNodeFactory
{
    ILocalSendReceiverNode Create(LocalSendReceiverNodeOptions options);
}

public interface ILocalSendReceiverNode : IAsyncDisposable
{
    bool DiscoveryLimited { get; }

    IAsyncEnumerable<LocalSendIncomingRequest> WatchIncomingTransfersAsync(
        CancellationToken cancellationToken = default);

    Task StartAsync(CancellationToken cancellationToken = default);

    Task StopAsync(CancellationToken cancellationToken = default);

    Task<LocalSendReceiveResult> AcceptAsync(
        Guid requestId,
        LocalSendAcceptOptions options,
        CancellationToken cancellationToken = default);

    Task DeclineAsync(
        Guid requestId,
        CancellationToken cancellationToken = default);
}

public interface ILocalSendReceiverService : IAsyncDisposable
{
    LocalSendReceiverSnapshot Snapshot { get; }

    event Action<LocalSendReceiverSnapshot>? SnapshotChanged;

    event Action<LocalSendReceiveSummary>? TransferCompleted;

    Task StartAsync(CancellationToken cancellationToken = default);

    Task StopAsync(CancellationToken cancellationToken = default);

    Task<LocalSendPairingSession> BeginPairingAsync(
        CancellationToken cancellationToken = default);

    Task CancelPairingAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<LocalSendTrustedDeviceSummary>> GetTrustedDevicesAsync(
        CancellationToken cancellationToken = default);

    Task<bool> RemoveTrustedDeviceAsync(
        string deviceId,
        CancellationToken cancellationToken = default);
}
