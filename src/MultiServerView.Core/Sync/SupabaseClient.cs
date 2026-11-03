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
/// Minimaler Supabase-Client (nur REST): Auth (E-Mail/Passwort) + Tabelle "vaults".
/// In der Cloud liegt ausschliesslich der verschlüsselte Vault-Blob (Base64) - nie Klartext.
/// Das SQL zum Anlegen der Tabelle steht in docs/supabase.sql.
/// </summary>
public sealed class SupabaseClient
{
    private readonly HttpClient _http;
    private readonly string _url;
    private readonly string _anonKey;

    public SupabaseClient(HttpClient http, string url, string anonKey)
    {
        _http = http;
        _url = url.Trim().TrimEnd('/');
        _anonKey = anonKey.Trim();
        if (!Uri.TryCreate(_url, UriKind.Absolute, out var uri) || (uri.Scheme != "https" && !uri.IsLoopback))
            throw new SyncException("Die Supabase-URL muss mit https:// beginnen.");
        if (_anonKey.Length == 0) throw new SyncException("Der Supabase-Anon-Key fehlt.");
    }

    // ------------------------------------------------------------------ Auth

    /// <summary>Meldet an und gibt den Access-Token zurück.</summary>
    public async Task<string> SignInAsync(string email, string password, CancellationToken ct = default)
    {
        var json = await SendAsync(HttpMethod.Post, "/auth/v1/token?grant_type=password", null,
            new JsonObject { ["email"] = email, ["password"] = password }, null, ct);
        var token = json?["access_token"]?.GetValue<string>();
        return string.IsNullOrEmpty(token) ? throw new SyncException("Anmeldung fehlgeschlagen (kein Token erhalten).") : token;
    }

    /// <summary>Legt ein Konto an. true = direkt angemeldet, false = E-Mail muss erst bestätigt werden.</summary>
    public async Task<bool> SignUpAsync(string email, string password, CancellationToken ct = default)
    {
        var json = await SendAsync(HttpMethod.Post, "/auth/v1/signup", null,
            new JsonObject { ["email"] = email, ["password"] = password }, null, ct);
        return !string.IsNullOrEmpty(json?["access_token"]?.GetValue<string>());
    }

    // ------------------------------------------------------------------ Vault-Zeile

    public async Task<RemoteVault?> GetVaultAsync(string token, CancellationToken ct = default)
    {
        var json = await SendAsync(HttpMethod.Get, "/rest/v1/vaults?select=version,blob&limit=1", token, null, null, ct);
        if (json is not JsonArray { Count: > 0 } arr) return null;
        return Parse(arr[0]!);
    }

    /// <summary>Legt die Zeile erstmals an (Version 1).</summary>
    public async Task<long> InsertVaultAsync(string token, byte[] blob, CancellationToken ct = default)
    {
        var json = await SendAsync(HttpMethod.Post, "/rest/v1/vaults", token,
            new JsonObject { ["blob"] = Convert.ToBase64String(blob), ["version"] = 1 },
            "return=representation", ct);
        return json is JsonArray { Count: > 0 } arr ? Parse(arr[0]!).Version : 1;
    }

    /// <summary>
    /// Optimistisches Update: klappt nur, wenn die Cloud noch auf <paramref name="expectedVersion"/> steht.
    /// Gibt die neue Version zurück oder null bei Konflikt (jemand anderes war schneller).
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
        var blob = row["blob"]?.GetValue<string>() ?? throw new SyncException("Antwort ohne Blob.");
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
            throw new SyncException("Cloud nicht erreichbar: " + ex.Message, ex);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new SyncException("Cloud antwortet nicht (Zeitüberschreitung).", ex);
        }

        using (res)
        {
            var text = await res.Content.ReadAsStringAsync(ct);
            if (!res.IsSuccessStatusCode) throw new SyncException(Describe(res.StatusCode, text));
            if (string.IsNullOrWhiteSpace(text)) return null;
            try { return JsonNode.Parse(text); }
            catch (JsonException ex) { throw new SyncException("Ungültige Antwort der Cloud.", ex); }
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
        catch { /* kein JSON */ }

        if (detail != null && detail.Contains("Invalid login credentials", StringComparison.OrdinalIgnoreCase))
            return "E-Mail oder Passwort der Cloud stimmt nicht.";
        if (status == HttpStatusCode.NotFound && (detail?.Contains("vaults") ?? false))
            return "Die Tabelle «vaults» fehlt in Supabase. Führe docs/supabase.sql aus.";

        return $"Cloud-Fehler {(int)status}" + (detail != null ? ": " + detail : "");
    }
}
