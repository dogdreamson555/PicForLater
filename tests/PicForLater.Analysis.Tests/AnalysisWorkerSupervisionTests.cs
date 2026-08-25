using PicForLater.Core.Analysis;
using PicForLater.Core.Images;
using PicForLater.Core.Runtime;

namespace PicForLater.Analysis.Tests;

public sealed class AnalysisWorkerSupervisionTests
{
    [Fact]
    public async Task FirstLeaseQueryTransientFailure_IsRestartedBySupervisor()
    {
        using var cancellation = new CancellationTokenSource();
        using var wakeSignal = new AnalysisQueueWakeSignal();
        var store = new FirstLeaseFailureStore();
        var worker = new AnalysisWorker(
            "supervised-analysis-worker",
            store,
            new NeverUsedImageStorage(),
            new NeverUsedOcrProvider(),
            new ExtractiveTextComposer(),
            wakeSignal);
        var supervisor = new BackgroundWorkerSupervisor(
            BackgroundWorkerKind.Analysis,
            worker.RunAsync,
            exception => exception is SyntheticLeaseException
                ? new BackgroundWorkerFailure("background.analysis.storage-busy", true)
                : new BackgroundWorkerFailure("background.analysis.unexpected", false),
            "background.analysis.unexpected",
            new BackgroundWorkerSupervisorOptions(
                [TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero],
                TimeSpan.FromMinutes(1)));

        Assert.True(supervisor.Start(cancellation.Token));
        await store.SecondLeaseAttempted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await cancellation.CancelAsync();
        await supervisor.Completion.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(2, store.LeaseAttempts);
        Assert.Equal(BackgroundWorkerState.Stopped, supervisor.CurrentStatus.State);
    }

    private sealed class FirstLeaseFailureStore : IAnalysisJobStore
    {
        public TaskCompletionSource SecondLeaseAttempted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public int LeaseAttempts { get; private set; }

        public Task<AnalysisLeaseAttempt> TryLeaseNextAsync(
            string workerId,
            DateTimeOffset nowUtc,
            TimeSpan leaseDuration,
            int maximumAttempts,
            CancellationToken cancellationToken = default)
        {
            LeaseAttempts++;
            if (LeaseAttempts == 1)
            {
                throw new SyntheticLeaseException();
            }

            SecondLeaseAttempted.TrySetResult();
            return Task.FromResult(new AnalysisLeaseAttempt(null, null));
        }

        public Task<AnalysisStageCheckpoint?> GetCheckpointAsync(
            Guid jobId,
            AnalysisStage stage,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<AnalysisCompositionContext> GetCompositionContextAsync(
            Guid imageItemId,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task SaveCheckpointAsync(
            string workerId,
            AnalysisStageCheckpoint checkpoint,
            DateTimeOffset leaseExpiresAtUtc,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task CompleteAsync(
            string workerId,
            AnalysisJobLease lease,
            AnalysisStageCheckpoint compositionCheckpoint,
            ExtractiveContentDraft draft,
            DateTimeOffset completedAtUtc,
            AnalysisCompletionFailure? completionFailure = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task FailAsync(
            string workerId,
            AnalysisJobLease lease,
            string errorCode,
            bool retryable,
            DateTimeOffset retryAtUtc,
            int maximumAttempts,
            DateTimeOffset failedAtUtc,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task AbandonAsync(
            string workerId,
            AnalysisJobLease lease,
            DateTimeOffset retryAtUtc,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class NeverUsedImageStorage : IManagedImageStorage
    {
        public long MaximumStagedBytes => 1;

        public Task<StagedImage> StageAsync(
            Stream source,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<PromotedImage> PromoteAsync(
            StagedImage stagedImage,
            ManagedImageFormat format,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<Stream> OpenReadAsync(
            ManagedRelativePath relativePath,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<bool> VerifyAsync(
            ManagedRelativePath relativePath,
            Sha256Hash expectedHash,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task DeleteStagingAsync(
            ManagedRelativePath relativePath,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<ManagedRelativePath> StoreThumbnailAsync(
            Sha256Hash contentHash,
            ReadOnlyMemory<byte> pngBytes,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task DeleteManagedAsync(
            ManagedRelativePath relativePath,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class NeverUsedOcrProvider : IOcrProvider
    {
        public OcrProviderDescriptor Descriptor { get; } = new(
            "never-used",
            "Never used",
            ["und"],
            ["Latn"],
            SupportsMixedLanguages: true);

        public ValueTask<bool> IsAvailableAsync(
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(false);

        public Task<OcrDocument> RecognizeAsync(
            OcrRequest request,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class SyntheticLeaseException : Exception;
}
