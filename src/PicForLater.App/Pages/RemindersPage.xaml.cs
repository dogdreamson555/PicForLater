using System.ComponentModel;
using System.Globalization;
using System.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.Windows.ApplicationModel.Resources;
using PicForLater.Analysis;
using PicForLater.App.Models;
using PicForLater.App.ViewModels;

namespace PicForLater.App.Pages;

public sealed partial class RemindersPage : Page
{
    private const double WideLayoutThreshold = 1008;
    private const double MasterPaneWidth = 480;
    private const double EditorContentMaximumWidth = 880;
    private const double PureCjkTextOpticalOffset = 1;
    private static readonly ResourceLoader _resources = new();
    private bool _analysisRefreshPending;
    private bool _analysisRefreshRunning;
    private int _analysisRefreshVersion;
    private AnalysisQueueWakeSignal? _analysisUpdatesSource;
    private bool _viewModelSubscribed = true;
    private int _loadGeneration;
    private bool _isSynchronizingSelection;
    private Guid? _manualCreationTargetImageItemId;

    public RemindersPageViewModel ViewModel { get; } = new(
        App.StorageReadiness,
        () => App.Reminders,
        () => App.Library,
        () => App.DataPaths);

    public RemindersPage()
    {
        InitializeComponent();
        ViewModel.PropertyChanged += ViewModel_PropertyChanged;
        Loaded += RemindersPage_Loaded;
        Unloaded += RemindersPage_Unloaded;
    }

    public static Visibility BoolToVisibility(bool value) =>
        value ? Visibility.Visible : Visibility.Collapsed;

    public static Visibility StatusPanelVisibility(
        bool notificationsUnavailable,
        bool hasStatusMessage,
        bool hasErrorMessage) =>
        BoolToVisibility(notificationsUnavailable || hasStatusMessage || hasErrorMessage);

    public static Visibility InvertBoolToVisibility(bool value) =>
        value ? Visibility.Collapsed : Visibility.Visible;

    public static Visibility StringToVisibility(string value) =>
        string.IsNullOrWhiteSpace(value) ? Visibility.Collapsed : Visibility.Visible;

    public static ImageSource? ToImageSource(string? uri) =>
        string.IsNullOrWhiteSpace(uri) ? null : new BitmapImage(new Uri(uri));

    public static string GetCandidateAutomationId(Guid candidateId) =>
        $"ReviewReminderCandidate_{candidateId:N}";

    public static string GetCandidateRowAutomationId(Guid candidateId) =>
        $"ReminderCandidateRow_{candidateId:N}";

    public static string GetReminderAutomationId(Guid reminderId) =>
        $"EditConfirmedReminder_{reminderId:N}";

    public static string GetReminderRowAutomationId(Guid reminderId) =>
        $"ConfirmedReminderRow_{reminderId:N}";

    public static string GetCandidateImageAutomationId(Guid candidateId) =>
        $"OpenReminderCandidateImage_{candidateId:N}";

    public static string GetReminderImageAutomationId(Guid reminderId) =>
        $"OpenConfirmedReminderImage_{reminderId:N}";

    public static string GetCandidateContextOpenAutomationId(Guid candidateId) =>
        $"ReminderCandidateContextOpen_{candidateId:N}";

    public static string GetCandidateContextReviewAutomationId(Guid candidateId) =>
        $"ReminderCandidateContextReview_{candidateId:N}";

    public static string GetCandidateContextDismissAutomationId(Guid candidateId) =>
        $"ReminderCandidateContextDismiss_{candidateId:N}";

    public static string GetReminderContextOpenAutomationId(Guid reminderId) =>
        $"ReminderContextOpen_{reminderId:N}";

    public static string GetReminderContextEditAutomationId(Guid reminderId) =>
        $"ReminderContextEdit_{reminderId:N}";

    public static string GetReminderContextSelectAutomationId(Guid reminderId) =>
        $"ReminderContextSelect_{reminderId:N}";

