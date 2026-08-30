namespace PicForLater.App.Models;

public enum UpdateCheckOutcome
{
    UpToDate,
    UpdateAvailable,
    LocalAhead,
    Unavailable,
}

public enum UpdateCheckFailureKind
{
    Network,
    Timeout,
    ReleaseUnavailable,
    InvalidResponse,
}

public sealed record UpdateCheckResult(
    AppReleaseVersion CurrentVersion,
    AppReleaseVersion? LatestVersion,
    UpdateCheckOutcome Outcome,
    UpdateCheckFailureKind? FailureKind = null,
    Uri? ReleasePageUri = null);
