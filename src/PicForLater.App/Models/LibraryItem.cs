using CommunityToolkit.Mvvm.ComponentModel;
using PicForLater.Core.Images;

namespace PicForLater.App.Models;

public sealed partial class LibraryItem : ObservableObject
{
    public LibraryItem(
        Guid id,
        string title,
        string summary,
        AnalysisState analysisState,
        string thumbnailUri,
        string originalFileName,
        string categorySummary,
        string createdDisplay,
        string sizeDisplay = "")
    {
        Id = id;
        Title = title;
        Summary = summary;
        AnalysisState = analysisState;
        ThumbnailUri = thumbnailUri;
        OriginalFileName = originalFileName;
        CategorySummary = categorySummary;
        CreatedDisplay = createdDisplay;
        SizeDisplay = sizeDisplay;
    }

    public Guid Id { get; }

    [ObservableProperty]
    public partial string Title { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ListSummary))]
    public partial string Summary { get; private set; }

    [ObservableProperty]
    public partial AnalysisState AnalysisState { get; private set; }

    public string ThumbnailUri { get; }

    public string OriginalFileName { get; }

    [ObservableProperty]
    public partial string CategorySummary { get; private set; }

    public string CreatedDisplay { get; }

    public string SizeDisplay { get; }

    public string AutomationId => $"LibraryItem_{Id:N}";

    public string ListSummary => string.IsNullOrWhiteSpace(Summary) ? OriginalFileName : Summary;

    public void UpdateDisplayContent(
        string title,
        string summary,
        AnalysisState analysisState,
        string categorySummary)
    {
        Title = title;
        Summary = summary;
        AnalysisState = analysisState;
        CategorySummary = categorySummary;
    }
}
