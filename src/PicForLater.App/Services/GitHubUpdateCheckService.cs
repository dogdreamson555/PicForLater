using System.Net.Http.Headers;
using System.Text.Json;
using PicForLater.App.Models;

namespace PicForLater.App.Services;

public sealed class GitHubUpdateCheckService : IUpdateCheckService
{
    internal static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(12);
    private static readonly Uri LatestReleaseApiUri = new(
        "https://api.github.com/repos/dogdreamson555/PicForLater/releases/latest");
    private static readonly Uri ReleaseTagBaseUri = new(
        "https://github.com/dogdreamson555/PicForLater/releases/tag/");

    private readonly HttpClient _httpClient;
    private readonly AppReleaseVersion _currentVersion;
    private readonly TimeSpan _timeout;

    public GitHubUpdateCheckService(
        HttpClient httpClient,
        AppReleaseVersion currentVersion)
        : this(httpClient, currentVersion, DefaultTimeout)
    {
    }

    internal GitHubUpdateCheckService(
        HttpClient httpClient,
        AppReleaseVersion currentVersion,
        TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        if (timeout <= TimeSpan.Zero && timeout != Timeout.InfiniteTimeSpan)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        _httpClient = httpClient;
        _currentVersion = currentVersion;
        _timeout = timeout;
    }

    public async Task<UpdateCheckResult> CheckForUpdatesAsync(
        CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_timeout);

        try
        {
            using var request = CreateRequest();
            using var response = await _httpClient.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    timeout.Token)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return Unavailable(UpdateCheckFailureKind.ReleaseUnavailable);
            }

            await using var responseStream = await response.Content
                .ReadAsStreamAsync(timeout.Token)
                .ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(
                    responseStream,
                    cancellationToken: timeout.Token)
                .ConfigureAwait(false);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("tag_name", out var tagElement)
                || tagElement.ValueKind != JsonValueKind.String
                || !AppReleaseVersion.TryParseReleaseTag(
                    tagElement.GetString(),
                    out var latestVersion))
            {
                return Unavailable(UpdateCheckFailureKind.InvalidResponse);
            }

            var comparison = _currentVersion.CompareTo(latestVersion);
            if (comparison == 0)
            {
                return new UpdateCheckResult(
                    _currentVersion,
                    latestVersion,
                    UpdateCheckOutcome.UpToDate);
            }

            if (comparison > 0)
            {
                return new UpdateCheckResult(
                    _currentVersion,
                    latestVersion,
                    UpdateCheckOutcome.LocalAhead);
            }

            var releasePageUri = new Uri(
                ReleaseTagBaseUri,
                $"v{latestVersion}");
            return new UpdateCheckResult(
                _currentVersion,
                latestVersion,
                UpdateCheckOutcome.UpdateAvailable,
                ReleasePageUri: releasePageUri);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return Unavailable(UpdateCheckFailureKind.Timeout);
        }
        catch (HttpRequestException)
        {
            return Unavailable(UpdateCheckFailureKind.Network);
        }
        catch (IOException)
        {
            return Unavailable(UpdateCheckFailureKind.Network);
        }
        catch (JsonException)
        {
            return Unavailable(UpdateCheckFailureKind.InvalidResponse);
        }
    }

    private HttpRequestMessage CreateRequest()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, LatestReleaseApiUri);
        request.Headers.UserAgent.Add(
            new ProductInfoHeaderValue("PicForLater", _currentVersion.ToString()));
        request.Headers.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return request;
    }

    private UpdateCheckResult Unavailable(UpdateCheckFailureKind failureKind) =>
        new(
            _currentVersion,
            LatestVersion: null,
            UpdateCheckOutcome.Unavailable,
            failureKind);
}
