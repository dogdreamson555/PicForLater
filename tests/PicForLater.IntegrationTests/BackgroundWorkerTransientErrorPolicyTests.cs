using PicForLater.Infrastructure.Storage;
using Microsoft.Data.Sqlite;

namespace PicForLater.IntegrationTests;

public sealed class BackgroundWorkerTransientErrorPolicyTests
{
    [Theory]
    [InlineData(unchecked((int)0x80070020))]
    [InlineData(unchecked((int)0x80070021))]
    public void WindowsSharingAndLockViolations_AreTransient(int hresult)
    {
        var exception = new IOException("sensitive path", hresult);

        Assert.True(BackgroundWorkerTransientErrorPolicy.IsTransient(exception));
    }

    [Fact]
    public void UnknownIoAndNestedUnknownErrors_AreNotTransient()
    {
        Assert.False(BackgroundWorkerTransientErrorPolicy.IsTransient(
            new IOException("disk or permission failure")));
        Assert.False(BackgroundWorkerTransientErrorPolicy.IsTransient(
            new InvalidOperationException(
                "outer",
                new UnauthorizedAccessException("secret path"))));
    }

    [Theory]
    [InlineData(5)]
    [InlineData(6)]
    public void SqliteBusyAndLocked_AreTransient(int errorCode)
    {
        var exception = new SqliteException("sensitive database path", errorCode, errorCode);

        Assert.Equal(errorCode, exception.SqliteErrorCode);
        Assert.True(BackgroundWorkerTransientErrorPolicy.IsTransient(exception));
    }
}
