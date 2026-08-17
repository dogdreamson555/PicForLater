using System.Text;
using Microsoft.ML.OnnxRuntimeGenAI;
using PicForLater.Analysis;
using PicForLater.Core.Analysis;

namespace PicForLater.LocalInference;

public sealed class OnnxRuntimeGenAiQwenRuntime : IQwenGenerationRuntime
{
    private const string CpuProvider = "CPU";
    private const string DirectMlProvider = "DirectML";
    private const string CudaProvider = "CUDA";
    private readonly SemaphoreSlim _inferenceGate = new(1, 1);
    private readonly IInferenceExecutionContext _acceleration;

    public OnnxRuntimeGenAiQwenRuntime(IInferenceExecutionContext acceleration)
    {
        _acceleration = acceleration ?? throw new ArgumentNullException(nameof(acceleration));
    }

#if PICFORLATER_CUDA_RUNTIME
    public IReadOnlySet<string> SupportedExecutionProviders { get; } =
        new HashSet<string>([CpuProvider, CudaProvider], StringComparer.Ordinal);
#else
    public IReadOnlySet<string> SupportedExecutionProviders { get; } =
        new HashSet<string>([CpuProvider, DirectMlProvider], StringComparer.Ordinal);
#endif

    public async Task<string> GenerateAsync(
        string modelDirectoryPath,
        string imageFilePath,
        string prompt,
        string jsonSchema,
        int maximumOutputTokens,
        InferenceAccelerationMode accelerationMode,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelDirectoryPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(imageFilePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);
        ArgumentException.ThrowIfNullOrWhiteSpace(jsonSchema);
        if (maximumOutputTokens is <= 0 or > 2048)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumOutputTokens));
        }

        await _inferenceGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await Task.Run(
                () => GenerateWithPolicy(
                    modelDirectoryPath,
                    imageFilePath,
                    prompt,
                    jsonSchema,
                    maximumOutputTokens,
                    accelerationMode,
                    cancellationToken),
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _inferenceGate.Release();
        }
    }

    private string GenerateWithPolicy(
        string modelDirectoryPath,
        string imageFilePath,
        string prompt,
        string jsonSchema,
        int maximumOutputTokens,
        InferenceAccelerationMode accelerationMode,
        CancellationToken cancellationToken)
    {
        if (!Enum.IsDefined(accelerationMode))
        {
            throw new ArgumentOutOfRangeException(nameof(accelerationMode));
        }

        var provider = ResolveProvider(accelerationMode);
        var executionDevice = provider switch
        {
            CpuProvider => InferenceExecutionDevice.Cpu,
            DirectMlProvider => InferenceExecutionDevice.DirectMlGpu,
            CudaProvider => InferenceExecutionDevice.CudaGpu,
            _ => throw new InvalidOperationException("The selected execution provider is unsupported."),
        };
        try
        {
            var output = GenerateCore(
                modelDirectoryPath,
                imageFilePath,
                prompt,
                jsonSchema,
                maximumOutputTokens,
                provider,
                cancellationToken);
            _acceleration.ReportExecution("Qwen3Vl", executionDevice);
            return output;
        }
        catch (OcrProviderException exception)
        {
            _acceleration.ReportExecution(
                "Qwen3Vl",
                executionDevice,
                failureCode: exception.ErrorCode);
            throw;
        }
    }

    private static string GenerateCore(
        string modelDirectoryPath,
        string imageFilePath,
        string prompt,
        string jsonSchema,
        int maximumOutputTokens,
        string provider,
        CancellationToken cancellationToken)
    {
        var failureCode = provider switch
        {
            DirectMlProvider => "qwen.directml-provider-load-failed",
            CudaProvider => "qwen.cuda-provider-load-failed",
            _ => "qwen.model-load-failed",
        };
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (provider == CudaProvider)
            {
                CudaRuntimeDependencyLoader.Prepare();
            }

            // CPU and CUDA packages carry their qualified provider settings in
            // genai_config.json. Keep those options intact; only DirectML needs
            // a runtime override because it shares the CPU package.
            using var config = provider == DirectMlProvider
                ? CreateDirectMlConfig(modelDirectoryPath)
                : null;
            using var model = config is null ? new Model(modelDirectoryPath) : new Model(config);
            failureCode = "qwen.model-inspection-failed";
            if (!model.GetModelType().Equals("qwen3_vl", StringComparison.Ordinal))
            {
                throw new InvalidDataException("The loaded model is not a Qwen3-VL model.");
            }

            failureCode = "qwen.processor-load-failed";
            using var processor = new MultiModalProcessor(model);
            failureCode = "qwen.image-load-failed";
            using var images = Images.Load([imageFilePath]);
            failureCode = "qwen.generator-parameters-failed";
            using var parameters = new GeneratorParams(model);
            parameters.SetSearchOption("max_length", 4096d);
            // Validation and normal structured generation must be repeatable.
            // The publisher qualification path is greedy as well; sampling made
            // the fixed self-test intermittently pass during import and fail on
            // the identical package during a later slot switch.
            parameters.SetSearchOption("do_sample", false);
            parameters.SetGuidance("json_schema", jsonSchema);
            var formattedPrompt =
                $"<|im_start|>user\n<|vision_start|><|image_pad|><|vision_end|>\n{prompt}<|im_end|>\n<|im_start|>assistant\n";
            failureCode = "qwen.input-processing-failed";
            using var inputs = processor.ProcessImages(formattedPrompt, images);
            failureCode = "qwen.generator-create-failed";
            using var generator = new Generator(model, parameters);
            failureCode = "qwen.input-binding-failed";
            generator.SetInputs(inputs);
            failureCode = "qwen.tokenizer-stream-failed";
            using var tokenizer = new Tokenizer(model);
            using var stream = tokenizer.CreateStream();
            var output = new StringBuilder();
            var generatedTokens = 0;
            while (!generator.IsDone() && generatedTokens < maximumOutputTokens)
            {
                failureCode = "qwen.generation-failed";
                cancellationToken.ThrowIfCancellationRequested();
                generator.GenerateNextToken();
                if (generator.IsDone())
                {
                    break;
                }

                // MultiModalProcessor inputs use the next-token buffer for streaming;
                // this matches the ONNX Runtime GenAI ModelMM C# reference flow.
                var token = generator.GetNextTokens()[0];
                output.Append(stream.Decode(token));
                generatedTokens++;
                if (QwenStructuredOutputParser.TryExtractCompleteJsonObject(
                        output.ToString(),
                        out var completeJson))
                {
                    return completeJson;
                }

                if (output.Length > QwenStructuredOutputParser.MaximumOutputCharacters)
                {
                    failureCode = "qwen.output-character-limit-exceeded";
                    throw new InvalidDataException("The model output exceeded the configured limit.");
                }
            }

            if (!generator.IsDone())
            {
                failureCode = "qwen.output-token-limit-exceeded";
                throw new InvalidDataException("The model output exceeded the token limit.");
            }

            return output.ToString();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new OcrProviderException(failureCode, isRetryable: false, exception);
        }
    }

    private string ResolveProvider(InferenceAccelerationMode accelerationMode)
    {
        var provider = accelerationMode switch
        {
            InferenceAccelerationMode.Cpu => CpuProvider,
            InferenceAccelerationMode.DirectMlGpu => DirectMlProvider,
            InferenceAccelerationMode.CudaGpu => CudaProvider,
#if PICFORLATER_CUDA_RUNTIME
            InferenceAccelerationMode.Automatic => CudaProvider,
#else
            InferenceAccelerationMode.Automatic => DirectMlProvider,
#endif
            _ => throw new ArgumentOutOfRangeException(nameof(accelerationMode)),
        };
        if (!SupportedExecutionProviders.Contains(provider))
        {
            throw new OcrProviderException(
                provider == CudaProvider
                    ? "qwen.cuda-runtime-unavailable"
                    : "qwen.directml-runtime-unavailable",
                isRetryable: false);
        }

        return provider;
    }

    private static Config CreateDirectMlConfig(string modelDirectoryPath)
    {
        var config = new Config(modelDirectoryPath);
        try
        {
            config.ClearProviders();
            config.AppendProvider("DML");
            config.SetProviderOption("DML", "device_id", "0");
            return config;
        }
        catch
        {
            config.Dispose();
            throw;
        }
    }

}
