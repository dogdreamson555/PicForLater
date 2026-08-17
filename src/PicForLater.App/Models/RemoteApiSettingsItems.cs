namespace PicForLater.App.Models;

using PicForLater.Core.Analysis;

public sealed record RemoteApiProviderOption(
    string ProfileId,
    string DisplayName,
    RemoteApiProviderCategory Category,
    bool IsCustom,
    string PricingUrl,
    string RetentionResourceName,
    string ModelSuggestion,
    IReadOnlyList<RemoteReasoningMode> SupportedReasoningModes);

public sealed record RemoteReasoningOption(
    RemoteReasoningMode Mode,
    string DisplayName);

public enum RemoteApiProviderCategory
{
    InternationalOfficial,
    ChinaOfficial,
    Aggregator,
    LocalPrivate,
    Custom,
}

public sealed record RemoteApiCategoryOption(
    RemoteApiProviderCategory Category,
    string DisplayName);

public enum SettingsStatusKind
{
    Informational,
    Success,
    Warning,
    Error,
}
