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
    private int _detailLoadGeneration;
    private int _detailEditGeneration;
    private int _detailSaveGeneration;
    private bool _loadingDetail;
    private Guid? _detailSessionItemId;
    private string _detailTitleBaseline = string.Empty;
    private string _detailSummaryBaseline = string.Empty;
    private string _detailNotesBaseline = string.Empty;
    private string? _pendingTitleFromBackend;
    private string? _pendingSummaryFromBackend;
    private string? _pendingNotesFromBackend;
    private Task<bool>? _detailSaveTask;

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
    public partial string DetailNotes { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSaveDetail))]
    [NotifyPropertyChangedFor(nameof(DetailSaveStatusMessage))]
    [NotifyPropertyChangedFor(nameof(HasDetailSaveStatus))]
    public partial bool IsDetailDirty { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSaveDetail))]
    [NotifyPropertyChangedFor(nameof(CanUseDetailActions))]
    [NotifyPropertyChangedFor(nameof(DetailSaveStatusMessage))]
    [NotifyPropertyChangedFor(nameof(HasDetailSaveStatus))]
    public partial bool IsDetailSaving { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DetailSaveStatusMessage))]
    [NotifyPropertyChangedFor(nameof(HasDetailSaveStatus))]
    public partial bool HasDetailSaveFailure { get; set; }

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
    [NotifyPropertyChangedFor(nameof(CanUseDetailActions))]
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

    public bool CanSaveDetail => IsDetailDirty && !IsDetailSaving;

    public bool CanUseDetailActions => !IsWorking && !IsDetailSaving;

    public string DetailSaveStatusMessage =>
        IsDetailSaving
            ? _resources.GetString("DetailsSavingStatus")
            : HasDetailSaveFailure
                ? _resources.GetString("DetailsSaveFailedStatus")
                : string.Empty;

    public bool HasDetailSaveStatus => !string.IsNullOrWhiteSpace(DetailSaveStatusMessage);

    public async Task InitializeAsync()
    {
        await UpdateStorageStateAsync(forceRetry: false).ConfigureAwait(true);
    }

    [RelayCommand]
    private Task RetryAsync() => UpdateStorageStateAsync(forceRetry: true);

    [RelayCommand]
    private Task SaveDetailAsync() => TrySaveDetailAsync();

    [RelayCommand]
    private Task LoadMoreAsync() =>
        IsWorking ? Task.CompletedTask : LoadItemsAsync(reset: false);

    public async Task<bool> SetSelectedItemAsync(LibraryItem? item)
    {
        if (item is null)
        {
            ClearDetail();
            return true;
        }

        if (SelectedItemId == item.Id && _detailSessionItemId == item.Id)
        {
            return true;
        }

        return await LoadDetailAsync(item.Id).ConfigureAwait(true);
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
        var detailLoadGeneration = _detailLoadGeneration;
        var detailSaveGeneration = _detailSaveGeneration;
        var saveWasInProgress = IsDetailSaving;
        await library.SetCategoryAssignmentAsync(imageItemId, option.Id, isAssigned).ConfigureAwait(true);
        option.IsAssigned = isAssigned;
        var entry = await library.GetAsync(imageItemId).ConfigureAwait(true);
        if (entry is not null
            && entry.Item.DeletedAtUtc is null
            && SelectedItemId == imageItemId
            && detailLoadGeneration == _detailLoadGeneration
            && detailSaveGeneration == _detailSaveGeneration
            && !saveWasInProgress
            && !IsDetailSaving)
        {
            UpdateDisplayedItem(entry);
            await ApplyDetailAsync(entry).ConfigureAwait(true);
        }
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
        var successfulIds = new HashSet<Guid>();
        var deleted = 0;
        foreach (var imageItemId in uniqueIds)
        {
            try
            {
                await GetLibrary().SoftDeleteAsync(imageItemId).ConfigureAwait(true);
                successfulIds.Add(imageItemId);
                deleted++;
            }
            catch
            {
                // Each item is an independent transaction. Continue so one failure does
                // not prevent already-confirmed items from moving to the recycle bin.
            }
        }

        if (SelectedItemId is Guid selectedId && successfulIds.Contains(selectedId))
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

    public async Task<bool> RefreshAndSelectAsync(Guid imageItemId)
    {
        await LoadItemsAsync(reset: true).ConfigureAwait(true);
        var item = Items.FirstOrDefault(candidate => candidate.Id == imageItemId);
        return item is not null && await SetSelectedItemAsync(item).ConfigureAwait(true);
    }

    public Task RefreshItemsAsync() => LoadItemsAsync(reset: true);

    public async Task RefreshAnalysisResultAsync(Guid imageItemId)
    {
        var detailLoadGeneration = _detailLoadGeneration;
        var detailSaveGeneration = _detailSaveGeneration;
        var saveWasInProgress = IsDetailSaving;
        var entry = await GetLibrary().GetAsync(imageItemId).ConfigureAwait(true);
        if (entry is null || entry.Item.DeletedAtUtc is not null)
        {
            return;
        }

        if (detailSaveGeneration != _detailSaveGeneration
            || saveWasInProgress
            || IsDetailSaving)
        {
            return;
        }

        UpdateDisplayedItem(entry);

        if (SelectedItemId == imageItemId
            && detailLoadGeneration == _detailLoadGeneration
            && detailSaveGeneration == _detailSaveGeneration)
        {
            await ApplyDetailAsync(entry).ConfigureAwait(true);
        }
    }

    public void ShowStatus(string message)
    {
        StatusMessage = message ?? string.Empty;
    }

    public void CloseDetail() => ClearDetail();

    public Task<bool> TrySaveDetailAsync()
    {
        if (_detailSaveTask is { IsCompleted: false } activeTask)
        {
            return activeTask;
        }

        _detailSaveTask = null;

        var task = SaveDetailCoreAsync();
        _detailSaveTask = task;
        _ = ClearCompletedSaveTaskAsync(task);
        return task;
    }

    partial void OnSearchTextChanged(string value)
    {
        _itemsLoadGeneration++;
        _searchCancellation?.Cancel();
        _searchCancellation?.Dispose();
        _searchCancellation = new CancellationTokenSource();
        _ = DebouncedSearchAsync(_searchCancellation.Token);
    }

    partial void OnDetailTitleChanged(string value) => MarkDetailFieldChanged(DetailField.Title);

    partial void OnDetailSummaryChanged(string value) => MarkDetailFieldChanged(DetailField.Summary);

    partial void OnDetailNotesChanged(string value) => MarkDetailFieldChanged(DetailField.Notes);

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

    private async Task<bool> LoadDetailAsync(Guid imageItemId)
    {
        var loadGeneration = ++_detailLoadGeneration;
        var saveGeneration = _detailSaveGeneration;
        var saveWasInProgress = IsDetailSaving;
        var entry = await GetLibrary().GetAsync(imageItemId).ConfigureAwait(true);
        if (!IsCurrentDetailLoad(loadGeneration)
            || saveGeneration != _detailSaveGeneration
            || saveWasInProgress
            || IsDetailSaving)
        {
            return false;
        }

        if (entry is null || entry.Item.DeletedAtUtc is not null)
        {
            if (SelectedItemId == imageItemId)
            {
                ClearDetail();
            }

            return false;
        }

        await ApplyDetailAsync(entry).ConfigureAwait(true);
        return true;
    }

    private Task ApplyDetailAsync(LibraryEntry entry, bool replaceEditSession = false)
    {
        _loadingDetail = true;
        try
        {
            SelectedItemId = entry.Item.Id;
            if (replaceEditSession || _detailSessionItemId != entry.Item.Id)
            {
                BeginDetailSession(entry);
            }
            else
            {
                MergeDetailField(
                    DetailField.Title,
                    entry.Item.Title);
                MergeDetailField(
                    DetailField.Summary,
                    entry.Item.Summary);
                MergeDetailField(
                    DetailField.Notes,
                    entry.Item.Notes);
            }

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

        UpdateDetailDirtyState();

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

    private async Task<bool> SaveDetailCoreAsync()
    {
        if (SelectedItemId is not Guid imageItemId)
        {
            return true;
        }

        UpdateDetailDirtyState();
        if (!IsDetailDirty)
        {
            return true;
        }

        var loadGeneration = ++_detailLoadGeneration;
        var editGeneration = _detailEditGeneration;
        _detailSaveGeneration++;
        var update = CreateDetailUpdate();
        IsDetailSaving = true;
        HasDetailSaveFailure = false;
        try
        {
            var library = GetLibrary();
            await library.UpdateDetailFieldsAsync(
                imageItemId,
                update).ConfigureAwait(true);

            LibraryEntry? entry;
            try
            {
                entry = await library.GetAsync(imageItemId).ConfigureAwait(true);
            }
            catch
            {
                if (IsCurrentDetailOperation(imageItemId, loadGeneration))
                {
                    CommitSubmittedValues(update);
                    ShowStatus(_resources.GetString("DetailsSavedRefreshFailedStatus"));
                }

                return true;
            }

            if (!IsCurrentDetailOperation(imageItemId, loadGeneration))
            {
                return true;
            }

            if (entry is null || entry.Item.DeletedAtUtc is not null)
            {
                ClearDetail();
                ShowStatus(_resources.GetString("DetailsSavedStatus"));
                return true;
            }

            UpdateDisplayedItem(entry);
            await ApplyDetailAsync(
                    entry,
                    replaceEditSession: editGeneration == _detailEditGeneration)
                .ConfigureAwait(true);
            ShowStatus(_resources.GetString("DetailsSavedStatus"));
            return true;
        }
        catch
        {
            if (IsCurrentDetailOperation(imageItemId, loadGeneration))
            {
                HasDetailSaveFailure = true;
                ShowStatus(_resources.GetString("DetailsSaveFailedStatus"));
            }

            return false;
        }
        finally
        {
            IsDetailSaving = false;
        }
    }

    private async Task ClearCompletedSaveTaskAsync(Task<bool> task)
    {
        try
        {
            await task.ConfigureAwait(true);
        }
        catch
        {
            // SaveDetailCoreAsync reports failures through the view model. Keep the
            // shared task from becoming an unobserved exception if a command caller
            // does not await it.
        }

        if (ReferenceEquals(_detailSaveTask, task))
        {
            _detailSaveTask = null;
        }
    }

    private ImageDetailUpdate CreateDetailUpdate() => new(
        IsFieldDirty(DetailField.Title) ? DetailTitle : null,
        IsFieldDirty(DetailField.Summary) ? DetailSummary : null,
        IsFieldDirty(DetailField.Notes) ? DetailNotes : null);

    private void CommitSubmittedValues(ImageDetailUpdate update)
    {
        _loadingDetail = true;
        try
        {
            CommitSubmittedValue(
                DetailField.Title,
                update.Title,
                NormalizeTitleForComparison);
            CommitSubmittedValue(
                DetailField.Summary,
                update.Summary,
                NormalizeSummaryForComparison);
            CommitSubmittedValue(
                DetailField.Notes,
                update.Notes,
                NormalizeNotesForComparison);
        }
        finally
        {
            _loadingDetail = false;
        }

        UpdateDetailDirtyState();
    }

    private void CommitSubmittedValue(
        DetailField field,
        string? submittedValue,
        Func<string, string> normalize)
    {
        if (submittedValue is null)
        {
            return;
        }

        var actualValue = normalize(submittedValue);
        SetBaselineValue(field, actualValue);
        SetPendingBackendValue(field, null);
    }

    private void BeginDetailSession(LibraryEntry entry)
    {
        _detailSessionItemId = entry.Item.Id;
        _detailEditGeneration++;
        _detailTitleBaseline = entry.Item.Title;
        _detailSummaryBaseline = entry.Item.Summary;
        _detailNotesBaseline = entry.Item.Notes ?? string.Empty;
        _pendingTitleFromBackend = null;
        _pendingSummaryFromBackend = null;
        _pendingNotesFromBackend = null;
        DetailTitle = entry.Item.Title;
        DetailSummary = entry.Item.Summary;
        DetailNotes = entry.Item.Notes ?? string.Empty;
        HasDetailSaveFailure = false;
    }

    private void MergeDetailField(DetailField field, string? backendValue)
    {
        backendValue ??= string.Empty;
        if (IsFieldAtBaseline(field))
        {
            SetFieldValue(field, backendValue);
            SetBaselineValue(field, backendValue);
            SetPendingBackendValue(field, null);
            return;
        }

        SetPendingBackendValue(field, backendValue);
    }

    private void UpdateDetailDirtyState()
    {
        var isDirty = IsFieldDirty(DetailField.Title)
            || IsFieldDirty(DetailField.Summary)
            || IsFieldDirty(DetailField.Notes);
        IsDetailDirty = isDirty;
    }

    private bool IsCurrentDetailOperation(Guid imageItemId, int loadGeneration) =>
        SelectedItemId == imageItemId && loadGeneration == _detailLoadGeneration;

    private bool IsCurrentDetailLoad(int loadGeneration) =>
        loadGeneration == _detailLoadGeneration;

    private bool IsFieldDirty(DetailField field) =>
        !AreEquivalent(field, GetFieldValue(field), GetBaselineValue(field));

    private bool IsFieldAtBaseline(DetailField field) =>
        !IsFieldDirty(field);

    private static bool AreEquivalent(DetailField field, string current, string baseline) =>
        GetNormalizer(field)(current) == GetNormalizer(field)(baseline);

    private static Func<string, string> GetNormalizer(DetailField field) => field switch
    {
        DetailField.Title => NormalizeTitleForComparison,
        DetailField.Summary => NormalizeSummaryForComparison,
        DetailField.Notes => NormalizeNotesForComparison,
        _ => throw new ArgumentOutOfRangeException(nameof(field)),
    };

    private static string NormalizeTitleForComparison(string value) => value.Trim();

    private static string NormalizeSummaryForComparison(string value) => value.Trim();

    private static string NormalizeNotesForComparison(string value) =>
        string.IsNullOrWhiteSpace(value) ? string.Empty : value;

    private string GetFieldValue(DetailField field) => field switch
    {
        DetailField.Title => DetailTitle,
        DetailField.Summary => DetailSummary,
        DetailField.Notes => DetailNotes,
        _ => throw new ArgumentOutOfRangeException(nameof(field)),
    };

    private void SetFieldValue(DetailField field, string value)
    {
        switch (field)
        {
            case DetailField.Title:
                DetailTitle = value;
                break;
            case DetailField.Summary:
                DetailSummary = value;
                break;
            case DetailField.Notes:
                DetailNotes = value;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(field));
        }
    }

    private string GetBaselineValue(DetailField field) => field switch
    {
        DetailField.Title => _detailTitleBaseline,
        DetailField.Summary => _detailSummaryBaseline,
        DetailField.Notes => _detailNotesBaseline,
        _ => throw new ArgumentOutOfRangeException(nameof(field)),
    };

    private void SetBaselineValue(DetailField field, string value)
    {
        switch (field)
        {
            case DetailField.Title:
                _detailTitleBaseline = value;
                break;
            case DetailField.Summary:
                _detailSummaryBaseline = value;
                break;
            case DetailField.Notes:
                _detailNotesBaseline = value;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(field));
        }
    }

    private string? GetPendingBackendValue(DetailField field) => field switch
    {
        DetailField.Title => _pendingTitleFromBackend,
        DetailField.Summary => _pendingSummaryFromBackend,
        DetailField.Notes => _pendingNotesFromBackend,
        _ => throw new ArgumentOutOfRangeException(nameof(field)),
    };

    private void SetPendingBackendValue(DetailField field, string? value)
    {
        switch (field)
        {
            case DetailField.Title:
                _pendingTitleFromBackend = value;
                break;
            case DetailField.Summary:
                _pendingSummaryFromBackend = value;
                break;
            case DetailField.Notes:
                _pendingNotesFromBackend = value;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(field));
        }
    }

    private void UpdateDisplayedItem(LibraryEntry entry)
    {
        var displayedItem = Items.FirstOrDefault(item => item.Id == entry.Item.Id);
        if (displayedItem is null)
        {
            return;
        }

        var updatedItem = MapEntry(entry);
        displayedItem.UpdateDisplayContent(
            updatedItem.Title,
            updatedItem.Summary,
            updatedItem.AnalysisState,
            updatedItem.CategorySummary);
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
        _detailLoadGeneration++;
        _detailEditGeneration++;
        _loadingDetail = true;
        try
        {
            _detailSessionItemId = null;
            _detailTitleBaseline = string.Empty;
            _detailSummaryBaseline = string.Empty;
            _detailNotesBaseline = string.Empty;
            _pendingTitleFromBackend = null;
            _pendingSummaryFromBackend = null;
            _pendingNotesFromBackend = null;
            SelectedItemId = null;
            DetailTitle = string.Empty;
            DetailSummary = string.Empty;
            DetailNotes = string.Empty;
            DetailImageUri = null;
            DetailFileName = string.Empty;
            DetailMetadata = string.Empty;
            IsDetailDirty = false;
            HasDetailSaveFailure = false;
            CategoryOptions.Clear();
        }
        finally
        {
            _loadingDetail = false;
        }
    }

    private void MarkDetailFieldChanged(DetailField field)
    {
        if (_loadingDetail || SelectedItemId is null)
        {
            return;
        }

        _detailEditGeneration++;
        HasDetailSaveFailure = false;
        if (IsFieldAtBaseline(field) && GetPendingBackendValue(field) is { } pendingValue)
        {
            _loadingDetail = true;
            try
            {
                SetFieldValue(field, pendingValue);
                SetBaselineValue(field, pendingValue);
                SetPendingBackendValue(field, null);
            }
            finally
            {
                _loadingDetail = false;
            }
        }

        UpdateDetailDirtyState();
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

    private enum DetailField
    {
        Title,
        Summary,
        Notes,
    }
}
