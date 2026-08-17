namespace PicForLater.App.Models;

public enum StorageReadinessStatus
{
    Ready,
    PermissionDenied,
    Unsupported,
    Error,
}

public sealed record StorageReadinessResult(
    StorageReadinessStatus Status,
    string? ErrorCode = null);
