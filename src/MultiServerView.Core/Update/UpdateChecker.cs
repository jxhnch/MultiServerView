using System.Net.Http.Headers;
using System.Text.Json;

namespace MultiServerView.Core.Update;

/// <summary>A newer release found on GitHub.</summary>
public sealed record UpdateInfo(string Version, string Notes, string PageUrl, string? SetupUrl, string? SetupName = null, string? ChecksumsUrl = null);

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
        return Parse(await response.Content.ReadAsStringAsync(ct), currentVersion, repository);
    }

    public static UpdateInfo? Parse(string json, string currentVersion, string repository = Repository)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.TryGetProperty("draft", out var draft) && draft.GetBoolean()) return null;
        if (root.TryGetProperty("prerelease", out var pre) && pre.GetBoolean()) return null;

        var tag = root.TryGetProperty("tag_name", out var t) ? t.GetString() ?? "" : "";
        var latest = tag.TrimStart('v', 'V');
        if (!IsNewer(latest, currentVersion)) return null;

        string? setup = null;
        string? setupName = null;
        string? checksums = null;
        if (root.TryGetProperty("assets", out var assets))
            foreach (var asset in assets.EnumerateArray())
            {
                var name = asset.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                var url = asset.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
                if (url == null || !IsGitHubUrl(url, repository)) continue;
                if (name.Contains("Setup", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                {
                    setup = url;
                    setupName = name;
                }
                else if (name.Equals("SHA256SUMS.txt", StringComparison.OrdinalIgnoreCase) || name.Equals("SHA256SUMS", StringComparison.OrdinalIgnoreCase))
                {
                    checksums = url;
                }
            }

        var pageUrl = root.TryGetProperty("html_url", out var h) ? h.GetString() ?? "" : "";
        if (!IsGitHubUrl(pageUrl, repository)) pageUrl = $"https://github.com/{repository}/releases";

        return new UpdateInfo(
            latest,
            root.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "",
            pageUrl,
            setup,
            setupName,
            checksums);
    }

    /// <summary>Extracts the lowercase 64-char SHA-256 hex digest for <paramref name="fileName"/> from a sha256sum manifest.</summary>
    public static string? ParseSha256Sum(string checksumsText, string fileName)
    {
        foreach (var raw in checksumsText.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length < 66) continue;
            var sep = line.IndexOfAny(new[] { ' ', '\t' });
            if (sep != 64) continue;
            var hash = line[..64];
            if (!hash.All(Uri.IsHexDigit)) continue;
            var name = line[64..].TrimStart(' ', '\t', '*');
            if (name.Equals(fileName, StringComparison.OrdinalIgnoreCase)) return hash.ToLowerInvariant();
        }
        return null;
    }

    /// <summary>Downloads a release SHA256SUMS manifest from GitHub and returns the expected SHA-256 for <paramref name="fileName"/>.</summary>
    public static async Task<string> FetchExpectedSha256Async(HttpClient http, string checksumsUrl, string fileName, CancellationToken ct = default, string repository = Repository)
    {
        if (!IsGitHubUrl(checksumsUrl, repository))
            throw new InvalidOperationException("Refusing to download checksums from an untrusted URL.");
        using var response = await http.GetAsync(checksumsUrl, ct);
        response.EnsureSuccessStatusCode();
        var finalUrl = response.RequestMessage?.RequestUri?.ToString() ?? "";
        if (!IsGitHubUrl(finalUrl, repository))
            throw new InvalidOperationException("Checksum download redirected to an untrusted host.");
        var text = await response.Content.ReadAsStringAsync(ct);
        return ParseSha256Sum(text, fileName)
            ?? throw new InvalidOperationException($"The release checksums file does not contain an entry for {fileName}.");
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

    /// <summary>Validates a version string before embedding it in a local filename.</summary>
    public static string SanitizeVersionForFileName(string version)
    {
        if (string.IsNullOrWhiteSpace(version) || version.Length > 64 || version.Contains("..", StringComparison.Ordinal))
            throw new InvalidOperationException("Invalid update version string.");
        foreach (var ch in version)
            if (!char.IsAsciiLetterOrDigit(ch) && ch is not ('.' or '-' or '_'))
                throw new InvalidOperationException("Invalid update version string.");
        return version;
    }

    /// <summary>Only downloads from GitHub (and optionally a specific repository's releases) are accepted.</summary>
    public static bool IsGitHubUrl(string url, string? repository = null)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps) return false;
        if (uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase))
            return string.IsNullOrEmpty(repository) || uri.AbsolutePath.StartsWith($"/{repository}/releases/", StringComparison.OrdinalIgnoreCase);
        return uri.Host.EndsWith(".githubusercontent.com", StringComparison.OrdinalIgnoreCase);
    }
}
