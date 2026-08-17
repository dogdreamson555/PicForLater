using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Windows.ApplicationModel.Resources;
using PicForLater.Analysis;
using PicForLater.App.Models;
using PicForLater.App.Services;
using PicForLater.Core.Library;
using PicForLater.Core.Reminders;
using PicForLater.Infrastructure.Storage;

namespace PicForLater.App.ViewModels;

public partial class RemindersPageViewModel : ObservableObject
{
    private const int MaximumReminderTitleLength = 300;
    private const int PageSize = 50;
    private static readonly TimeSpan DefaultCandidateTime = new(10, 0, 0);
    private static readonly ResourceLoader _resources = new();
    private static readonly ReminderCandidatePrefillResolver _candidatePrefillResolver = new();
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly IStorageReadinessService _storageReadiness;
    private readonly Func<IReminderService?> _reminderAccessor;
    private readonly Func<ILibraryService?> _libraryAccessor;
    private readonly Func<AppDataPaths?> _pathsAccessor;
    private readonly Dictionary<Guid, string> _imageSummaries = [];

    public RemindersPageViewModel(
        IStorageReadinessService storageReadiness,
        Func<IReminderService?> reminderAccessor,
        Func<ILibraryService?> libraryAccessor,
        Func<AppDataPaths?> pathsAccessor)
    {
        _storageReadiness = storageReadiness ?? throw new ArgumentNullException(nameof(storageReadiness));
        _reminderAccessor = reminderAccessor ?? throw new ArgumentNullException(nameof(reminderAccessor));
        _libraryAccessor = libraryAccessor ?? throw new ArgumentNullException(nameof(libraryAccessor));
        _pathsAccessor = pathsAccessor ?? throw new ArgumentNullException(nameof(pathsAccessor));
        foreach (var zone in TimeZoneInfo.GetSystemTimeZones()
                     .OrderBy(zone => zone.BaseUtcOffset)
                     .ThenBy(zone => zone.DisplayName, StringComparer.CurrentCulture))
        {
            TimeZones.Add(new TimeZoneOption(zone.Id, zone.DisplayName));
        }
    }

    public ObservableCollection<ReminderCandidateItem> Candidates { get; } = [];

    public ObservableCollection<ReminderListItem> Reminders { get; } = [];

