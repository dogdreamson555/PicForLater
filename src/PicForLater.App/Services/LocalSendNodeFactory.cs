using System.Runtime.CompilerServices;
using LocalSendDotNet;
using PicForLater.Infrastructure.LocalSend;

namespace PicForLater.App.Services;

internal sealed class LocalSendNodeFactory : ILocalSendReceiverNodeFactory
{
    public ILocalSendReceiverNode Create(LocalSendReceiverNodeOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return new LocalSendReceiverNode(new LocalSendNode(new LocalSendOptions
        {
            Alias = options.Alias,
            DeviceModel = "Windows",
            DeviceType = LocalSendDeviceType.Desktop,
            DataDirectory = options.IdentityDirectoryPath,
            DownloadDirectory = options.InboxDirectoryPath,
            Port = options.Port,
            EnableHttps = true,
            ReceivePin = options.ReceivePin,
            MaxConcurrentTransfers = options.MaximumConcurrentTransfers,
            MaxConcurrentFileUploads = options.MaximumConcurrentFileUploads,
            MaxIncomingItemsPerTransfer = options.MaximumIncomingItemsPerTransfer,
            MaxIncomingTransferBytes = options.MaximumIncomingTransferBytes,
            MaxPrepareRequestBytes = options.MaximumPrepareRequestBytes,
        }));
    }

    private sealed class LocalSendReceiverNode(LocalSendNode node) : ILocalSendReceiverNode
    {
        public bool DiscoveryLimited => node.DiscoveryError is not null;

        public async IAsyncEnumerable<LocalSendIncomingRequest> WatchIncomingTransfersAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await foreach (var request in node
                               .WatchIncomingTransfersAsync(cancellationToken)
                               .ConfigureAwait(false))
            {
                yield return new(
                    request.RequestId,
                    request.TransferId,
                    request.Sender.Fingerprint,
                    request.Sender.Alias,
                    request.Items
                        .Select(static item => new LocalSendIncomingItem(
                            item.Id,
                            item.FileName,
                            item.Size,
                            item.ContentType))
                        .ToArray());
            }
        }

        public Task StartAsync(CancellationToken cancellationToken = default) =>
            node.StartAsync(cancellationToken);

        public Task StopAsync(CancellationToken cancellationToken = default) =>
            node.StopAsync(cancellationToken);

        public async Task<LocalSendReceiveResult> AcceptAsync(
            Guid requestId,
            LocalSendAcceptOptions options,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(options);
            var result = await node.AcceptAsync(
                requestId,
                new AcceptTransferOptions
                {
                    DestinationDirectory = options.DestinationDirectory,
                    AcceptedItemIds = options.AcceptedItemIds,
                    TargetFileNames = options.TargetFileNames,
                },
                progress: null,
                cancellationToken).ConfigureAwait(false);
            return new(
                result.TransferId,
                result.State switch
                {
                    TransferState.Completed => LocalSendReceiveTransferState.Completed,
                    TransferState.Cancelled => LocalSendReceiveTransferState.Cancelled,
                    _ => LocalSendReceiveTransferState.Failed,
                },
                result.Items
                    .Select(static item => new LocalSendTransferredItem(
                        item.ItemId,
                        item.FileName,
                        item.BytesTransferred,
                        item.SavedPath))
                    .ToArray(),
                result.Failure?.Code);
        }

        public Task DeclineAsync(
            Guid requestId,
            CancellationToken cancellationToken = default) =>
            node.DeclineAsync(requestId, cancellationToken);

        public ValueTask DisposeAsync() => node.DisposeAsync();
    }
}