    public static string GetReminderContextDeleteAutomationId(Guid reminderId) =>
        $"ReminderContextDelete_{reminderId:N}";

    private async void RemindersPage_Loaded(object sender, RoutedEventArgs e)
    {
        var loadGeneration = ++_loadGeneration;
        if (!_viewModelSubscribed)
        {
            ViewModel.PropertyChanged += ViewModel_PropertyChanged;
            _viewModelSubscribed = true;
        }

        TrySubscribeAnalysisUpdates();

        await ViewModel.InitializeAsync();
        if (!IsCurrentLoad(loadGeneration))
        {
            return;
        }

        if (_manualCreationTargetImageItemId is Guid imageItemId)
        {
            await ViewModel.CreateManualReminderAsync(imageItemId);
            if (!IsCurrentLoad(loadGeneration))
            {
                return;
            }

            _manualCreationTargetImageItemId = null;
        }

        TrySubscribeAnalysisUpdates();
        UpdateResponsiveLayout();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (e.Parameter is string value && Guid.TryParse(value, out var imageItemId))
        {
            _manualCreationTargetImageItemId = imageItemId;
        }
    }

    private void RemindersPage_Unloaded(object sender, RoutedEventArgs e)
    {
        _loadGeneration++;
        _analysisRefreshVersion++;
        if (_viewModelSubscribed)
        {
            ViewModel.PropertyChanged -= ViewModel_PropertyChanged;
            _viewModelSubscribed = false;
        }

        UnsubscribeAnalysisUpdates();

        _analysisRefreshPending = false;
    }

    private void AnalysisUpdates_ItemChanged(object? sender, AnalysisItemChangedEventArgs e)
    {
        if (sender is not AnalysisQueueWakeSignal source)
        {
            return;
        }

        var loadGeneration = _loadGeneration;
        DispatcherQueue.TryEnqueue(() =>
        {
            if (!IsCurrentLoad(loadGeneration)
                || !ReferenceEquals(source, _analysisUpdatesSource))
            {
                return;
            }

            _analysisRefreshPending = true;
            _analysisRefreshVersion++;
            _ = RefreshAfterAnalysisAsync();
        });
    }

    private async Task RefreshAfterAnalysisAsync()
    {
        if (_analysisRefreshRunning)
        {
            return;
        }

        _analysisRefreshRunning = true;
        try
        {
            while (_analysisRefreshPending && IsLoaded && _analysisUpdatesSource is not null)
            {
                var source = _analysisUpdatesSource;
                var refreshVersion = _analysisRefreshVersion;
                _analysisRefreshPending = false;
                await ViewModel.RefreshAfterAnalysisAsync();
                if (!IsLoaded
                    || refreshVersion != _analysisRefreshVersion
                    || !ReferenceEquals(source, _analysisUpdatesSource))
                {
                    continue;
                }

                UpdateResponsiveLayout();
            }
        }
        catch
        {
            // SQLite remains authoritative. A later analysis event or navigation
            // reload can recover a view update that raced page teardown.
        }
        finally
        {
            _analysisRefreshRunning = false;
            if (_analysisRefreshPending && IsLoaded && _analysisUpdatesSource is not null)
            {
                _ = RefreshAfterAnalysisAsync();
            }
        }
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
        _analysisRefreshVersion++;
        _analysisRefreshPending = false;
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

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!IsLoaded)
        {
            return;
        }

        if (e.PropertyName == nameof(RemindersPageViewModel.State))
        {
            TrySubscribeAnalysisUpdates();
        }

