namespace PicForLater.Core.Analysis;

public sealed record ModelPackageFile(
    string Path,
    long ByteLength,
    string Sha256);

public sealed record ModelPackageManifest(
    int ManifestVersion,
    string Id,
    string Version,
    string Backend,
    string Format,
    string Architecture,
    string Quantization,
    IReadOnlyList<ModelCapability> Capabilities,
    IReadOnlyList<string> InputLanguages,
    IReadOnlyList<string> OutputLanguages,
    IReadOnlyList<string> Scripts,
    bool MixedLanguageSupport,
    IReadOnlyList<ModelPackageFile> Files,
    string License,
    string SourceUrl,
    long DownloadBytes,
    long InstalledBytes,
    long MinRamBytes,
    string RecommendedHardware,
    string InputSignature,
    string OutputSchemaVersion,
    IReadOnlyList<string>? SupportedExecutionProviders = null);

public sealed record ValidatedModelPackage(
    string PackageKey,
    ModelPackageManifest Manifest,
    string ManifestJson,
    DateTimeOffset SelfTestedAtUtc);

public sealed record InstalledModelPackage(
    string PackageKey,
    ModelPackageManifest Manifest,
    string InstalledDirectoryPath,
    DateTimeOffset InstalledAtUtc,
    DateTimeOffset SelfTestedAtUtc,
    string BenchmarkStatus);

public sealed record ModelManagementState(
    ModelProfileSnapshot CurrentProfile,
    IReadOnlyList<InstalledModelPackage> Packages);

public sealed record ModelPackageImportResult(
    InstalledModelPackage Package,
    bool ReplacedExistingPackage);

public sealed record ReanalysisQueueResult(
    int RequestedCount,
    int QueuedCount,
    int SkippedCount);

public enum RecommendedModelPackageKind
{
    PpOcrV6Small = 0,
    Qwen3Vl2BInstruct = 1,
}

public enum ModelDownloadStage
{
    Preparing = 0,
    Downloading = 1,
    Verifying = 2,
    Installing = 3,
    Enabling = 4,
    Completed = 5,
}

public sealed record RecommendedModelDescriptor(
    string Id,
    RecommendedModelPackageKind Kind,
    string Name,
    string Version,
    string Description,
    IReadOnlyList<ModelCapability> Capabilities,
    long DownloadBytes,
    long InstalledBytes,
    long MinRamBytes,
    string RecommendedHardware,
    string License,
    string SourceUrl,
    bool IsExperimental,
    string BenchmarkStatus,
    bool IsInstalled,
    bool IsEnabled);

public sealed record ModelDownloadProgress(
    string ModelId,
    ModelDownloadStage Stage,
    long DownloadedBytes,
    long TotalBytes,
    string? CurrentFile);

public sealed record RecommendedModelInstallResult(
    RecommendedModelDescriptor Model,
    bool DownloadWasRequired);

public enum NvidiaCudaEnvironmentState
{
    Ready = 0,
    UnsupportedArchitecture = 1,
    DriverMissing = 2,
    NoCudaDevice = 3,
    DriverTooOld = 4,
    InsufficientVram = 5,
    RuntimeMissing = 6,
    RuntimeIncomplete = 7,
}

public enum NvidiaCudaRuntimeSource
{
    None = 0,
    AppManaged = 1,
    System = 2,
}

public sealed record NvidiaGpuDevice(
    string Name,
    long DedicatedMemoryBytes,
    int ComputeCapabilityMajor,
    int ComputeCapabilityMinor);

public sealed record NvidiaCudaRuntimePackageInfo(
    string CudaVersion,
    string CudnnVersion,
    long DownloadBytes,
    long InstalledBytes,
    string CudaLicenseUrl,
    string CudnnLicenseUrl,
    string SourceUrl);

public sealed record NvidiaCudaEnvironmentStatus(
    NvidiaCudaEnvironmentState State,
    NvidiaCudaRuntimeSource RuntimeSource,
    NvidiaGpuDevice? Device,
    int DriverCudaVersion,
    IReadOnlyList<string> MissingRuntimeFiles)
{
    public bool CanInstallRuntime =>
        State is NvidiaCudaEnvironmentState.RuntimeMissing
            or NvidiaCudaEnvironmentState.RuntimeIncomplete;

    public bool CanUseCudaModel => State == NvidiaCudaEnvironmentState.Ready;
}

public sealed record NvidiaCudaRuntimeInstallResult(
    NvidiaCudaEnvironmentStatus Status,
    bool DownloadWasRequired);

public sealed record LocalOcrPackageInstallResult(
    bool AlreadyInstalled);

public sealed class RecommendedModelInstallException : Exception
{
    public RecommendedModelInstallException(string errorCode, Exception? innerException = null)
        : base("The recommended local model could not be downloaded and enabled.", innerException)
    {
        ErrorCode = errorCode;
    }

    public string ErrorCode { get; }
}
