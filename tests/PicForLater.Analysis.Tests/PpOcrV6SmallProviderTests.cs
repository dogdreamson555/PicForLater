using System.Security.Cryptography;
using System.Text.Json;
using PicForLater.Analysis.PpOcr;
using PicForLater.Core.Analysis;

namespace PicForLater.Analysis.Tests;

public sealed class PpOcrV6SmallProviderTests
{
    [Fact]
    public async Task Recognize_ValidatesPackageAndDecodesDetectionAndCtcOutputs()
    {
        using var package = await TestModelPackage.CreateAsync();
        using var runtime = new FakeRuntime();
        var provider = new PpOcrV6SmallProvider(
            package.DirectoryPath,
            new FakeDecoder(),
            runtime);

        var result = await provider.RecognizeAsync(new OcrRequest(
            _ => ValueTask.FromResult<Stream>(new MemoryStream([1], writable: false)),
            "sample.png",
            8,
            4,
            ["en"]));

        Assert.Equal("A", result.Text);
        Assert.Single(result.Lines);
        Assert.Equal("pp-ocrv6-small", result.Provenance.ModelId);
        Assert.Equal(2, runtime.CallCount);
        Assert.All(result.Provenance.ModelFileHashes.Values, hash => Assert.Equal(64, hash.Length));
        Assert.Equal(AnalysisExecutionLocation.Local, result.Provenance.ExecutionLocation);
        Assert.Equal(AnalysisOutputKind.OcrFacts, result.Provenance.OutputKind);
    }

    [Fact]
    public async Task Validator_RejectsTamperedModelFile()
    {
        using var package = await TestModelPackage.CreateAsync();
        await File.AppendAllTextAsync(Path.Combine(package.DirectoryPath, "detection.onnx"), "tampered");

        await Assert.ThrowsAsync<InvalidDataException>(
            () => PpOcrModelPackageValidator.ValidateAsync(package.DirectoryPath));
    }

    [Fact]
    public async Task Validator_ReadsCharacterDictionaryFromOfficialInferenceYamlShape()
    {
        using var package = await TestModelPackage.CreateAsync(useYamlDictionary: true);

        var validated = await PpOcrModelPackageValidator.ValidateAsync(package.DirectoryPath);

        Assert.Equal(["A", "'", "\""], validated.Dictionary.Take(3));
    }

    private sealed class FakeDecoder : IOcrImageDecoder
    {
        public Task<DecodedOcrImage> DecodeAsync(
            Stream source,
            CancellationToken cancellationToken = default)
        {
            var pixels = Enumerable.Repeat((byte)255, 8 * 4 * 4).ToArray();
            return Task.FromResult(new DecodedOcrImage(pixels, 8, 4));
        }
    }

    private sealed class FakeRuntime : IPpOcrV6InferenceRuntime
    {
        public int CallCount { get; private set; }

        public Task<OcrTensorResult> RunAsync(
            string modelPath,
            string inputName,
            string outputName,
            float[] input,
            IReadOnlyList<int> dimensions,
            CancellationToken cancellationToken = default,
            InferenceAccelerationMode? accelerationMode = null)
        {
            CallCount++;
            if (Path.GetFileName(modelPath) == "detection.onnx")
            {
                var map = new float[4 * 8];
                for (var y = 1; y <= 2; y++)
                {
                    for (var x = 1; x <= 5; x++)
                    {
                        map[(y * 8) + x] = 0.9f;
                    }
                }

                return Task.FromResult(new OcrTensorResult(map, [1, 1, 4, 8]));
            }

            return Task.FromResult(new OcrTensorResult(
            [
                0, 8, 0,
                0, 8, 0,
                8, 0, 0,
                8, 0, 0,
            ], [1, 4, 3]));
        }

        public void Dispose()
        {
        }
    }

    private sealed class TestModelPackage : IDisposable
    {
        private TestModelPackage(string directoryPath)
        {
            DirectoryPath = directoryPath;
        }

        public string DirectoryPath { get; }

        public static async Task<TestModelPackage> CreateAsync(bool useYamlDictionary = false)
        {
            var directory = Path.Combine(Path.GetTempPath(), "PicForLater.Tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var dictionaryFileName = useYamlDictionary ? "inference.yml" : "dictionary.txt";
            var dictionaryBytes = useYamlDictionary
                ? "PostProcess:\n  name: CTCLabelDecode\n  character_dict:\n  - A\n  - \"'\"\n  - '\"'\nGlobal:\n  model_name: test\n"u8.ToArray()
                : "A\n"u8.ToArray();
            var files = new Dictionary<string, byte[]>
            {
                ["detection.onnx"] = [1, 2, 3],
                ["recognition.onnx"] = [4, 5, 6],
                [dictionaryFileName] = dictionaryBytes,
            };
            foreach (var file in files)
            {
                await File.WriteAllBytesAsync(Path.Combine(directory, file.Key), file.Value);
            }

            object FileEntry(string role, string path) => new
            {
                role,
                path,
                sha256 = Convert.ToHexString(SHA256.HashData(files[path])).ToLowerInvariant(),
                bytes = files[path].LongLength,
            };
            var installedBytes = files.Values.Sum(value => value.LongLength);
            var manifest = new
            {
                manifestVersion = 1,
                id = "pp-ocrv6-small",
                version = "test-v1",
                backend = "onnxruntime",
                format = "onnx",
                architecture = "PP-OCRv6-small",
                capabilities = new[] { "ocr" },
                inputLanguages = new[] { "en" },
                outputLanguages = new[] { "en" },
                scripts = new[] { "Latn" },
                mixedLanguageSupport = true,
                files = new[]
                {
                    FileEntry("detection", "detection.onnx"),
                    FileEntry("recognition", "recognition.onnx"),
                    FileEntry("dictionary", dictionaryFileName),
                },
                license = "Apache-2.0",
                sourceUrl = "https://github.com/PaddlePaddle/PaddleOCR",
                downloadBytes = installedBytes,
                installedBytes,
                minRamBytes = 1,
                recommendedHardware = "test CPU",
                inputSignature = new
                {
                    detection = new { inputName = "x", outputName = "maps" },
                    recognition = new { inputName = "x", outputName = "logits" },
                    detectionMaxSideLength = 320,
                    detectionThreshold = 0.3,
                    boxThreshold = 0.5,
                    recognitionHeight = 48,
                    recognitionWidth = 320,
                    ctcBlankIndex = 0,
                    appendSpaceCharacter = true,
                },
                outputSchemaVersion = "test.v1",
            };
            await File.WriteAllTextAsync(
                Path.Combine(directory, "manifest.json"),
                JsonSerializer.Serialize(manifest, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
            return new TestModelPackage(directory);
        }

        public void Dispose()
        {
            if (Directory.Exists(DirectoryPath))
            {
                Directory.Delete(DirectoryPath, recursive: true);
            }
        }
    }
}
