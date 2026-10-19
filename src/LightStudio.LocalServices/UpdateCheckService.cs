using System;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using LightStudio.LightPlayer.Services;
using LightStudio.Logging;

namespace LightStudio.LocalServices;

/// <summary>
/// Checks the project's GitHub "latest release" endpoint for a newer version. An update is only
/// reported when the release is newer than the running assembly <em>and</em> ships the asset named
/// for this platform, since the download link
/// must point at a package the current build can install.
/// </summary>
public sealed class GitHubUpdateCheckService : IUpdateCheckService
{
    private const string LatestReleaseUrl = "https://api.github.com/repos/hjc4869/LightPlayer/releases/latest";

    // A single shared client: GitHub requires a User-Agent and the endpoint is polled infrequently.
    private static readonly HttpClient SharedClient = CreateHttpClient();

    private readonly string? assetName;
    private readonly Version currentVersion;

    public GitHubUpdateCheckService(string? assetName)
    {
        this.assetName = string.IsNullOrWhiteSpace(assetName) ? null : assetName;
        currentVersion = Normalize(Assembly.GetExecutingAssembly().GetName().Version ?? new Version(1, 0, 0, 0));
    }

    public bool IsSupported => assetName is not null;

    public async Task<UpdateCheckResult> CheckForUpdatesAsync(CancellationToken cancellationToken = default)
    {
        if (assetName is null)
        {
            return new UpdateCheckResult(UpdateCheckStatus.Failed, ErrorMessage: "Updates are not supported for this build.");
        }

        try
        {
            using var response = await SharedClient.GetAsync(LatestReleaseUrl, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
            var root = document.RootElement;

            if (!root.TryGetProperty("tag_name", out var tagElement)
                || tagElement.GetString() is not { } tag
                || !TryParseTagVersion(tag, out var latestVersion, out var latestText))
            {
                return new UpdateCheckResult(UpdateCheckStatus.Failed, ErrorMessage: "Could not read the latest version.");
            }

            // Surface an update only when it is newer AND has a package this platform can install.
            if (latestVersion > currentVersion && TryFindAssetUrl(root, out var downloadUrl))
            {
                return new UpdateCheckResult(UpdateCheckStatus.UpdateAvailable, latestText, downloadUrl);
            }

            return new UpdateCheckResult(UpdateCheckStatus.UpToDate);
        }
        catch (Exception ex)
        {
            AppLog.Warning("Update", "The update check failed.", ex);
            return new UpdateCheckResult(UpdateCheckStatus.Failed, ErrorMessage: ex.GetBaseException().Message);
        }
    }

    private bool TryFindAssetUrl(JsonElement root, out string downloadUrl)
    {
        downloadUrl = string.Empty;
        if (assetName is null
            || !root.TryGetProperty("assets", out var assets)
            || assets.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        foreach (var asset in assets.EnumerateArray())
        {
            if (asset.TryGetProperty("name", out var nameElement)
                && string.Equals(nameElement.GetString(), assetName, StringComparison.OrdinalIgnoreCase)
                && asset.TryGetProperty("browser_download_url", out var urlElement)
                && urlElement.GetString() is { Length: > 0 } url)
            {
                downloadUrl = url;
                return true;
            }
        }

        return false;
    }

    /// <summary>Parses a "vX.Y.Z" release tag into a comparable version and its display text.</summary>
    private static bool TryParseTagVersion(string tag, out Version version, out string displayText)
    {
        version = new Version(0, 0);
        displayText = string.Empty;

        var trimmed = tag.Trim();
        if (trimmed.StartsWith("v", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed[1..];
        }

        if (!Version.TryParse(trimmed, out var parsed))
        {
            return false;
        }

        version = Normalize(parsed);
        displayText = trimmed;
        return true;
    }

    // Pads unspecified components to 0 so "1.0" and "1.0.0.0" compare as equal.
    private static Version Normalize(Version version) =>
        new(version.Major, version.Minor, Math.Max(0, version.Build), Math.Max(0, version.Revision));

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.Add("User-Agent", "LightPlayer");
        client.DefaultRequestHeaders.Add("Accept", "application/vnd.github+json");
        client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2026-03-10");
        return client;
    }
}
