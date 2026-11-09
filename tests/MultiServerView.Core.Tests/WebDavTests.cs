using System.Net;
using MultiServerView.Core.Sync;
using Xunit;

namespace MultiServerView.Core.Tests;

/// <summary>A tiny in-memory WebDAV file with ETag handling.</summary>
internal sealed class FakeWebDav : HttpMessageHandler
{
    public byte[]? Content;
    private int _rev;
    public string? LastAuth;
    public bool NoEtags;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        LastAuth = request.Headers.Authorization?.Parameter;
        var etag = $"\"v{_rev}\"";
        HttpResponseMessage Ok(HttpStatusCode code, byte[]? body = null)
        {
            var r = new HttpResponseMessage(code);
            if (body != null) r.Content = new ByteArrayContent(body);
            if (!NoEtags && Content != null) r.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue(code == HttpStatusCode.OK && request.Method == HttpMethod.Get ? etag : $"\"v{_rev}\"");
            return r;
        }

        if (request.Method == HttpMethod.Get)
            return Content == null ? new HttpResponseMessage(HttpStatusCode.NotFound) : Ok(HttpStatusCode.OK, Content);

        if (request.Method == HttpMethod.Put)
        {
            if (request.Headers.TryGetValues("If-None-Match", out var none) && none.Contains("*") && Content != null)
                return new HttpResponseMessage(HttpStatusCode.PreconditionFailed);
            if (request.Headers.TryGetValues("If-Match", out var match) && match.First() != etag)
                return new HttpResponseMessage(HttpStatusCode.PreconditionFailed);
            Content = await request.Content!.ReadAsByteArrayAsync(ct);
            _rev++;
            return Ok(HttpStatusCode.Created);
        }
        return new HttpResponseMessage(HttpStatusCode.MethodNotAllowed);
    }
}

public class WebDavTests
{
    private static WebDavRemote Remote(FakeWebDav fake) => new(new HttpClient(fake), "https://cloud.example.com/dav/msv", "john", "app-pw");

    [Fact]
    public async Task Insert_get_update_round_trip()
    {
        var fake = new FakeWebDav();
        var remote = Remote(fake);
        Assert.Null(await remote.GetAsync());

        var v1 = await remote.InsertAsync(new byte[] { 1, 2, 3 });
        var got = await remote.GetAsync();
        Assert.Equal(v1, got!.Version);
        Assert.Equal(new byte[] { 1, 2, 3 }, got.Blob);

        var v2 = await remote.UpdateAsync(v1, new byte[] { 4, 5 });
        Assert.NotNull(v2);
        Assert.Equal(new byte[] { 4, 5 }, fake.Content);
        Assert.Equal(Convert.ToBase64String("john:app-pw"u8.ToArray()), fake.LastAuth);
    }

    [Fact]
    public async Task Update_after_another_device_changed_the_file_reports_a_conflict()
    {
        var fake = new FakeWebDav();
        var a = Remote(fake);
        var b = Remote(fake);
        var v1 = await a.InsertAsync(new byte[] { 1 });
        await b.GetAsync();
        await a.GetAsync();

        Assert.NotNull(await b.UpdateAsync(v1, new byte[] { 2 })); // b is first
        Assert.Null(await a.UpdateAsync(v1, new byte[] { 3 }));    // a still holds the old ETag
        Assert.Equal(new byte[] { 2 }, fake.Content);
    }

    [Fact]
    public async Task Server_without_etags_falls_back_to_comparing_content()
    {
        var fake = new FakeWebDav { NoEtags = true };
        var remote = Remote(fake);
        var v1 = await remote.InsertAsync(new byte[] { 1 });
        Assert.NotNull(await remote.UpdateAsync(v1, new byte[] { 2 }));
        Assert.Null(await remote.UpdateAsync(v1, new byte[] { 3 })); // v1 is outdated now
    }

    [Fact]
    public async Task Creating_over_an_existing_vault_is_refused()
    {
        var fake = new FakeWebDav();
        await Remote(fake).InsertAsync(new byte[] { 1 });
        await Assert.ThrowsAsync<SyncException>(() => Remote(fake).InsertAsync(new byte[] { 2 }));
    }

    [Theory]
    [InlineData("http://cloud.example.com/dav")]
    [InlineData("")]
    public void Plain_http_to_a_remote_host_and_empty_addresses_are_rejected(string url) =>
        Assert.Throws<SyncException>(() => new WebDavRemote(new HttpClient(), url, "u", "p"));
}
