using PicForLater.Core.Analysis;

namespace PicForLater.LocalInference;

internal sealed class WorkerInferenceExecutionContext : IInferenceExecutionContext
{
    public InferenceAccelerationMode CurrentMode { get; private set; } =
        InferenceAccelerationMode.Automatic;

    public InferenceExecutionStatus? LastExecutionStatus { get; private set; }

    public void Begin(InferenceAccelerationMode mode)
    {
        if (!Enum.IsDefined(mode))
        {
            throw new ArgumentOutOfRangeException(nameof(mode));
        }

        CurrentMode = mode;
        LastExecutionStatus = null;
    }

    public void ReportExecution(
        string workload,
        InferenceExecutionDevice device,
        bool usedAutomaticFallback = false,
        string? failureCode = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workload);
        LastExecutionStatus = new InferenceExecutionStatus(
            workload,
            device,
            usedAutomaticFallback,
            failureCode,
            DateTimeOffset.UtcNow);
    }
}
