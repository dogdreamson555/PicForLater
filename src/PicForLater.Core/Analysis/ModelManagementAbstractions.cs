namespace PicForLater.Core.Analysis;

public interface IModelOperationFailure
{
    string ErrorCode { get; }
}

public interface IModelPackageValidator
{
    Task<ValidatedModelPackage> ValidateAsync(
        string packageDirectoryPath,
        bool runInferenceSelfTest,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Validates a private staging directory whose file bytes were already checked against
    /// <paramref name="expectedManifest"/> while they were copied into that directory.
    /// Callers must not use this path for arbitrary or user-controlled package directories.
    /// </summary>
    Task<ValidatedModelPackage> ValidateVerifiedStagingAsync(
        string packageDirectoryPath,
        ModelPackageManifest expectedManifest,
        CancellationToken cancellationToken = default) =>
        ValidateAsync(
            packageDirectoryPath,
            runInferenceSelfTest: true,
            cancellationToken);
}

public interface IModelPackageService : IAnalysisProfileSnapshotProvider
{
    Task<ModelManagementState> GetStateAsync(
        CancellationToken cancellationToken = default);

    Task<ModelPackageImportResult> ImportAsync(
        string manifestFilePath,
        CancellationToken cancellationToken = default);

    Task SwitchAsync(
        ModelCapability capability,
        string? packageKey,
        CancellationToken cancellationToken = default);

    Task SwitchManyAsync(
        IReadOnlyCollection<ModelCapability> capabilities,
        string? packageKey,
        CancellationToken cancellationToken = default);

    Task SetAnalysisModeAsync(
        AnalysisMode mode,
        CancellationToken cancellationToken = default);

    Task<InstalledModelPackage?> ResolveAsync(
        string packageKey,
        CancellationToken cancellationToken = default);
}

public interface IQwenGenerationRuntime
{
    IReadOnlySet<string> SupportedExecutionProviders { get; }

    Task<string> GenerateAsync(
        string modelDirectoryPath,
        string imageFilePath,
        string prompt,
        string jsonSchema,
        int maximumOutputTokens,
        InferenceAccelerationMode accelerationMode,
        CancellationToken cancellationToken = default);
}

public interface IAnalysisReanalysisService
{
    Task<ReanalysisQueueResult> QueueAsync(
        IReadOnlyCollection<Guid> imageItemIds,
        CancellationToken cancellationToken = default);
}

public interface ILocalOcrPackageInstaller
{
    Task<bool> IsInstalledAsync(CancellationToken cancellationToken = default);

    Task<LocalOcrPackageInstallResult> InstallAsync(
        string downloadedPackageDirectoryPath,
        CancellationToken cancellationToken = default);
}

public interface IRecommendedModelService
{
    Task<IReadOnlyList<RecommendedModelDescriptor>> GetCatalogAsync(
        CancellationToken cancellationToken = default);

    Task<RecommendedModelInstallResult> DownloadInstallAndEnableAsync(
        string modelId,
        IProgress<ModelDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default);
}

public interface INvidiaCudaEnvironmentService
{
    NvidiaCudaRuntimePackageInfo RuntimePackage { get; }

    string ManagedRuntimeDirectoryPath { get; }

    Task<NvidiaCudaEnvironmentStatus> DetectAsync(
        CancellationToken cancellationToken = default);

    Task<NvidiaCudaRuntimeInstallResult> DownloadAndInstallRuntimeAsync(
        IProgress<ModelDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default);
}
