namespace PicForLater.Infrastructure.Storage;

public sealed record DatabaseInitializationResult(
    int PreviousVersion,
    int CurrentVersion,
    string? BackupFilePath);

public class DatabaseInitializationException : Exception
{
    public DatabaseInitializationException(string message)
        : base(message)
    {
    }

    public DatabaseInitializationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

public sealed class DatabaseSchemaException : DatabaseInitializationException
{
    public DatabaseSchemaException(string message)
        : base(message)
    {
    }
}

public sealed class DatabaseMigrationException : DatabaseInitializationException
{
    public DatabaseMigrationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
