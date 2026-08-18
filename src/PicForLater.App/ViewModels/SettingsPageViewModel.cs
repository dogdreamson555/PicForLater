using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Windows.ApplicationModel.Resources;
using PicForLater.App.Models;
using PicForLater.App.Services;
using PicForLater.Core.Analysis;
using PicForLater.Infrastructure.Analysis;

namespace PicForLater.App.ViewModels;

public partial class SettingsPageViewModel : ObservableObject
{
    private readonly IThemePreferenceService _themePreferenceService;
    private readonly IInferenceAccelerationPreferenceService _inferenceAcceleration;
    private readonly IStorageReadinessService _storageReadinessService;
    private readonly Func<IModelPackageService?> _modelPackageServiceAccessor;
    private readonly Func<IRecommendedModelService?> _recommendedModelServiceAccessor;
    private readonly Func<INvidiaCudaEnvironmentService?> _nvidiaCudaEnvironmentServiceAccessor;
    private readonly Func<LocalInferenceComponentInstaller?> _localInferenceComponentInstallerAccessor;
    private readonly Func<LocalInferenceComponentStore?> _localInferenceComponentStoreAccessor;
    private CancellationTokenSource? _modelOperationCancellation;
    private static readonly ResourceLoader _resources = new();

    public SettingsPageViewModel(
        IThemePreferenceService themePreferenceService,
        IInferenceAccelerationPreferenceService inferenceAcceleration,
        IStorageReadinessService storageReadinessService,
        Func<IModelPackageService?> modelPackageServiceAccessor,
        Func<IRecommendedModelService?> recommendedModelServiceAccessor,
        Func<INvidiaCudaEnvironmentService?> nvidiaCudaEnvironmentServiceAccessor,
        Func<LocalInferenceComponentInstaller?> localInferenceComponentInstallerAccessor,
        Func<LocalInferenceComponentStore?> localInferenceComponentStoreAccessor)
    {
        _themePreferenceService = themePreferenceService
            ?? throw new ArgumentNullException(nameof(themePreferenceService));
        _inferenceAcceleration = inferenceAcceleration
            ?? throw new ArgumentNullException(nameof(inferenceAcceleration));
        _storageReadinessService = storageReadinessService
            ?? throw new ArgumentNullException(nameof(storageReadinessService));
        _modelPackageServiceAccessor = modelPackageServiceAccessor
            ?? throw new ArgumentNullException(nameof(modelPackageServiceAccessor));
        _recommendedModelServiceAccessor = recommendedModelServiceAccessor
            ?? throw new ArgumentNullException(nameof(recommendedModelServiceAccessor));
        _nvidiaCudaEnvironmentServiceAccessor = nvidiaCudaEnvironmentServiceAccessor
            ?? throw new ArgumentNullException(nameof(nvidiaCudaEnvironmentServiceAccessor));
        _localInferenceComponentInstallerAccessor = localInferenceComponentInstallerAccessor
            ?? throw new ArgumentNullException(nameof(localInferenceComponentInstallerAccessor));
        _localInferenceComponentStoreAccessor = localInferenceComponentStoreAccessor
            ?? throw new ArgumentNullException(nameof(localInferenceComponentStoreAccessor));
        SelectedThemeIndex = (int)_themePreferenceService.CurrentPreference;
        InferenceAccelerationOptions.Add(
            InferenceAccelerationOptionItem.FromMode(InferenceAccelerationMode.Automatic));
        if (_inferenceAcceleration.IsDirectMlAvailable)
        {
            InferenceAccelerationOptions.Add(
                InferenceAccelerationOptionItem.FromMode(InferenceAccelerationMode.DirectMlGpu));
        }

        InferenceAccelerationOptions.Add(
            InferenceAccelerationOptionItem.FromMode(InferenceAccelerationMode.Cpu));
        if (_inferenceAcceleration.IsCudaAvailable)
        {
            InferenceAccelerationOptions.Add(
                InferenceAccelerationOptionItem.FromMode(InferenceAccelerationMode.CudaGpu));
        }

        SelectedInferenceAccelerationIndex = Math.Max(
            0,
            InferenceAccelerationOptions
                .Select((item, index) => (item, index))
                .FirstOrDefault(pair => pair.item.Mode == _inferenceAcceleration.CurrentMode)
                .index);
        RefreshInferenceAccelerationStatus();
    }

    public ObservableCollection<ModelPackageOption> VisionOptions { get; } = [];

    public ObservableCollection<ModelPackageOption> TextCompositionOptions { get; } = [];

    public ObservableCollection<InstalledModelPackageItem> InstalledPackages { get; } = [];

    public string InstalledModelsSummary => InstalledPackages.Count == 0
        ? _resources.GetString("InstalledModelsSummaryEmpty")
        : string.Format(
            CultureInfo.CurrentCulture,
            _resources.GetString("InstalledModelsSummaryFormat"),
            InstalledPackages.Count);

    public ObservableCollection<RecommendedModelItem> RecommendedModels { get; } = [];

    public ObservableCollection<InferenceAccelerationOptionItem> InferenceAccelerationOptions { get; } = [];

