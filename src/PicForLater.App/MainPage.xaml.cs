using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PicForLater.App.Pages;
using PicForLater.App.ViewModels;
using PicForLater.Core.Runtime;

namespace PicForLater.App;

/// <summary>
/// Hosts the app's top-level navigation and content frame.
/// </summary>
public sealed partial class MainPage : Page
{
    private bool _notificationImageRequestedSubscribed;
    private bool _reminderCreationRequestedSubscribed;
    private bool _backgroundWorkerStatusChangedSubscribed;
    private bool _suppressSelectionNavigation;
    private bool _initialized;
    private object? _lastSelectedNavigationItem;

    public BackgroundWorkersStatusViewModel ViewModel { get; } = new(
        App.RetryFaultedBackgroundWorkersAsync);

    public MainPage()
    {
        InitializeComponent();
        Loaded += MainPage_Loaded;
        Unloaded += MainPage_Unloaded;
        SubscribeToNavigationRequests();
        SubscribeToBackgroundWorkerStatus();
    }

    public static bool Not(bool value) => !value;

    public static Visibility BoolToVisibility(bool value) =>
        value ? Visibility.Visible : Visibility.Collapsed;

    private async void MainPage_Loaded(object sender, RoutedEventArgs e)
    {
        SubscribeToNavigationRequests();
        SubscribeToBackgroundWorkerStatus();
        ViewModel.Update(App.GetBackgroundWorkerStatuses());
        if (_initialized)
        {
            return;
        }

        _initialized = true;

        if (ShellNavigation.MenuItems[0] is NavigationViewItem libraryItem)
        {
            _suppressSelectionNavigation = true;
            try
            {
                ShellNavigation.SelectedItem = libraryItem;
            }
            finally
            {
                _suppressSelectionNavigation = false;
            }

            _lastSelectedNavigationItem = libraryItem;
        }

        if (App.PendingNotificationImageItemId is Guid imageItemId)
        {
            await NavigateToLibraryItemAsync(imageItemId);
        }
        else if (App.PendingReminderCreationImageItemId is Guid reminderImageItemId)
        {
            await NavigateToReminderEditorAsync(reminderImageItemId);
        }
        else if (ShellFrame.Content is null)
        {
            ShellFrame.Navigate(typeof(LibraryPage));
        }
    }

    private void MainPage_Unloaded(object sender, RoutedEventArgs e)
    {
        if (_notificationImageRequestedSubscribed)
        {
            App.NotificationImageRequested -= App_NotificationImageRequested;
            _notificationImageRequestedSubscribed = false;
        }

        if (_reminderCreationRequestedSubscribed)
        {
            App.ReminderCreationRequested -= App_ReminderCreationRequested;
            _reminderCreationRequestedSubscribed = false;
        }

        if (_backgroundWorkerStatusChangedSubscribed)
        {
            App.BackgroundWorkerStatusChanged -= App_BackgroundWorkerStatusChanged;
            _backgroundWorkerStatusChangedSubscribed = false;
        }
    }

    private async void App_NotificationImageRequested(Guid imageItemId) =>
        await NavigateToLibraryItemAsync(imageItemId);

    private async void App_ReminderCreationRequested(Guid imageItemId) =>
        await NavigateToReminderEditorAsync(imageItemId);

    private void App_BackgroundWorkerStatusChanged(BackgroundWorkerStatus status)
    {
        if (DispatcherQueue.HasThreadAccess)
        {
            ViewModel.Update(App.GetBackgroundWorkerStatuses());
            return;
        }

        _ = DispatcherQueue.TryEnqueue(
            () => ViewModel.Update(App.GetBackgroundWorkerStatuses()));
    }

    private async Task NavigateToLibraryItemAsync(Guid imageItemId)
    {
        if (ShellFrame.Content is LibraryPage libraryPage)
        {
            var selected = await libraryPage.NavigateToImageAsync(imageItemId);
            if (!selected)
            {
                return;
            }

            SetSelectedNavigationItem(ShellNavigation.MenuItems[0]);
            App.ClearPendingNotificationNavigation(imageItemId);
            return;
        }

        if (!await TryLeaveCurrentPageAsync())
        {
            return;
        }

        SetSelectedNavigationItem(ShellNavigation.MenuItems[0]);
        if (ShellFrame.Navigate(typeof(LibraryPage), imageItemId.ToString("D")))
        {
            // LibraryPage consumes a notification request only after it has
            // actually selected the requested image.
        }
    }

    private async Task NavigateToReminderEditorAsync(Guid imageItemId)
    {
        if (!await TryLeaveCurrentPageAsync())
        {
            return;
        }

        SetSelectedNavigationItem(ShellNavigation.MenuItems[1]);
        if (ShellFrame.Navigate(typeof(RemindersPage), imageItemId.ToString("D")))
        {
            App.ClearPendingReminderCreation(imageItemId);
        }
    }

    internal async Task<bool> TryLeaveCurrentPageAsync()
    {
        if (ShellFrame.Content is LibraryPage libraryPage)
        {
            return await libraryPage.ConfirmLeaveAsync();
        }

        return true;
    }

    private void SetSelectedNavigationItem(object? item)
    {
        _suppressSelectionNavigation = true;
        try
        {
            ShellNavigation.SelectedItem = item;
        }
        finally
        {
            _suppressSelectionNavigation = false;
        }

        _lastSelectedNavigationItem = item;
    }

    private void RestoreSelectedNavigationItem()
    {
        _suppressSelectionNavigation = true;
        try
        {
            ShellNavigation.SelectedItem = _lastSelectedNavigationItem;
        }
        finally
        {
            _suppressSelectionNavigation = false;
        }
    }

    private void SubscribeToNavigationRequests()
    {
        if (!_notificationImageRequestedSubscribed)
        {
            App.NotificationImageRequested += App_NotificationImageRequested;
            _notificationImageRequestedSubscribed = true;
        }

        if (!_reminderCreationRequestedSubscribed)
        {
            App.ReminderCreationRequested += App_ReminderCreationRequested;
            _reminderCreationRequestedSubscribed = true;
        }
    }

    private void SubscribeToBackgroundWorkerStatus()
    {
        if (_backgroundWorkerStatusChangedSubscribed)
        {
            return;
        }

        App.BackgroundWorkerStatusChanged += App_BackgroundWorkerStatusChanged;
        _backgroundWorkerStatusChangedSubscribed = true;
    }

    private async void ShellNavigation_SelectionChanged(
        NavigationView sender,
        NavigationViewSelectionChangedEventArgs args)
    {
        if (_suppressSelectionNavigation)
        {
            return;
        }

        var requestedItem = args.SelectedItemContainer ?? ShellNavigation.SelectedItem;
        if (!await TryLeaveCurrentPageAsync())
        {
            RestoreSelectedNavigationItem();
            return;
        }

        _lastSelectedNavigationItem = requestedItem;
        if (args.IsSettingsSelected)
        {
            if (ShellFrame.CurrentSourcePageType != typeof(SettingsPage))
            {
                ShellFrame.Navigate(typeof(SettingsPage));
            }

            return;
        }

        if (args.SelectedItemContainer?.Tag is not string destination)
        {
            return;
        }

        Type pageType = destination switch
        {
            "Library" => typeof(LibraryPage),
            "Reminders" => typeof(RemindersPage),
            "RecycleBin" => typeof(RecycleBinPage),
            _ => typeof(LibraryPage),
        };

        if (ShellFrame.CurrentSourcePageType != pageType)
        {
            ShellFrame.Navigate(pageType);
        }
    }
}
