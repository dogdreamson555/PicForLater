using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Windows.ApplicationModel.Resources;
using PicForLater.App.Models;
using PicForLater.App.ViewModels;

namespace PicForLater.App.Pages;

public sealed partial class RecycleBinPage : Page
{
    private const double GridCardMinimumWidth = 196;
    private const double GridCardGap = 12;
    private const double GridCardSlotHeight = 240;
    private static readonly ResourceLoader _resources = new();
    private readonly HashSet<Guid> _selectedItemIds = [];
    private bool _synchronizingSelection;
    private ItemsWrapGrid? _itemsPanel;

    public RecycleBinPageViewModel ViewModel { get; } = new(
        App.StorageReadiness,
        () => App.Library,
        () => App.DataPaths);

    public RecycleBinPage()
    {
        InitializeComponent();
        Loaded += RecycleBinPage_Loaded;
    }

    public static string GetContextPermanentDeleteAutomationId(Guid id) =>
        $"RecycleBinContextPermanentDelete_{id:N}";

    public static string GetContextRestoreAutomationId(Guid id) =>
        $"RecycleBinContextRestore_{id:N}";

    public static string GetContextSelectAutomationId(Guid id) =>
        $"RecycleBinContextSelect_{id:N}";

    public static Visibility BoolToVisibility(bool value) =>
        value ? Visibility.Visible : Visibility.Collapsed;

    public static Visibility InvertBoolToVisibility(bool value) =>
        value ? Visibility.Collapsed : Visibility.Visible;

    private async void RecycleBinPage_Loaded(object sender, RoutedEventArgs e)
    {
        Loaded -= RecycleBinPage_Loaded;
        await ViewModel.InitializeAsync();
    }

    private void RecycleBinGridView_ContainerContentChanging(
        ListViewBase sender,
        ContainerContentChangingEventArgs args)
    {
        if (args.ItemContainer is not GridViewItem container
            || args.Item is not LibraryItem item)
        {
            return;
        }

        AutomationProperties.SetAutomationId(container, $"RecycleBinItem_{item.Id:N}");
        AutomationProperties.SetName(container, item.Title);
    }

    private async void RestoreButton_Click(object sender, RoutedEventArgs e)
    {
        await RestoreItemsAsync(GetActionItems());
    }

    private async void PermanentDeleteButton_Click(object sender, RoutedEventArgs e)
    {
        await ConfirmPermanentDeleteAsync(GetActionItems());
    }

    private void RecycleBinSelectionModeButton_Click(object sender, RoutedEventArgs e) =>
        SetSelectionMode(isActive: true);

    private void RecycleBinCancelSelectionButton_Click(object sender, RoutedEventArgs e) =>
        SetSelectionMode(isActive: false);

    private void RecycleBinSelectAllButton_Click(object sender, RoutedEventArgs e)
    {
        SetSelectionMode(isActive: true);
        _synchronizingSelection = true;
        try
        {
            RecycleBinGridView.SelectAll();
        }
        finally
        {
            _synchronizingSelection = false;
        }

        CaptureSelection();
    }

