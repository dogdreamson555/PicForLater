using PicForLater.Core.Images;

namespace PicForLater.App.Models;

public sealed record LibraryItem(
    Guid Id,
    string Title,
    string Summary,
    AnalysisState AnalysisState,
    string ThumbnailUri,
    string OriginalFileName,
    string CategorySummary,
    string CreatedDisplay,
    string SizeDisplay = "")
{
    public string AutomationId => $"LibraryItem_{Id:N}";

    public string ListSummary => string.IsNullOrWhiteSpace(Summary) ? OriginalFileName : Summary;
}
