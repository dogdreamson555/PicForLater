using System.Text.Json;
using PicForLater.Core.Analysis;

namespace PicForLater.Analysis.PpOcr;

public sealed class PpOcrRecommendedPackageInstaller : ILocalOcrPackageInstaller
{
    public const string PackageVersion = "6.0.0-hf-28fe589-b8f84f0";
    public const long DownloadBytes = 31_190_469;
    private const long MinRamBytes = 2L * 1024 * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };
    private static readonly PpOcrModelPackageManifest ExpectedManifest = CreateManifest();
    private readonly string _installedDirectoryPath;
    private readonly IPpOcrV6InferenceRuntime _runtime;
    private readonly SemaphoreSlim _mutationGate = new(1, 1);

    public PpOcrRecommendedPackageInstaller(
        string installedDirectoryPath,
        IPpOcrV6InferenceRuntime runtime)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(installedDirectoryPath);
        _installedDirectoryPath = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(installedDirectoryPath));
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
    }

    public async Task<bool> IsInstalledAsync(CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(_installedDirectoryPath))
        {
            return false;
        }

        try
        {
            var package = await PpOcrModelPackageValidator.ValidateAsync(
                _installedDirectoryPath,
                cancellationToken).ConfigureAwait(false);
            return MatchesExpectedPackage(package.Manifest);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            return false;
        }
    }

    public async Task<LocalOcrPackageInstallResult> InstallAsync(
        string downloadedPackageDirectoryPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(downloadedPackageDirectoryPath);
        var sourceDirectoryPath = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(downloadedPackageDirectoryPath));
        await _mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Directory.Exists(_installedDirectoryPath))
            {
                var installed = await PpOcrModelPackageValidator.ValidateAsync(
                    _installedDirectoryPath,
                    cancellationToken).ConfigureAwait(false);
                if (!MatchesExpectedPackage(installed.Manifest))
                {
                    throw new InvalidDataException("A different PP-OCR package already occupies the managed directory.");
                }

                return new LocalOcrPackageInstallResult(AlreadyInstalled: true);
            }

            var manifestPath = Path.Combine(sourceDirectoryPath, "manifest.json");
            if (File.Exists(manifestPath))
            {
                throw new InvalidDataException("The downloaded OCR staging directory already contains a manifest.");
            }

            await File.WriteAllTextAsync(
                manifestPath,
                JsonSerializer.Serialize(ExpectedManifest, JsonOptions),
                cancellationToken).ConfigureAwait(false);
            var package = await PpOcrModelPackageValidator.ValidateAsync(
                sourceDirectoryPath,
                cancellationToken).ConfigureAwait(false);
            if (!MatchesExpectedPackage(package.Manifest))
            {
                throw new InvalidDataException("The downloaded OCR package identity is not the pinned package.");
            }

            await Task.Run(
                () => RunSelfTestAsync(package, cancellationToken),
                cancellationToken).ConfigureAwait(false);

            Directory.CreateDirectory(Path.GetDirectoryName(_installedDirectoryPath)!);
            Directory.Move(sourceDirectoryPath, _installedDirectoryPath);
            return new LocalOcrPackageInstallResult(AlreadyInstalled: false);
        }
        finally
        {
            _mutationGate.Release();
        }
    }

    private async Task RunSelfTestAsync(
        ValidatedPpOcrModelPackage package,
        CancellationToken cancellationToken)
    {
        var signature = package.Manifest.InputSignature;
        var detection = await _runtime.RunAsync(
            package.DetectionModelPath,
            signature.Detection.InputName,
            signature.Detection.OutputName,
            new float[1 * 3 * 32 * 32],
            [1, 3, 32, 32],
            cancellationToken,
            InferenceAccelerationMode.Cpu).ConfigureAwait(false);
        var recognition = await _runtime.RunAsync(
            package.RecognitionModelPath,
            signature.Recognition.InputName,
            signature.Recognition.OutputName,
            new float[1 * 3 * signature.RecognitionHeight * signature.RecognitionWidth],
            [1, 3, signature.RecognitionHeight, signature.RecognitionWidth],
            cancellationToken,
            InferenceAccelerationMode.Cpu).ConfigureAwait(false);
        if (detection.Values.Length == 0
            || recognition.Values.Length == 0
            || detection.Values.Any(value => !float.IsFinite(value))
            || recognition.Values.Any(value => !float.IsFinite(value)))
        {
            throw new InvalidDataException("The PP-OCR package did not pass its minimal inference self-test.");
        }
    }

    private static bool MatchesExpectedPackage(PpOcrModelPackageManifest manifest) =>
        manifest.Id == ExpectedManifest.Id
        && manifest.Version == ExpectedManifest.Version
        && manifest.Files.OrderBy(file => file.Role, StringComparer.Ordinal)
            .Select(file => (file.Role, file.Path, file.Bytes, file.Sha256))
            .SequenceEqual(ExpectedManifest.Files
                .OrderBy(file => file.Role, StringComparer.Ordinal)
                .Select(file => (file.Role, file.Path, file.Bytes, file.Sha256)));

    private static PpOcrModelPackageManifest CreateManifest() => new()
    {
        ManifestVersion = PpOcrModelPackageValidator.SupportedManifestVersion,
        Id = "pp-ocrv6-small",
        Version = PackageVersion,
        Backend = "onnxruntime",
        Format = "onnx",
        Architecture = "PP-OCRv6-small",
        Capabilities = ["ocr"],
        InputLanguages = ["und", "zh-Hans", "zh-Hant", "en", "ja"],
        OutputLanguages = ["und", "zh-Hans", "zh-Hant", "en", "ja"],
        Scripts = ["Hans", "Hant", "Jpan", "Latn"],
        MixedLanguageSupport = true,
        Files =
        [
            new PpOcrModelPackageFile
            {
                Role = "detection",
                Path = "detection.onnx",
                Bytes = 9_880_512,
                Sha256 = "d73e0058b7a8086bbd57f3d10b8bcd4ff95363f67e06e2762b5e814fe9c9410e",
            },
            new PpOcrModelPackageFile
            {
                Role = "recognition",
                Path = "recognition.onnx",
                Bytes = 21_159_378,
                Sha256 = "5435fd747c9e0efe15a96d0b378d5bd157e9492ed8fd80edf08f30d02fa24634",
            },
            new PpOcrModelPackageFile
            {
                Role = "dictionary",
                Path = "inference.yml",
                Bytes = 150_579,
                Sha256 = "ab078671bb49f06228eadccd34f1bb501e157f7a047095ffb943ba81512c77d1",
            },
        ],
        License = "Apache-2.0",
        SourceUrl = "https://huggingface.co/PaddlePaddle/PP-OCRv6_small_rec_onnx",
        DownloadBytes = DownloadBytes,
        InstalledBytes = DownloadBytes,
        MinRamBytes = MinRamBytes,
        RecommendedHardware = "2 GiB RAM; x64/ARM64 CPU baseline",
        InputSignature = new PpOcrInputSignature
        {
            Detection = new PpOcrTensorSignature { InputName = "x", OutputName = "fetch_name_0" },
            Recognition = new PpOcrTensorSignature { InputName = "x", OutputName = "fetch_name_0" },
            DetectionMaxSideLength = 960,
            DetectionThreshold = 0.3f,
            BoxThreshold = 0.5f,
            RecognitionHeight = 48,
            RecognitionWidth = 320,
            CtcBlankIndex = 0,
            AppendSpaceCharacter = true,
        },
        OutputSchemaVersion = "picforlater.ocr.v1",
    };
}
