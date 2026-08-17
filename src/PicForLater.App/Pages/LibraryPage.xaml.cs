using System.ComponentModel;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.Windows.ApplicationModel.Resources;
using Microsoft.Windows.Storage.Pickers;
using PicForLater.Analysis;
using PicForLater.App.Models;
using PicForLater.App.ViewModels;
using PicForLater.Core.Images;
using PicForLater.Core.Library;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;

namespace PicForLater.App.Pages;

public sealed partial class LibraryPage : Page
{
    private const double LibrarySplitViewMinimumWidth = 960;
    private const double LibraryDetailMinimumWidth = 360;
    private const double LibraryDetailMaximumWidth = 420;
    private const double LibraryDetailWidthRatio = 0.4;
    private const double LibraryGridCardMinimumWidth = 196;
    private const double LibraryGridCardGap = 12;
    private const double LibraryGridCardHeight = 240;
    private static readonly Thickness DetailTitleDefaultPadding = new(10, 4, 6, 5);
    private static readonly Thickness DetailTitlePureCjkPadding = new(10, 5, 6, 4);
    private static readonly ResourceLoader _resources = new();
    private readonly HashSet<Guid> _selectedItemIds = [];
    private bool _synchronizingSelection;
    private AnalysisQueueWakeSignal? _analysisUpdatesSource;
    private bool _viewModelSubscribed = true;
    private int _loadGeneration;
    private LibraryDisplayMode _displayMode = LibraryDisplayMode.Grid;
    private Guid? _navigationTargetImageItemId;
    private ItemsWrapGrid? _libraryGridItemsPanel;

    private ListViewBase ActiveCollection =>
        _displayMode == LibraryDisplayMode.Grid ? LibraryGridView : LibraryListView;

    public LibraryPageViewModel ViewModel { get; } = new(
        App.StorageReadiness,
        () => App.Library,
        () => App.DataPaths,
        () => App.Reanalysis);

    public LibraryPage()
    {
        InitializeComponent();
        ViewModel.PropertyChanged += ViewModel_PropertyChanged;
        Loaded += LibraryPage_Loaded;
        Unloaded += LibraryPage_Unloaded;
    }

    public static Visibility BoolToVisibility(bool value) =>
        value ? Visibility.Visible : Visibility.Collapsed;

    public static bool Not(bool value) => !value;

    public static ImageSource? ToImageSource(string? uri) =>
        string.IsNullOrWhiteSpace(uri) ? null : new BitmapImage(new Uri(uri));

    public static string GetAnalysisStatus(AnalysisState state) =>
        _resources.GetString($"AnalysisState{state}");

    public static string GetAnalysisStateAutomationId(Guid id) => $"AnalysisState_{id:N}";

    public static string GetCategoryRenameAutomationId(Guid id) => $"RenameCategory_{id:N}";

    public static string GetCategoryDeleteAutomationId(Guid id) => $"DeleteCategory_{id:N}";

    public static string GetContextOpenAutomationId(Guid id) => $"ContextOpen_{id:N}";

    public static string GetContextDetailsAutomationId(Guid id) => $"ContextDetails_{id:N}";

    public static string GetContextAddReminderAutomationId(Guid id) => $"ContextAddReminder_{id:N}";

    public static string GetContextReanalyzeAutomationId(Guid id) => $"ContextReanalyze_{id:N}";

    public static string GetContextSelectAutomationId(Guid id) => $"ContextSelect_{id:N}";

    public static string GetContextDeleteAutomationId(Guid id) => $"ContextDelete_{id:N}";

    public static string GetSelectionSummary(int count) => string.Format(
        System.Globalization.CultureInfo.CurrentCulture,
        _resources.GetString("SelectionSummaryFormat"),
        count);

    private async void LibraryPage_Loaded(object sender, RoutedEventArgs e)
    {
        var loadGeneration = ++_loadGeneration;
        if (!_viewModelSubscribed)
        {
            ViewModel.PropertyChanged += ViewModel_PropertyChanged;
            _viewModelSubscribed = true;
        }

        await ViewModel.InitializeAsync();
        if (!IsCurrentLoad(loadGeneration))
        {
            return;
        }

        if (_navigationTargetImageItemId is Guid imageItemId)
        {
            await ViewModel.RefreshAndSelectAsync(imageItemId);
            if (!IsCurrentLoad(loadGeneration))
            {
                return;
            }

            _navigationTargetImageItemId = null;
        }

        TrySubscribeAnalysisUpdates();
        UpdateSortMenuChecks();
        UpdateResponsiveLayout();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (e.Parameter is string value && Guid.TryParse(value, out var imageItemId))
        {
            _navigationTargetImageItemId = imageItemId;
        }
    }

