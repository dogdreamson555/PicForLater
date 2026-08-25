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

    private void MainPage_Loaded(object sender, RoutedEventArgs e)
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
            ShellNavigation.SelectedItem = libraryItem;
        }

        if (App.PendingNotificationImageItemId is Guid imageItemId)
        {
            NavigateToLibraryItem(imageItemId);
        }
        else if (App.PendingReminderCreationImageItemId is Guid reminderImageItemId)
        {
            NavigateToReminderEditor(reminderImageItemId);
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

    private void App_NotificationImageRequested(Guid imageItemId) =>
        NavigateToLibraryItem(imageItemId);

    private void App_ReminderCreationRequested(Guid imageItemId) =>
        NavigateToReminderEditor(imageItemId);

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

    private void NavigateToLibraryItem(Guid imageItemId)
    {
        _suppressSelectionNavigation = true;
        try
        {
            if (ShellNavigation.MenuItems[0] is NavigationViewItem libraryItem)
            {
                ShellNavigation.SelectedItem = libraryItem;
            }
        }
        finally
        {
            _suppressSelectionNavigation = false;
        }

        ShellFrame.Navigate(typeof(LibraryPage), imageItemId.ToString("D"));
        App.ClearPendingNotificationNavigation(imageItemId);
    }

    private void NavigateToReminderEditor(Guid imageItemId)
    {
        _suppressSelectionNavigation = true;
        try
        {
            if (ShellNavigation.MenuItems[1] is NavigationViewItem remindersItem)
            {
                ShellNavigation.SelectedItem = remindersItem;
            }
        }
        finally
        {
            _suppressSelectionNavigation = false;
        }

        ShellFrame.Navigate(typeof(RemindersPage), imageItemId.ToString("D"));
        App.ClearPendingReminderCreation(imageItemId);
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

    private void ShellNavigation_SelectionChanged(
        NavigationView sender,
        NavigationViewSelectionChangedEventArgs args)
    {
        if (_suppressSelectionNavigation)
        {
            return;
        }

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
