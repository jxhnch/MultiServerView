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

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
    {
        Requests++;
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
    public readonly SyncService Sync;

    public Device(FakeSupabase cloud)
    {
        Store = new VaultStore(Path.Combine(Dir, "vault.msv"));
        var settings = new SyncSettingsStore(Path.Combine(Dir, "sync.json"), new PlainProtector());
        Sync = new SyncService(Store, settings, cfg => new SupabaseClient(new HttpClient(cloud), cfg.Url, cfg.AnonKey));
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

public class SyncTests
{
    [Fact]
    public async Task First_sync_uploads_then_second_device_downloads_and_unlocks()
    {
        var cloud = new FakeSupabase();
        using var a = new Device(cloud);
        using var b = new Device(cloud);

        a.Store.Create("master-password");
        a.Store.Upsert(new ServerEntry { Name = "web-1", Host = "h", Password = "secret" });
        a.Sync.Save(Device.Config(cloud));

        var r1 = await a.Sync.SyncAsync();
        Assert.Equal(SyncOutcome.Created, r1.Outcome);
        Assert.DoesNotContain("secret", cloud.Blob!); // Base64 of the blob contains no plain-text password
        Assert.DoesNotContain("web-1", Encoding.UTF8.GetString(Convert.FromBase64String(cloud.Blob!)));

        await b.Sync.DownloadInitialAsync(Device.Config(cloud));
        b.Store.Unlock("master-password");
        Assert.Equal("web-1", b.Store.List().Single().Name);
        Assert.Equal("secret", b.Store.List().Single().Password);
    }

    [Fact]
    public async Task Changes_on_both_devices_are_merged()
    {
        var cloud = new FakeSupabase();
        using var a = new Device(cloud);
        using var b = new Device(cloud);

        a.Store.Create("master-password");
        a.Sync.Save(Device.Config(cloud));
        await a.Sync.SyncAsync();
        await b.Sync.DownloadInitialAsync(Device.Config(cloud));
        b.Store.Unlock("master-password");

        a.Store.Upsert(new ServerEntry { Name = "from-a", Host = "a" });
        b.Store.Upsert(new ServerEntry { Name = "from-b", Host = "b" });

        Assert.Equal(SyncOutcome.Pushed, (await a.Sync.SyncAsync()).Outcome);
        Assert.Equal(SyncOutcome.Merged, (await b.Sync.SyncAsync()).Outcome);
        Assert.Equal(SyncOutcome.Pulled, (await a.Sync.SyncAsync()).Outcome);

        Assert.Equal(new[] { "from-a", "from-b" }, a.Store.List().Select(s => s.Name).Order());
        Assert.Equal(new[] { "from-a", "from-b" }, b.Store.List().Select(s => s.Name).Order());
        Assert.Equal(SyncOutcome.UpToDate, (await b.Sync.SyncAsync()).Outcome);
    }

    [Fact]
    public async Task Deletions_propagate_between_devices()
    {
        var cloud = new FakeSupabase();
        using var a = new Device(cloud);
        using var b = new Device(cloud);

        a.Store.Create("master-password");
        var s = a.Store.Upsert(new ServerEntry { Name = "weg", Host = "h" });
        a.Sync.Save(Device.Config(cloud));
        await a.Sync.SyncAsync();
        await b.Sync.DownloadInitialAsync(Device.Config(cloud));
        b.Store.Unlock("master-password");

        a.Store.Delete(s.Id);
        await a.Sync.SyncAsync();
        await b.Sync.SyncAsync();

        Assert.Empty(b.Store.List());
    }

    [Fact]
    public async Task Conflict_on_push_is_retried()
    {
        var cloud = new FakeSupabase();
        using var a = new Device(cloud);
        using var other = new Device(cloud);

        a.Store.Create("master-password");
        a.Sync.Save(Device.Config(cloud));
        await a.Sync.SyncAsync();
        await other.Sync.DownloadInitialAsync(Device.Config(cloud));
        other.Store.Unlock("master-password");

        a.Store.Upsert(new ServerEntry { Name = "local", Host = "a" });
        other.Store.Upsert(new ServerEntry { Name = "anderes-geraet", Host = "b" });

        // The other device pushes exactly between "load" and "upload" of a.
        cloud.BeforePatch = async () => await other.Sync.SyncAsync();

        var r = await a.Sync.SyncAsync();

        Assert.Equal(2, a.Store.List().Count);
        Assert.Equal(SyncOutcome.Merged, r.Outcome);
    }

    [Fact]
    public async Task Different_vault_in_cloud_gives_clear_error()
    {
        var cloud = new FakeSupabase();
        using var a = new Device(cloud);
        using var b = new Device(cloud);

        a.Store.Create("master-password");
        a.Sync.Save(Device.Config(cloud));
        await a.Sync.SyncAsync();

        b.Store.Create("master-password"); // same password, but created separately -> different salt
        b.Sync.Save(Device.Config(cloud));

        var ex = await Assert.ThrowsAsync<SyncException>(() => b.Sync.SyncAsync());
        Assert.Contains("Load vault", ex.Message);
    }

    [Fact]
    public async Task Wrong_cloud_password_gives_friendly_error()
    {
        var cloud = new FakeSupabase();
        using var a = new Device(cloud);
        a.Store.Create("master-password");
        var cfg = Device.Config(cloud);
        cfg.Password = "wrong";
        a.Sync.Save(cfg);

        var ex = await Assert.ThrowsAsync<SyncException>(() => a.Sync.SyncAsync());
        Assert.Contains("is wrong", ex.Message);
    }

    [Fact]
    public async Task Download_without_cloud_vault_fails_clearly()
    {
        var cloud = new FakeSupabase();
        using var b = new Device(cloud);

        var ex = await Assert.ThrowsAsync<SyncException>(() => b.Sync.DownloadInitialAsync(Device.Config(cloud)));
        Assert.Contains("no vault in the cloud", ex.Message);
    }

    [Fact]
    public void Url_must_be_https()
    {
        Assert.Throws<SyncException>(() => new SupabaseClient(new HttpClient(), "http://example.com", "key"));
    }

    [Fact]
    public void Settings_are_stored_with_protected_password()
    {
        var dir = Path.Combine(Path.GetTempPath(), "msv-set-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new SyncSettingsStore(Path.Combine(dir, "sync.json"), new PlainProtector());
            store.Save(new SyncSettings { Url = "https://x.supabase.co", AnonKey = "k", Email = "e@x.ch", Password = "plaintext-pw" });

            Assert.DoesNotContain("plaintext-pw", File.ReadAllText(Path.Combine(dir, "sync.json")));
            Assert.Equal("plaintext-pw", store.Load().Password);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }
}
