using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Windows.ApplicationModel.Resources;
using PicForLater.App.Models;
using PicForLater.App.Services;
using PicForLater.Core.Analysis;
using PicForLater.Core.Images;
using PicForLater.Core.Library;
using PicForLater.Infrastructure.Storage;

namespace PicForLater.App.ViewModels;

public partial class LibraryPageViewModel : ObservableObject
{
    private static readonly ResourceLoader _resources = new();
    private readonly IStorageReadinessService _storageReadinessService;
    private readonly Func<ILibraryService?> _libraryAccessor;
    private readonly Func<AppDataPaths?> _pathsAccessor;
    private readonly Func<IAnalysisReanalysisService?> _reanalysisAccessor;
    private CancellationTokenSource? _searchCancellation;
    private int _itemsLoadGeneration;
    private bool _loadingDetail;

    public LibraryPageViewModel(
        IStorageReadinessService storageReadinessService,
        Func<ILibraryService?> libraryAccessor,
        Func<AppDataPaths?> pathsAccessor,
        Func<IAnalysisReanalysisService?> reanalysisAccessor)
    {
        _storageReadinessService = storageReadinessService
            ?? throw new ArgumentNullException(nameof(storageReadinessService));
        _libraryAccessor = libraryAccessor ?? throw new ArgumentNullException(nameof(libraryAccessor));
        _pathsAccessor = pathsAccessor ?? throw new ArgumentNullException(nameof(pathsAccessor));
        _reanalysisAccessor = reanalysisAccessor ?? throw new ArgumentNullException(nameof(reanalysisAccessor));
    }

    public ObservableCollection<LibraryItem> Items { get; } = [];

    public ObservableCollection<CategoryFilterOption> CategoryFilters { get; } = [];

