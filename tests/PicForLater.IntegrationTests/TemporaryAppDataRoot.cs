using Microsoft.Data.Sqlite;
using PicForLater.Infrastructure.Storage;

namespace PicForLater.IntegrationTests;

internal sealed class TemporaryAppDataRoot : IDisposable
{
    public TemporaryAppDataRoot()
    {
        RootPath = Path.Combine(
            Path.GetTempPath(),
            "PicForLater.Tests",
            Guid.NewGuid().ToString("N"));
        Paths = new AppDataPaths(RootPath);
    }

    public string RootPath { get; }

    public AppDataPaths Paths { get; }

    internal static async Task<SqliteConnection> OpenAsync(string databasePath)
    {
        var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder
            {
                DataSource = databasePath,
                Mode = SqliteOpenMode.ReadWrite,
                Pooling = false,
            }.ToString());
        try
        {
            await connection.OpenAsync();
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    public void Dispose()
    {
        if (!Directory.Exists(RootPath))
        {
            return;
        }

        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                Directory.Delete(RootPath, recursive: true);
                return;
            }
            catch (IOException) when (attempt < 2)
            {
                Thread.Sleep(25 * (attempt + 1));
            }
            catch (UnauthorizedAccessException) when (attempt < 2)
            {
                Thread.Sleep(25 * (attempt + 1));
            }
        }

        Directory.Delete(RootPath, recursive: true);
    }
}
