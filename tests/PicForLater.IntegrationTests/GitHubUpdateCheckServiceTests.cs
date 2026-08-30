using System.Net;
using System.Text;
using PicForLater.App.Models;
using PicForLater.App.Services;

namespace PicForLater.IntegrationTests;

public sealed class GitHubUpdateCheckServiceTests
{
    private static readonly AppReleaseVersion CurrentVersion = new(1, 1, 1);

    [Fact]
    public void Constructor_DoesNotSendRequest()
    {
        var handler = new RecordingHandler((_, _) =>
            Task.FromResult(JsonResponse("v1.1.1")));
        using var client = new HttpClient(handler);

        _ = new GitHubUpdateCheckService(client, CurrentVersion);

        Assert.Equal(0, handler.RequestCount);
        Assert.Equal(TimeSpan.FromSeconds(12), GitHubUpdateCheckService.DefaultTimeout);
    }

    [Theory]
    [InlineData("v1.1.1", UpdateCheckOutcome.UpToDate, null)]
    [InlineData("v1.2.0", UpdateCheckOutcome.UpdateAvailable,
        "https://github.com/dogdreamson555/PicForLater/releases/tag/v1.2.0")]
    [InlineData("v1.0.9", UpdateCheckOutcome.LocalAhead, null)]
    public async Task ValidRelease_ComparesAllThreeVersionParts(
        string tag,
        UpdateCheckOutcome expectedOutcome,
        string? expectedReleaseUri)
    {
        using var client = new HttpClient(new RecordingHandler((_, _) =>
            Task.FromResult(JsonResponse(tag))));
        var service = new GitHubUpdateCheckService(client, CurrentVersion);

        var result = await service.CheckForUpdatesAsync();

        Assert.Equal(CurrentVersion, result.CurrentVersion);
        Assert.True(AppReleaseVersion.TryParseReleaseTag(tag, out var expectedLatestVersion));
        Assert.Equal(expectedLatestVersion, result.LatestVersion);
        Assert.Equal(expectedOutcome, result.Outcome);
        Assert.Null(result.FailureKind);
        Assert.Equal(expectedReleaseUri, result.ReleasePageUri?.AbsoluteUri);
    }

    [Fact]
    public async Task Request_UsesOnlyPinnedEndpointAndRequiredGitHubHeaders()
    {
        HttpRequestMessage? captured = null;
        using var client = new HttpClient(new RecordingHandler((request, _) =>
        {
            captured = request;
            return Task.FromResult(JsonResponse("v1.1.1"));
        }));
        var service = new GitHubUpdateCheckService(client, CurrentVersion);

        await service.CheckForUpdatesAsync();

        Assert.NotNull(captured);
        Assert.Equal(HttpMethod.Get, captured.Method);
        Assert.Equal(
            "https://api.github.com/repos/dogdreamson555/PicForLater/releases/latest",
            captured.RequestUri?.AbsoluteUri);
        Assert.Equal("PicForLater/1.1.1", captured.Headers.UserAgent.ToString());
        Assert.Equal(
            "application/vnd.github+json",
            Assert.Single(captured.Headers.Accept).MediaType);
        Assert.Null(captured.Headers.Authorization);
        Assert.Null(captured.Content);
    }

    [Fact]
    public async Task NonSuccessResponse_ReturnsReleaseUnavailable()
    {
        using var client = new HttpClient(new RecordingHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden))));
        var service = new GitHubUpdateCheckService(client, CurrentVersion);

        var result = await service.CheckForUpdatesAsync();

        AssertUnavailable(result, UpdateCheckFailureKind.ReleaseUnavailable);
    }

    [Theory]
    [InlineData("not-json")]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("\"text\"")]
    [InlineData("42")]
    [InlineData("{}")]
    [InlineData("{\"tag_name\":null}")]
    [InlineData("{\"tag_name\":\"V1.2.0\"}")]
    [InlineData("{\"tag_name\":\"v1.2.0-beta\"}")]
    public async Task InvalidPayload_ReturnsInvalidResponse(string payload)
    {
        using var client = new HttpClient(new RecordingHandler((_, _) =>
            Task.FromResult(JsonResponsePayload(payload))));
        var service = new GitHubUpdateCheckService(client, CurrentVersion);

        var result = await service.CheckForUpdatesAsync();

        AssertUnavailable(result, UpdateCheckFailureKind.InvalidResponse);
    }

    [Fact]
    public async Task NetworkException_ReturnsNetworkFailure()
    {
        using var client = new HttpClient(new RecordingHandler((_, _) =>
            Task.FromException<HttpResponseMessage>(new HttpRequestException("fixture"))));
        var service = new GitHubUpdateCheckService(client, CurrentVersion);

        var result = await service.CheckForUpdatesAsync();

        AssertUnavailable(result, UpdateCheckFailureKind.Network);
    }

    [Fact]
    public async Task ServiceTimeout_ReturnsTimeoutFailure()
    {
        using var client = new HttpClient(new RecordingHandler(async (_, cancellationToken) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("Unreachable.");
        }));
        var service = new GitHubUpdateCheckService(
            client,
            CurrentVersion,
            TimeSpan.FromMilliseconds(20));

        var result = await service.CheckForUpdatesAsync();

        AssertUnavailable(result, UpdateCheckFailureKind.Timeout);
    }

    [Fact]
    public async Task CallerCancellation_RemainsCancellation()
    {
        using var client = new HttpClient(new RecordingHandler(async (_, cancellationToken) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("Unreachable.");
        }));
        var service = new GitHubUpdateCheckService(client, CurrentVersion);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.CheckForUpdatesAsync(cancellation.Token));
    }

    private static HttpResponseMessage JsonResponse(string tag) =>
        JsonResponsePayload($"{{\"tag_name\":\"{tag}\"}}");

    private static HttpResponseMessage JsonResponsePayload(string payload) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json"),
        };

    private static void AssertUnavailable(
        UpdateCheckResult result,
        UpdateCheckFailureKind expectedFailure)
    {
        Assert.Equal(CurrentVersion, result.CurrentVersion);
        Assert.Null(result.LatestVersion);
        Assert.Equal(UpdateCheckOutcome.Unavailable, result.Outcome);
        Assert.Equal(expectedFailure, result.FailureKind);
        Assert.Null(result.ReleasePageUri);
    }

    private sealed class RecordingHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler)
        : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            return handler(request, cancellationToken);
        }
    }
}
