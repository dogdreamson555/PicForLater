using CommunityToolkit.WinUI.Notifications;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;
using Microsoft.Windows.ApplicationModel.Resources;
using PicForLater.Core.Reminders;
using Windows.Data.Xml.Dom;
using Windows.UI.Notifications;

namespace PicForLater.App.Services;

public sealed class WindowsReminderNotificationScheduler : IReminderNotificationScheduler
{
    internal const string NotificationGroup = "picforlater";
    private static readonly ResourceLoader _resources = new();

    public bool IsSupported
    {
        get
        {
            try
            {
                return ToastNotificationManagerCompat.CreateToastNotifier().Setting
                       == NotificationSetting.Enabled;
            }
            catch
            {
                return false;
            }
        }
    }

    public Task<IReadOnlySet<string>> GetScheduledIdsAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var ids = ToastNotificationManagerCompat.CreateToastNotifier()
            .GetScheduledToastNotifications()
            .Where(notification =>
                string.Equals(notification.Group, NotificationGroup, StringComparison.Ordinal))
            .Select(notification => notification.Tag)
            .Where(tag => !string.IsNullOrWhiteSpace(tag))
            .ToHashSet(StringComparer.Ordinal);
        return Task.FromResult<IReadOnlySet<string>>(ids);
    }

    public Task ScheduleAsync(
        ReminderNotification notification,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(notification);
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsSupported)
        {
            throw new InvalidOperationException("NotificationsUnsupported");
        }

        var builder = new AppNotificationBuilder()
            .AddArgument("reminderId", notification.ReminderId.ToString("D"))
            .AddArgument("imageItemId", notification.ImageItemId.ToString("D"))
            .AddText(notification.Title)
            .AddText(_resources.GetString("NotificationReminderBody"))
            .AddButton(
                new AppNotificationButton(_resources.GetString("NotificationOpenButton"))
                    .AddArgument("reminderId", notification.ReminderId.ToString("D"))
                    .AddArgument("imageItemId", notification.ImageItemId.ToString("D")))
            .SetScenario(AppNotificationScenario.Reminder);
        if (!string.IsNullOrWhiteSpace(notification.Location))
        {
            builder.AddText(string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                _resources.GetString("NotificationLocationFormat"),
                notification.Location));
        }

        var document = new XmlDocument();
        document.LoadXml(builder.BuildNotification().Payload);
        var scheduled = new ScheduledToastNotification(document, notification.DueAtUtc)
        {
            Tag = notification.SchedulerId,
            Group = NotificationGroup,
        };
        var notifier = ToastNotificationManagerCompat.CreateToastNotifier();
        foreach (var existing in notifier.GetScheduledToastNotifications().Where(existing =>
                     string.Equals(existing.Tag, notification.SchedulerId, StringComparison.Ordinal)
                     && string.Equals(existing.Group, NotificationGroup, StringComparison.Ordinal)))
        {
            notifier.RemoveFromSchedule(existing);
        }

        notifier.AddToSchedule(scheduled);
        return Task.CompletedTask;
    }

    public Task CancelAsync(
        string schedulerId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(schedulerId);
        cancellationToken.ThrowIfCancellationRequested();
        var notifier = ToastNotificationManagerCompat.CreateToastNotifier();
        foreach (var existing in notifier.GetScheduledToastNotifications().Where(existing =>
                     string.Equals(existing.Tag, schedulerId, StringComparison.Ordinal)
                     && string.Equals(existing.Group, NotificationGroup, StringComparison.Ordinal)))
        {
            notifier.RemoveFromSchedule(existing);
        }

        return Task.CompletedTask;
    }
}

#if PICFORLATER_UI_TESTING
public sealed class InMemoryReminderNotificationScheduler : IReminderNotificationScheduler
{
    private readonly Dictionary<string, ReminderNotification> _scheduled =
        new(StringComparer.Ordinal);

    public bool IsSupported => true;

    public Task<IReadOnlySet<string>> GetScheduledIdsAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_scheduled)
        {
            return Task.FromResult<IReadOnlySet<string>>(
                _scheduled.Keys.ToHashSet(StringComparer.Ordinal));
        }
    }

    public Task ScheduleAsync(
        ReminderNotification notification,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(notification);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_scheduled)
        {
            _scheduled[notification.SchedulerId] = notification;
        }

        return Task.CompletedTask;
    }

    public Task CancelAsync(
        string schedulerId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_scheduled)
        {
            _scheduled.Remove(schedulerId);
        }

        return Task.CompletedTask;
    }
}
#endif
