using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Windows.ApplicationModel.Resources;
using PicForLater.Core.Analysis;

namespace PicForLater.App.Models;

public sealed record ModelPackageOption(
    string? PackageKey,
    string DisplayName,
    string Description);

public sealed record InferenceAccelerationOptionItem(InferenceAccelerationMode Mode)
{
    public static InferenceAccelerationOptionItem FromMode(InferenceAccelerationMode mode) => new(mode);
}

public partial class RecommendedModelItem : ObservableObject
{
    private static readonly ResourceLoader _resources = new();

    private RecommendedModelItem(
        RecommendedModelDescriptor descriptor,
        string description,
        string details,
        string hardware,
        string licenseAndSource,
        string benchmarkStatus,
        string actionText,
        string confirmationMessage)
    {
        Descriptor = descriptor;
        Description = description;
        Details = details;
        Hardware = hardware;
        LicenseAndSource = licenseAndSource;
        BenchmarkStatus = benchmarkStatus;
        ActionText = actionText;
        ConfirmationMessage = confirmationMessage;
        IsActionEnabled = !descriptor.IsEnabled;
    }

    public RecommendedModelDescriptor Descriptor { get; }

    public string Id => Descriptor.Id;

    public bool RequiresNvidiaCudaRuntime =>
        Id == "qwen3-vl-2b-instruct-picforlater-q4f16-cuda";

    public string Name => _resources.GetString(Id switch
    {
        "pp-ocrv6-small-official-onnx" => "RecommendedPpOcrDisplayName",
        "qwen3-vl-2b-instruct-picforlater-q4f32-cpu" => "RecommendedQwenCpuDisplayName",
        "qwen3-vl-2b-instruct-picforlater-q4f16-cuda" => "RecommendedQwenCudaDisplayName",
        _ => "RecommendedLocalModelDisplayName",
    });

    public string TechnicalName => Descriptor.Name;

    public string DetailsAutomationId => $"RecommendedModelDetails_{Id}";

    public string AutomationId => Descriptor.Id switch
    {
        "pp-ocrv6-small-official-onnx" => "DownloadPpOcrModelButton",
        "qwen3-vl-2b-instruct-picforlater-q4f32-cpu" => "DownloadQwenCpuModelButton",
        "qwen3-vl-2b-instruct-picforlater-q4f16-cuda" => "DownloadQwenCudaModelButton",
        _ => $"DownloadModelButton_{Descriptor.Id}",
    };

    public string Description { get; }

    public string Details { get; }

    public string Hardware { get; }

    public string LicenseAndSource { get; }

    public string BenchmarkStatus { get; }

    public string ActionText { get; }

    public string ConfirmationMessage { get; }

    [ObservableProperty]
    public partial bool IsActionEnabled { get; set; }

    public static RecommendedModelItem FromDescriptor(RecommendedModelDescriptor descriptor)
    {
        var descriptionKey = descriptor.Id switch
        {
            "qwen3-vl-2b-instruct-picforlater-q4f32-cpu" => "RecommendedQwenCpuDescription",
            "qwen3-vl-2b-instruct-picforlater-q4f16-cuda" => "RecommendedQwenCudaDescription",
            _ => "RecommendedPpOcrDescription",
        };
        var hardwareGuidanceKey = descriptor.Id switch
        {
            "qwen3-vl-2b-instruct-picforlater-q4f32-cpu" => "RecommendedQwenCpuHardwareGuidance",
            "qwen3-vl-2b-instruct-picforlater-q4f16-cuda" => "RecommendedQwenCudaHardwareGuidance",
            _ => "RecommendedPpOcrHardwareGuidance",
        };
        var hardwareGuidance = _resources.GetString(hardwareGuidanceKey);
        var actionText = descriptor.IsEnabled
            ? _resources.GetString("RecommendedModelEnabledAction")
            : descriptor.IsInstalled
                ? _resources.GetString("RecommendedModelEnableAction")
                : _resources.GetString("RecommendedModelDownloadAction");
        var confirmationKey = descriptor.IsInstalled
            ? "RecommendedModelEnableConfirmationFormat"
            : "RecommendedModelDownloadConfirmationFormat";
        var benchmarkStatus = descriptor.Id == "qwen3-vl-2b-instruct-picforlater-q4f32-cpu"
            ? _resources.GetString("RecommendedQwenCpuBenchmarkStatus")
            : descriptor.Id == "qwen3-vl-2b-instruct-picforlater-q4f16-cuda"
                ? _resources.GetString("RecommendedQwenCudaBenchmarkStatus")
                : descriptor.IsExperimental
                    ? _resources.GetString("RecommendedModelExperimentalStatus")
                    : _resources.GetString("RecommendedModelArtifactPinnedStatus");
        return new RecommendedModelItem(
            descriptor,
            _resources.GetString(descriptionKey),
            Format(
                "RecommendedModelDetailsFormat",
                descriptor.Version,
                FormatBytes(descriptor.DownloadBytes),
                FormatBytes(descriptor.InstalledBytes)),
            Format(
                "RecommendedModelHardwareFormat",
                FormatBytes(descriptor.MinRamBytes),
                hardwareGuidance),
            Format("ModelPackageLicenseSourceFormat", descriptor.License, descriptor.SourceUrl),
            benchmarkStatus,
            actionText,
            Format(
                confirmationKey,
                descriptor.Name,
                FormatBytes(descriptor.DownloadBytes),
                FormatBytes(descriptor.InstalledBytes),
                descriptor.License,
                descriptor.SourceUrl,
                FormatBytes(descriptor.MinRamBytes),
                hardwareGuidance));
    }

