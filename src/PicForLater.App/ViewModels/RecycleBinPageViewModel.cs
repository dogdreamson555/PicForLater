using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Windows.ApplicationModel.Resources;
using PicForLater.App.Models;
using PicForLater.App.Services;
using PicForLater.Core.Images;
using PicForLater.Core.Library;
using PicForLater.Infrastructure.Storage;

namespace PicForLater.App.ViewModels;

public partial class RecycleBinPageViewModel : ObservableObject
{
    private const int PageSize = 100;
    private static readonly ResourceLoader _resources = new();
    private readonly IStorageReadinessService _readiness;
    private readonly Func<ILibraryService?> _libraryAccessor;
    private readonly Func<AppDataPaths?> _pathsAccessor;

    public RecycleBinPageViewModel(
        IStorageReadinessService readiness,
        Func<ILibraryService?> libraryAccessor,
        Func<AppDataPaths?> pathsAccessor)
    {
        _readiness = readiness;
        _libraryAccessor = libraryAccessor;
        _pathsAccessor = pathsAccessor;
    }

    public ObservableCollection<LibraryItem> Items { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLoading))]
    [NotifyPropertyChangedFor(nameof(IsEmpty))]
    [NotifyPropertyChangedFor(nameof(HasItems))]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial LibraryViewState State { get; set; } = LibraryViewState.Loading;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    public partial LibraryItem? SelectedItem { get; set; }

    [ObservableProperty]
    public partial bool IsSelectionModeActive { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    [NotifyPropertyChangedFor(nameof(SelectionSummary))]
    public partial int SelectedCount { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStatusMessage))]
    public partial string StatusMessage { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool HasMore { get; set; }

    [ObservableProperty]
    public partial bool IsWorking { get; set; }

    public bool IsLoading => State == LibraryViewState.Loading;

    public bool IsEmpty => State == LibraryViewState.Empty;

    public bool HasItems => State == LibraryViewState.Ready;

    public bool HasError => State is LibraryViewState.Error or LibraryViewState.PermissionDenied or LibraryViewState.Unsupported;

    public bool HasSelection => SelectedItem is not null || SelectedCount > 0;

    public string SelectionSummary => string.Format(
        CultureInfo.CurrentCulture,
        _resources.GetString("RecycleBinSelectionCountFormat"),
        SelectedCount);

    public bool HasStatusMessage => !string.IsNullOrWhiteSpace(StatusMessage);

    public async Task InitializeAsync()
    {
        await UpdateStorageStateAsync(forceRetry: false).ConfigureAwait(true);
    }

    [RelayCommand]
    private Task RetryAsync() => UpdateStorageStateAsync(forceRetry: true);

    [RelayCommand]
    private Task LoadMoreAsync() =>
        IsWorking || !HasMore ? Task.CompletedTask : LoadPageAsync(reset: false);

    private async Task UpdateStorageStateAsync(bool forceRetry)
    {
        State = LibraryViewState.Loading;
        var readiness = await _readiness.EnsureReadyAsync(forceRetry).ConfigureAwait(true);
        if (readiness.Status != StorageReadinessStatus.Ready || _libraryAccessor() is null)
        {
            State = readiness.Status switch
            {
                StorageReadinessStatus.PermissionDenied => LibraryViewState.PermissionDenied,
                StorageReadinessStatus.Unsupported => LibraryViewState.Unsupported,
                _ => LibraryViewState.Error,
            };
            return;
        }

        await LoadPageAsync(reset: true).ConfigureAwait(true);
    }

    public async Task RestoreSelectedAsync()
    {
        if (SelectedItem is null)
        {
            return;
        }

        await RestoreItemsAsync([SelectedItem.Id]).ConfigureAwait(true);
    }

    public async Task<PermanentDeleteResult?> PermanentlyDeleteSelectedAsync()
    {
        if (SelectedItem is null)
        {
            return null;
        }

        var loadedCount = Items.Count;
        var result = await GetLibrary().PermanentlyDeleteAsync(SelectedItem.Id).ConfigureAwait(true);
        var completedCount = result.Status == PermanentDeleteStatus.Completed ? 1 : 0;
        SetPermanentDeleteStatus(completedCount, 1);
        SetSelectionMode(isActive: false);
        await ReloadToDepthAsync(Math.Max(PageSize, loadedCount - completedCount)).ConfigureAwait(true);
        return result;
    }

    public void SetSelectionMode(bool isActive)
    {
        IsSelectionModeActive = isActive;
        SelectedItem = null;
        SelectedCount = 0;
    }

    public void SetSelectedCount(int count)
    {
        SelectedCount = Math.Max(0, count);
    }

    public async Task<int> RestoreItemsAsync(IReadOnlyCollection<Guid> imageItemIds)
    {
        var ids = imageItemIds.Distinct().ToArray();
        if (ids.Length == 0)
        {
            return 0;
        }

        var loadedCount = Items.Count;
        var restoredCount = 0;
        foreach (var id in ids)
        {
            await GetLibrary().RestoreAsync(id).ConfigureAwait(true);
            restoredCount++;
        }

        StatusMessage = ids.Length == 1
            ? _resources.GetString("RestoreCompletedStatus")
            : string.Format(
                CultureInfo.CurrentCulture,
                _resources.GetString("RestoreBatchCompletedStatusFormat"),
                restoredCount,
                ids.Length);
        SetSelectionMode(isActive: false);
        await ReloadToDepthAsync(Math.Max(PageSize, loadedCount - restoredCount)).ConfigureAwait(true);
        return restoredCount;
    }

    public async Task<int> PermanentlyDeleteItemsAsync(IReadOnlyCollection<Guid> imageItemIds)
    {
        var ids = imageItemIds.Distinct().ToArray();
        if (ids.Length == 0)
        {
            return 0;
        }

        var loadedCount = Items.Count;
        var completedCount = 0;
        foreach (var id in ids)
        {
            var result = await GetLibrary().PermanentlyDeleteAsync(id).ConfigureAwait(true);
            if (result.Status == PermanentDeleteStatus.Completed)
            {
                completedCount++;
            }
        }

        SetPermanentDeleteStatus(completedCount, ids.Length);
        SetSelectionMode(isActive: false);
        await ReloadToDepthAsync(Math.Max(PageSize, loadedCount - completedCount)).ConfigureAwait(true);
        return completedCount;
    }

    private async Task ReloadToDepthAsync(int targetCount)
    {
        await LoadPageAsync(reset: true).ConfigureAwait(true);
        while (State == LibraryViewState.Ready
               && HasMore
               && Items.Count < targetCount)
        {
            var previousCount = Items.Count;
            await LoadPageAsync(reset: false).ConfigureAwait(true);
            if (Items.Count == previousCount)
            {
                break;
            }
        }
    }

    private async Task LoadPageAsync(bool reset)
    {
        var previousHasMore = HasMore;
        IsWorking = true;
        try
        {
            var library = GetLibrary();
            var offset = reset ? 0 : Items.Count;
            var query = new LibraryQuery(IsDeleted: true, Offset: offset, Limit: PageSize);
            var result = await Task.Run(() => library.QueryAsync(query)).ConfigureAwait(true);

            if (reset)
            {
                Items.Clear();
                SelectedItem = null;
                SelectedCount = 0;
            }

            var existingIds = Items.Select(item => item.Id).ToHashSet();
            foreach (var entry in result.Items)
            {
                if (existingIds.Add(entry.Item.Id))
                {
                    Items.Add(MapEntry(entry));
                }
            }

            HasMore = result.HasMore;
            State = Items.Count == 0 ? LibraryViewState.Empty : LibraryViewState.Ready;
        }
        catch
        {
            if (reset || Items.Count == 0)
            {
                HasMore = false;
                State = LibraryViewState.Error;
            }
            else
            {
                HasMore = previousHasMore;
                StatusMessage = _resources.GetString("RecycleBinLoadMoreFailedStatus");
            }
        }
        finally
        {
            IsWorking = false;
        }
    }

    private LibraryItem MapEntry(LibraryEntry entry)
    {
        var relativePath = entry.Asset.ThumbnailRelativePath ?? entry.Asset.OriginalRelativePath;
        var absolutePath = (_pathsAccessor()
            ?? throw new InvalidOperationException("Managed paths are unavailable."))
            .Resolve(relativePath);
        return new LibraryItem(
            entry.Item.Id,
            entry.Item.Title,
            entry.Item.Summary,
            entry.Item.AnalysisState,
            new Uri(absolutePath).AbsoluteUri,
            entry.Item.OriginalFileName,
            entry.Categories.Count == 0
                ? _resources.GetString("Uncategorized")
                : string.Join(", ", entry.Categories.Select(category => category.Category.Name)),
#if PICFORLATER_UI_VISUAL_FIXTURE
            entry.Item.DeletedAtUtc is { } deletedAtUtc
                ? UiTestVisualFixtureSeeder.FormatDisplayTime(deletedAtUtc)
                : string.Empty);
#else
            entry.Item.DeletedAtUtc?.ToLocalTime().ToString("g", CultureInfo.CurrentCulture) ?? string.Empty);
#endif
    }

    private ILibraryService GetLibrary() =>
        _libraryAccessor() ?? throw new InvalidOperationException("The local library is unavailable.");

    private void SetPermanentDeleteStatus(int completedCount, int totalCount)
    {
        StatusMessage = totalCount == 1
            ? completedCount == 1
                ? _resources.GetString("PermanentDeleteCompletedStatus")
                : _resources.GetString("PermanentDeleteRetryStatus")
            : completedCount == totalCount
                ? string.Format(
                    CultureInfo.CurrentCulture,
                    _resources.GetString("PermanentDeleteBatchCompletedStatusFormat"),
                    totalCount)
            : string.Format(
                CultureInfo.CurrentCulture,
                _resources.GetString("PermanentDeleteBatchPartialStatusFormat"),
                completedCount,
                totalCount);
    }
}
