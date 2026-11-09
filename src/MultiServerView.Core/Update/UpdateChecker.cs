using System.Net.Http.Headers;
using System.Text.Json;

namespace MultiServerView.Core.Update;

/// <summary>A newer release found on GitHub.</summary>
public sealed record UpdateInfo(string Version, string Notes, string PageUrl, string? SetupUrl);

/// <summary>
/// Asks the GitHub Releases API of the official repository whether a newer version exists.
/// Only this one request is made (no identifiers are sent besides the app name in the User-Agent).
/// </summary>
public static class UpdateChecker
{
    public const string Repository = "jxhnch/MultiServerView";

    public static async Task<UpdateInfo?> CheckAsync(HttpClient http, string currentVersion, CancellationToken ct = default, string repository = Repository)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{repository}/releases/latest");
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("MultiServerView", currentVersion));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        using var response = await http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode) return null; // no release yet, rate limit, offline proxy ...
        return Parse(await response.Content.ReadAsStringAsync(ct), currentVersion);
    }

    public static UpdateInfo? Parse(string json, string currentVersion)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.TryGetProperty("draft", out var draft) && draft.GetBoolean()) return null;
        if (root.TryGetProperty("prerelease", out var pre) && pre.GetBoolean()) return null;

        var tag = root.TryGetProperty("tag_name", out var t) ? t.GetString() ?? "" : "";
        var latest = tag.TrimStart('v', 'V');
        if (!IsNewer(latest, currentVersion)) return null;

        string? setup = null;
        if (root.TryGetProperty("assets", out var assets))
            foreach (var asset in assets.EnumerateArray())
            {
                var name = asset.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                var url = asset.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
                if (url != null && name.Contains("Setup", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && IsGitHubUrl(url))
                    setup = url;
            }

        return new UpdateInfo(
            latest,
            root.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "",
            root.TryGetProperty("html_url", out var h) ? h.GetString() ?? "" : "",
            setup);
    }

    /// <summary>true if <paramref name="candidate"/> (e.g. "1.2.0") is a higher version than <paramref name="current"/>.</summary>
    public static bool IsNewer(string candidate, string current) =>
        TryParse(candidate, out var a) && TryParse(current, out var b) && a > b;

    private static bool TryParse(string text, out Version version)
    {
        var core = text.Split('-', '+')[0];
        if (Version.TryParse(core, out var v) && v != null)
        {
            version = new Version(v.Major, v.Minor, Math.Max(v.Build, 0));
            return true;
        }
        version = new Version();
        return false;
    }

    /// <summary>Only downloads from GitHub are ever run.</summary>
    public static bool IsGitHubUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps &&
        (uri.Host == "github.com" || uri.Host.EndsWith(".githubusercontent.com", StringComparison.Ordinal));
}