    private static string Format(string resourceName, params object[] arguments) => string.Format(
        System.Globalization.CultureInfo.CurrentCulture,
        _resources.GetString(resourceName),
        arguments);

    private static string FormatBytes(long bytes)
    {
        var value = (double)bytes;
        if (value >= 1024 * 1024 * 1024)
        {
            return $"{value / (1024 * 1024 * 1024):0.##} GiB";
        }

        return $"{value / (1024 * 1024):0.##} MiB";
    }
}

public sealed record InstalledModelPackageItem(
    string PackageKey,
    string DisplayName,
    string Name,
    string Details,
    string Capabilities,
    string Languages,
    string Hardware,
    string LicenseAndSource,
    string BenchmarkStatus)
{
    private static readonly ResourceLoader _resources = new();

    public string DetailsAutomationId => $"InstalledModelDetails_{PackageKey}";

    public static InstalledModelPackageItem FromPackage(InstalledModelPackage package)
    {
        var manifest = package.Manifest;
        return new InstalledModelPackageItem(
            package.PackageKey,
            manifest.Id,
            $"{manifest.Id} {manifest.Version}",
            Format(
                "ModelPackageDetailsFormat",
                manifest.Architecture,
                manifest.Quantization.ToUpperInvariant(),
                FormatBytes(manifest.DownloadBytes),
                FormatBytes(manifest.InstalledBytes)),
            Format(
                "ModelPackageCapabilitiesFormat",
                string.Join(", ", manifest.Capabilities.Select(GetCapabilityName))),
            Format(
                "ModelPackageLanguagesFormat",
                string.Join(", ", manifest.InputLanguages),
                string.Join(", ", manifest.OutputLanguages),
                string.Join(", ", manifest.Scripts)),
            Format(
                "ModelPackageHardwareFormat",
                FormatBytes(manifest.MinRamBytes),
                manifest.RecommendedHardware,
                string.Join(", ", manifest.SupportedExecutionProviders ?? ["CPU"])),
            Format("ModelPackageLicenseSourceFormat", manifest.License, manifest.SourceUrl),
            package.BenchmarkStatus == "SelfTestPassed"
                ? _resources.GetString("ModelPackageSelfTestPassedStatus")
                : Format("ModelPackageBenchmarkStatusFormat", package.BenchmarkStatus));
    }

    private static string GetCapabilityName(ModelCapability capability) => capability switch
    {
        ModelCapability.Ocr => _resources.GetString("ModelCapabilityOcrName"),
        ModelCapability.VisionCaption => _resources.GetString("ModelCapabilityVisionCaptionName"),
        ModelCapability.TextComposition => _resources.GetString("ModelCapabilityTextCompositionName"),
        ModelCapability.EntityExtraction => _resources.GetString("ModelCapabilityEntityExtractionName"),
        _ => capability.ToString(),
    };

    private static string Format(string resourceName, params object[] arguments) => string.Format(
        System.Globalization.CultureInfo.CurrentCulture,
        _resources.GetString(resourceName),
        arguments);

    private static string FormatBytes(long bytes)
    {
        var value = (double)bytes;
        string unit;
        if (value >= 1024 * 1024 * 1024)
        {
            value /= 1024 * 1024 * 1024;
            unit = "GiB";
        }
        else
        {
            value /= 1024 * 1024;
            unit = "MiB";
        }

        return $"{value:0.#} {unit}";
    }
}
