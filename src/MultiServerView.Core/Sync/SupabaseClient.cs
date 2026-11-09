using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MultiServerView.Core.Sync;

public sealed class SyncException : Exception
{
    public SyncException(string message, Exception? inner = null) : base(message, inner) { }
}

public sealed record RemoteVault(long Version, byte[] Blob);

/// <summary>
/// Minimal Supabase client (REST only): auth (e-mail/password) + table "vaults".
/// Only the encrypted vault blob (Base64) is stored in the cloud - never plain text.
/// The SQL to create the table is in docs/supabase.sql.
/// </summary>
public sealed class SupabaseClient
{
    private readonly HttpClient _http;
    private readonly string _url;
    private readonly string _anonKey;

    public SupabaseClient(HttpClient http, string url, string anonKey)
    {
        _http = http;
        _url = NormalizeUrl(url);
        _anonKey = anonKey.Trim();
        if (!Uri.TryCreate(_url, UriKind.Absolute, out var uri) || (uri.Scheme != "https" && !uri.IsLoopback))
            throw new SyncException("The Supabase URL must start with https://.");
        if (_anonKey.Length == 0) throw new SyncException("The Supabase anon key is missing.");
    }

    /// <summary>
    /// Accepts what people paste: the project URL, a URL with a path (https://x.supabase.co/rest/v1/), or only the project id ("abcdxyz").
    /// </summary>
    public static string NormalizeUrl(string input)
    {
        var text = input.Trim();
        if (text.Length == 0) return "";
        if (!text.Contains("://", StringComparison.Ordinal))
            text = text.Contains('.') ? "https://" + text : $"https://{text}.supabase.co";
        return Uri.TryCreate(text, UriKind.Absolute, out var uri) ? uri.GetLeftPart(UriPartial.Authority) : text.TrimEnd('/');
    }

    // ------------------------------------------------------------------ Auth

    /// <summary>Signs in and returns the access token.</summary>
    public async Task<string> SignInAsync(string email, string password, CancellationToken ct = default)
    {
        var json = await SendAsync(HttpMethod.Post, "/auth/v1/token?grant_type=password", null,
            new JsonObject { ["email"] = email, ["password"] = password }, null, ct);
        var token = json?["access_token"]?.GetValue<string>();
        return string.IsNullOrEmpty(token) ? throw new SyncException("Authentication failed (no token received).") : token;
    }

    /// <summary>Creates an account. true = signed in directly, false = the e-mail must be confirmed first.</summary>
    public async Task<bool> SignUpAsync(string email, string password, CancellationToken ct = default)
    {
        var json = await SendAsync(HttpMethod.Post, "/auth/v1/signup", null,
            new JsonObject { ["email"] = email, ["password"] = password }, null, ct);
        return !string.IsNullOrEmpty(json?["access_token"]?.GetValue<string>());
    }

    // ------------------------------------------------------------------ Vault row

    public async Task<RemoteVault?> GetVaultAsync(string token, CancellationToken ct = default)
    {
        var json = await SendAsync(HttpMethod.Get, "/rest/v1/vaults?select=version,blob&limit=1", token, null, null, ct);
        if (json is not JsonArray { Count: > 0 } arr) return null;
        return Parse(arr[0]!);
    }

    /// <summary>Creates the row for the first time (version 1).</summary>
    public async Task<long> InsertVaultAsync(string token, byte[] blob, CancellationToken ct = default)
    {
        var json = await SendAsync(HttpMethod.Post, "/rest/v1/vaults", token,
            new JsonObject { ["blob"] = Convert.ToBase64String(blob), ["version"] = 1 },
            "return=representation", ct);
        return json is JsonArray { Count: > 0 } arr ? Parse(arr[0]!).Version : 1;
    }

    /// <summary>
    /// Optimistic update: only works if the remote is still at <paramref name="expectedVersion"/>.
    /// Returns the new version or null on a conflict (someone else was faster).
    /// </summary>
    public async Task<long?> UpdateVaultAsync(string token, long expectedVersion, byte[] blob, CancellationToken ct = default)
    {
        var json = await SendAsync(HttpMethod.Patch, $"/rest/v1/vaults?version=eq.{expectedVersion}", token,
            new JsonObject
            {
                ["blob"] = Convert.ToBase64String(blob),
                ["version"] = expectedVersion + 1,
                ["updated_at"] = DateTimeOffset.UtcNow.ToString("o")
            },
            "return=representation", ct);
        return json is JsonArray { Count: > 0 } arr ? Parse(arr[0]!).Version : null;
    }

    private static RemoteVault Parse(JsonNode row)
    {
        var version = row["version"]?.GetValue<long>() ?? 0;
        var blob = row["blob"]?.GetValue<string>() ?? throw new SyncException("Response without a blob.");
        return new RemoteVault(version, Convert.FromBase64String(blob));
    }

    // ------------------------------------------------------------------ HTTP

    private async Task<JsonNode?> SendAsync(HttpMethod method, string path, string? token, JsonNode? body, string? prefer, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(method, _url + path);
        req.Headers.Add("apikey", _anonKey);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token ?? _anonKey);
        if (prefer != null) req.Headers.Add("Prefer", prefer);
        if (body != null) req.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");

        HttpResponseMessage res;
        try
        {
            res = await _http.SendAsync(req, ct);
        }
        catch (HttpRequestException ex)
        {
            throw new SyncException("Cloud not reachable: " + ex.Message, ex);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new SyncException("The cloud does not answer (timeout).", ex);
        }

        using (res)
        {
            var text = await res.Content.ReadAsStringAsync(ct);
            if (!res.IsSuccessStatusCode) throw new SyncException(Describe(res.StatusCode, text));
            if (string.IsNullOrWhiteSpace(text)) return null;
            try { return JsonNode.Parse(text); }
            catch (JsonException ex) { throw new SyncException("Invalid response from the cloud.", ex); }
        }
    }

    private static string Describe(HttpStatusCode status, string body)
    {
        string? detail = null;
        try
        {
            var j = JsonNode.Parse(body);
            detail = j?["error_description"]?.GetValue<string>()
                     ?? j?["msg"]?.GetValue<string>()
                     ?? j?["message"]?.GetValue<string>()
                     ?? j?["error"]?.GetValue<string>();
        }
        catch { /* not JSON */ }

        if (detail != null && detail.Contains("Invalid login credentials", StringComparison.OrdinalIgnoreCase))
            return "E-mail or password of the cloud is wrong.";
        if (status == HttpStatusCode.NotFound && (detail?.Contains("vaults") ?? false))
            return "The table \"vaults\" is missing in Supabase. Run docs/supabase.sql.";

        return $"Cloud error {(int)status}" + (detail != null ? ": " + detail : "");
    }
}
