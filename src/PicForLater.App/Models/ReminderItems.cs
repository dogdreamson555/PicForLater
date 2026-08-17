using PicForLater.Core.Reminders;

namespace PicForLater.App.Models;

public sealed record ReminderCandidateItem(
    ReminderCandidate Candidate,
    string DisplayTitle,
    string SuggestedDateText,
    string SourceText,
    string AmbiguityText,
    string LocationText,
    string SummaryText,
    string ThumbnailUri);

public sealed record ReminderListItem(
    Reminder Reminder,
    string DueText,
    string StateText,
    string NotificationStateText,
    string LocationText,
    string SummaryText,
    string ThumbnailUri);

public sealed record TimeZoneOption(string Id, string DisplayName);

public enum RemindersViewState
{
    Loading,
    Ready,
    Empty,
    Error,
    PermissionDenied,
    Unsupported,
}
