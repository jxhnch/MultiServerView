using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace MultiServerView.Core.Sync;

/// <summary>
/// A private (secret) GitHub Gist as the store. The only thing the user needs is a GitHub token with the "gist" permission.
/// The gist is found by its file name, so a second PC only needs the same token. Only the encrypted vault (as Base64 text)
/// is uploaded; GitHub never sees hosts or passwords. Conflicts are detected by comparing the content before every update.
/// </summary>
public sealed class GistRemote : ISyncRemote
{
    public const string FileName = "MultiServerView-Vault.msv.txt";
    private const string Description = "MultiServerView vault (encrypted, do not edit)";
    private const string Api = "https://api.github.com";

    private readonly HttpClient _http;
    private readonly string _token;
    private string? _id;

    public GistRemote(HttpClient http, string token)
    {
        if (string.IsNullOrWhiteSpace(token)) throw new SyncException("Please enter your GitHub token.");
        _http = http;
        _token = token.Trim();
    }

    public async Task<RemoteVault?> GetAsync(CancellationToken ct = default)
    {
        _id ??= await FindAsync(ct);
        if (_id == null) return null;

        var gist = await SendAsync(HttpMethod.Get, $"{Api}/gists/{_id}", null, ct);
        var file = gist["files"]?[FileName] as JsonObject;
        if (file == null) { _id = null; return null; }

        var content = file["content"]?.GetValue<string>() ?? "";
        if (file["truncated"]?.GetValue<bool>() == true && file["raw_url"]?.GetValue<string>() is { } raw)
            content = await RawAsync(raw, ct);
        var blob = Convert.FromBase64String(content.Trim());
        return new RemoteVault(VersionOf(blob), blob);
    }

    public async Task<long> InsertAsync(byte[] blob, CancellationToken ct = default)
    {
        var body = new JsonObject
        {
            ["description"] = Description,
            ["public"] = false,
            ["files"] = new JsonObject { [FileName] = new JsonObject { ["content"] = Convert.ToBase64String(blob) } }
        };
        var created = await SendAsync(HttpMethod.Post, $"{Api}/gists", body, ct);
        _id = created["id"]?.GetValue<string>() ?? throw new SyncException("GitHub did not return the id of the new gist.");
        return VersionOf(blob);
    }

    public async Task<long?> UpdateAsync(long expectedVersion, byte[] blob, CancellationToken ct = default)
    {
        var current = await GetAsync(ct);
        if (current == null || current.Version != expectedVersion) return null;

        var body = new JsonObject { ["files"] = new JsonObject { [FileName] = new JsonObject { ["content"] = Convert.ToBase64String(blob) } } };
        await SendAsync(HttpMethod.Patch, $"{Api}/gists/{_id}", body, ct);
        return VersionOf(blob);
    }

    private async Task<string?> FindAsync(CancellationToken ct)
    {
        for (var page = 1; page <= 5; page++)
        {
            var list = await SendAsync(HttpMethod.Get, $"{Api}/gists?per_page=100&page={page}", null, ct) as JsonArray ?? new JsonArray();
            foreach (var g in list)
                if (g?["files"] is JsonObject files && files.ContainsKey(FileName))
                    return g["id"]?.GetValue<string>();
            if (list.Count < 100) break;
        }
        return null;
    }

    private async Task<string> RawAsync(string url, CancellationToken ct)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || !uri.Host.EndsWith("githubusercontent.com", StringComparison.Ordinal))
            throw new SyncException("Unexpected download address from GitHub.");
        using var response = await _http.GetAsync(uri, ct);
        Check(response);
        return await response.Content.ReadAsStringAsync(ct);
    }

    private async Task<JsonNode> SendAsync(HttpMethod method, string url, JsonNode? body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("MultiServerView", "1"));
        request.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");
        if (body != null) request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        try
        {
            using var response = await _http.SendAsync(request, ct);
            Check(response);
            return JsonNode.Parse(await response.Content.ReadAsStringAsync(ct)) ?? throw new SyncException("Empty answer from GitHub.");
        }
        catch (HttpRequestException ex)
        {
            throw new SyncException("GitHub is not reachable: " + ex.Message);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new SyncException("GitHub does not answer (timeout).");
        }
    }

    private static void Check(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode) return;
        throw new SyncException(response.StatusCode switch
        {
            HttpStatusCode.Unauthorized => "GitHub refused the token. Check that it is correct and has not expired.",
            HttpStatusCode.Forbidden or HttpStatusCode.NotFound => "GitHub says no access. The token needs the \"gist\" permission (classic token) or Gists read/write (fine-grained token).",
            _ => $"GitHub answered {(int)response.StatusCode} {response.ReasonPhrase}."
        });
    }

    private static long VersionOf(byte[] blob) => BitConverter.ToInt64(SHA256.HashData(blob), 0);
}
