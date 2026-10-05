# ADR 0002: Local Storage and Migration Baseline

- Status: Accepted (production root path revised by ADR 0015)
- Date: 2026-07-17

## Context

Images must be stored reliably before entering a recoverable analysis workflow. SQLite, the file system, and future system notifications cannot form a single transaction, so the first phase must establish stable paths, migration behavior, and failure semantics. This avoids later consistency fixes that delete or recreate the database.

## Decision

The application composition root passes `%LocalAppData%\PicForLater` to Infrastructure as the unpackaged production runtime root. Core and tests do not read real user directories. The fixed layout is:

```text
data/picforlater.db
data/backups/
assets/originals/
cache/thumbnails/
staging/
```

- SQLite stores metadata, jobs, and stable normalized relative paths; it does not store full image BLOBs.
- Final original-image paths are generated from lowercase SHA-256 hashes and allowlisted extensions, never from user filenames. The original-image API exposes read streams only.
- If any existing path segment between the managed root and a target has `FileAttributes.ReparsePoint` (including a symbolic link or junction), storage operations stop immediately; paths are checked again after directory creation. Staging cleanup, database/backup access, and promotion of original images must never follow a reparse point outside the managed directory.
- Staging and originals are on the same root directory/volume. SHA-256 is computed as data is streamed into staging; promotion by a no-overwrite atomic move is allowed only after the stream is closed and the staged content is verified. After the move, the final file's SHA-256 and byte length are computed again; any mismatch rejects the original image. Encoded files staged for import have a hard limit of 512 MiB; exceeding it or cancelling deletes the partial file.
- Before promotion, the storage layer rechecks PNG/JPEG/WebP file signatures to prevent callers from creating multiple originals with different extensions for the same content. A signature check does not prove that a file can be decoded. The stage-two import validator must still use the Windows decode API to check full decodability, pixel dimensions, and the decompressed-pixel budget; APIs that have not passed validation are not exposed to the UI.
- The v1 database contains `SchemaMigrations`, `ImageAssets`, `ImageItems`, `ImportJobs`, and `AnalysisJobs`. Categories, reminders, and model results are added through later migrations when real behavior requires them.
- Each migration has a fixed version, name, and SQL checksum. If the database version is newer than the application or an applied checksum has changed, processing stops immediately; there is no downgrade or rebuild.
- When the database has pending migrations, acquire a cross-process `BEGIN IMMEDIATE` write-reservation lock first, then reread the version and migration history while holding the lock. Whether a backup is required must not depend on a cached pre-lock check for whether the file exists: once the lock is held, a current version greater than 0 or any existing user-schema object requires a backup first. A separate read-only connection uses the SQLite Backup API to create a consistent snapshot, runs `quick_check`, and atomically renames the snapshot to its final backup name; the lock-holding connection then applies all pending migrations. A concurrent startup that finds the version already updated after acquiring the lock does not rerun migrations. If backup or migration fails, storage features do not start, and the main database and verified backup are preserved.
- Do not set persistent PRAGMAs such as `journal_mode` before validating migration history and checksums. Incompatible or future-version databases remain byte-for-byte unchanged and fail closed.
- Automatically generated migration backups are for internal failure recovery only. They do not provide a user backup/restore capability and are not cleaned up automatically.

## Dependencies

SQLite uses ADO.NET directly; no ORM is introduced. The native library is explicitly pinned to a patched version to avoid vulnerable transitive versions.
See the [dependency inventory](../dependencies.md) for versions and security audits, and the [third-party notices](../../THIRD-PARTY-NOTICES.md) for licenses.

## Consequences

- All automated tests must inject an isolated temporary root and clean it up after releasing connections; access to the real user data root is prohibited.
- Persistent jobs, unique hashes, and stable relative paths support idempotent recovery of import transactions.
- Integration tests must cover a race where another initializer creates the database while a process waits for the migration lock, and verify that the upgrader still creates a v1 snapshot based on the state observed under the lock. Path tests must verify that directory and file reparse points are rejected and that external targets are not modified.
- The effect of SQLite on the Release package size must be measured against the published artifact; NuGet download size is not a substitute.