    public bool IsDirectMlAvailable => _inferenceAcceleration.IsDirectMlAvailable;

    public bool IsCudaAvailable => _inferenceAcceleration.IsCudaAvailable;

    [ObservableProperty]
    public partial int SelectedThemeIndex { get; set; }

    [ObservableProperty]
    public partial int SelectedAnalysisModeIndex { get; set; } = 1;

    [ObservableProperty]
    public partial int SelectedInferenceAccelerationIndex { get; set; }

    [ObservableProperty]
    public partial string InferenceAccelerationStatusMessage { get; set; } = string.Empty;

    [ObservableProperty]
    public partial NvidiaCudaEnvironmentState? NvidiaEnvironmentState { get; set; }

    [ObservableProperty]
    public partial string NvidiaEnvironmentStatusMessage { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsNvidiaRuntimeInstallAvailable { get; set; }

    [ObservableProperty]
    public partial bool IsNvidiaDriverHelpVisible { get; set; }

    [ObservableProperty]
    public partial ModelPackageOption? SelectedVisionOption { get; set; }

    [ObservableProperty]
    public partial ModelPackageOption? SelectedTextCompositionOption { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStatusMessage))]
    public partial string StatusMessage { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanInstallLocalInferenceComponent))]
    [NotifyPropertyChangedFor(nameof(CanRemoveLocalInferenceComponent))]
    public partial bool IsWorking { get; set; }

    [ObservableProperty]
    public partial bool CanCancelModelOperation { get; set; }

    [ObservableProperty]
    public partial bool IsModelProgressIndeterminate { get; set; } = true;

    [ObservableProperty]
    public partial double ModelProgressPercent { get; set; }

    [ObservableProperty]
    public partial bool IsStatusError { get; set; }

    [ObservableProperty]
    public partial bool IsInitialized { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LocalInferenceComponentActionText))]
    [NotifyPropertyChangedFor(nameof(CanRemoveLocalInferenceComponent))]
    public partial bool IsLocalInferenceComponentInstalled { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanInstallLocalInferenceComponent))]
    public partial bool IsLocalInferenceComponentDownloadAvailable { get; set; }

    [ObservableProperty]
    public partial string LocalInferenceComponentVersion { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string LocalInferenceComponentStatusMessage { get; set; } = string.Empty;

    public bool HasStatusMessage => !string.IsNullOrWhiteSpace(StatusMessage);

    public bool CanInstallLocalInferenceComponent =>
        IsLocalInferenceComponentDownloadAvailable && !IsWorking;

    public bool CanRemoveLocalInferenceComponent =>
        IsLocalInferenceComponentInstalled && !IsWorking;

    public string LocalInferenceComponentActionText => _resources.GetString(
        IsLocalInferenceComponentInstalled
            ? "LocalInferenceComponentRepairAction"
            : "LocalInferenceComponentInstallAction");

    partial void OnSelectedThemeIndexChanged(int value)
    {
        if (Enum.IsDefined(typeof(AppThemePreference), value))
        {
            _themePreferenceService.SetPreference((AppThemePreference)value);
        }
    }

    public async Task InitializeAsync()
    {
        if (IsInitialized)
        {
            return;
        }

        var readiness = await _storageReadinessService.EnsureReadyAsync(forceRetry: false).ConfigureAwait(true);
        if (readiness.Status != StorageReadinessStatus.Ready)
        {
            StatusMessage = _resources.GetString("ModelManagementUnavailableStatus");
            IsStatusError = true;
            return;
        }

        await ReloadAsync().ConfigureAwait(true);
        await RefreshLocalInferenceComponentAsync().ConfigureAwait(true);
        await RefreshNvidiaEnvironmentAsync().ConfigureAwait(true);
        IsInitialized = true;
    }

    public async Task RefreshLocalInferenceComponentAsync()
    {
        var store = _localInferenceComponentStoreAccessor();
        IsLocalInferenceComponentDownloadAvailable =
            _localInferenceComponentInstallerAccessor() is not null;
        var component = store is null
            ? null
            : await store.GetActiveAsync().ConfigureAwait(true);
        IsLocalInferenceComponentInstalled = component is not null;
        LocalInferenceComponentVersion = component?.Version ?? string.Empty;
        SetRecommendedActionsEnabled(!IsWorking);
        LocalInferenceComponentStatusMessage = component is not null
            ? string.Format(
                CultureInfo.CurrentCulture,
                _resources.GetString("LocalInferenceComponentInstalledStatusFormat"),
                component.Version)
            : IsLocalInferenceComponentDownloadAvailable
                ? _resources.GetString("LocalInferenceComponentNotInstalledStatus")
                : _resources.GetString("LocalInferenceComponentTrustUnavailableStatus");
    }

    public async Task InstallOrRepairLocalInferenceComponentAsync()
    {
        if (IsWorking)
        {
            return;
        }

        var installer = _localInferenceComponentInstallerAccessor();
        if (installer is null)
        {
            StatusMessage = _resources.GetString("LocalInferenceComponentTrustUnavailableStatus");
            IsStatusError = true;
            return;
        }

        _modelOperationCancellation?.Dispose();
        _modelOperationCancellation = new CancellationTokenSource();
        IsWorking = true;
        CanCancelModelOperation = true;
        IsModelProgressIndeterminate = true;
        ModelProgressPercent = 0;
        SetRecommendedActionsEnabled(false);
        try
        {
            var progress = new Progress<LocalInferenceComponentInstallProgress>(
                UpdateLocalInferenceComponentProgress);
            var result = await installer.InstallOrRepairAsync(
                    progress,
                    _modelOperationCancellation.Token)
                .ConfigureAwait(true);
            await RefreshLocalInferenceComponentAsync().ConfigureAwait(true);
            StatusMessage = string.Format(
                CultureInfo.CurrentCulture,
                _resources.GetString(result.DownloadWasRequired
                    ? "LocalInferenceComponentInstalledSuccessFormat"
                    : "LocalInferenceComponentAlreadyCurrentFormat"),
                result.Component.Version);
            IsStatusError = false;
        }
        catch (OperationCanceledException)
        {
            StatusMessage = _resources.GetString("LocalInferenceComponentCanceledStatus");
            IsStatusError = false;
        }
        catch (LocalInferenceComponentInstallException exception)
        {
            ShowLocalInferenceComponentFailure(exception.ErrorCode);
        }
        catch
        {
            StatusMessage = _resources.GetString("LocalInferenceComponentInstallFailedStatus");
            IsStatusError = true;
        }
        finally
        {
            CanCancelModelOperation = false;
            IsWorking = false;
            SetRecommendedActionsEnabled(true);
            _modelOperationCancellation?.Dispose();
            _modelOperationCancellation = null;
        }
    }

    public async Task RemoveLocalInferenceComponentAsync()
    {
        if (IsWorking || _localInferenceComponentStoreAccessor() is not { } store)
        {
            return;
        }

        IsWorking = true;
        CanCancelModelOperation = false;
        IsModelProgressIndeterminate = true;
        SetRecommendedActionsEnabled(false);
        try
        {
            StatusMessage = _resources.GetString("LocalInferenceComponentRemovingStatus");
            IsStatusError = false;
            _ = await store.RemoveAllAsync().ConfigureAwait(true);
            await RefreshLocalInferenceComponentAsync().ConfigureAwait(true);
            StatusMessage = _resources.GetString("LocalInferenceComponentRemovedStatus");
            IsStatusError = false;
        }
        catch
        {
            await RefreshLocalInferenceComponentAsync().ConfigureAwait(true);
            StatusMessage = _resources.GetString("LocalInferenceComponentRemoveFailedStatus");
            IsStatusError = true;
        }
        finally
        {
            IsWorking = false;
            SetRecommendedActionsEnabled(true);
        }
    }

    public async Task SetAnalysisModeAsync(int selectedIndex)
    {
        var mode = selectedIndex switch
        {
            0 => AnalysisMode.OcrOnly,
            1 => AnalysisMode.Balanced,
            2 => AnalysisMode.AlwaysEnhance,
            _ => throw new ArgumentOutOfRangeException(nameof(selectedIndex)),
        };
        await GetModelPackageService().SetAnalysisModeAsync(mode).ConfigureAwait(true);
        SelectedAnalysisModeIndex = selectedIndex;
        StatusMessage = _resources.GetString("AnalysisModeSavedStatus");
        IsStatusError = false;
    }

    public void SetInferenceAccelerationMode(int selectedIndex)
    {
        if (selectedIndex < 0 || selectedIndex >= InferenceAccelerationOptions.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(selectedIndex));
        }

        _inferenceAcceleration.SetMode(InferenceAccelerationOptions[selectedIndex].Mode);
        SelectedInferenceAccelerationIndex = selectedIndex;
        RefreshInferenceAccelerationStatus();
        StatusMessage = _resources.GetString("InferenceAccelerationSavedStatus");
        IsStatusError = false;
    }

    public void RefreshInferenceAccelerationStatus()
    {
        var status = _inferenceAcceleration.LastExecutionStatus;
        if (status is null)
        {
            InferenceAccelerationStatusMessage = _resources.GetString(
                "InferenceAccelerationNotRunStatus");
            return;
        }

        var workload = _resources.GetString(status.Workload == "PpOcr"
            ? "InferenceWorkloadPpOcr"
            : "InferenceWorkloadQwen3Vl");
        var device = _resources.GetString(status.Device switch
        {
            InferenceExecutionDevice.DirectMlGpu => "InferenceDeviceDirectMlGpu",
            InferenceExecutionDevice.CudaGpu => "InferenceDeviceCudaGpu",
            _ => "InferenceDeviceCpu",
        });
        var formatKey = status.FailureCode is not null
            ? "InferenceExecutionFailedStatusFormat"
            : status.UsedAutomaticFallback
                ? "InferenceExecutionFallbackStatusFormat"
                : "InferenceExecutionStatusFormat";
        InferenceAccelerationStatusMessage = string.Format(
            System.Globalization.CultureInfo.CurrentCulture,
            _resources.GetString(formatKey),
            workload,
            device);
    }

    public async Task ImportAsync(string manifestFilePath)
    {
        IsWorking = true;
        try
        {
            var result = await GetModelPackageService().ImportAsync(manifestFilePath).ConfigureAwait(true);
            await ReloadAsync().ConfigureAwait(true);
            StatusMessage = string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                _resources.GetString("ModelImportCompletedStatusFormat"),
                result.Package.PackageKey);
            IsStatusError = false;
        }
        finally
        {
            IsWorking = false;
        }
    }

    public async Task SwitchAsync(ModelCapability capability, ModelPackageOption option)
    {
        ArgumentNullException.ThrowIfNull(option);
        IsWorking = true;
        try
        {
            await GetModelPackageService().SwitchAsync(capability, option.PackageKey).ConfigureAwait(true);
            await ReloadAsync().ConfigureAwait(true);
            StatusMessage = _resources.GetString("ModelSwitchCompletedStatus");
            IsStatusError = false;
        }
        finally
        {
            IsWorking = false;
        }
    }

    public async Task DownloadInstallAndEnableAsync(
        RecommendedModelItem item,
        bool installNvidiaRuntimeFirst = false)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (IsWorking || item.Descriptor.IsEnabled)
        {
            return;
        }

        _modelOperationCancellation?.Dispose();
        _modelOperationCancellation = new CancellationTokenSource();
        IsWorking = true;
        CanCancelModelOperation = true;
        IsModelProgressIndeterminate = true;
        ModelProgressPercent = 0;
        SetRecommendedActionsEnabled(false);
        try
        {
            var progress = new Progress<ModelDownloadProgress>(UpdateDownloadProgress);
            if (item.RequiresNvidiaCudaRuntime)
            {
                var environment = await GetNvidiaCudaEnvironmentService()
                    .DetectAsync(_modelOperationCancellation.Token)
                    .ConfigureAwait(true);
                ApplyNvidiaEnvironmentStatus(environment);
                if (!environment.CanUseCudaModel)
                {
                    if (!installNvidiaRuntimeFirst || !environment.CanInstallRuntime)
                    {
                        throw CreateNvidiaEnvironmentException(environment.State);
                    }

                    var runtimeResult = await GetNvidiaCudaEnvironmentService()
                        .DownloadAndInstallRuntimeAsync(
                            progress,
                            _modelOperationCancellation.Token)
                        .ConfigureAwait(true);
                    ApplyNvidiaEnvironmentStatus(runtimeResult.Status);
                }
            }

            var result = await GetRecommendedModelService().DownloadInstallAndEnableAsync(
                item.Id,
                progress,
                _modelOperationCancellation.Token).ConfigureAwait(true);
            await ReloadAsync().ConfigureAwait(true);
            StatusMessage = string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                _resources.GetString(result.DownloadWasRequired
                    ? "RecommendedModelInstalledStatusFormat"
                    : "RecommendedModelEnabledStatusFormat"),
                result.Model.Name);
            IsStatusError = false;
        }
        catch (OperationCanceledException)
        {
            StatusMessage = _resources.GetString("RecommendedModelCanceledStatus");
            IsStatusError = false;
        }
        catch (RecommendedModelInstallException exception)
        {
            ShowModelOperationFailure(exception, "RecommendedModelInstallFailedStatus");
        }
        catch
        {
            StatusMessage = _resources.GetString("RecommendedModelInstallFailedStatus");
            IsStatusError = true;
        }
        finally
        {
            CanCancelModelOperation = false;
            IsWorking = false;
            SetRecommendedActionsEnabled(true);
            _modelOperationCancellation?.Dispose();
            _modelOperationCancellation = null;
        }
    }

    public async Task<NvidiaCudaEnvironmentStatus?> RefreshNvidiaEnvironmentAsync()
    {
        if (!IsCudaAvailable)
        {
            NvidiaEnvironmentState = null;
            NvidiaEnvironmentStatusMessage = string.Empty;
            IsNvidiaRuntimeInstallAvailable = false;
            IsNvidiaDriverHelpVisible = false;
            return null;
        }

        try
        {
            var status = await GetNvidiaCudaEnvironmentService().DetectAsync().ConfigureAwait(true);
            ApplyNvidiaEnvironmentStatus(status);
            return status;
        }
        catch
        {
            NvidiaEnvironmentState = NvidiaCudaEnvironmentState.RuntimeMissing;
            NvidiaEnvironmentStatusMessage = _resources.GetString(
                "NvidiaEnvironmentDetectionFailedStatus");
            IsNvidiaRuntimeInstallAvailable = false;
            IsNvidiaDriverHelpVisible = false;
            return null;
        }
    }

    public async Task InstallNvidiaRuntimeAsync()
    {
        if (IsWorking)
        {
            return;
        }

        _modelOperationCancellation?.Dispose();
        _modelOperationCancellation = new CancellationTokenSource();
        IsWorking = true;
        CanCancelModelOperation = true;
        IsModelProgressIndeterminate = true;
        ModelProgressPercent = 0;
        SetRecommendedActionsEnabled(false);
        try
        {
            var progress = new Progress<ModelDownloadProgress>(UpdateDownloadProgress);
            var result = await GetNvidiaCudaEnvironmentService()
                .DownloadAndInstallRuntimeAsync(progress, _modelOperationCancellation.Token)
                .ConfigureAwait(true);
            ApplyNvidiaEnvironmentStatus(result.Status);
            StatusMessage = _resources.GetString(result.DownloadWasRequired
                ? "NvidiaRuntimeInstalledStatus"
                : "NvidiaRuntimeAlreadyAvailableStatus");
            IsStatusError = false;
        }
        catch (OperationCanceledException)
        {
            StatusMessage = _resources.GetString("RecommendedModelCanceledStatus");
            IsStatusError = false;
        }
        catch (RecommendedModelInstallException exception)
        {
            ShowModelOperationFailure(exception, "NvidiaRuntimeInstallFailedStatus");
            await RefreshNvidiaEnvironmentAsync().ConfigureAwait(true);
        }
        catch
        {
            StatusMessage = _resources.GetString("NvidiaRuntimeInstallFailedStatus");
            IsStatusError = true;
        }
        finally
        {
            CanCancelModelOperation = false;
            IsWorking = false;
            SetRecommendedActionsEnabled(true);
            _modelOperationCancellation?.Dispose();
            _modelOperationCancellation = null;
        }
    }

    public string GetNvidiaRuntimeConfirmationMessage()
    {
        var package = GetNvidiaCudaEnvironmentService().RuntimePackage;
        return string.Format(
            CultureInfo.CurrentCulture,
            _resources.GetString("NvidiaRuntimeInstallConfirmationFormat"),
            package.CudaVersion,
            package.CudnnVersion,
            FormatBytes(package.DownloadBytes),
            FormatBytes(package.InstalledBytes),
            package.CudaLicenseUrl,
            package.CudnnLicenseUrl,
            package.SourceUrl);
    }

    public void CancelModelOperation() => _modelOperationCancellation?.Cancel();

    public async Task ReloadAsync()
    {
        var state = await GetModelPackageService().GetStateAsync().ConfigureAwait(true);
        SelectedAnalysisModeIndex = state.CurrentProfile.AnalysisMode switch
        {
            AnalysisMode.OcrOnly => 0,
            AnalysisMode.Balanced => 1,
            AnalysisMode.AlwaysEnhance => 2,
            _ => 1,
        };

        // Clearing the collections detaches the ComboBox selections from their
        // old item instances. Reset both properties as well so a subsequent
        // reload always publishes the matching instances from the rebuilt
        // collections, even when the record values compare equal.
        SelectedVisionOption = null;
        SelectedTextCompositionOption = null;
        VisionOptions.Clear();
        VisionOptions.Add(new ModelPackageOption(
            PackageKey: null,
            _resources.GetString("VisionBuiltInOptionName"),
            _resources.GetString("VisionBuiltInOptionDescription")));
        TextCompositionOptions.Clear();
        TextCompositionOptions.Add(new ModelPackageOption(
            PackageKey: null,
            _resources.GetString("TextComposerBuiltInOptionName"),
            _resources.GetString("TextComposerBuiltInOptionDescription")));
        InstalledPackages.Clear();
        foreach (var package in state.Packages)
        {
            InstalledPackages.Add(InstalledModelPackageItem.FromPackage(package));
            var option = new ModelPackageOption(
                package.PackageKey,
                $"{package.Manifest.Id} {package.Manifest.Version}",
                $"{package.Manifest.Quantization.ToUpperInvariant()} · {package.Manifest.License}");
            if (package.Manifest.Capabilities.Contains(ModelCapability.VisionCaption))
            {
                VisionOptions.Add(option);
            }

            if (package.Manifest.Capabilities.Contains(ModelCapability.TextComposition))
            {
                TextCompositionOptions.Add(option);
            }
        }
        OnPropertyChanged(nameof(InstalledModelsSummary));

        var visionKey = state.CurrentProfile.GetSlot(ModelCapability.VisionCaption).PackageKey;
        var textKey = state.CurrentProfile.GetSlot(ModelCapability.TextComposition).PackageKey;
        SelectedVisionOption = VisionOptions.First(option => option.PackageKey == visionKey);
        SelectedTextCompositionOption = TextCompositionOptions.First(option => option.PackageKey == textKey);

        var recommended = await GetRecommendedModelService().GetCatalogAsync().ConfigureAwait(true);
        RecommendedModels.Clear();
        foreach (var descriptor in recommended)
        {
            RecommendedModels.Add(RecommendedModelItem.FromDescriptor(descriptor));
        }

        SetRecommendedActionsEnabled(!IsWorking);
    }

    public void ShowStatus(string message, bool isError = true)
    {
        StatusMessage = message ?? string.Empty;
        IsStatusError = isError;
    }

    public void ShowModelOperationFailure(Exception exception, string fallbackResourceName)
    {
        ArgumentNullException.ThrowIfNull(exception);
        ArgumentException.ThrowIfNullOrWhiteSpace(fallbackResourceName);
        var errorCode = ClassifyModelOperationFailure(exception);
        var resourceName = errorCode switch
        {
            "model.insufficient-disk-space" => "RecommendedModelInsufficientSpaceStatus",
            "model.download-timeout" => "RecommendedModelDownloadTimedOutStatus",
            "model.download-network-failed" => "RecommendedModelNetworkFailedStatus",
            "model.download-size-mismatch" or "model.download-hash-mismatch" or
                "model.download-uri-rejected" or "model.file-size-mismatch" or
                "model.file-hash-mismatch" or "model.installed-size-mismatch" =>
                    "RecommendedModelVerificationFailedStatus",
            "model.manifest-missing-or-invalid" or
                "model.manifest-invalid-json" or
                "model.manifest-empty" or
                "model.manifest-unsupported" or
                "model.manifest-fields-invalid" or
                "model.required-files-missing" or
                "model.file-entry-invalid" or
                "model.file-path-invalid" or
                "model.reparse-point-rejected" or
                "model.package-directory-missing" or
                "model.package-too-large" => "LocalModelPackageContractFailedStatus",
            "model.execution-provider-unavailable" => "LocalModelExecutionProviderUnavailableStatus",
            "model.same-version-content-conflict" or
                "model.install-directory-conflict" or
                "model.staged-package-mismatch" => "LocalModelPackageConflictStatus",
            "model.file-io-failed" => "LocalModelFileIoFailedStatus",
            "model.file-access-denied" => "LocalModelFileAccessDeniedStatus",
            "model.insufficient-memory" => "LocalModelInsufficientMemoryStatus",
            "model.genai-config-architecture-mismatch" or
                "model.genai-config-file-not-declared" or
                "model.genai-config-invalid" => "RecommendedQwenConfigurationFailedStatus",
            "model.inference-self-test-failed" => "RecommendedQwenSelfTestFailedStatus",
            "model.inference-self-test-output-mismatch" => "RecommendedQwenSelfTestOutputMismatchStatus",
            "qwen.model-load-failed" => "RecommendedQwenModelLoadFailedStatus",
            "qwen.cuda-provider-load-failed" or
                "qwen.cuda-runtime-unavailable" => "RecommendedQwenCudaUnavailableStatus",
            "qwen.directml-provider-load-failed" or
                "qwen.directml-runtime-unavailable" => "RecommendedQwenDirectMlUnavailableStatus",
            "qwen.model-inspection-failed" => "RecommendedQwenModelInspectionFailedStatus",
            "qwen.processor-load-failed" => "RecommendedQwenProcessorFailedStatus",
            "qwen.image-load-failed" => "RecommendedQwenImageInputFailedStatus",
            "qwen.generator-parameters-failed" => "RecommendedQwenParametersFailedStatus",
            "qwen.input-processing-failed" => "RecommendedQwenInputProcessingFailedStatus",
            "qwen.generator-create-failed" => "RecommendedQwenGeneratorFailedStatus",
            "qwen.input-binding-failed" => "RecommendedQwenInputBindingFailedStatus",
            "qwen.tokenizer-stream-failed" => "RecommendedQwenTokenizerFailedStatus",
            "qwen.output-token-limit-exceeded" => "RecommendedQwenTokenLimitFailedStatus",
            "qwen.output-character-limit-exceeded" or
                "qwen.output-too-large" or
                "qwen.invalid-json" or
                "qwen.schema-version-mismatch" or
                "qwen.invalid-category-id" or
                "qwen.invalid-entity-evidence" or
                "qwen.invalid-language-tag" or
                "qwen.title-empty" or
                "qwen.degenerate-text-output" or
                "qwen.ungrounded-numeric-output" or
                "qwen.schema-validation-failed" => "RecommendedQwenStructuredOutputFailedStatus",
            "qwen.generation-failed" => "RecommendedQwenGenerationFailedStatus",
            "local-worker.native-runtime-missing" => "LocalWorkerNativeRuntimeMissingStatus",
            "nvidia.driver-missing" => "NvidiaDriverMissingStatus",
            "nvidia.cuda-device-missing" => "NvidiaCudaDeviceMissingStatus",
            "nvidia.driver-too-old" => "NvidiaDriverTooOldStatus",
            "nvidia.insufficient-vram" => "NvidiaInsufficientVramStatus",
            "nvidia.cuda-runtime-unsupported" => "NvidiaRuntimeUnsupportedStatus",
            "nvidia.runtime-archive-invalid" or
                "nvidia.runtime-install-incomplete" or
                "nvidia.runtime-install-failed" => "NvidiaRuntimeInstallFailedStatus",
            _ => fallbackResourceName,
        };
        var message = _resources.GetString(resourceName);
        StatusMessage = string.IsNullOrWhiteSpace(errorCode)
            ? message
            : string.Format(
                CultureInfo.CurrentCulture,
                _resources.GetString("ModelOperationErrorCodeFormat"),
                message,
                errorCode);
        IsStatusError = true;
    }

    private static string? ClassifyModelOperationFailure(Exception exception)
    {
        string? classifiedFailure = null;
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is OcrProviderException providerFailure)
            {
                return providerFailure.ErrorCode;
            }

            if (current is IModelOperationFailure failure)
            {
                classifiedFailure = failure.ErrorCode;
            }
            else if (current is UnauthorizedAccessException)
            {
                classifiedFailure = "model.file-access-denied";
            }
            else if (current is IOException)
            {
                classifiedFailure = "model.file-io-failed";
            }
            else if (current is OutOfMemoryException)
            {
                classifiedFailure = "model.insufficient-memory";
            }
        }

        return classifiedFailure;
    }

    private IModelPackageService GetModelPackageService() =>
        _modelPackageServiceAccessor()
        ?? throw new InvalidOperationException("The local model package service is unavailable.");

    private IRecommendedModelService GetRecommendedModelService() =>
        _recommendedModelServiceAccessor()
        ?? throw new InvalidOperationException("The recommended model service is unavailable.");

    private INvidiaCudaEnvironmentService GetNvidiaCudaEnvironmentService() =>
        _nvidiaCudaEnvironmentServiceAccessor()
        ?? throw new InvalidOperationException("The NVIDIA CUDA environment service is unavailable.");

    private void UpdateDownloadProgress(ModelDownloadProgress progress)
    {
        IsModelProgressIndeterminate = progress.Stage != ModelDownloadStage.Downloading
            || progress.TotalBytes <= 0;
        if (!IsModelProgressIndeterminate)
        {
            ModelProgressPercent = Math.Clamp(
                progress.DownloadedBytes * 100d / progress.TotalBytes,
                0,
                100);
        }

        var isNvidiaRuntime = progress.ModelId == "nvidia-cuda-runtime";
        StatusMessage = _resources.GetString(isNvidiaRuntime
            ? progress.Stage switch
            {
                ModelDownloadStage.Preparing => "NvidiaRuntimePreparingStatus",
                ModelDownloadStage.Downloading => "NvidiaRuntimeDownloadingStatus",
                ModelDownloadStage.Verifying => "NvidiaRuntimeVerifyingStatus",
                ModelDownloadStage.Installing => "NvidiaRuntimeInstallingStatus",
                ModelDownloadStage.Completed => "NvidiaRuntimeCompletingStatus",
                _ => "NvidiaRuntimePreparingStatus",
            }
            : progress.Stage switch
            {
                ModelDownloadStage.Preparing => "RecommendedModelPreparingStatus",
                ModelDownloadStage.Downloading => "RecommendedModelDownloadingStatus",
                ModelDownloadStage.Verifying => "RecommendedModelVerifyingStatus",
                ModelDownloadStage.Installing => "RecommendedModelInstallingStatus",
                ModelDownloadStage.Enabling => "RecommendedModelEnablingStatus",
                ModelDownloadStage.Completed => "RecommendedModelCompletingStatus",
                _ => "RecommendedModelPreparingStatus",
            });
        IsStatusError = false;
    }

    private void UpdateLocalInferenceComponentProgress(
        LocalInferenceComponentInstallProgress progress)
    {
        IsModelProgressIndeterminate = progress.Stage
            is not LocalInferenceComponentInstallStage.Downloading
            || progress.TotalBytes <= 0;
        if (!IsModelProgressIndeterminate)
        {
            ModelProgressPercent = Math.Clamp(
                progress.DownloadedBytes * 100d / progress.TotalBytes,
                0,
                100);
        }

        StatusMessage = _resources.GetString(progress.Stage switch
        {
            LocalInferenceComponentInstallStage.Preparing =>
                "LocalInferenceComponentPreparingStatus",
            LocalInferenceComponentInstallStage.Downloading =>
                "LocalInferenceComponentDownloadingStatus",
            LocalInferenceComponentInstallStage.Verifying =>
                "LocalInferenceComponentVerifyingStatus",
            LocalInferenceComponentInstallStage.Installing =>
                "LocalInferenceComponentActivatingStatus",
            LocalInferenceComponentInstallStage.Completed =>
                "LocalInferenceComponentCompletingStatus",
            _ => "LocalInferenceComponentPreparingStatus",
        });
        IsStatusError = false;
    }

    private void ShowLocalInferenceComponentFailure(string errorCode)
    {
        var resourceName = errorCode switch
        {
            "component.insufficient-disk-space" =>
                "LocalInferenceComponentInsufficientSpaceStatus",
            "component.download-timeout" =>
                "LocalInferenceComponentDownloadTimedOutStatus",
            "component.download-network-failed" =>
                "LocalInferenceComponentNetworkFailedStatus",
            "component.signature-invalid" =>
                "LocalInferenceComponentSignatureFailedStatus",
            "component.archive-size-mismatch" or
                "component.archive-hash-mismatch" or
                "component.expanded-size-mismatch" or
                "component.component-manifest-hash-mismatch" or
                "component.release-manifest-invalid" or
                "component.release-manifest-incompatible" or
                "component.download-uri-rejected" =>
                    "LocalInferenceComponentVerificationFailedStatus",
            _ => "LocalInferenceComponentInstallFailedStatus",
        };
        StatusMessage = string.Format(
            CultureInfo.CurrentCulture,
            _resources.GetString("LocalInferenceComponentErrorCodeFormat"),
            _resources.GetString(resourceName),
            errorCode);
        IsStatusError = true;
    }

    private void ApplyNvidiaEnvironmentStatus(NvidiaCudaEnvironmentStatus status)
    {
        NvidiaEnvironmentState = status.State;
        IsNvidiaRuntimeInstallAvailable = status.CanInstallRuntime;
        IsNvidiaDriverHelpVisible = status.State is
            NvidiaCudaEnvironmentState.DriverMissing
            or NvidiaCudaEnvironmentState.DriverTooOld;
        var device = status.Device;
        var deviceName = device?.Name ?? _resources.GetString("NvidiaUnknownDeviceName");
        var vram = device is null ? "—" : FormatBytes(device.DedicatedMemoryBytes);
        var computeCapability = device is null
            ? "—"
            : $"{device.ComputeCapabilityMajor}.{device.ComputeCapabilityMinor}";
        var driverCudaVersion = status.DriverCudaVersion <= 0
            ? "—"
            : $"{status.DriverCudaVersion / 1000}.{status.DriverCudaVersion % 1000 / 10}";
        var source = _resources.GetString(status.RuntimeSource switch
        {
            NvidiaCudaRuntimeSource.AppManaged => "NvidiaRuntimeSourceApp",
            NvidiaCudaRuntimeSource.System => "NvidiaRuntimeSourceSystem",
            _ => "NvidiaRuntimeSourceNone",
        });
        var formatKey = status.State switch
        {
            NvidiaCudaEnvironmentState.Ready => "NvidiaEnvironmentReadyStatusFormat",
            NvidiaCudaEnvironmentState.UnsupportedArchitecture => "NvidiaEnvironmentUnsupportedStatus",
            NvidiaCudaEnvironmentState.DriverMissing => "NvidiaDriverMissingStatus",
            NvidiaCudaEnvironmentState.NoCudaDevice => "NvidiaCudaDeviceMissingStatus",
            NvidiaCudaEnvironmentState.DriverTooOld => "NvidiaDriverTooOldStatusFormat",
            NvidiaCudaEnvironmentState.InsufficientVram => "NvidiaInsufficientVramStatusFormat",
            NvidiaCudaEnvironmentState.RuntimeIncomplete => "NvidiaRuntimeIncompleteStatusFormat",
            _ => "NvidiaRuntimeMissingStatusFormat",
        };
        NvidiaEnvironmentStatusMessage = status.State switch
        {
            NvidiaCudaEnvironmentState.Ready => string.Format(
                CultureInfo.CurrentCulture,
                _resources.GetString(formatKey),
                deviceName,
                vram,
                computeCapability,
                driverCudaVersion,
                source),
            NvidiaCudaEnvironmentState.DriverTooOld => string.Format(
                CultureInfo.CurrentCulture,
                _resources.GetString(formatKey),
                deviceName,
                driverCudaVersion),
            NvidiaCudaEnvironmentState.InsufficientVram => string.Format(
                CultureInfo.CurrentCulture,
                _resources.GetString(formatKey),
                deviceName,
                vram),
            NvidiaCudaEnvironmentState.RuntimeMissing or
                NvidiaCudaEnvironmentState.RuntimeIncomplete => string.Format(
                    CultureInfo.CurrentCulture,
                    _resources.GetString(formatKey),
                    deviceName,
                    vram,
                    driverCudaVersion),
            _ => _resources.GetString(formatKey),
        };
    }

    private static RecommendedModelInstallException CreateNvidiaEnvironmentException(
        NvidiaCudaEnvironmentState state) => new(state switch
        {
            NvidiaCudaEnvironmentState.DriverMissing => "nvidia.driver-missing",
            NvidiaCudaEnvironmentState.NoCudaDevice => "nvidia.cuda-device-missing",
            NvidiaCudaEnvironmentState.DriverTooOld => "nvidia.driver-too-old",
            NvidiaCudaEnvironmentState.InsufficientVram => "nvidia.insufficient-vram",
            _ => "nvidia.cuda-runtime-unsupported",
        });

    private static string FormatBytes(long bytes)
    {
        var value = (double)bytes;
        return value >= 1024 * 1024 * 1024
            ? $"{value / (1024 * 1024 * 1024):0.##} GiB"
            : $"{value / (1024 * 1024):0.##} MiB";
    }

    private void SetRecommendedActionsEnabled(bool enabled)
    {
        // Production always has a component store. UiTest intentionally uses an
        // in-process fake runtime and therefore has no component store to install.
        var hasLocalInferenceRuntime = _localInferenceComponentStoreAccessor() is null
                                       || IsLocalInferenceComponentInstalled;
        foreach (var recommendedModel in RecommendedModels)
        {
            recommendedModel.IsActionEnabled = enabled
                                               && hasLocalInferenceRuntime
                                               && !recommendedModel.Descriptor.IsEnabled;
        }
    }
}
