#if PICFORLATER_UI_TESTING
using PicForLater.Core.Analysis;

namespace PicForLater.App.Services;

internal sealed class UiTestLocalInferenceRuntime :
    IPpOcrV6InferenceRuntime,
    IQwenGenerationRuntime
{
#if PICFORLATER_CUDA_RUNTIME
    public IReadOnlySet<string> SupportedExecutionProviders { get; } =
        new HashSet<string>(["CPU", "CUDA"], StringComparer.Ordinal);
#else
    public IReadOnlySet<string> SupportedExecutionProviders { get; } =
        new HashSet<string>(["CPU", "DirectML"], StringComparer.Ordinal);
#endif

    public Task<OcrTensorResult> RunAsync(
        string modelPath,
        string inputName,
        string outputName,
        float[] input,
        IReadOnlyList<int> dimensions,
        CancellationToken cancellationToken = default,
        InferenceAccelerationMode? accelerationMode = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        throw new OcrProviderUnavailableException("ui-test.local-component-unavailable");
    }

    public Task<string> GenerateAsync(
        string modelDirectoryPath,
        string imageFilePath,
        string prompt,
        string jsonSchema,
        int maximumOutputTokens,
        InferenceAccelerationMode accelerationMode,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        throw new OcrProviderUnavailableException("ui-test.local-component-unavailable");
    }

    public void Dispose()
    {
    }
}
#endif
