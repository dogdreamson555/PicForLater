using System.Globalization;
using Microsoft.Data.Sqlite;

namespace PicForLater.Infrastructure.Storage;

/// <summary>
/// Opens the metadata database, validates migration history, creates a verified
/// pre-migration backup, and applies pending migrations atomically.
/// </summary>
public sealed class SqliteDatabaseInitializer
{
    private readonly AppDataPaths _paths;
    private readonly IReadOnlyList<SqliteMigration> _migrations;
    private readonly Func<CancellationToken, Task>? _beforeMigrationLockAsync;

    public SqliteDatabaseInitializer(AppDataPaths paths)
        : this(paths, SqliteSchema.Migrations)
    {
    }

    internal SqliteDatabaseInitializer(
        AppDataPaths paths,
        IReadOnlyList<SqliteMigration> migrations,
        Func<CancellationToken, Task>? beforeMigrationLockAsync = null)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        ArgumentNullException.ThrowIfNull(migrations);
        _beforeMigrationLockAsync = beforeMigrationLockAsync;

        _migrations = migrations.OrderBy(migration => migration.Version).ToArray();
        for (var index = 0; index < _migrations.Count; index++)
        {
            var expectedVersion = index + 1;
            if (_migrations[index].Version != expectedVersion)
            {
                throw new ArgumentException("Migrations must be contiguous and start at version 1.", nameof(migrations));
            }
        }
    }

    public int CurrentVersion => _migrations.Count;

    public async Task<DatabaseInitializationResult> InitializeAsync(
        CancellationToken cancellationToken = default)
    {
        _paths.EnsureCreated();
        _paths.EnsureSafePath(_paths.DatabasePath);

        await using var connection = CreateConnection(_paths.DatabasePath, SqliteOpenMode.ReadWriteCreate);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await ConfigureConnectionAsync(connection, cancellationToken).ConfigureAwait(false);

        var applied = await ReadAppliedMigrationsAsync(connection, cancellationToken).ConfigureAwait(false);
        var userVersion = await ReadUserVersionAsync(connection, cancellationToken).ConfigureAwait(false);
        ValidateAppliedMigrations(applied, userVersion);

        var previousVersion = applied.Count == 0 ? 0 : applied[^1].Version;
        var pending = _migrations.Where(migration => migration.Version > previousVersion).ToArray();
        if (pending.Length == 0)
        {
            return new DatabaseInitializationResult(previousVersion, previousVersion, BackupFilePath: null);
        }

        if (_beforeMigrationLockAsync is not null)
        {
            await _beforeMigrationLockAsync(cancellationToken).ConfigureAwait(false);
        }

        return await ApplyPendingMigrationsAsync(
            connection,
            cancellationToken).ConfigureAwait(false);
    }

    private static SqliteConnection CreateConnection(string path, SqliteOpenMode mode)
    {
        return new SqliteConnection(
            new SqliteConnectionStringBuilder
            {
                DataSource = path,
                Mode = mode,
                Cache = SqliteCacheMode.Private,
                Pooling = false,
            }.ToString());
    }

    private static async Task ConfigureConnectionAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await ExecuteNonQueryAsync(connection, "PRAGMA foreign_keys = ON;", cancellationToken).ConfigureAwait(false);
        await ExecuteNonQueryAsync(connection, "PRAGMA busy_timeout = 5000;", cancellationToken).ConfigureAwait(false);
        await ExecuteNonQueryAsync(connection, "PRAGMA synchronous = FULL;", cancellationToken).ConfigureAwait(false);
    }

    private async Task<DatabaseInitializationResult> ApplyPendingMigrationsAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        try
        {
            await ExecuteNonQueryAsync(connection, "BEGIN IMMEDIATE;", cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new DatabaseInitializationException(
                "The database migration lock could not be acquired.",
                exception);
        }

        var transactionOpen = true;
        try
        {
            // Another app instance may have migrated while this connection waited for
            // the immediate lock. Re-read and re-validate under the write lock.
            var applied = await ReadAppliedMigrationsAsync(connection, cancellationToken).ConfigureAwait(false);
            var userVersion = await ReadUserVersionAsync(connection, cancellationToken).ConfigureAwait(false);
            ValidateAppliedMigrations(applied, userVersion);

            var previousVersion = applied.Count == 0 ? 0 : applied[^1].Version;
            var pending = _migrations.Where(migration => migration.Version > previousVersion).ToArray();
            if (pending.Length == 0)
            {
                await ExecuteNonQueryAsync(connection, "COMMIT;", cancellationToken).ConfigureAwait(false);
                transactionOpen = false;
                return new DatabaseInitializationResult(previousVersion, previousVersion, BackupFilePath: null);
            }

            string? backupPath = null;
            if (previousVersion > 0
                || await HasUserSchemaObjectsAsync(connection, cancellationToken).ConfigureAwait(false))
            {
                try
                {
                    backupPath = await CreateVerifiedBackupAsync(
                        previousVersion,
                        pending[^1].Version,
                        cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (DatabaseInitializationException)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    throw new DatabaseInitializationException(
                        "The pre-migration database backup could not be created.",
                        exception);
                }
            }

            try
            {
                await ApplyMigrationStatementsAsync(connection, pending, cancellationToken).ConfigureAwait(false);
                await ExecuteNonQueryAsync(connection, "COMMIT;", cancellationToken).ConfigureAwait(false);
                transactionOpen = false;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                throw new DatabaseMigrationException(
                    "The database migration failed and was rolled back.",
                    exception);
            }

            return new DatabaseInitializationResult(previousVersion, pending[^1].Version, backupPath);
        }
        finally
        {
            if (transactionOpen)
            {
                await TryRollbackAsync(connection).ConfigureAwait(false);
            }
        }
    }

    private static async Task<bool> HasUserSchemaObjectsAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT EXISTS(SELECT 1 FROM sqlite_schema WHERE name NOT LIKE 'sqlite_%');";
        return Convert.ToInt64(
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            CultureInfo.InvariantCulture) != 0;
    }

    private static async Task<List<AppliedMigration>> ReadAppliedMigrationsAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var tableCommand = connection.CreateCommand();
        tableCommand.CommandText =
            "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'SchemaMigrations';";
        var tableExists = Convert.ToInt64(
            await tableCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            CultureInfo.InvariantCulture) > 0;
        if (!tableExists)
        {
            return [];
        }

        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT Version, Name, SqlChecksum FROM SchemaMigrations ORDER BY Version;";

        var applied = new List<AppliedMigration>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            applied.Add(new AppliedMigration(reader.GetInt32(0), reader.GetString(1), reader.GetString(2)));
        }

        return applied;
    }

    private static async Task<int> ReadUserVersionAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version;";
        return Convert.ToInt32(
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            CultureInfo.InvariantCulture);
    }

    private void ValidateAppliedMigrations(IReadOnlyList<AppliedMigration> applied, int userVersion)
    {
        for (var index = 0; index < applied.Count; index++)
        {
            var expectedVersion = index + 1;
            var entry = applied[index];
            if (entry.Version != expectedVersion || entry.Version > CurrentVersion)
            {
                throw new DatabaseSchemaException(
                    "The database migration history is newer than or incompatible with this application version.");
            }

            var expected = _migrations[index];
            if (!entry.Name.Equals(expected.Name, StringComparison.Ordinal)
                || !entry.Checksum.Equals(expected.Checksum, StringComparison.OrdinalIgnoreCase))
            {
                throw new DatabaseSchemaException(
                    $"Database migration {entry.Version} does not match the migration shipped by this application.");
            }
        }

        var historyVersion = applied.Count == 0 ? 0 : applied[^1].Version;
        if (userVersion != historyVersion)
        {
            throw new DatabaseSchemaException(
                "The SQLite user version does not match the recorded migration history.");
        }
    }

    private async Task<string> CreateVerifiedBackupAsync(
        int fromVersion,
        int toVersion,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var suffix = $"{DateTimeOffset.UtcNow:yyyyMMddTHHmmssfffffffZ}-{Guid.NewGuid():N}";
        var fileName = $"picforlater-pre-v{fromVersion}-to-v{toVersion}-{suffix}.db";
        var finalPath = Path.Combine(_paths.BackupDirectoryPath, fileName);
        var temporaryPath = finalPath + ".tmp";
        _paths.EnsureSafePath(finalPath);
        _paths.EnsureSafePath(temporaryPath);

        try
        {
            // The migration connection already holds BEGIN IMMEDIATE. A separate
            // read-only source connection can still take a consistent snapshot,
            // while the write reservation prevents another app instance from
            // changing the database between backup and migration.
            await using (var source = CreateConnection(_paths.DatabasePath, SqliteOpenMode.ReadOnly))
            await using (var destination = CreateConnection(temporaryPath, SqliteOpenMode.ReadWriteCreate))
            {
                await source.OpenAsync(cancellationToken).ConfigureAwait(false);
                await destination.OpenAsync(cancellationToken).ConfigureAwait(false);
                source.BackupDatabase(destination);
            }

            cancellationToken.ThrowIfCancellationRequested();
            await VerifyDatabaseAsync(temporaryPath, cancellationToken).ConfigureAwait(false);

            await using (var backupFile = new FileStream(
                temporaryPath,
                new FileStreamOptions
                {
                    Mode = FileMode.Open,
                    Access = FileAccess.ReadWrite,
                    Share = FileShare.None,
                    Options = FileOptions.WriteThrough,
                }))
            {
                backupFile.Flush(flushToDisk: true);
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryPath, finalPath, overwrite: false);
            return finalPath;
        }
        catch
        {
            TryDeleteFile(temporaryPath);
            throw;
        }
    }

    private static async Task VerifyDatabaseAsync(
        string path,
        CancellationToken cancellationToken)
    {
        await using var connection = CreateConnection(path, SqliteOpenMode.ReadOnly);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA quick_check;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        var sawResult = false;
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            sawResult = true;
            if (!reader.GetString(0).Equals("ok", StringComparison.OrdinalIgnoreCase))
            {
                throw new DatabaseInitializationException("The pre-migration database backup failed its integrity check.");
            }
        }

        if (!sawResult)
        {
            throw new DatabaseInitializationException("The pre-migration database backup produced no integrity result.");
        }
    }

    private static async Task ApplyMigrationStatementsAsync(
        SqliteConnection connection,
        IReadOnlyList<SqliteMigration> migrations,
        CancellationToken cancellationToken)
    {
        foreach (var migration in migrations)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await ExecuteNonQueryAsync(connection, migration.Sql, cancellationToken).ConfigureAwait(false);

            await using var recordCommand = connection.CreateCommand();
            recordCommand.CommandText =
                """
                INSERT INTO SchemaMigrations (Version, Name, SqlChecksum, AppliedAtUtc)
                VALUES ($version, $name, $checksum, $appliedAtUtc);
                """;
            recordCommand.Parameters.AddWithValue("$version", migration.Version);
            recordCommand.Parameters.AddWithValue("$name", migration.Name);
            recordCommand.Parameters.AddWithValue("$checksum", migration.Checksum);
            recordCommand.Parameters.AddWithValue(
                "$appliedAtUtc",
                DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            await recordCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

            await ExecuteNonQueryAsync(
                connection,
                $"PRAGMA user_version = {migration.Version};",
                cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task ExecuteNonQueryAsync(
        SqliteConnection connection,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task TryRollbackAsync(SqliteConnection connection)
    {
        try
        {
            await ExecuteNonQueryAsync(connection, "ROLLBACK;", CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Preserve the original migration or cancellation exception.
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // Keep the initialization failure as the primary exception.
        }
        catch (UnauthorizedAccessException)
        {
            // Keep the initialization failure as the primary exception.
        }
    }

    private sealed record AppliedMigration(int Version, string Name, string Checksum);
}
