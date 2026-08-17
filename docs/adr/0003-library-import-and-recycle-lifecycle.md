# ADR 0003: Library import, search, categories, and recycle lifecycle

- Status: Accepted
- Date: 2026-07-18

## Context

The first application shell already established a WinUI 3 app, an
app-private SQLite database, content-addressed immutable originals, and a
recoverable migration process. The next vertical slice must make images useful
without coupling a successful import to OCR or model availability.

This slice also changes the persisted schema. Existing databases must be
upgraded without rebuilding or silently discarding user data.

## Decision

1. Import remains an application use case with platform-neutral persistence.
   WinUI supplies a Windows Imaging Component provider that validates decoding,
   respects EXIF orientation, and emits a PNG thumbnail whose longest edge is
   at most 320 pixels.
2. Every import independently follows staging, hashing, decoding, immutable
   promotion, thumbnail creation, and one SQLite commit for the asset, item,
   completed import job, and queued analysis job. Failure performs best-effort
   compensation; it never rolls back other images in the same UI batch.
3. Clipboard bitmaps are first normalized to a lossless PNG. Clipboard access
   occurs only for an explicit paste command or the in-window `Ctrl+V`
   accelerator.
4. Content hashes are the duplicate key. A duplicate import completes its own
   import job as `Duplicate` and returns the existing item ID instead of creating
   another managed copy.
5. SQLite migration 2 adds categories and assignments, manual category
   exclusions, searchable analysis/reminder fields, and persistent deletion
   jobs. The existing initializer creates and verifies a pre-migration backup
   before applying it atomically.
6. Search executes in SQLite with bounded paging and covers titles, summaries,
   OCR text, category names, and confirmed reminder locations. The UI never
   loads the full library simply to filter it.
7. Normal deletion only sets `DeletedAtUtc` and cancels scheduled reminder
   state. Restore keeps metadata and categories, and marks future reminders for
   user reconfirmation instead of automatically rescheduling them.
8. Permanent deletion first records a deletion plan. Files are removed only
   when the asset has no other item reference; metadata is removed after file
   cleanup succeeds. A partial failure leaves the soft-deleted item and retryable
   plan rather than creating an active record that points to a missing file.

## Consequences

- Imports are useful offline before the analysis worker exists; new items show
  `Pending` and have a durable queued analysis job.
- Originals remain immutable and are never used as scrolling-grid sources;
  the grid reads bounded thumbnail files.
- Schema upgrades consume backup space temporarily. The application does not
  automatically delete those verified migration backups.
- `LIKE`-based bounded search is sufficient for the first local slice. If
  benchmarks later justify FTS, it will require a separate tested migration and
  rebuild strategy rather than an in-place assumption.
- Deletion jobs are durable but this slice retries them when the user repeats the
  permanent-delete action; automatic startup reconciliation can be added later
  without changing the tombstone format.