    public ObservableCollection<TimeZoneOption> TimeZones { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLoading))]
    [NotifyPropertyChangedFor(nameof(IsReady))]
    [NotifyPropertyChangedFor(nameof(IsEmpty))]
    [NotifyPropertyChangedFor(nameof(HasError))]
    [NotifyPropertyChangedFor(nameof(IsPermissionDenied))]
    [NotifyPropertyChangedFor(nameof(IsUnsupported))]
    public partial RemindersViewState State { get; set; } = RemindersViewState.Loading;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCandidates))]
    public partial int CandidateCount { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasReminders))]
    public partial int ReminderCount { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCandidateEditor))]
    [NotifyPropertyChangedFor(nameof(IsReminderEditor))]
    public partial bool IsEditorOpen { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCandidateEditor))]
    [NotifyPropertyChangedFor(nameof(IsReminderEditor))]
    public partial bool IsEditingReminder { get; set; }

    [ObservableProperty]
    public partial string EditorTitle { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string EditorEvidence { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string EditorSummary { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasEditorAmbiguity))]
    public partial string EditorAmbiguity { get; set; } = string.Empty;

    [ObservableProperty]
    public partial DateTimeOffset? EditorDate { get; set; }

    [ObservableProperty]
    public partial TimeSpan? EditorTime { get; set; }

    [ObservableProperty]
    public partial TimeZoneOption? SelectedTimeZone { get; set; }

    [ObservableProperty]
    public partial string EditorLocation { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string EditorThumbnailUri { get; set; } = string.Empty;

    [ObservableProperty]
    public partial Guid? EditorImageItemId { get; set; }

    [ObservableProperty]
    public partial bool EditorReminderIsCancelled { get; set; }

    [ObservableProperty]
    public partial bool EditorReminderCanBeCancelled { get; set; }

    [ObservableProperty]
    public partial string EditorPrimaryActionText { get; set; } =
        _resources.GetString("ReminderPrimaryConfirmText");

    [ObservableProperty]
    public partial string EditorPrimaryActionAutomationName { get; set; } =
        _resources.GetString("ReminderPrimaryConfirmAutomationName");

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStatusMessage))]
    public partial string StatusMessage { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasErrorMessage))]
    public partial string ErrorMessage { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NotificationsUnavailable))]
    public partial bool NotificationsSupported { get; set; } = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanInteract))]
    public partial bool IsWorking { get; set; }

    [ObservableProperty]
    public partial bool HasMoreCandidates { get; set; }

    [ObservableProperty]
    public partial bool HasMoreReminders { get; set; }

    [ObservableProperty]
    public partial bool IsSelectionModeActive { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedReminders))]
    public partial int SelectedReminderCount { get; set; }

    [ObservableProperty]
    public partial string SelectionSummary { get; set; } = string.Empty;

    private Guid? EditingCandidateId { get; set; }

    private Guid? EditingLocationCandidateId { get; set; }

    private Guid? EditingReminderId { get; set; }

    public Guid? EditorReminderId => EditingReminderId;

    public bool IsLoading => State == RemindersViewState.Loading;

    public bool IsReady => State == RemindersViewState.Ready;

    public bool IsEmpty => State == RemindersViewState.Empty;

    public bool HasError => State == RemindersViewState.Error;

    public bool IsPermissionDenied => State == RemindersViewState.PermissionDenied;

    public bool IsUnsupported => State == RemindersViewState.Unsupported;

    public bool HasCandidates => CandidateCount > 0;

    public bool HasReminders => ReminderCount > 0;

    public bool IsCandidateEditor =>
        IsEditorOpen && !IsEditingReminder && EditingCandidateId is not null;

    public bool IsReminderEditor => IsEditorOpen && IsEditingReminder;

    public bool HasEditorAmbiguity => !string.IsNullOrWhiteSpace(EditorAmbiguity);

    public bool HasStatusMessage => !string.IsNullOrWhiteSpace(StatusMessage);

    public bool HasErrorMessage => !string.IsNullOrWhiteSpace(ErrorMessage);

    public bool NotificationsUnavailable => !NotificationsSupported;

    public bool CanInteract => !IsWorking;

    public bool HasSelectedReminders => SelectedReminderCount > 0;

    public async Task InitializeAsync(bool forceRetry = false)
    {
        State = RemindersViewState.Loading;
        ErrorMessage = string.Empty;
        var readiness = await _storageReadiness.EnsureReadyAsync(forceRetry).ConfigureAwait(true);
        if (readiness.Status != StorageReadinessStatus.Ready)
        {
            State = readiness.Status switch
            {
                StorageReadinessStatus.PermissionDenied => RemindersViewState.PermissionDenied,
                StorageReadinessStatus.Unsupported => RemindersViewState.Unsupported,
                _ => RemindersViewState.Error,
            };
            return;
        }

        await RefreshAsync(reconcile: true).ConfigureAwait(true);
    }

    public Task RefreshAfterAnalysisAsync() => RefreshAsync(reconcile: false);

    public void EditCandidate(ReminderCandidateItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        ResetEditor();
        var candidate = item.Candidate;
        EditingCandidateId = candidate.Id;
        EditingLocationCandidateId = candidate.SuggestedLocationCandidateId;
        EditorImageItemId = candidate.ImageItemId;
        EditorThumbnailUri = item.ThumbnailUri;
        IsEditingReminder = false;
        SetEditorPrimaryAction("ReminderPrimaryConfirmText", "ReminderPrimaryConfirmAutomationName");
        EditorTitle = item.DisplayTitle;
        EditorSummary = item.SummaryText;
        EditorEvidence = string.IsNullOrWhiteSpace(candidate.SuggestedLocationEvidence)
            ? candidate.Evidence
            : string.Format(
                CultureInfo.CurrentCulture,
                _resources.GetString("ReminderDateLocationEvidenceFormat"),
                candidate.Evidence,
                candidate.SuggestedLocationEvidence);
        EditorAmbiguity = item.AmbiguityText;
        EditorLocation = candidate.SuggestedLocation ?? string.Empty;
        ApplyCandidateDate(candidate);
        SelectTimeZone(candidate.TimeZoneId);
        IsEditorOpen = true;
    }

    public void EditReminder(ReminderListItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        ResetEditor();
        var reminder = item.Reminder;
        EditingReminderId = reminder.Id;
        EditorImageItemId = reminder.ImageItemId;
        EditorThumbnailUri = item.ThumbnailUri;
        IsEditingReminder = true;
        EditorReminderIsCancelled = string.Equals(
            reminder.CompletionReason,
            "Cancelled",
            StringComparison.Ordinal);
        EditorReminderCanBeCancelled = reminder.State == ReminderState.Active;
        SetEditorPrimaryAction(
            EditorReminderIsCancelled
                ? "ReminderPrimaryRestoreText"
                : "ReminderPrimarySaveChangesText",
            EditorReminderIsCancelled
                ? "ReminderPrimaryRestoreAutomationName"
                : "ReminderPrimarySaveChangesAutomationName");
        EditorTitle = reminder.ImageTitle;
        EditorSummary = item.SummaryText;
        EditorEvidence = _resources.GetString(
            EditorReminderIsCancelled
                ? "ReminderCancelledEvidence"
                : "ReminderConfirmedEvidence");
        EditorLocation = reminder.ConfirmedLocation ?? string.Empty;
        SelectTimeZone(reminder.TimeZoneId);
        var zone = SelectedTimeZone is null
            ? TimeZoneInfo.Local
            : TimeZoneInfo.FindSystemTimeZoneById(SelectedTimeZone.Id);
        var localDue = TimeZoneInfo.ConvertTime(reminder.DueAtUtc, zone);
        EditorDate = new DateTimeOffset(localDue.Date, TimeSpan.Zero);
        EditorTime = localDue.TimeOfDay;
        IsEditorOpen = true;
    }

    public async Task<bool> CreateManualReminderAsync(Guid imageItemId)
    {
        ResetEditor();
        var entry = await GetLibrary().GetAsync(imageItemId).ConfigureAwait(true);
        if (entry is null || entry.Item.DeletedAtUtc is not null)
        {
            ErrorMessage = _resources.GetString("ReminderManualImageUnavailableError");
            return false;
        }

        var initialDue = DateTimeOffset.Now.AddHours(1);
        EditorImageItemId = imageItemId;
        EditorThumbnailUri = ToUri(
            entry.Asset.ThumbnailRelativePath ?? entry.Asset.OriginalRelativePath);
        IsEditingReminder = false;
        SetEditorPrimaryAction("ReminderPrimaryConfirmText", "ReminderPrimaryConfirmAutomationName");
        EditorTitle = entry.Item.Title;
        EditorSummary = entry.Item.Summary;
        EditorEvidence = _resources.GetString("ReminderManualEvidence");
        EditorAmbiguity = string.Empty;
        EditorDate = new DateTimeOffset(initialDue.Date, TimeSpan.Zero);
        EditorTime = new TimeSpan(initialDue.Hour, initialDue.Minute, 0);
        EditorLocation = string.Empty;
        SelectTimeZone(TimeZoneInfo.Local.Id);
        State = RemindersViewState.Ready;
        IsEditorOpen = true;
        return true;
    }

    [RelayCommand]
    private Task RetryAsync() => InitializeAsync(forceRetry: true);

    [RelayCommand]
    private async Task SaveEditorAsync()
    {
        if (EditorDate is null || EditorTime is null || SelectedTimeZone is null)
        {
            ErrorMessage = _resources.GetString("ReminderDateTimeRequiredError");
            return;
        }

        var localDue = EditorDate.Value.Date.Add(EditorTime.Value);
        IsWorking = true;
        ErrorMessage = string.Empty;
        try
        {
            if (IsEditingReminder && EditingReminderId is Guid reminderId)
            {
                var wasCancelled = EditorReminderIsCancelled;
                await GetService().UpdateAsync(
                    new ReminderUpdate(
                        reminderId,
                        EditorTitle,
                        localDue,
                        SelectedTimeZone.Id,
                        EditorLocation)).ConfigureAwait(true);
                StatusMessage = _resources.GetString(
                    wasCancelled
                        ? "ReminderRestoredStatus"
                        : "ReminderUpdatedStatus");
            }
            else if (EditorImageItemId is Guid imageItemId)
            {
                await GetService().ConfirmAsync(
                    new ReminderConfirmation(
                        imageItemId,
                        EditingCandidateId,
                        EditingLocationCandidateId,
                        EditorTitle,
                        localDue,
                        SelectedTimeZone.Id,
                        EditorLocation)).ConfigureAwait(true);
                StatusMessage = _resources.GetString("ReminderConfirmedStatus");
            }

            ResetEditor();
            await RefreshAsync(reconcile: false).ConfigureAwait(true);
        }
        catch (ReminderValidationException exception)
        {
            ErrorMessage = GetValidationMessage(exception.ErrorCode);
        }
        catch
        {
            ErrorMessage = _resources.GetString("ReminderSaveFailedError");
        }
        finally
        {
            IsWorking = false;
        }
    }

    [RelayCommand]
    private async Task DismissCandidateAsync()
    {
        if (EditingCandidateId is not Guid candidateId)
        {
            return;
        }

        IsWorking = true;
        try
        {
            await GetService().DismissCandidateAsync(candidateId).ConfigureAwait(true);
            StatusMessage = _resources.GetString("ReminderCandidateDismissedStatus");
            ResetEditor();
            await RefreshAsync(reconcile: false).ConfigureAwait(true);
        }
        catch
        {
            ErrorMessage = _resources.GetString("ReminderCandidateDismissFailedError");
        }
        finally
        {
            IsWorking = false;
        }
    }

    [RelayCommand]
    private async Task CancelReminderAsync()
    {
        if (EditingReminderId is not Guid reminderId)
        {
            return;
        }

        IsWorking = true;
        try
        {
            await GetService().CancelAsync(reminderId).ConfigureAwait(true);
            StatusMessage = _resources.GetString("ReminderCancelledStatus");
            ResetEditor();
            await RefreshAsync(reconcile: false).ConfigureAwait(true);
        }
        catch
        {
            ErrorMessage = _resources.GetString("ReminderCancelFailedError");
        }
        finally
        {
            IsWorking = false;
        }
    }

    public void SetSelectionMode(bool isActive)
    {
        IsSelectionModeActive = isActive;
        if (!isActive)
        {
            SetSelectedReminderCount(0);
        }
    }

    public void SetSelectedReminderCount(int count)
    {
        SelectedReminderCount = Math.Max(0, count);
        SelectionSummary = string.Format(
            CultureInfo.CurrentCulture,
            _resources.GetString("ReminderSelectionCountFormat"),
            SelectedReminderCount);
    }

    public async Task<(int DeletedCount, int FailedCount)> DeleteRemindersAsync(
        IReadOnlyCollection<Guid> reminderIds)
    {
        ArgumentNullException.ThrowIfNull(reminderIds);
        var ids = reminderIds.Distinct().ToArray();
        if (ids.Length == 0)
        {
            return (0, 0);
        }

        IsWorking = true;
        ErrorMessage = string.Empty;
        var deleted = 0;
        var failed = 0;
        foreach (var reminderId in ids)
        {
            try
            {
                await GetService().DeleteAsync(reminderId).ConfigureAwait(true);
                deleted++;
            }
            catch
            {
                failed++;
            }
        }

        if (EditingReminderId is Guid editingId && ids.Contains(editingId))
        {
            ResetEditor();
        }

        IsSelectionModeActive = false;
        SetSelectedReminderCount(0);
        try
        {
            await RefreshAsync(reconcile: false).ConfigureAwait(true);
            StatusMessage = string.Format(
                CultureInfo.CurrentCulture,
                _resources.GetString("ReminderDeletedStatusFormat"),
                deleted,
                failed);
        }
        finally
        {
            IsWorking = false;
        }

        return (deleted, failed);
    }

    [RelayCommand]
    private void CloseEditor() => ResetEditor();

    [RelayCommand]
    private async Task LoadMoreCandidatesAsync()
    {
        if (IsWorking || !HasMoreCandidates)
        {
            return;
        }

        IsWorking = true;
        try
        {
            await AppendCandidatesAsync().ConfigureAwait(true);
        }
        catch
        {
            ErrorMessage = _resources.GetString("ReminderLoadMoreFailedError");
        }
        finally
        {
            IsWorking = false;
        }
    }

    [RelayCommand]
    private async Task LoadMoreRemindersAsync()
    {
        if (IsWorking || !HasMoreReminders)
        {
            return;
        }

        IsWorking = true;
        try
        {
            await AppendRemindersAsync().ConfigureAwait(true);
        }
        catch
        {
            ErrorMessage = _resources.GetString("ReminderLoadMoreFailedError");
        }
        finally
        {
            IsWorking = false;
        }
    }

    private async Task RefreshAsync(bool reconcile)
    {
        await _refreshGate.WaitAsync().ConfigureAwait(true);
        try
        {
            var service = GetService();
            if (reconcile)
            {
                var result = await Task.Run(() => service.ReconcileAsync()).ConfigureAwait(true);
                NotificationsSupported = result.NotificationsSupported;
                if (result.MissedCount > 0)
                {
                    StatusMessage = string.Format(
                        CultureInfo.CurrentCulture,
                        _resources.GetString("MissedReminderReconciledFormat"),
                        result.MissedCount);
                }
            }

            Candidates.Clear();
            Reminders.Clear();
            _imageSummaries.Clear();
            await AppendCandidatesAsync().ConfigureAwait(true);
            await AppendRemindersAsync().ConfigureAwait(true);
            State = CandidateCount == 0 && ReminderCount == 0
                ? RemindersViewState.Empty
                : RemindersViewState.Ready;
        }
        catch
        {
            State = RemindersViewState.Error;
            ErrorMessage = _resources.GetString("ReminderLoadFailedError");
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    private async Task AppendCandidatesAsync()
    {
        var service = GetService();
        var offset = Candidates.Count;
        var page = await Task.Run(
            () => service.GetPendingCandidatesAsync(offset, PageSize + 1)).ConfigureAwait(true);
        var allCandidates = Candidates
            .Select(item => item.Candidate)
            .Concat(page.Take(PageSize))
            .GroupBy(candidate => candidate.Id)
            .Select(group => group.First())
            .ToArray();
        var candidateCountsByImage = allCandidates
            .GroupBy(candidate => candidate.ImageItemId)
            .ToDictionary(group => group.Key, group => group.Count());
        await EnsureImageSummariesAsync(
            allCandidates.Select(candidate => candidate.ImageItemId)).ConfigureAwait(true);

        Candidates.Clear();
        foreach (var candidate in allCandidates)
        {
            Candidates.Add(MapCandidate(
                candidate,
                candidateCountsByImage[candidate.ImageItemId] > 1));
        }

        HasMoreCandidates = page.Count > PageSize;
        CandidateCount = Candidates.Count;
    }

    private async Task AppendRemindersAsync()
    {
        var service = GetService();
        var offset = Reminders.Count;
        var page = await Task.Run(
            () => service.GetRemindersAsync(offset, PageSize + 1)).ConfigureAwait(true);
        var remindersToAppend = page.Take(PageSize).ToArray();
        await EnsureImageSummariesAsync(
            remindersToAppend.Select(reminder => reminder.ImageItemId)).ConfigureAwait(true);
        foreach (var reminder in remindersToAppend)
        {
            if (Reminders.All(existing => existing.Reminder.Id != reminder.Id))
            {
                Reminders.Add(MapReminder(reminder));
            }
        }

        HasMoreReminders = page.Count > PageSize;
        ReminderCount = Reminders.Count;
    }

    private ReminderCandidateItem MapCandidate(
        ReminderCandidate candidate,
        bool imageHasMultipleCandidates)
    {
        var suggestedDate = candidate.NormalizedValue is null
            ? candidate.RawText
            : FormatNormalizedDate(candidate.NormalizedValue);
        var source = _resources.GetString($"ReminderCandidateSource{candidate.Source}");
        var ambiguity = GetCandidateAmbiguity(candidate);
        return new ReminderCandidateItem(
            candidate,
            CreateCandidateDisplayTitle(candidate, imageHasMultipleCandidates),
            suggestedDate,
            source,
            ambiguity,
            candidate.SuggestedLocation ?? _resources.GetString("ReminderNoLocation"),
            _imageSummaries.GetValueOrDefault(candidate.ImageItemId, string.Empty),
            ToUri(candidate.PreviewRelativePath));
    }

    private static string CreateCandidateDisplayTitle(
        ReminderCandidate candidate,
        bool imageHasMultipleCandidates)
    {
        if (!imageHasMultipleCandidates)
        {
            return candidate.ImageTitle;
        }

        var evidenceLines = candidate.Evidence.Split(
            ['\r', '\n'],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var title = evidenceLines.FirstOrDefault(line =>
                line.Contains(candidate.RawText, StringComparison.CurrentCulture))
            ?? evidenceLines.FirstOrDefault()
            ?? candidate.RawText;
        if (string.IsNullOrWhiteSpace(title))
        {
            title = candidate.ImageTitle;
        }

        if (title.Length <= MaximumReminderTitleLength)
        {
            return title;
        }

        var length = MaximumReminderTitleLength;
        if (char.IsHighSurrogate(title[length - 1]))
        {
            length--;
        }

        return title[..length];
    }

    private ReminderListItem MapReminder(Reminder reminder)
    {
        TimeZoneInfo zone;
        try
        {
            zone = TimeZoneInfo.FindSystemTimeZoneById(reminder.TimeZoneId);
        }
        catch
        {
            zone = TimeZoneInfo.Utc;
        }

        var due = TimeZoneInfo.ConvertTime(reminder.DueAtUtc, zone);
        var stateResourceName = reminder.CompletionReason == "Cancelled"
            ? "ReminderStateCancelled"
            : $"ReminderState{reminder.State}";
        return new ReminderListItem(
            reminder,
            string.Format(
                CultureInfo.CurrentCulture,
                _resources.GetString("ReminderDueWithZoneFormat"),
                due,
                zone.StandardName),
            _resources.GetString(stateResourceName),
            _resources.GetString($"ReminderNotificationState{reminder.NotificationState}"),
            reminder.ConfirmedLocation ?? _resources.GetString("ReminderNoLocation"),
            _imageSummaries.GetValueOrDefault(reminder.ImageItemId, string.Empty),
            ToUri(reminder.PreviewRelativePath));
    }

    private async Task EnsureImageSummariesAsync(IEnumerable<Guid> imageItemIds)
    {
        var missingIds = imageItemIds
            .Distinct()
            .Where(imageItemId => !_imageSummaries.ContainsKey(imageItemId))
            .ToArray();
        if (missingIds.Length == 0)
        {
            return;
        }

        var library = GetLibrary();
        var summaries = await Task.Run(
            () => library.GetSummariesAsync(missingIds)).ConfigureAwait(true);
        foreach (var imageItemId in missingIds)
        {
            _imageSummaries[imageItemId] = summaries.GetValueOrDefault(imageItemId, string.Empty);
        }
    }

    private void ApplyNormalizedDate(string? normalized)
    {
        EditorDate = null;
        EditorTime = null;
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return;
        }

        if (DateTime.TryParseExact(
                normalized,
                [
                    "yyyy-MM-dd'T'HH:mm:ss",
                    "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF",
                ],
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var dateTime))
        {
            EditorDate = new DateTimeOffset(dateTime.Date, TimeSpan.Zero);
            EditorTime = dateTime.TimeOfDay;
            return;
        }

        if (DateTime.TryParseExact(
                normalized,
                "yyyy-MM-dd",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var date))
        {
            EditorDate = new DateTimeOffset(date.Date, TimeSpan.Zero);
            EditorTime = DefaultCandidateTime;
            return;
        }

        if (DateTime.TryParseExact(
                normalized,
                "yyyy-MM",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var yearMonth))
        {
            EditorDate = new DateTimeOffset(yearMonth.Date, TimeSpan.Zero);
            EditorTime = DefaultCandidateTime;
            return;
        }

        if (DateTime.TryParseExact(
                normalized,
                "yyyy",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var year))
        {
            EditorDate = new DateTimeOffset(year.Date, TimeSpan.Zero);
            EditorTime = DefaultCandidateTime;
        }
    }

    private void ApplyCandidateDate(ReminderCandidate candidate)
    {
        ApplyNormalizedDate(candidate.NormalizedValue);
        if (EditorDate is not null)
        {
            return;
        }

        // Older model candidates may retain evidence while an older schema
        // discarded their normalized value. Recover only an editable prefill;
        // the stored fact remains unchanged until the user confirms.
        var resolved = _candidatePrefillResolver.Resolve(candidate);
        if (resolved is null)
        {
            return;
        }

        ApplyNormalizedDate(resolved.NormalizedValue);
        if (resolved.AmbiguityReason == "MissingMonthAndDay")
        {
            AppendEditorAmbiguity(_resources.GetString("ReminderMissingMonthAndDay"));
        }
        else if (resolved.AmbiguityReason == "MissingDay")
        {
            AppendEditorAmbiguity(_resources.GetString("ReminderMissingDay"));
        }

        if (IsDateOnlyNormalized(resolved.NormalizedValue))
        {
            AppendEditorAmbiguity(_resources.GetString("ReminderDefaultTime"));
        }
    }

    private void SelectTimeZone(string? timeZoneId)
    {
        SelectedTimeZone = TimeZones.FirstOrDefault(zone =>
                string.Equals(zone.Id, timeZoneId, StringComparison.Ordinal))
            ?? TimeZones.FirstOrDefault(zone =>
                string.Equals(zone.Id, TimeZoneInfo.Local.Id, StringComparison.Ordinal))
            ?? TimeZones.FirstOrDefault();
    }

    private void ResetEditor()
    {
        IsEditorOpen = false;
        IsEditingReminder = false;
        EditorReminderIsCancelled = false;
        EditorReminderCanBeCancelled = false;
        SetEditorPrimaryAction("ReminderPrimaryConfirmText", "ReminderPrimaryConfirmAutomationName");
        EditingCandidateId = null;
        EditingLocationCandidateId = null;
        EditingReminderId = null;
        EditorImageItemId = null;
        EditorThumbnailUri = string.Empty;
        EditorTitle = string.Empty;
        EditorSummary = string.Empty;
        EditorEvidence = string.Empty;
        EditorAmbiguity = string.Empty;
        EditorDate = null;
        EditorTime = null;
        EditorLocation = string.Empty;
        SelectedTimeZone = null;
        ErrorMessage = string.Empty;
        if (CandidateCount == 0
            && ReminderCount == 0
            && State == RemindersViewState.Ready)
        {
            State = RemindersViewState.Empty;
        }
    }

    private void SetEditorPrimaryAction(string textResource, string automationNameResource)
    {
        EditorPrimaryActionText = _resources.GetString(textResource);
        EditorPrimaryActionAutomationName = _resources.GetString(automationNameResource);
    }

    private static string FormatNormalizedDate(string normalized)
    {
        if (DateTime.TryParseExact(
                normalized,
                "yyyy",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var year))
        {
            return year.ToString("yyyy", CultureInfo.CurrentCulture);
        }

        if (DateTime.TryParseExact(
                normalized,
                "yyyy-MM",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var yearMonth))
        {
            return yearMonth.ToString("Y", CultureInfo.CurrentCulture);
        }

        if (DateTime.TryParse(
                normalized,
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var date))
        {
            return date.ToString(
                normalized.Contains('T') ? "g" : "d",
                CultureInfo.CurrentCulture);
        }

        return normalized;
    }

    private static string GetCandidateAmbiguity(ReminderCandidate candidate)
    {
        var messages = new List<string>();
        if (IsYear(candidate.NormalizedValue))
        {
            messages.Add(_resources.GetString("ReminderMissingMonthAndDay"));
        }
        else if (IsYearMonth(candidate.NormalizedValue))
        {
            messages.Add(_resources.GetString("ReminderMissingDay"));
        }
        else
        {
            var message = candidate.AmbiguityReason switch
            {
                "DateOrder" => _resources.GetString("ReminderAmbiguousDateOrder"),
                "MissingYear" => _resources.GetString("ReminderMissingYear"),
                "MissingDate" => _resources.GetString("ReminderMissingDate"),
                "MissingMonthAndDay" => _resources.GetString("ReminderMissingMonthAndDay"),
                "MissingDay" => _resources.GetString("ReminderMissingDay"),
                "TimeOfDay" => _resources.GetString("ReminderAmbiguousTimeOfDay"),
                "RelativeDate" => _resources.GetString("ReminderRelativeDate"),
                "ModelInterpretation" => _resources.GetString("ReminderModelInterpretation"),
                "ModelOnlyInterpretation" => _resources.GetString("ReminderModelOnlyInterpretation"),
                "RemoteVisionNoLocalOcrEvidence" =>
                    _resources.GetString("ReminderRemoteVisionNoLocalOcrEvidence"),
                null or "" => string.Empty,
                _ => _resources.GetString("ReminderCandidateNeedsReview"),
            };
            if (!string.IsNullOrWhiteSpace(message))
            {
                messages.Add(message);
            }
        }

        if (IsDateOnlyNormalized(candidate.NormalizedValue))
        {
            messages.Add(_resources.GetString("ReminderDefaultTime"));
        }

        return string.Join(" ", messages.Distinct(StringComparer.CurrentCulture));
    }

    private void AppendEditorAmbiguity(string message)
    {
        if (string.IsNullOrWhiteSpace(message)
            || EditorAmbiguity.Contains(message, StringComparison.CurrentCulture))
        {
            return;
        }

        EditorAmbiguity = string.IsNullOrWhiteSpace(EditorAmbiguity)
            ? message
            : $"{EditorAmbiguity} {message}";
    }

    private static bool IsDateOnlyNormalized(string? normalized) =>
        IsYear(normalized)
        || IsYearMonth(normalized)
        || DateTime.TryParseExact(
            normalized,
            "yyyy-MM-dd",
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out _);

    private static bool IsYear(string? normalized) =>
        DateTime.TryParseExact(
            normalized,
            "yyyy",
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out _);

    private static bool IsYearMonth(string? normalized) =>
        DateTime.TryParseExact(
            normalized,
            "yyyy-MM",
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out _);

    private static string GetValidationMessage(string errorCode)
    {
        var resourceName = errorCode switch
        {
            "DueTimeMustBeFuture" => "ReminderDueTimeFutureError",
            "DaylightSavingInvalidTime" => "ReminderDstInvalidError",
            "DaylightSavingAmbiguousTime" => "ReminderDstAmbiguousError",
            "TimeZoneUnavailable" or "TimeZoneInvalid" => "ReminderTimeZoneError",
            "TitleRequired" => "ReminderTitleRequiredError",
            "TitleTooLong" => "ReminderTitleTooLongError",
            "LocationTooLong" => "ReminderLocationTooLongError",
            "CandidateUnavailable" => "ReminderCandidateUnavailableError",
            _ => "ReminderSaveFailedError",
        };
        return _resources.GetString(resourceName);
    }

    private IReminderService GetService() =>
        _reminderAccessor() ?? throw new InvalidOperationException("The reminder service is unavailable.");

    private ILibraryService GetLibrary() =>
        _libraryAccessor() ?? throw new InvalidOperationException("The library service is unavailable.");

    private string ToUri(PicForLater.Core.Images.ManagedRelativePath? relativePath)
    {
        if (relativePath is null)
        {
            return string.Empty;
        }

        var paths = _pathsAccessor()
            ?? throw new InvalidOperationException("Managed paths are unavailable.");
        return new Uri(paths.Resolve(relativePath)).AbsoluteUri;
    }
}
