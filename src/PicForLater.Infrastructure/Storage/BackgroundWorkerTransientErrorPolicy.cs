using Microsoft.Data.Sqlite;

namespace PicForLater.Infrastructure.Storage;

public static class BackgroundWorkerTransientErrorPolicy
{
    private const int SqliteBusy = 5;
    private const int SqliteLocked = 6;
    private const int ErrorSharingViolation = 32;
    private const int ErrorLockViolation = 33;

    public static bool IsTransient(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is SqliteException sqlite
                && sqlite.SqliteErrorCode is SqliteBusy or SqliteLocked)
            {
                return true;
            }

            if (current is IOException io
                && (io.HResult & 0xFFFF) is ErrorSharingViolation or ErrorLockViolation)
            {
                return true;
            }
        }

        return false;
    }
}
