using PicForLater.Core.Analysis;

namespace PicForLater.App.Services;

public interface IInferenceAccelerationPreferenceService : IInferenceExecutionContext
{
    event EventHandler? StateChanged;

    bool IsDirectMlAvailable { get; }

    bool IsCudaAvailable { get; }

    void SetMode(InferenceAccelerationMode mode);
}
