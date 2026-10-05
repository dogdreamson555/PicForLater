# ADR 0007: Local Reminder Candidates, User Confirmation, and Notification Outbox

- Status: Accepted
- Date: 2026-07-28
- Revised: 2026-07-30, 2026-08-13 (see ADR 0015 for the unpackaged notification path)

## Context

Dates and locations in images may come from OCR, trusted metadata, or a local model, and may be ambiguous because of date ordering, missing years, time zones, or daylight saving time. System notifications are not a source of truth either: after a device is shut down or asleep beyond the delivery window, a notification may not be delivered later.

## Decision

1. Every analysis mode runs OCR first; the local vision model continues to run under its existing routing conditions. Reminder discovery independently merges deterministic OCR candidates with vision-model candidates and does not depend on the composer ultimately used for the title/summary. A model-supplemented candidate that OCR cannot verify verbatim is marked as low-confidence `ModelOnlyInterpretation`; the UI identifies its source and requires the user to compare it with the original image. It is not an OCR fact and does not create a reminder automatically.
2. After OCR, deterministic entity extraction generates date and location candidates. Date/time parsing uses the local `Microsoft.Recognizers.Text.DateTime`, which currently supports the formally declared and tested languages: Chinese, English, Spanish, French, Portuguese, German, Italian, and Turkish. Do not continue expanding application-specific scenario regexes. Candidates retain the original text, normalized value, source, OCR evidence and bounding box, reference time, time zone, and ambiguity. A model may supplement other languages or semantic scenarios, but cannot overwrite OCR facts or claim a capability that is not installed.

   For BCP-47 tags whose language is undetermined but writing system is known (such as `und-Hani` and `und-Latn`), select local parsing capabilities by writing system. A date and time may be combined only under restricted rules within the same text evidence: either (a) the date and time appear on the same line, separated by a weekday annotation or light punctuation, or (b) two adjacent, left-aligned lines with a small gap in the same text block contain only one date and one time, respectively. Do not combine across commas, semicolons, sentence boundaries, distant lines, or multiple events. Merge OCR and model results only when they resolve to the same instant in the same time zone. A complete date/time may absorb repeated date/time fragments in the same evidence; different instants remain separate candidates. If both types of evidence omit the year, use the current analysis year in the reference time zone and mark `MissingYear`; do not roll a past date into the next year.
3. Candidates are for user confirmation only. The user must verify the date, time, and Windows time zone, and may edit the location. A complete date without a time is provisionally set to `10:00`; a year and month only is provisionally set to the first day of that month at `10:00`; a year only is provisionally set to January 1 of that year at `10:00`. The UI explains these default fields; no reminder is written or notification scheduled before confirmation. Invalid dates, ambiguous numeric date ordering, and daylight-saving gaps or overlaps are never resolved automatically.
4. The reminders page uses a responsive list-and-editor layout. In a narrow window, the user can switch between the two views, and stable identifiers are retained for keyboard and automation interactions.
5. SQLite `Reminders` is the source of truth. Confirmation or editing first commits to the database and writes to `ReminderNotificationOutbox`; the database commit and system scheduling are not one transaction. A stable scheduler ID allows scheduling, retries, edits, and cancellations to be handled idempotently.
6. System notifications are a local projection. Clicking one opens the library item using stable reminder/image IDs. Reconcile on startup and when entering the reminders page: recover interrupted outbox work, requeue future reminders whose schedules are missing, and cancel orphan notifications with no active record. A reminder more than five minutes overdue without activation is marked “missed”; redelivery is not promised.
7. Soft-deleting an image writes a cancellation to the outbox and pauses its reminders. After restoration, future reminders require the user to confirm them again; past reminders are marked “missed.” Neither is rescheduled automatically.
8. The user may add a reminder manually from the detail view or context menu, even if the image has no recognized candidates. The editor starts with the title, thumbnail, and local time zone. `SourceDateCandidateId` and `SourceLocationCandidateId` may be null, but saving still uses the same fact records, outbox, future-time validation, and daylight-saving validation.
9. Analysis-completed events wake the current reminders page to refresh its query, with consecutive events coalesced. SQLite remains the source of candidate facts; background polling is not used.
10. “Candidates awaiting confirmation” is an actionable projection of SQLite facts. Absolute date-times that are known to have expired under the provisional defaults do not enter the queue. Times without a date and values that cannot be interpreted safely retain their evidence, but are not assumed to represent future reminders. Reanalysis atomically replaces only old candidates that are still `Pending`; confirmed or ignored decisions are not reset. Reminder titles come from each candidate's evidence line, so multiple reminders do not share the first event's title; the library title and summary continue to be saved by the existing analysis logic.

## Consequences

- The workflow stays local; it adds no online geocoding, cloud inference, telemetry, or account dependency. If notifications are unavailable, images, candidates, and reminders remain viewable and editable, and the UI explains delivery limitations.
- The schema migration upgrades to version 6, continuing to back up before migration and roll back on failure. Reminder titles are stored separately in `Reminders`; old blank titles fall back to the library title for display, and editing a reminder does not rewrite the library item.
- Entity parsing adds no model, network capability, or resident service. Deterministic location extraction retains only address snippets; it does not complete addresses online or perform reverse geocoding.

## Platform References

- [Scheduled app notifications](https://learn.microsoft.com/windows/apps/develop/notifications/app-notifications/app-notifications-scheduled)
- [App notifications quickstart](https://learn.microsoft.com/windows/apps/develop/notifications/app-notifications/app-notifications-quickstart)