        if (e.PropertyName == nameof(RemindersPageViewModel.IsEditorOpen))
        {
            UpdateResponsiveLayout();
        }
        else if (e.PropertyName == nameof(RemindersPageViewModel.ReminderCount)
                 && ViewModel.ReminderCount == 0
                 && ViewModel.IsSelectionModeActive)
        {
            SetSelectionMode(false);
        }
    }

    private void RemindersPage_SizeChanged(object sender, SizeChangedEventArgs e) =>
        UpdateResponsiveLayout();

    private void ReminderCandidatesList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is ReminderCandidateItem item)
        {
            ViewModel.EditCandidate(item);
            UpdateResponsiveLayout();
        }
    }

    private void ConfirmedRemindersList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (ViewModel.IsSelectionModeActive)
        {
            return;
        }

        if (e.ClickedItem is ReminderListItem item)
        {
            ViewModel.EditReminder(item);
            UpdateResponsiveLayout();
        }
    }

    private void ReviewReminderCandidateButton_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is ReminderCandidateItem item)
        {
            ViewModel.EditCandidate(item);
            UpdateResponsiveLayout();
        }
    }

    private void EditConfirmedReminderButton_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.IsSelectionModeActive)
        {
            return;
        }

        if ((sender as FrameworkElement)?.DataContext is ReminderListItem item)
        {
            ViewModel.EditReminder(item);
            UpdateResponsiveLayout();
        }
    }

    private void ReminderCandidateThumbnailButton_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is ReminderCandidateItem item)
        {
            ViewModel.EditCandidate(item);
            UpdateResponsiveLayout();
        }
    }

    private void ConfirmedReminderThumbnailButton_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.IsSelectionModeActive)
        {
            return;
        }

        if ((sender as FrameworkElement)?.DataContext is ReminderListItem item)
        {
            ViewModel.EditReminder(item);
            UpdateResponsiveLayout();
        }
    }

    private void OpenImageButton_DoubleTapped(
        object sender,
        DoubleTappedRoutedEventArgs e)
    {
        OpenImageFromTag(sender);
        e.Handled = true;
    }

    private void OpenImageMenuItem_Click(object sender, RoutedEventArgs e) =>
        OpenImageFromTag(sender);

    private void ReminderEditorImageButton_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.EditorImageItemId is Guid imageItemId)
        {
            App.RequestLibraryImageNavigation(imageItemId);
        }
    }

    private void ReviewCandidateMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (TryGetGuidTag(sender, out var candidateId)
            && ViewModel.Candidates.FirstOrDefault(item => item.Candidate.Id == candidateId)
                is { } item)
        {
            ViewModel.EditCandidate(item);
            UpdateResponsiveLayout();
        }
    }

    private async void DismissCandidateMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (TryGetGuidTag(sender, out var candidateId)
            && ViewModel.Candidates.FirstOrDefault(item => item.Candidate.Id == candidateId)
                is { } item)
        {
            ViewModel.EditCandidate(item);
            await ViewModel.DismissCandidateCommand.ExecuteAsync(null);
            UpdateResponsiveLayout();
        }
    }

    private void EditReminderMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (TryGetGuidTag(sender, out var reminderId)
            && ViewModel.Reminders.FirstOrDefault(item => item.Reminder.Id == reminderId)
                is { } item)
        {
            SetSelectionMode(false);
            ViewModel.EditReminder(item);
            UpdateResponsiveLayout();
        }
    }

    private void SelectReminderMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetGuidTag(sender, out var reminderId)
            || ViewModel.Reminders.FirstOrDefault(item => item.Reminder.Id == reminderId)
                is not { } item)
        {
            return;
        }

        SetSelectionMode(true);
        if (!ConfirmedRemindersList.SelectedItems.Contains(item))
        {
            ConfirmedRemindersList.SelectedItems.Add(item);
        }
    }

    private async void DeleteReminderMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (TryGetGuidTag(sender, out var reminderId))
        {
            await DeleteSingleReminderAsync(reminderId);
        }
    }

    private async void ReminderDeleteEditorButton_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.EditorReminderId is Guid reminderId)
        {
            await DeleteSingleReminderAsync(reminderId);
        }
    }

    private void ReminderSelectionModeButton_Click(object sender, RoutedEventArgs e) =>
        SetSelectionMode(true);

    private void ReminderCancelSelectionButton_Click(object sender, RoutedEventArgs e) =>
        SetSelectionMode(false);

    private void ReminderSelectAllButton_Click(object sender, RoutedEventArgs e)
    {
        if (!ViewModel.IsSelectionModeActive)
        {
            SetSelectionMode(true);
        }

        ConfirmedRemindersList.SelectAll();
    }

    private async void ReminderDeleteSelectedButton_Click(object sender, RoutedEventArgs e)
    {
        var selected = ConfirmedRemindersList.SelectedItems
            .OfType<ReminderListItem>()
            .ToArray();
        if (selected.Length == 0
            || !await ConfirmDeleteAsync(selected.Length, title: null))
        {
            return;
        }

        await ViewModel.DeleteRemindersAsync(
            selected.Select(item => item.Reminder.Id).ToArray());
        SetSelectionMode(false);
        UpdateResponsiveLayout();
    }

    private void ConfirmedRemindersList_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        if (_isSynchronizingSelection)
        {
            return;
        }

        ViewModel.SetSelectedReminderCount(ConfirmedRemindersList.SelectedItems.Count);
    }

    private async Task DeleteSingleReminderAsync(Guid reminderId)
    {
        var item = ViewModel.Reminders.FirstOrDefault(
            candidate => candidate.Reminder.Id == reminderId);
        var title = item?.Reminder.ImageTitle ?? ViewModel.EditorTitle;
        if (!await ConfirmDeleteAsync(1, title))
        {
            return;
        }

        await ViewModel.DeleteRemindersAsync([reminderId]);
        SetSelectionMode(false);
        UpdateResponsiveLayout();
    }

    private async Task<bool> ConfirmDeleteAsync(int count, string? title)
    {
        var isBatch = count > 1;
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            RequestedTheme = ActualTheme,
            Title = _resources.GetString(
                isBatch ? "ReminderDeleteBatchDialogTitle" : "ReminderDeleteDialogTitle"),
            Content = string.Format(
                CultureInfo.CurrentCulture,
                _resources.GetString(
                    isBatch
                        ? "ReminderDeleteBatchDialogMessageFormat"
                        : "ReminderDeleteDialogMessageFormat"),
                isBatch ? count : title),
            PrimaryButtonText = _resources.GetString("ReminderDeleteDialogPrimary"),
            CloseButtonText = _resources.GetString("CancelButtonText"),
            DefaultButton = ContentDialogButton.Close,
        };

        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    private void SetSelectionMode(bool isActive)
    {
        if (ConfirmedRemindersList is null || ReminderSelectionModeButton is null)
        {
            return;
        }

        _isSynchronizingSelection = true;
        try
        {
            if (!isActive
                && ConfirmedRemindersList.SelectionMode != ListViewSelectionMode.None)
            {
                ConfirmedRemindersList.SelectedItems.Clear();
            }

            ConfirmedRemindersList.SelectionMode = isActive
                ? ListViewSelectionMode.Multiple
                : ListViewSelectionMode.None;
            ViewModel.SetSelectionMode(isActive);
            ViewModel.SetSelectedReminderCount(
                isActive ? ConfirmedRemindersList.SelectedItems.Count : 0);
        }
        finally
        {
            _isSynchronizingSelection = false;
        }
    }

    private static bool TryGetGuidTag(object sender, out Guid value)
    {
        var tag = (sender as FrameworkElement)?.Tag;
        if (tag is Guid guid)
        {
            value = guid;
            return true;
        }

        return Guid.TryParse(tag?.ToString(), out value);
    }

    private static void OpenImageFromTag(object sender)
    {
        if (TryGetGuidTag(sender, out var imageItemId))
        {
            App.RequestLibraryImageNavigation(imageItemId);
        }
    }

    private void UpdateResponsiveLayout()
    {
        if (ReminderWorkspace is null)
        {
            return;
        }

        var isWide = ActualWidth >= WideLayoutThreshold;
        if (isWide)
        {
            MasterColumn.Width = new GridLength(MasterPaneWidth);
            DividerColumn.Width = new GridLength(1);
            EditorColumn.Width = new GridLength(1, GridUnitType.Star);
            Grid.SetColumn(ReminderMasterPane, 0);
            Grid.SetColumnSpan(ReminderMasterPane, 1);
            Grid.SetColumn(ReminderEditorPane, 2);
            Grid.SetColumnSpan(ReminderEditorPane, 1);
            ReminderMasterPane.Visibility = Visibility.Visible;
            ReminderPaneDivider.Visibility = Visibility.Visible;
            ReminderEditorPane.Visibility = Visibility.Visible;
            ReminderEditorBackButton.Visibility = Visibility.Collapsed;
            UpdateEditorContentWidth();
            return;
        }

        MasterColumn.Width = new GridLength(1, GridUnitType.Star);
        DividerColumn.Width = new GridLength(0);
        EditorColumn.Width = new GridLength(0);
        Grid.SetColumn(ReminderMasterPane, 0);
        Grid.SetColumnSpan(ReminderMasterPane, 3);
        Grid.SetColumn(ReminderEditorPane, 0);
        Grid.SetColumnSpan(ReminderEditorPane, 3);
        ReminderPaneDivider.Visibility = Visibility.Collapsed;
        ReminderMasterPane.Visibility = ViewModel.IsEditorOpen
            ? Visibility.Collapsed
            : Visibility.Visible;
        ReminderEditorPane.Visibility = ViewModel.IsEditorOpen
            ? Visibility.Visible
            : Visibility.Collapsed;
        ReminderEditorBackButton.Visibility = Visibility.Visible;
        UpdateEditorContentWidth();
    }

    private void ReminderEditorPane_SizeChanged(object sender, SizeChangedEventArgs e) =>
        UpdateEditorContentWidth();

    private void ReminderTimePicker_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not TimePicker timePicker)
        {
            return;
        }

        timePicker.ApplyTemplate();
        if (FindVisualDescendant<Button>(timePicker, "FlyoutButton") is { } flyoutButton)
        {
            flyoutButton.MinWidth = 0;
            flyoutButton.MinHeight = 32;
            flyoutButton.Height = 32;
            flyoutButton.HorizontalAlignment = HorizontalAlignment.Stretch;
        }
    }

    private void EditorTextBox_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox textBox)
        {
            UpdateEditorTextBoxOpticalAlignment(textBox);
        }
    }

    private void EditorTextBox_TextChanging(TextBox sender, TextBoxTextChangingEventArgs e)
    {
        if (FindVisualDescendant<ScrollViewer>(sender, "ContentElement")?.RenderTransform is TranslateTransform transform)
        {
            transform.Y = IsPureCjkText(sender.Text) ? PureCjkTextOpticalOffset : 0;
        }
    }

    private static void UpdateEditorTextBoxOpticalAlignment(TextBox textBox)
    {
        textBox.ApplyTemplate();
        if (FindVisualDescendant<ScrollViewer>(textBox, "ContentElement") is not { } contentElement)
        {
            return;
        }

        var offset = IsPureCjkText(textBox.Text) ? PureCjkTextOpticalOffset : 0;
        if (contentElement.RenderTransform is TranslateTransform transform)
        {
            transform.Y = offset;
            return;
        }

        contentElement.RenderTransform = new TranslateTransform { Y = offset };
    }

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

    private static T? FindVisualDescendant<T>(DependencyObject parent, string name)
        where T : FrameworkElement
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T element && string.Equals(element.Name, name, StringComparison.Ordinal))
            {
                return element;
            }

            if (FindVisualDescendant<T>(child, name) is { } descendant)
            {
                return descendant;
            }
        }

        return null;
    }

    private void UpdateEditorContentWidth()
    {
        if (ReminderEditorContent is null || ReminderEditorPane.ActualWidth <= 0)
        {
            return;
        }

        ReminderEditorContent.Width = Math.Min(
            ReminderEditorPane.ActualWidth,
            EditorContentMaximumWidth);
    }
}
