using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SuperCV;

/// <summary>
/// Reads the newest published GitHub release from SuperCV's public repository.
/// </summary>
internal sealed class GitHubReleaseUpdateService
{
    private const string DefaultRepository = "ShuaiKeAng/OpenSuperCV";
    private static readonly HttpClient HttpClient = CreateHttpClient();

    internal async Task<ReleaseUpdateCheckResult> CheckAsync(CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"repos/{DefaultRepository}/releases/latest");

        using HttpResponseMessage response = await HttpClient
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            return ReleaseUpdateCheckResult.RequestFailed(response.StatusCode);
        }

        await using Stream responseStream = await response.Content
            .ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);
        GitHubRelease? release = await JsonSerializer.DeserializeAsync<GitHubRelease>(
                responseStream,
                JsonSerializerOptions,
                cancellationToken)
            .ConfigureAwait(false);
        if (release is null || string.IsNullOrWhiteSpace(release.TagName))
        {
            return ReleaseUpdateCheckResult.InvalidRelease();
        }

        if (!TryParseVersion(release.TagName, out Version? remoteVersion))
        {
            return ReleaseUpdateCheckResult.UnsupportedVersion(release.TagName);
        }

        Version localVersion = typeof(SuperCVWindow).Assembly.GetName().Version ?? new Version(0, 0, 0);
        string localVersionText = localVersion.ToString(3);
        string downloadUrl = SelectDownloadUrl(release) ?? release.HtmlUrl ?? string.Empty;
        return remoteVersion > localVersion
            ? ReleaseUpdateCheckResult.UpdateAvailable(release.TagName, localVersionText, downloadUrl)
            : ReleaseUpdateCheckResult.UpToDate(localVersionText);
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient
        {
            BaseAddress = new Uri("https://api.github.com/"),
            Timeout = TimeSpan.FromSeconds(15),
        };
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        client.DefaultRequestHeaders.Add("User-Agent", "SuperCV-Update-Checker");
        client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2026-03-10");
        return client;
    }

    private static bool TryParseVersion(string tagName, out Version? version)
    {
        string normalized = tagName.Trim().TrimStart('v', 'V');
        int prereleaseSeparator = normalized.IndexOf('-');
        if (prereleaseSeparator >= 0)
        {
            normalized = normalized[..prereleaseSeparator];
        }

        return Version.TryParse(normalized, out version);
    }

    private static string? SelectDownloadUrl(GitHubRelease release)
    {
        GitHubReleaseAsset? installer = release.Assets?
            .FirstOrDefault(asset =>
                asset.Name?.Contains("setup", StringComparison.OrdinalIgnoreCase) == true &&
                asset.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            ?? release.Assets?.FirstOrDefault(asset =>
                asset.Name?.Contains("supercv", StringComparison.OrdinalIgnoreCase) == true &&
                asset.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            ?? release.Assets?
            .FirstOrDefault(asset => asset.Name?.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) == true)
            ?? release.Assets?.FirstOrDefault(asset =>
                asset.Name?.EndsWith(".msi", StringComparison.OrdinalIgnoreCase) == true)
            ?? release.Assets?.FirstOrDefault(asset =>
                asset.Name?.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) == true);
        return installer?.BrowserDownloadUrl;
    }

    private static readonly JsonSerializerOptions JsonSerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private sealed class GitHubRelease
    {
        [JsonPropertyName("tag_name")]
        public string? TagName { get; init; }

        [JsonPropertyName("html_url")]
        public string? HtmlUrl { get; init; }

        [JsonPropertyName("assets")]
        public GitHubReleaseAsset[]? Assets { get; init; }
    }

    private sealed class GitHubReleaseAsset
    {
        [JsonPropertyName("name")]
        public string? Name { get; init; }

        [JsonPropertyName("browser_download_url")]
        public string? BrowserDownloadUrl { get; init; }
    }
}

internal sealed record ReleaseUpdateCheckResult(
    ReleaseUpdateCheckStatus Status,
    string? LocalVersion = null,
    string? ReleaseTag = null,
    string? DownloadUrl = null,
    HttpStatusCode? StatusCode = null)
{
    internal static ReleaseUpdateCheckResult UpToDate(string localVersion) =>
        new(ReleaseUpdateCheckStatus.UpToDate, LocalVersion: localVersion);

    internal static ReleaseUpdateCheckResult UpdateAvailable(string releaseTag, string localVersion, string downloadUrl) =>
        new(ReleaseUpdateCheckStatus.UpdateAvailable, localVersion, releaseTag, downloadUrl);

    internal static ReleaseUpdateCheckResult RequestFailed(HttpStatusCode statusCode) =>
        new(ReleaseUpdateCheckStatus.RequestFailed, StatusCode: statusCode);

    internal static ReleaseUpdateCheckResult InvalidRelease() =>
        new(ReleaseUpdateCheckStatus.InvalidRelease);

    internal static ReleaseUpdateCheckResult UnsupportedVersion(string releaseTag) =>
        new(ReleaseUpdateCheckStatus.UnsupportedVersion, ReleaseTag: releaseTag);
}

internal enum ReleaseUpdateCheckStatus
{
    UpToDate,
    UpdateAvailable,
    RequestFailed,
    InvalidRelease,
    UnsupportedVersion,
}
