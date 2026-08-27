using System.Text;
using Microsoft.ML.OnnxRuntimeGenAI;
using PicForLater.Analysis;
using PicForLater.Core.Analysis;

namespace PicForLater.LocalInference;

internal interface IQwenGenerationContextFactory
{
    IQwenGenerationContext Create(string modelDirectoryPath, string provider);
}

internal interface IQwenGenerationContext : IDisposable
{
    string Generate(
        string imageFilePath,
        string prompt,
        string jsonSchema,
        int maximumOutputTokens,
        CancellationToken cancellationToken);
}

internal sealed class OnnxRuntimeGenAiQwenContextFactory : IQwenGenerationContextFactory
{
    public IQwenGenerationContext Create(string modelDirectoryPath, string provider)
    {
        var failureCode = provider switch
        {
            OnnxRuntimeGenAiQwenRuntime.DirectMlProvider => "qwen.directml-provider-load-failed",
            OnnxRuntimeGenAiQwenRuntime.CudaProvider => "qwen.cuda-provider-load-failed",
            _ => "qwen.model-load-failed",
        };
        Config? config = null;
        Model? model = null;
        MultiModalProcessor? processor = null;
        Tokenizer? tokenizer = null;
        try
        {
            if (provider == OnnxRuntimeGenAiQwenRuntime.CudaProvider)
            {
                CudaRuntimeDependencyLoader.Prepare();
            }

            // CPU and CUDA packages carry their qualified provider settings in
            // genai_config.json. Keep those options intact; only DirectML needs
            // a runtime override because it shares the CPU package.
            config = provider == OnnxRuntimeGenAiQwenRuntime.DirectMlProvider
                ? CreateDirectMlConfig(modelDirectoryPath)
                : null;
            model = config is null ? new Model(modelDirectoryPath) : new Model(config);
            failureCode = "qwen.model-inspection-failed";
            if (!model.GetModelType().Equals("qwen3_vl", StringComparison.Ordinal))
            {
                throw new InvalidDataException("The loaded model is not a Qwen3-VL model.");
            }

            failureCode = "qwen.processor-load-failed";
            processor = new MultiModalProcessor(model);
            // Keep the existing public error code for both tokenizer construction
            // and per-request stream creation.
            failureCode = "qwen.tokenizer-stream-failed";
            tokenizer = new Tokenizer(model);
            return new OnnxRuntimeGenAiQwenContext(config, model, processor, tokenizer);
        }
        catch (Exception exception)
        {
            try
            {
                DisposeContextParts(config, model, processor, tokenizer);
            }
            catch
            {
                // Preserve the model/provider failure and its stable error code.
            }

            throw new OcrProviderException(failureCode, isRetryable: false, exception);
        }
    }

    private static void DisposeContextParts(
        Config? config,
        Model? model,
        MultiModalProcessor? processor,
        Tokenizer? tokenizer)
    {
        try
        {
            tokenizer?.Dispose();
        }
        finally
        {
            try
            {
                processor?.Dispose();
            }
            finally
            {
                try
                {
                    model?.Dispose();
                }
                finally
                {
                    config?.Dispose();
                }
            }
        }
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

internal sealed class OnnxRuntimeGenAiQwenContext : IQwenGenerationContext
{
    private readonly Config? _config;
    private readonly Model _model;
    private readonly MultiModalProcessor _processor;
    private readonly Tokenizer _tokenizer;
    private bool _disposed;

    public OnnxRuntimeGenAiQwenContext(
        Config? config,
        Model model,
        MultiModalProcessor processor,
        Tokenizer tokenizer)
    {
        _config = config;
        _model = model;
        _processor = processor;
        _tokenizer = tokenizer;
    }

    public string Generate(
        string imageFilePath,
        string prompt,
        string jsonSchema,
        int maximumOutputTokens,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var failureCode = "qwen.image-load-failed";
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var images = Images.Load([imageFilePath]);
            failureCode = "qwen.generator-parameters-failed";
            using var parameters = new GeneratorParams(_model);
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
            using var inputs = _processor.ProcessImages(formattedPrompt, images);
            failureCode = "qwen.generator-create-failed";
            using var generator = new Generator(_model, parameters);
            failureCode = "qwen.input-binding-failed";
            generator.SetInputs(inputs);
            failureCode = "qwen.tokenizer-stream-failed";
            using var stream = _tokenizer.CreateStream();
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

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            _tokenizer.Dispose();
        }
        finally
        {
            try
            {
                _processor.Dispose();
            }
            finally
            {
                try
                {
                    _model.Dispose();
                }
                finally
                {
                    _config?.Dispose();
                }
            }
        }
    }
}