    private void LibraryPage_Unloaded(object sender, RoutedEventArgs e)
    {
        _loadGeneration++;
        if (_viewModelSubscribed)
        {
            ViewModel.PropertyChanged -= ViewModel_PropertyChanged;
            _viewModelSubscribed = false;
        }

        UnsubscribeAnalysisUpdates();
    }

    private void AnalysisUpdates_ItemChanged(object? sender, AnalysisItemChangedEventArgs e)
    {
        if (sender is not AnalysisQueueWakeSignal source)
        {
            return;
        }

        var loadGeneration = _loadGeneration;
        DispatcherQueue.TryEnqueue(async () =>
        {
            if (!IsCurrentLoad(loadGeneration)
                || !ReferenceEquals(source, _analysisUpdatesSource))
            {
                return;
            }

            try
            {
                await ViewModel.RefreshAnalysisResultAsync(e.ImageItemId);
                if (!IsCurrentLoad(loadGeneration)
                    || !ReferenceEquals(source, _analysisUpdatesSource))
                {
                    return;
                }

                RestoreCollectionSelection();
                UpdateResponsiveLayout();
            }
            catch
            {
                // SQLite remains authoritative. Navigation or an explicit refresh
                // can recover a view update that raced page teardown.
            }
        });
    }

    private void TrySubscribeAnalysisUpdates()
    {
        if (!IsLoaded)
        {
            return;
        }

        var source = App.AnalysisUpdates;
        if (ReferenceEquals(source, _analysisUpdatesSource))
        {
            return;
        }

        UnsubscribeAnalysisUpdates();
        _analysisUpdatesSource = source;
        if (source is not null)
        {
            source.ItemChanged += AnalysisUpdates_ItemChanged;
        }
    }

    private void UnsubscribeAnalysisUpdates()
    {
        var source = _analysisUpdatesSource;
        _analysisUpdatesSource = null;
        if (source is not null)
        {
            source.ItemChanged -= AnalysisUpdates_ItemChanged;
        }
    }

    private bool IsCurrentLoad(int loadGeneration) =>
        IsLoaded && loadGeneration == _loadGeneration;

    private async void LibraryCollection_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (ViewModel.IsSelectionModeActive)
        {
            return;
        }