    public ObservableCollection<CategoryOption> CategoryOptions { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLoading))]
    [NotifyPropertyChangedFor(nameof(IsEmpty))]
    [NotifyPropertyChangedFor(nameof(HasItems))]
    [NotifyPropertyChangedFor(nameof(HasError))]
    [NotifyPropertyChangedFor(nameof(IsPermissionDenied))]
    [NotifyPropertyChangedFor(nameof(IsUnsupported))]
    public partial LibraryViewState State { get; set; } = LibraryViewState.Loading;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    public partial int SelectedCount { get; set; }

    [ObservableProperty]
    public partial bool IsSelectionModeActive { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDetail))]
    public partial Guid? SelectedItemId { get; set; }

    [ObservableProperty]
    public partial string SearchText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial CategoryFilterOption? SelectedCategoryFilter { get; set; }

    [ObservableProperty]
    public partial string DetailTitle { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string DetailSummary { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string DetailContentSourceMessage { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsDetailDirty { get; set; }

    [ObservableProperty]
    public partial string? DetailImageUri { get; set; }

    [ObservableProperty]
    public partial string DetailFileName { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string DetailMetadata { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStatusMessage))]
    public partial string StatusMessage { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string? LastErrorCode { get; set; }

    [ObservableProperty]
    public partial bool HasMore { get; set; }

    [ObservableProperty]
    public partial bool IsWorking { get; set; }

    [ObservableProperty]
    public partial LibrarySortField SortField { get; set; } = LibrarySortField.CreatedAt;

    [ObservableProperty]
    public partial LibrarySortDirection SortDirection { get; set; } = LibrarySortDirection.Descending;

    public bool IsLoading => State == LibraryViewState.Loading;

    public bool IsEmpty => State == LibraryViewState.Empty;

    public bool HasItems => State == LibraryViewState.Ready;

    public bool HasError => State == LibraryViewState.Error;

    public bool IsPermissionDenied => State == LibraryViewState.PermissionDenied;

    public bool IsUnsupported => State == LibraryViewState.Unsupported;

    public bool HasSelection => SelectedCount > 0;

    public bool HasDetail => SelectedItemId is not null;

    public bool HasStatusMessage => !string.IsNullOrWhiteSpace(StatusMessage);

    public async Task InitializeAsync()
    {
        await UpdateStorageStateAsync(forceRetry: false).ConfigureAwait(true);
    }

    [RelayCommand]
    private Task RetryAsync() => UpdateStorageStateAsync(forceRetry: true);

    [RelayCommand]
    private Task SaveDetailAsync() => SaveDetailCoreAsync();

    [RelayCommand]
    private Task LoadMoreAsync() =>
        IsWorking ? Task.CompletedTask : LoadItemsAsync(reset: false);

    public async Task SetSelectedItemAsync(LibraryItem? item)
    {
        if (item is null)
        {
            ClearDetail();
            return;
        }

        await LoadDetailAsync(item.Id).ConfigureAwait(true);
    }

    public void SetSelectionMode(bool isActive)
    {
        IsSelectionModeActive = isActive;
        SelectedCount = 0;
    }

    public void SetSelectedCount(int count)
    {
        SelectedCount = Math.Max(0, count);
    }

    public Task ApplySortAsync(LibrarySortField field, LibrarySortDirection direction)
    {
        if (!Enum.IsDefined(field))
        {
            throw new ArgumentOutOfRangeException(nameof(field));
        }

        if (!Enum.IsDefined(direction))
        {
            throw new ArgumentOutOfRangeException(nameof(direction));
        }

        SortField = field;
        SortDirection = direction;
        return LoadItemsAsync(reset: true);
    }

    public Task ApplyCategoryFilterAsync(CategoryFilterOption? filter)
    {
        SelectedCategoryFilter = filter;
        return LoadItemsAsync(reset: true);
    }

    public async Task SetCategoryAssignmentAsync(CategoryOption option, bool isAssigned)
    {
        ArgumentNullException.ThrowIfNull(option);
        if (SelectedItemId is not Guid imageItemId)
        {
            return;
        }

        var library = GetLibrary();
        await library.SetCategoryAssignmentAsync(imageItemId, option.Id, isAssigned).ConfigureAwait(true);
        option.IsAssigned = isAssigned;
        await LoadItemsAsync(reset: true).ConfigureAwait(true);
        await LoadDetailAsync(imageItemId).ConfigureAwait(true);
    }

    public async Task<CategoryFilterOption> CreateCategoryAsync(string name)
    {
        var category = await GetLibrary().CreateCategoryAsync(name).ConfigureAwait(true);
        await LoadCategoriesAsync().ConfigureAwait(true);
        if (SelectedItemId is Guid imageItemId)
        {
            await LoadDetailAsync(imageItemId).ConfigureAwait(true);
        }

        return CategoryFilters.First(filter => filter.Id == category.Id);
    }

    public async Task RenameCategoryAsync(Guid categoryId, string name)
    {
        await GetLibrary().RenameCategoryAsync(categoryId, name).ConfigureAwait(true);
        await LoadCategoriesAsync().ConfigureAwait(true);
        await LoadItemsAsync(reset: true).ConfigureAwait(true);
        if (SelectedItemId is Guid imageItemId)
        {
            await LoadDetailAsync(imageItemId).ConfigureAwait(true);
        }
    }

    public async Task DeleteCategoryAsync(Guid categoryId)
    {
        await GetLibrary().DeleteCategoryAsync(categoryId).ConfigureAwait(true);
        await LoadCategoriesAsync().ConfigureAwait(true);
        await LoadItemsAsync(reset: true).ConfigureAwait(true);
        if (SelectedItemId is Guid imageItemId)
        {
            await LoadDetailAsync(imageItemId).ConfigureAwait(true);
        }
    }

    public async Task SoftDeleteSelectedAsync()
    {
        if (SelectedItemId is not Guid imageItemId)
        {
            return;
        }

        await GetLibrary().SoftDeleteAsync(imageItemId).ConfigureAwait(true);
        ClearDetail();
        await LoadItemsAsync(reset: true).ConfigureAwait(true);
    }

    public async Task<int> SoftDeleteItemsAsync(IReadOnlyCollection<Guid> imageItemIds)
    {
        ArgumentNullException.ThrowIfNull(imageItemIds);
        var uniqueIds = imageItemIds.Distinct().ToArray();
        var deleted = 0;
        foreach (var imageItemId in uniqueIds)
        {
            try
            {
                await GetLibrary().SoftDeleteAsync(imageItemId).ConfigureAwait(true);
                deleted++;
            }
            catch
            {
                // Each item is an independent transaction. Continue so one failure does
                // not prevent already-confirmed items from moving to the recycle bin.
            }
        }

        if (SelectedItemId is Guid selectedId && uniqueIds.Contains(selectedId))
        {
            ClearDetail();
        }

        await LoadItemsAsync(reset: true).ConfigureAwait(true);
        return deleted;
    }

    public async Task<ReanalysisQueueResult> QueueReanalysisAsync(
        IReadOnlyCollection<Guid> imageItemIds)
    {
        ArgumentNullException.ThrowIfNull(imageItemIds);
        var service = _reanalysisAccessor()
            ?? throw new InvalidOperationException("The reanalysis service is unavailable.");
        IsWorking = true;
        try
        {
            var result = await service.QueueAsync(imageItemIds).ConfigureAwait(true);
            await LoadItemsAsync(reset: true).ConfigureAwait(true);
            return result;
        }
        finally
        {
            IsWorking = false;
        }
    }

    public async Task RefreshAndSelectAsync(Guid imageItemId)
    {
        await LoadItemsAsync(reset: true).ConfigureAwait(true);
        var item = Items.FirstOrDefault(candidate => candidate.Id == imageItemId);
        if (item is not null)
        {
            await SetSelectedItemAsync(item).ConfigureAwait(true);
        }
    }

    public Task RefreshItemsAsync() => LoadItemsAsync(reset: true);

    public async Task RefreshAnalysisResultAsync(Guid imageItemId)
    {
        var entry = await GetLibrary().GetAsync(imageItemId).ConfigureAwait(true);
        if (entry is null || entry.Item.DeletedAtUtc is not null)
        {
            return;
        }

        var itemIndex = Items
            .Select((item, index) => (item, index))
            .FirstOrDefault(candidate => candidate.item.Id == imageItemId)
            .index;
        if (itemIndex >= 0 && itemIndex < Items.Count && Items[itemIndex].Id == imageItemId)
        {
            Items[itemIndex] = MapEntry(entry);
        }

        if (SelectedItemId == imageItemId)
        {
            await ApplyDetailAsync(entry, preserveUserInput: IsDetailDirty).ConfigureAwait(true);
        }
    }

    public void ShowStatus(string message)
    {
        StatusMessage = message ?? string.Empty;
    }

    public void CloseDetail() => ClearDetail();

    partial void OnSearchTextChanged(string value)
    {
        _itemsLoadGeneration++;
        _searchCancellation?.Cancel();
        _searchCancellation?.Dispose();
        _searchCancellation = new CancellationTokenSource();
        _ = DebouncedSearchAsync(_searchCancellation.Token);
    }

    partial void OnDetailTitleChanged(string value) => MarkDetailDirty();

    partial void OnDetailSummaryChanged(string value) => MarkDetailDirty();

    private async Task DebouncedSearchAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(250, cancellationToken).ConfigureAwait(true);
            await LoadItemsAsync(reset: true, cancellationToken).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task UpdateStorageStateAsync(bool forceRetry)
    {
        State = LibraryViewState.Loading;
        LastErrorCode = null;
        var readiness = await _storageReadinessService.EnsureReadyAsync(forceRetry).ConfigureAwait(true);
        LastErrorCode = readiness.ErrorCode;
        if (readiness.Status != StorageReadinessStatus.Ready)
        {
            State = readiness.Status switch
            {
                StorageReadinessStatus.PermissionDenied => LibraryViewState.PermissionDenied,
                StorageReadinessStatus.Unsupported => LibraryViewState.Unsupported,
                _ => LibraryViewState.Error,
            };
            return;
        }

        await LoadCategoriesAsync().ConfigureAwait(true);
        await LoadItemsAsync(reset: true).ConfigureAwait(true);
    }

    private async Task LoadCategoriesAsync()
    {
        var selectedId = SelectedCategoryFilter?.Id;
        var library = GetLibrary();
        var categories = await Task.Run(() => library.GetCategoriesAsync()).ConfigureAwait(true);
        CategoryFilters.Clear();
        CategoryFilters.Add(new CategoryFilterOption(null, _resources.GetString("AllCategories")));
        foreach (var category in categories)
        {
            CategoryFilters.Add(new CategoryFilterOption(category.Id, category.Name));
        }

        SelectedCategoryFilter = CategoryFilters.FirstOrDefault(filter => filter.Id == selectedId)
            ?? CategoryFilters[0];
    }

    private async Task LoadItemsAsync(bool reset, CancellationToken cancellationToken = default)
    {
        var library = _libraryAccessor();
        if (library is null)
        {
            State = LibraryViewState.Error;
            LastErrorCode = "LibraryUnavailable";
            return;
        }

        var loadGeneration = reset ? ++_itemsLoadGeneration : _itemsLoadGeneration;
        var offset = reset ? 0 : Items.Count;
        var query = new LibraryQuery(
            SearchText: SearchText,
            CategoryId: SelectedCategoryFilter?.Id,
            IsDeleted: false,
            SortField: SortField,
            SortDirection: SortDirection,
            Offset: offset,
            Limit: 100);
        IsWorking = true;
        try
        {
            var result = await Task.Run(
                () => library.QueryAsync(query, cancellationToken),
                cancellationToken).ConfigureAwait(true);
            if (loadGeneration != _itemsLoadGeneration)
            {
                return;
            }

            if (reset)
            {
                Items.Clear();
            }

            foreach (var entry in result.Items)
            {
                Items.Add(MapEntry(entry));
            }

            HasMore = result.HasMore;
            State = Items.Count == 0 ? LibraryViewState.Empty : LibraryViewState.Ready;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            if (loadGeneration == _itemsLoadGeneration)
            {
                State = LibraryViewState.Error;
                LastErrorCode = "LibraryQueryFailed";
            }
        }
        finally
        {
            if (loadGeneration == _itemsLoadGeneration)
            {
                IsWorking = false;
            }
        }
    }

    private async Task LoadDetailAsync(Guid imageItemId)
    {
        var entry = await GetLibrary().GetAsync(imageItemId).ConfigureAwait(true);
        if (entry is null || entry.Item.DeletedAtUtc is not null)
        {
            ClearDetail();
            return;
        }

        await ApplyDetailAsync(entry, preserveUserInput: false).ConfigureAwait(true);
    }

    private Task ApplyDetailAsync(LibraryEntry entry, bool preserveUserInput)
    {
        _loadingDetail = true;
        try
        {
            SelectedItemId = entry.Item.Id;
            if (!preserveUserInput)
            {
                DetailTitle = entry.Item.Title;
                DetailSummary = entry.Item.Summary;
                IsDetailDirty = false;
            }

            DetailContentSourceMessage = preserveUserInput
                ? _resources.GetString("DetailContentSourceUnsaved")
                : GetDetailContentSourceMessage(entry.Item);
            DetailImageUri = ToUri(entry.Asset.ThumbnailRelativePath ?? entry.Asset.OriginalRelativePath);
            DetailFileName = entry.Item.OriginalFileName;
            DetailMetadata = string.Format(
                CultureInfo.CurrentCulture,
                _resources.GetString("ImageMetadataFormat"),
                entry.Asset.PixelWidth,
                entry.Asset.PixelHeight,
                FormatBytes(entry.Asset.ByteLength));
        }
        finally
        {
            _loadingDetail = false;
        }

        var assignedIds = entry.Categories.Select(assignment => assignment.Category.Id).ToHashSet();
        CategoryOptions.Clear();
        foreach (var category in CategoryFilters.Where(category => category.Id is not null))
        {
            CategoryOptions.Add(new CategoryOption(
                category.Id!.Value,
                category.Name,
                assignedIds.Contains(category.Id.Value)));
        }

        return Task.CompletedTask;
    }

    private async Task SaveDetailCoreAsync()
    {
        if (SelectedItemId is not Guid imageItemId)
        {
            return;
        }

        await GetLibrary().UpdateUserFieldsAsync(
            imageItemId,
            DetailTitle,
            DetailSummary).ConfigureAwait(true);
        ShowStatus(_resources.GetString("DetailsSavedStatus"));
        await LoadItemsAsync(reset: true).ConfigureAwait(true);
        await LoadDetailAsync(imageItemId).ConfigureAwait(true);
    }

    private LibraryItem MapEntry(LibraryEntry entry)
    {
        var categorySummary = entry.Categories.Count == 0
            ? _resources.GetString("Uncategorized")
            : string.Join(", ", entry.Categories.Select(category => category.Category.Name));
        return new LibraryItem(
            entry.Item.Id,
            entry.Item.Title,
            entry.Item.Summary,
            entry.Item.AnalysisState,
            ToUri(entry.Asset.ThumbnailRelativePath ?? entry.Asset.OriginalRelativePath),
            entry.Item.OriginalFileName,
            categorySummary,
#if PICFORLATER_UI_VISUAL_FIXTURE
            UiTestVisualFixtureSeeder.FormatDisplayTime(entry.Item.CreatedAtUtc),
#else
            entry.Item.CreatedAtUtc.ToLocalTime().ToString("g", CultureInfo.CurrentCulture),
#endif
            FormatBytes(entry.Asset.ByteLength));
    }

    private string ToUri(ManagedRelativePath relativePath)
    {
        var paths = _pathsAccessor() ?? throw new InvalidOperationException("Managed paths are unavailable.");
        return new Uri(paths.Resolve(relativePath)).AbsoluteUri;
    }

    private ILibraryService GetLibrary() =>
        _libraryAccessor() ?? throw new InvalidOperationException("The local library is unavailable.");

    private void ClearDetail()
    {
        _loadingDetail = true;
        try
        {
            SelectedItemId = null;
            DetailTitle = string.Empty;
            DetailSummary = string.Empty;
            DetailContentSourceMessage = string.Empty;
            DetailImageUri = null;
            DetailFileName = string.Empty;
            DetailMetadata = string.Empty;
            IsDetailDirty = false;
            CategoryOptions.Clear();
        }
        finally
        {
            _loadingDetail = false;
        }
    }

    private void MarkDetailDirty()
    {
        if (_loadingDetail || SelectedItemId is null)
        {
            return;
        }

        IsDetailDirty = true;
        DetailContentSourceMessage = _resources.GetString("DetailContentSourceUnsaved");
    }

    private static string GetDetailContentSourceMessage(ImageItem item)
    {
        if (item.TitleSource == ContentFieldSource.User
            || item.SummarySource == ContentFieldSource.User)
        {
            return _resources.GetString("DetailContentSourceUser");
        }

        if (item.TitleSource == ContentFieldSource.ModelSuggested
            || item.SummarySource == ContentFieldSource.ModelSuggested)
        {
            return _resources.GetString("DetailContentSourceModel");
        }

        return item.AnalysisState switch
        {
            AnalysisState.Completed => _resources.GetString("DetailContentSourceOcr"),
            AnalysisState.NeedsAttention => _resources.GetString("DetailContentSourceNeedsAttention"),
            _ => _resources.GetString("DetailContentSourcePending"),
        };
    }

    private static string FormatBytes(long bytes)
    {
        string[] units =
        [
            _resources.GetString("ByteUnitBytes"),
            _resources.GetString("ByteUnitKilobytes"),
            _resources.GetString("ByteUnitMegabytes"),
            _resources.GetString("ByteUnitGigabytes"),
        ];
        var value = (double)bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return string.Format(CultureInfo.CurrentCulture, "{0:0.#} {1}", value, units[unit]);
    }
}