    private void RecycleBinGridView_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_synchronizingSelection || !ViewModel.IsSelectionModeActive)
        {
            return;
        }

        CaptureSelection();
    }

    private async void ContextPermanentDeleteMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (GetContextItem(sender) is { } item)
        {
            await ConfirmPermanentDeleteAsync(GetActionItems(item));
        }
    }

    private async void ContextRestoreMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (GetContextItem(sender) is { } item)
        {
            await RestoreItemsAsync(GetActionItems(item));
        }
    }

    private void ContextSelectMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (GetContextItem(sender) is not { } item)
        {
            return;
        }

        // Keep this state change inside MenuFlyoutItem.Click. Deferring it until
        // PopupHost teardown can make WinUI fail fast with E_UNEXPECTED.
        SelectContextItem(item);
    }

    private void SelectContextItem(LibraryItem item)
    {
        SetSelectionMode(isActive: true);
        _synchronizingSelection = true;
        try
        {
            RecycleBinGridView.SelectedItems.Add(item);
        }
        finally
        {
            _synchronizingSelection = false;
        }

        CaptureSelection();
    }

    private async Task RestoreItemsAsync(IReadOnlyList<LibraryItem> items)
    {
        if (items.Count == 0)
        {
            return;
        }

        var ids = items.Select(item => item.Id).ToArray();
        SetSelectionMode(isActive: false);
        await ViewModel.RestoreItemsAsync(ids);
    }

    private async Task ConfirmPermanentDeleteAsync(IReadOnlyList<LibraryItem> items)
    {
        if (items.Count == 0)
        {
            return;
        }

        var title = items.Count == 1
            ? _resources.GetString("PermanentDeleteDialogTitle")
            : _resources.GetString("PermanentDeleteBatchDialogTitle");
        var message = items.Count == 1
            ? string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                _resources.GetString("PermanentDeleteDialogMessageFormat"),
                items[0].Title)
            : string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                _resources.GetString("PermanentDeleteBatchDialogMessageFormat"),
                items.Count);
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            RequestedTheme = ActualTheme,
            Title = title,
            Content = message,
            PrimaryButtonText = _resources.GetString("PermanentDeleteDialogPrimary"),
            CloseButtonText = _resources.GetString("CancelButtonText"),
            DefaultButton = ContentDialogButton.Close,
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            var ids = items.Select(item => item.Id).ToArray();
            SetSelectionMode(isActive: false);
            await ViewModel.PermanentlyDeleteItemsAsync(ids);
        }
    }

    private IReadOnlyList<LibraryItem> GetActionItems(LibraryItem? contextItem = null)
    {
        if (ViewModel.IsSelectionModeActive
            && (contextItem is null || _selectedItemIds.Contains(contextItem.Id)))
        {
            return ViewModel.Items
                .Where(item => _selectedItemIds.Contains(item.Id))
                .ToArray();
        }

        return contextItem is not null
            ? [contextItem]
            : ViewModel.SelectedItem is { } selectedItem
                ? [selectedItem]
                : [];
    }

    private void SetSelectionMode(bool isActive)
    {
        if (RecycleBinGridView is null)
        {
            return;
        }

        if (ViewModel.IsSelectionModeActive == isActive)
        {
            return;
        }

        _synchronizingSelection = true;
        try
        {
            ClearGridSelection();
            RecycleBinGridView.SelectionMode = isActive
                ? ListViewSelectionMode.Multiple
                : ListViewSelectionMode.Single;
            _selectedItemIds.Clear();
            ViewModel.SetSelectionMode(isActive);
        }
        finally
        {
            _synchronizingSelection = false;
        }
    }

    private void ClearGridSelection()
    {
        if (RecycleBinGridView.SelectionMode is ListViewSelectionMode.Multiple
            or ListViewSelectionMode.Extended)
        {
            RecycleBinGridView.SelectedItems.Clear();
            return;
        }

        RecycleBinGridView.SelectedItem = null;
    }

    private void CaptureSelection()
    {
        _selectedItemIds.Clear();
        foreach (var item in RecycleBinGridView.SelectedItems.OfType<LibraryItem>())
        {
            _selectedItemIds.Add(item.Id);
        }

        ViewModel.SetSelectedCount(_selectedItemIds.Count);
    }

    private void RecycleBinItemsWrapGrid_Loaded(object sender, RoutedEventArgs e)
    {
        _itemsPanel = (ItemsWrapGrid)sender;
        UpdateGridCardWidth();
    }

    private void RecycleBinGridView_SizeChanged(object sender, SizeChangedEventArgs e) =>
        UpdateGridCardWidth();

    private void UpdateGridCardWidth()
    {
        if (_itemsPanel is null || RecycleBinGridView.ActualWidth <= 0)
        {
            return;
        }

        var availableWidth = Math.Max(0, RecycleBinGridView.ActualWidth - 2);
        if (availableWidth < 1)
        {
            return;
        }

        var minimumSlotWidth = GridCardMinimumWidth + GridCardGap;
        var columnCount = Math.Max(
            1,
            (int)Math.Floor((availableWidth + GridCardGap) / minimumSlotWidth));
        var itemWidth = Math.Floor(availableWidth / columnCount);
        if (double.IsNaN(_itemsPanel.ItemWidth)
            || Math.Abs(_itemsPanel.ItemWidth - itemWidth) > 0.5)
        {
            _itemsPanel.ItemWidth = itemWidth;
        }

        if (double.IsNaN(_itemsPanel.ItemHeight)
            || Math.Abs(_itemsPanel.ItemHeight - GridCardSlotHeight) > 0.5)
        {
            _itemsPanel.ItemHeight = GridCardSlotHeight;
        }
    }

    private static LibraryItem? GetContextItem(object sender) =>
        sender is FrameworkElement { Tag: LibraryItem item } ? item : null;

}