        if (e.ClickedItem is LibraryItem item)
        {
            await ViewModel.SetSelectedItemAsync(item);
            SynchronizeSingleSelection(item);
            UpdateResponsiveLayout();
        }
    }

    private void LibraryGridView_ContainerContentChanging(
        ListViewBase sender,
        ContainerContentChangingEventArgs args)
    {
        if (args.ItemContainer is not FrameworkElement container
            || args.Item is not LibraryItem item)
        {
            return;
        }

        AutomationProperties.SetAutomationId(container, item.AutomationId);
        AutomationProperties.SetName(container, item.Title);
    }

    private void LibraryCollection_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_synchronizingSelection
            || !ViewModel.IsSelectionModeActive
            || !ReferenceEquals(sender, ActiveCollection))
        {
            return;
        }

        _selectedItemIds.Clear();
        foreach (var item in ActiveCollection.SelectedItems.OfType<LibraryItem>())
        {
            _selectedItemIds.Add(item.Id);
        }

        ViewModel.SetSelectedCount(_selectedItemIds.Count);
    }

    private async void CategoryFilterComboBox_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        if (!IsLoaded)
        {
            return;
        }

        await ViewModel.ApplyCategoryFilterAsync(
            CategoryFilterComboBox.SelectedItem as CategoryFilterOption);
    }

    private async void ClearFiltersButton_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.SearchText = string.Empty;
        if (ViewModel.CategoryFilters.Count > 0)
        {
            CategoryFilterComboBox.SelectedItem = ViewModel.CategoryFilters[0];
            await ViewModel.ApplyCategoryFilterAsync(ViewModel.CategoryFilters[0]);
        }
    }

    private void SelectionModeButton_Click(object sender, RoutedEventArgs e)
    {
        SetSelectionMode(isActive: true);
    }

    private void SelectAllLoadedButton_Click(object sender, RoutedEventArgs e)
    {
        if (!ViewModel.IsSelectionModeActive)
        {
            return;
        }

        ActiveCollection.SelectAll();
        CaptureActiveSelection();
    }

    private async void DeleteSelectedButton_Click(object sender, RoutedEventArgs e)
    {
        var selectedItems = ViewModel.Items
            .Where(item => _selectedItemIds.Contains(item.Id))
            .ToArray();
        await ConfirmBatchSoftDeleteAsync(selectedItems);
    }

    private async void ReanalyzeSelectedButton_Click(object sender, RoutedEventArgs e)
    {
        var selectedItems = ViewModel.Items
            .Where(item => _selectedItemIds.Contains(item.Id))
            .ToArray();
        await ConfirmReanalysisAsync(selectedItems, exitSelectionMode: true);
    }

    private void CancelSelectionButton_Click(object sender, RoutedEventArgs e)
    {
        SetSelectionMode(isActive: false);
    }

    private async void SortMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleMenuFlyoutItem { Tag: string choice })
        {
            return;
        }

        var sort = choice switch
        {
            "CreatedDescending" => (LibrarySortField.CreatedAt, LibrarySortDirection.Descending),
            "CreatedAscending" => (LibrarySortField.CreatedAt, LibrarySortDirection.Ascending),
            "TitleAscending" => (LibrarySortField.Title, LibrarySortDirection.Ascending),
            "TitleDescending" => (LibrarySortField.Title, LibrarySortDirection.Descending),
            "SizeDescending" => (LibrarySortField.ByteLength, LibrarySortDirection.Descending),
            "SizeAscending" => (LibrarySortField.ByteLength, LibrarySortDirection.Ascending),
            "CategoryAscending" => (LibrarySortField.Category, LibrarySortDirection.Ascending),
            "CategoryDescending" => (LibrarySortField.Category, LibrarySortDirection.Descending),
            _ => throw new InvalidOperationException("Unknown library sort choice."),
        };
        await ViewModel.ApplySortAsync(sort.Item1, sort.Item2);
        UpdateSortMenuChecks();
        RestoreCollectionSelection();
    }

    private void GridViewModeButton_Click(object sender, RoutedEventArgs e)
    {
        SetDisplayMode(LibraryDisplayMode.Grid);
    }

    private void ListViewModeButton_Click(object sender, RoutedEventArgs e)
    {
        SetDisplayMode(LibraryDisplayMode.List);
    }

    private async void ContextOpenImageMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (GetContextItem(sender) is { } item)
        {
            await OpenImageAsync(item.Id);
        }
    }

    private async void ContextViewDetailsMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (GetContextItem(sender) is not { } item)
        {
            return;
        }

        SetSelectionMode(isActive: false);
        await ViewModel.SetSelectedItemAsync(item);
        SynchronizeSingleSelection(item);
        UpdateResponsiveLayout();
    }

    private async void ContextAddReminderMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (GetContextItem(sender) is { } item)
        {
            await OpenReminderEditorAsync(item.Id);
        }
    }

    private async void ContextReanalyzeMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (GetContextItem(sender) is not { } item)
        {
            return;
        }

        if (ViewModel.IsSelectionModeActive && _selectedItemIds.Contains(item.Id))
        {
            var selectedItems = ViewModel.Items
                .Where(candidate => _selectedItemIds.Contains(candidate.Id))
                .ToArray();
            await ConfirmReanalysisAsync(selectedItems, exitSelectionMode: true);
            return;
        }

        await ConfirmReanalysisAsync([item], exitSelectionMode: false);
    }

    private void ContextSelectMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (GetContextItem(sender) is not { } item)
        {
            return;
        }

        SetSelectionMode(isActive: true);
        _synchronizingSelection = true;
        try
        {
            ActiveCollection.SelectedItems.Add(item);
        }
        finally
        {
            _synchronizingSelection = false;
        }

        CaptureActiveSelection();
    }

    private async void ContextDeleteMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (GetContextItem(sender) is not { } item)
        {
            return;
        }

        if (ViewModel.IsSelectionModeActive && _selectedItemIds.Contains(item.Id))
        {
            var selectedItems = ViewModel.Items
                .Where(candidate => _selectedItemIds.Contains(candidate.Id))
                .ToArray();
            await ConfirmBatchSoftDeleteAsync(selectedItems);
            return;
        }

        await ConfirmSingleSoftDeleteAsync(item.Id, item.Title);
    }

    private async void DetailImageButton_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedItemId is Guid imageItemId)
        {
            await OpenImageAsync(imageItemId);
        }
    }

    private async void DetailAddReminderButton_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedItemId is Guid imageItemId)
        {
            await OpenReminderEditorAsync(imageItemId);
        }
    }

    private async Task OpenReminderEditorAsync(Guid imageItemId)
    {
        if (ViewModel.SelectedItemId == imageItemId && ViewModel.IsDetailDirty)
        {
            await ViewModel.SaveDetailCommand.ExecuteAsync(null);
        }

        App.RequestReminderCreation(imageItemId);
    }

    private async void ImportButton_Click(object sender, RoutedEventArgs e)
    {
        SetSelectionMode(isActive: false);
        var picker = new FileOpenPicker(XamlRoot.ContentIslandEnvironment.AppWindowId)
        {
            SuggestedStartLocation = PickerLocationId.PicturesLibrary,
            CommitButtonText = _resources.GetString("ImportPickerCommitText"),
        };
        picker.FileTypeFilter.Add(".png");
        picker.FileTypeFilter.Add(".jpg");
        picker.FileTypeFilter.Add(".jpeg");
        picker.FileTypeFilter.Add(".webp");

        var files = await picker.PickMultipleFilesAsync();
        if (files.Count > 0)
        {
            await ImportFilePathsAsync(files.Select(file => file.Path));
        }
    }

    private async void PasteButton_Click(object sender, RoutedEventArgs e)
    {
        SetSelectionMode(isActive: false);
        var data = Clipboard.GetContent();
        if (data.Contains(StandardDataFormats.Bitmap))
        {
            try
            {
                var reference = await data.GetBitmapAsync();
                using var randomAccessStream = await reference.OpenReadAsync();
                using var source = randomAccessStream.AsStreamForRead();
                await using var png = await App.ImageProcessor.NormalizeToPngAsync(source);
                var fileName = string.Format(
                    System.Globalization.CultureInfo.CurrentCulture,
                    _resources.GetString("ClipboardFileNameFormat"),
                    DateTime.Now);
                await ImportSingleAsync(
                    png,
                    fileName,
                    ImageSourceKind.Clipboard,
                    ManagedImageFormat.Png);
            }
            catch (Exception)
            {
                ViewModel.ShowStatus(_resources.GetString("ClipboardImportFailedStatus"));
            }

            return;
        }

        if (data.Contains(StandardDataFormats.StorageItems))
        {
            var items = await data.GetStorageItemsAsync();
            await ImportFilePathsAsync(items.OfType<StorageFile>().Select(file => file.Path));
            return;
        }

        ViewModel.ShowStatus(_resources.GetString("ClipboardNoImageStatus"));
    }

    private void LibraryPage_DragOver(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            e.AcceptedOperation = DataPackageOperation.None;
            return;
        }

        e.AcceptedOperation = DataPackageOperation.Copy;
        e.DragUIOverride.Caption = _resources.GetString("DropCaption");
        e.DragUIOverride.IsCaptionVisible = true;
        e.DragUIOverride.IsGlyphVisible = true;
        DropOverlay.Visibility = Visibility.Visible;
    }

    private void LibraryPage_DragLeave(object sender, DragEventArgs e)
    {
        DropOverlay.Visibility = Visibility.Collapsed;
    }

    private async void LibraryPage_Drop(object sender, DragEventArgs e)
    {
        SetSelectionMode(isActive: false);
        DropOverlay.Visibility = Visibility.Collapsed;
        if (!e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            return;
        }

        var items = await e.DataView.GetStorageItemsAsync();
        await ImportFilePathsAsync(items.OfType<StorageFile>().Select(file => file.Path));
    }

    private async Task ImportFilePathsAsync(IEnumerable<string> paths)
    {
        var imported = 0;
        var duplicates = 0;
        var failed = 0;
        ViewModel.IsWorking = true;
        try
        {
            foreach (var path in paths)
            {
                if (!TryGetExpectedFormat(path, out var expectedFormat))
                {
                    failed++;
                    continue;
                }

                try
                {
                    await using var stream = new FileStream(
                        path,
                        new FileStreamOptions
                        {
                            Mode = FileMode.Open,
                            Access = FileAccess.Read,
                            Share = FileShare.Read,
                            Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
                        });
                    var result = await ImportSingleCoreAsync(
                        stream,
                        Path.GetFileName(path),
                        ImageSourceKind.File,
                        expectedFormat);
                    if (result.Status == ImageImportStatus.Imported)
                    {
                        imported++;
                    }
                    else
                    {
                        duplicates++;
                    }
                }
                catch (Exception)
                {
                    failed++;
                }
            }
        }
        finally
        {
            ViewModel.IsWorking = false;
        }

        ViewModel.ShowStatus(string.Format(
            System.Globalization.CultureInfo.CurrentCulture,
            _resources.GetString("ImportBatchStatusFormat"),
            imported,
            duplicates,
            failed));
    }

    private async Task ImportSingleAsync(
        Stream stream,
        string fileName,
        ImageSourceKind sourceKind,
        ManagedImageFormat expectedFormat)
    {
        ViewModel.IsWorking = true;
        try
        {
            var result = await ImportSingleCoreAsync(stream, fileName, sourceKind, expectedFormat);
            ViewModel.ShowStatus(result.Status == ImageImportStatus.Imported
                ? _resources.GetString("ImportCompletedStatus")
                : _resources.GetString("ImportDuplicateStatus"));
        }
        catch (ImageImportException)
        {
            ViewModel.ShowStatus(_resources.GetString("ImportFailedStatus"));
        }
        finally
        {
            ViewModel.IsWorking = false;
        }
    }

    private async Task<ImageImportResult> ImportSingleCoreAsync(
        Stream stream,
        string fileName,
        ImageSourceKind sourceKind,
        ManagedImageFormat expectedFormat)
    {
        var importer = App.ImageImporter
            ?? throw new InvalidOperationException("The image importer is unavailable.");
        var result = await importer.ImportAsync(
            stream,
            fileName,
            sourceKind,
            expectedFormat);
        await ViewModel.RefreshAndSelectAsync(result.ImageItemId);
        var selectedItem = ViewModel.Items.FirstOrDefault(item => item.Id == result.ImageItemId);
        if (selectedItem is not null)
        {
            SynchronizeSingleSelection(selectedItem);
        }

        UpdateResponsiveLayout();
        return result;
    }

    private void SetSelectionMode(bool isActive)
    {
        _synchronizingSelection = true;
        try
        {
            ClearCollectionSelection(LibraryGridView);
            ClearCollectionSelection(LibraryListView);
            LibraryGridView.SelectionMode = isActive
                ? ListViewSelectionMode.Multiple
                : ListViewSelectionMode.Single;
            LibraryListView.SelectionMode = isActive
                ? ListViewSelectionMode.Multiple
                : ListViewSelectionMode.Single;
            _selectedItemIds.Clear();
            ViewModel.SetSelectionMode(isActive);

            if (!isActive && ViewModel.SelectedItemId is Guid selectedItemId)
            {
                var selectedItem = ViewModel.Items.FirstOrDefault(item => item.Id == selectedItemId);
                if (selectedItem is not null)
                {
                    ActiveCollection.SelectedItem = selectedItem;
                }
            }
        }
        finally
        {
            _synchronizingSelection = false;
        }
    }

    private static void ClearCollectionSelection(ListViewBase collection)
    {
        if (collection.SelectionMode is ListViewSelectionMode.Multiple or ListViewSelectionMode.Extended)
        {
            collection.SelectedItems.Clear();
            return;
        }

        collection.SelectedItem = null;
    }

    private void SetDisplayMode(LibraryDisplayMode mode)
    {
        if (_displayMode == mode)
        {
            GridViewModeButton.IsChecked = mode == LibraryDisplayMode.Grid;
            ListViewModeButton.IsChecked = mode == LibraryDisplayMode.List;
            return;
        }

        CaptureActiveSelection();
        _displayMode = mode;
        LibraryGridView.Visibility = mode == LibraryDisplayMode.Grid
            ? Visibility.Visible
            : Visibility.Collapsed;
        LibraryListView.Visibility = mode == LibraryDisplayMode.List
            ? Visibility.Visible
            : Visibility.Collapsed;
        GridViewModeButton.IsChecked = mode == LibraryDisplayMode.Grid;
        ListViewModeButton.IsChecked = mode == LibraryDisplayMode.List;
        RestoreCollectionSelection();
    }

    private void CaptureActiveSelection()
    {
        if (!ViewModel.IsSelectionModeActive)
        {
            return;
        }

        _selectedItemIds.Clear();
        foreach (var item in ActiveCollection.SelectedItems.OfType<LibraryItem>())
        {
            _selectedItemIds.Add(item.Id);
        }

        ViewModel.SetSelectedCount(_selectedItemIds.Count);
    }

    private void RestoreCollectionSelection()
    {
        _synchronizingSelection = true;
        try
        {
            ClearCollectionSelection(ActiveCollection);
            if (ViewModel.IsSelectionModeActive)
            {
                foreach (var item in ViewModel.Items.Where(item => _selectedItemIds.Contains(item.Id)))
                {
                    ActiveCollection.SelectedItems.Add(item);
                }

                ViewModel.SetSelectedCount(ActiveCollection.SelectedItems.Count);
                return;
            }

            if (ViewModel.SelectedItemId is Guid selectedItemId)
            {
                ActiveCollection.SelectedItem = ViewModel.Items.FirstOrDefault(item => item.Id == selectedItemId);
            }
        }
        finally
        {
            _synchronizingSelection = false;
        }
    }

    private void SynchronizeSingleSelection(LibraryItem selectedItem)
    {
        _synchronizingSelection = true;
        try
        {
            LibraryGridView.SelectedItem = selectedItem;
            LibraryListView.SelectedItem = selectedItem;
        }
        finally
        {
            _synchronizingSelection = false;
        }
    }

    private void UpdateSortMenuChecks()
    {
        var field = ViewModel.SortField;
        var direction = ViewModel.SortDirection;
        SortNewestMenuItem.IsChecked = field == LibrarySortField.CreatedAt
            && direction == LibrarySortDirection.Descending;
        SortOldestMenuItem.IsChecked = field == LibrarySortField.CreatedAt
            && direction == LibrarySortDirection.Ascending;
        SortNameAscendingMenuItem.IsChecked = field == LibrarySortField.Title
            && direction == LibrarySortDirection.Ascending;
        SortNameDescendingMenuItem.IsChecked = field == LibrarySortField.Title
            && direction == LibrarySortDirection.Descending;
        SortSizeDescendingMenuItem.IsChecked = field == LibrarySortField.ByteLength
            && direction == LibrarySortDirection.Descending;
        SortSizeAscendingMenuItem.IsChecked = field == LibrarySortField.ByteLength
            && direction == LibrarySortDirection.Ascending;
        SortCategoryAscendingMenuItem.IsChecked = field == LibrarySortField.Category
            && direction == LibrarySortDirection.Ascending;
        SortCategoryDescendingMenuItem.IsChecked = field == LibrarySortField.Category
            && direction == LibrarySortDirection.Descending;
    }

    private async Task OpenImageAsync(Guid imageItemId)
    {
        try
        {
            var library = App.Library
                ?? throw new InvalidOperationException("The local library is unavailable.");
            var paths = App.DataPaths
                ?? throw new InvalidOperationException("Managed paths are unavailable.");
            var entry = await library.GetAsync(imageItemId);
            if (entry is null || entry.Item.DeletedAtUtc is not null)
            {
                throw new FileNotFoundException("The library item is unavailable.");
            }

            var absolutePath = paths.Resolve(entry.Asset.OriginalRelativePath);
            var file = await StorageFile.GetFileFromPathAsync(absolutePath);
            if (!await Windows.System.Launcher.LaunchFileAsync(file))
            {
                throw new InvalidOperationException("Windows did not open the managed image.");
            }
        }
        catch (Exception)
        {
            ViewModel.ShowStatus(_resources.GetString("OpenImageFailedStatus"));
        }
    }

    private async Task ConfirmSingleSoftDeleteAsync(Guid imageItemId, string title)
    {
        var dialog = CreateDialog(
            _resources.GetString("SoftDeleteDialogTitle"),
            string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                _resources.GetString("SoftDeleteDialogMessageFormat"),
                title),
            _resources.GetString("SoftDeleteDialogPrimary"));
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        var deleted = await ViewModel.SoftDeleteItemsAsync([imageItemId]);
        ViewModel.ShowStatus(deleted == 1
            ? _resources.GetString("SoftDeleteCompletedStatus")
            : _resources.GetString("SoftDeleteFailedStatus"));
        SetSelectionMode(isActive: false);
        UpdateResponsiveLayout();
    }

    private async Task ConfirmBatchSoftDeleteAsync(IReadOnlyList<LibraryItem> items)
    {
        if (items.Count == 0)
        {
            return;
        }

        var dialog = CreateDialog(
            _resources.GetString("SoftDeleteBatchDialogTitle"),
            string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                _resources.GetString("SoftDeleteBatchDialogMessageFormat"),
                items.Count),
            _resources.GetString("SoftDeleteDialogPrimary"));
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        var deleted = await ViewModel.SoftDeleteItemsAsync(items.Select(item => item.Id).ToArray());
        ViewModel.ShowStatus(string.Format(
            System.Globalization.CultureInfo.CurrentCulture,
            _resources.GetString("SoftDeleteBatchCompletedStatusFormat"),
            deleted,
            items.Count));
        SetSelectionMode(isActive: false);
        UpdateResponsiveLayout();
    }

    private async Task ConfirmReanalysisAsync(
        IReadOnlyList<LibraryItem> items,
        bool exitSelectionMode)
    {
        if (items.Count == 0)
        {
            return;
        }

        var dialog = CreateDialog(
            _resources.GetString("ReanalysisDialogTitle"),
            string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                _resources.GetString("ReanalysisDialogMessageFormat"),
                items.Count),
            _resources.GetString("ReanalysisDialogPrimary"));
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        try
        {
            var result = await ViewModel.QueueReanalysisAsync(
                items.Select(item => item.Id).ToArray());
            ViewModel.ShowStatus(string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                _resources.GetString("ReanalysisQueuedStatusFormat"),
                result.QueuedCount,
                result.SkippedCount));
        }
        catch
        {
            ViewModel.ShowStatus(_resources.GetString("ReanalysisFailedStatus"));
        }

        if (exitSelectionMode)
        {
            SetSelectionMode(isActive: false);
        }

        UpdateResponsiveLayout();
    }

    private static LibraryItem? GetContextItem(object sender) =>
        sender is FrameworkElement { Tag: LibraryItem item } ? item : null;

    private async void CategoryAssignmentCheckBox_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox { Tag: CategoryOption option } checkBox)
        {
            return;
        }

        var assigned = checkBox.IsChecked == true;
        try
        {
            await ViewModel.SetCategoryAssignmentAsync(option, assigned);
        }
        catch
        {
            option.IsAssigned = !assigned;
            ViewModel.ShowStatus(_resources.GetString("CategoryUpdateFailedStatus"));
        }
    }

    private async void AddCategoryButton_Click(object sender, RoutedEventArgs e)
    {
        var name = await ShowCategoryNameDialogAsync(
            _resources.GetString("AddCategoryDialogTitle"),
            _resources.GetString("AddCategoryDialogPrimary"),
            string.Empty);
        if (name is null)
        {
            return;
        }

        try
        {
            var category = await ViewModel.CreateCategoryAsync(name);
            if (ViewModel.SelectedItemId is not null)
            {
                var option = ViewModel.CategoryOptions.First(item => item.Id == category.Id);
                await ViewModel.SetCategoryAssignmentAsync(option, isAssigned: true);
            }
        }
        catch
        {
            ViewModel.ShowStatus(_resources.GetString("CategoryNameConflictStatus"));
        }
    }

    private async void RenameCategoryButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: CategoryOption option })
        {
            return;
        }

        var name = await ShowCategoryNameDialogAsync(
            _resources.GetString("RenameCategoryDialogTitle"),
            _resources.GetString("RenameCategoryDialogPrimary"),
            option.Name);
        if (name is null)
        {
            return;
        }

        try
        {
            await ViewModel.RenameCategoryAsync(option.Id, name);
        }
        catch
        {
            ViewModel.ShowStatus(_resources.GetString("CategoryNameConflictStatus"));
        }
    }

    private async void DeleteCategoryButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: CategoryOption option })
        {
            return;
        }

        var dialog = CreateDialog(
            _resources.GetString("DeleteCategoryDialogTitle"),
            string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                _resources.GetString("DeleteCategoryDialogMessageFormat"),
                option.Name),
            _resources.GetString("DeleteCategoryDialogPrimary"));
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        await ViewModel.DeleteCategoryAsync(option.Id);
    }

    private async void DeleteImageButton_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedItemId is Guid imageItemId)
        {
            await ConfirmSingleSoftDeleteAsync(imageItemId, ViewModel.DetailTitle);
        }
    }

    private void DetailBackButton_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.CloseDetail();
        LibraryGridView.SelectedItem = null;
        LibraryListView.SelectedItem = null;
        UpdateResponsiveLayout();
    }

    private void DetailTitleTextBox_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox textBox)
        {
            UpdateDetailTitleOpticalAlignment(textBox);
        }
    }

    private void DetailTitleTextBox_TextChanging(TextBox sender, TextBoxTextChangingEventArgs e)
        => UpdateDetailTitleOpticalAlignment(sender);

    private static void UpdateDetailTitleOpticalAlignment(TextBox textBox)
        => textBox.Padding = IsPureCjkText(textBox.Text)
            ? DetailTitlePureCjkPadding
            : DetailTitleDefaultPadding;

    private static bool IsPureCjkText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var containsHan = false;
        foreach (var rune in text.EnumerateRunes())
        {
            if (Rune.IsWhiteSpace(rune))
            {
                continue;
            }

            if (IsHanCharacter(rune.Value))
            {
                containsHan = true;
                continue;
            }

            if (IsCjkPunctuation(rune.Value))
            {
                continue;
            }

            return false;
        }

        return containsHan;
    }

    private static bool IsHanCharacter(int value) =>
        value is >= 0x3400 and <= 0x4DBF or
            >= 0x4E00 and <= 0x9FFF or
            >= 0xF900 and <= 0xFAFF or
            >= 0x20000 and <= 0x2FA1F or
            >= 0x30000 and <= 0x323AF;

    private static bool IsCjkPunctuation(int value) =>
        value is >= 0x3000 and <= 0x303F or
            >= 0xFE10 and <= 0xFE1F or
            >= 0xFE30 and <= 0xFE4F or
            >= 0xFF01 and <= 0xFF65;

    private void LibraryPage_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateResponsiveLayout();
    }

    private void DetailSurface_SizeChanged(object sender, SizeChangedEventArgs e) =>
        UpdateDetailContentWidth();

    private void LibraryGridItemsWrapGrid_Loaded(object sender, RoutedEventArgs e)
    {
        _libraryGridItemsPanel = (ItemsWrapGrid)sender;
        UpdateGridCardWidth();
    }

    private void LibraryGridView_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateGridCardWidth();
    }

    private void UpdateResponsiveLayout()
    {
        var pageWidth = ActualWidth > 0 ? ActualWidth : LayoutRoot.ActualWidth;
        var hasDetail = ViewModel.HasDetail;
        if (!hasDetail)
        {
            CollectionPane.Visibility = Visibility.Visible;
            CollectionColumn.Width = new GridLength(1, GridUnitType.Star);
            DetailColumn.Width = new GridLength(0);
            DetailSurface.Visibility = Visibility.Collapsed;
            DetailBackButton.Visibility = Visibility.Collapsed;
            DetailPane.Visibility = Visibility.Collapsed;
            UpdateDetailContentWidth();
            UpdateGridCardWidth();
            return;
        }

        DetailSurface.Visibility = Visibility.Visible;
        DetailPane.Visibility = Visibility.Visible;
        if (pageWidth >= LibrarySplitViewMinimumWidth)
        {
            var detailWidth = Math.Clamp(
                pageWidth * LibraryDetailWidthRatio,
                LibraryDetailMinimumWidth,
                LibraryDetailMaximumWidth);
            CollectionPane.Visibility = Visibility.Visible;
            CollectionColumn.Width = new GridLength(1, GridUnitType.Star);
            DetailColumn.Width = new GridLength(detailWidth);
            DetailBackButton.Visibility = Visibility.Collapsed;
        }
        else
        {
            CollectionPane.Visibility = Visibility.Collapsed;
            CollectionColumn.Width = new GridLength(0);
            DetailColumn.Width = new GridLength(1, GridUnitType.Star);
            DetailBackButton.Visibility = Visibility.Visible;
        }

        UpdateDetailContentWidth();
        UpdateGridCardWidth();
    }

    private void UpdateDetailContentWidth()
    {
        if (DetailContentPanel is null
            || DetailCommandGrid is null
            || DetailSurface.ActualWidth <= 0)
        {
            return;
        }

        var targetWidth = Math.Min(
            DetailSurface.ActualWidth,
            DetailContentPanel.MaxWidth);
        DetailContentPanel.Width = targetWidth;
        DetailCommandGrid.Width = targetWidth;
    }

    private void UpdateGridCardWidth()
    {
        if (_libraryGridItemsPanel is null || LibraryGridView.ActualWidth <= 0)
        {
            return;
        }

        var availableWidth = Math.Max(0, LibraryGridView.ActualWidth - 2);
        if (availableWidth < 1)
        {
            return;
        }

        var minimumSlotWidth = LibraryGridCardMinimumWidth + LibraryGridCardGap;
        var columnCount = Math.Max(
            1,
            (int)Math.Floor((availableWidth + LibraryGridCardGap) / minimumSlotWidth));
        var itemWidth = Math.Floor(availableWidth / columnCount);
        if (double.IsNaN(_libraryGridItemsPanel.ItemWidth)
            || Math.Abs(_libraryGridItemsPanel.ItemWidth - itemWidth) > 0.5)
        {
            _libraryGridItemsPanel.ItemWidth = itemWidth;
        }

        if (double.IsNaN(_libraryGridItemsPanel.ItemHeight)
            || Math.Abs(_libraryGridItemsPanel.ItemHeight - LibraryGridCardHeight) > 0.5)
        {
            _libraryGridItemsPanel.ItemHeight = LibraryGridCardHeight;
        }
    }

    private async Task<string?> ShowCategoryNameDialogAsync(
        string title,
        string primaryButtonText,
        string initialValue)
    {
        var textBox = new TextBox
        {
            Text = initialValue,
            MaxLength = 80,
            Header = _resources.GetString("CategoryNameFieldHeader"),
        };
        AutomationProperties.SetAutomationId(textBox, "CategoryNameDialogTextBox");
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            RequestedTheme = ActualTheme,
            Title = title,
            Content = textBox,
            PrimaryButtonText = primaryButtonText,
            CloseButtonText = _resources.GetString("CancelButtonText"),
            DefaultButton = ContentDialogButton.Primary,
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary
            ? textBox.Text.Trim()
            : null;
    }

    private ContentDialog CreateDialog(string title, string message, string primaryButtonText) => new()
    {
        XamlRoot = XamlRoot,
        RequestedTheme = ActualTheme,
        Title = title,
        Content = message,
        PrimaryButtonText = primaryButtonText,
        CloseButtonText = _resources.GetString("CancelButtonText"),
        DefaultButton = ContentDialogButton.Close,
    };

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!IsLoaded)
        {
            return;
        }

        if (e.PropertyName == nameof(LibraryPageViewModel.SelectedItemId))
        {
            UpdateResponsiveLayout();
        }

        if (e.PropertyName != nameof(LibraryPageViewModel.State))
        {
            return;
        }

        TrySubscribeAnalysisUpdates();

        DispatcherQueue.TryEnqueue(() =>
        {
            if (!IsLoaded)
            {
                return;
            }

            AutomationProperties.SetName(StateContent, GetLibraryStateAnnouncement(ViewModel.State));
            var peer = FrameworkElementAutomationPeer.FromElement(StateContent)
                ?? new FrameworkElementAutomationPeer(StateContent);
            peer.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
        });
    }

    private static bool TryGetExpectedFormat(string path, out ManagedImageFormat format)
    {
        format = Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".png" => ManagedImageFormat.Png,
            ".jpg" or ".jpeg" => ManagedImageFormat.Jpeg,
            ".webp" => ManagedImageFormat.WebP,
            _ => 0,
        };
        return format != 0;
    }

    private static string GetLibraryStateAnnouncement(LibraryViewState state) =>
        _resources.GetString($"LibraryState{state}Announcement");

    private enum LibraryDisplayMode
    {
        Grid,
        List,
    }
}
