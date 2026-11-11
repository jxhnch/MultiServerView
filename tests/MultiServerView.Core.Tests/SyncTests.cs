using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using MultiServerView.Core;
using MultiServerView.Core.Sync;
using Xunit;

namespace MultiServerView.Core.Tests;

/// <summary>Simulates the Supabase API (auth + table "vaults" with optimistic version check).</summary>
internal sealed class FakeSupabase : HttpMessageHandler
{
    public string Email = "me@example.com";
    public string Password = "cloud-pw";
    public string? Blob;
    public long Version;
    public int Requests;
    public Func<Task>? BeforePatch;
    public bool Down;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
    {
        Requests++;
        if (Down) throw new HttpRequestException("no connection");
        var path = req.RequestUri!.AbsolutePath;
        var body = req.Content == null ? null : JsonNode.Parse(await req.Content.ReadAsStringAsync(ct));

        if (path == "/auth/v1/token")
        {
            return body!["email"]!.GetValue<string>() == Email && body["password"]!.GetValue<string>() == Password
                ? Json(HttpStatusCode.OK, """{"access_token":"tok"}""")
                : Json(HttpStatusCode.BadRequest, """{"error_description":"Invalid login credentials"}""");
        }
        if (path == "/auth/v1/signup")
            return Json(HttpStatusCode.OK, """{"id":"u1"}""");

        if (req.Headers.Authorization?.Parameter != "tok")
            return Json(HttpStatusCode.Unauthorized, """{"message":"JWT invalid"}""");

        if (path == "/rest/v1/vaults")
        {
            if (req.Method == HttpMethod.Get)
                return Json(HttpStatusCode.OK, Blob == null ? "[]" : Row());

            if (req.Method == HttpMethod.Post)
            {
                Blob = body!["blob"]!.GetValue<string>();
                Version = 1;
                return Json(HttpStatusCode.Created, Row());
            }

            if (req.Method == HttpMethod.Patch)
            {
                var hook = BeforePatch;
                BeforePatch = null;
                if (hook != null) await hook();
                var expected = long.Parse(req.RequestUri.Query.Split("version=eq.")[1].Split('&')[0]);
                if (Blob == null || Version != expected) return Json(HttpStatusCode.OK, "[]");
                Blob = body!["blob"]!.GetValue<string>();
                Version = body["version"]!.GetValue<long>();
                return Json(HttpStatusCode.OK, Row());
            }
        }
        return Json(HttpStatusCode.NotFound, """{"message":"nope"}""");
    }

    private string Row() => $$"""[{"version":{{Version}},"blob":"{{Blob}}"}]""";

    private static HttpResponseMessage Json(HttpStatusCode code, string json) =>
        new(code) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
}

/// <summary>A "device": own vault, own settings, shared fake cloud.</summary>
internal sealed class Device : IDisposable
{
    public readonly string Dir = Path.Combine(Path.GetTempPath(), "msv-sync-" + Guid.NewGuid().ToString("N"));
    public readonly VaultStore Store;
    public readonly SourceService Source;
    public readonly SyncSettingsStore Settings;

    public Device(FakeSupabase cloud)
    {
        Store = new VaultStore(Path.Combine(Dir, "vault.msv"));
        Settings = new SyncSettingsStore(Path.Combine(Dir, "sync.json"), new PlainProtector());
        Source = new SourceService(Store, Settings, cfg => new SupabaseClient(new HttpClient(cloud), cfg.Url, cfg.AnonKey));
    }

    public static SyncSettings Config(FakeSupabase cloud) => new()
    {
        Url = "https://example.supabase.co",
        AnonKey = "anon",
        Email = cloud.Email,
        Password = cloud.Password
    };

    public void Dispose()
    {
        if (Directory.Exists(Dir)) Directory.Delete(Dir, true);
    }
}

public class SupabaseUrlTests
{
    [Theory]
    [InlineData("https://abcdxyz.supabase.co", "https://abcdxyz.supabase.co")]
    [InlineData("https://abcdxyz.supabase.co/", "https://abcdxyz.supabase.co")]
    [InlineData("  https://abcdxyz.supabase.co/rest/v1/  ", "https://abcdxyz.supabase.co")]
    [InlineData("abcdxyz.supabase.co", "https://abcdxyz.supabase.co")]
    [InlineData("abcdxyz", "https://abcdxyz.supabase.co")]
    public void Pasted_project_addresses_are_normalized(string input, string expected) =>
        Assert.Equal(expected, MultiServerView.Core.Sync.SupabaseClient.NormalizeUrl(input));
}
