using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using MultiServerView.Core.Sync;
using Xunit;

namespace MultiServerView.Core.Tests;

/// <summary>A tiny fake of the GitHub gists API: list, get, create, patch (one gist, one file).</summary>
internal sealed class FakeGitHub : HttpMessageHandler
{
    public string? Content;
    public string? Auth;
    public bool Bad;
    public int Lists;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        Auth = request.Headers.Authorization?.Parameter;
        if (Bad) return new HttpResponseMessage(HttpStatusCode.Unauthorized);
        var path = request.RequestUri!.AbsolutePath;
        static HttpResponseMessage Json(JsonNode n, HttpStatusCode c = HttpStatusCode.OK) => new(c) { Content = new StringContent(n.ToJsonString(), Encoding.UTF8, "application/json") };
        JsonObject Files() => new() { [GistRemote.FileName] = new JsonObject { ["content"] = Content, ["truncated"] = false } };

        if (request.Method == HttpMethod.Get && path == "/gists")
        {
            Lists++;
            var list = new JsonArray(new JsonObject { ["id"] = "other", ["files"] = new JsonObject { ["notes.txt"] = new JsonObject() } });
            if (Content != null) list.Add(new JsonObject { ["id"] = "g1", ["files"] = Files() });
            return Json(list);
        }
        if (request.Method == HttpMethod.Get && path == "/gists/g1") return Json(new JsonObject { ["id"] = "g1", ["files"] = Files() });

        var body = request.Content == null ? null : JsonNode.Parse(await request.Content.ReadAsStringAsync(ct));
        if (request.Method == HttpMethod.Post && path == "/gists")
        {
            Assert.False(body!["public"]!.GetValue<bool>()); // must be a secret gist
            Content = body["files"]![GistRemote.FileName]!["content"]!.GetValue<string>();
            return Json(new JsonObject { ["id"] = "g1" }, HttpStatusCode.Created);
        }
        if (request.Method == HttpMethod.Patch && path == "/gists/g1")
        {
            Content = body!["files"]![GistRemote.FileName]!["content"]!.GetValue<string>();
            return Json(new JsonObject { ["id"] = "g1" });
        }
        return new HttpResponseMessage(HttpStatusCode.NotFound);
    }
}

public class GistTests
{
    [Fact]
    public async Task Create_find_and_update_round_trip()
    {
        var fake = new FakeGitHub();
        var a = new GistRemote(new HttpClient(fake), "ghp_token");
        Assert.Null(await a.GetAsync());

        var v1 = await a.InsertAsync(new byte[] { 1, 2, 3 });
        Assert.Equal("ghp_token", fake.Auth);

        // a second PC only has the token: it finds the gist by its file name
        var b = new GistRemote(new HttpClient(fake), "ghp_token");
        var got = await b.GetAsync();
        Assert.Equal(new byte[] { 1, 2, 3 }, got!.Blob);
        Assert.Equal(v1, got.Version);

        Assert.NotNull(await b.UpdateAsync(v1, new byte[] { 9 }));
        Assert.Equal(Convert.ToBase64String(new byte[] { 9 }), fake.Content);
    }

    [Fact]
    public async Task Update_with_an_outdated_version_reports_a_conflict()
    {
        var fake = new FakeGitHub();
        var a = new GistRemote(new HttpClient(fake), "t");
        var v1 = await a.InsertAsync(new byte[] { 1 });
        Assert.NotNull(await a.UpdateAsync(v1, new byte[] { 2 }));
        Assert.Null(await a.UpdateAsync(v1, new byte[] { 3 }));
        Assert.Equal(Convert.ToBase64String(new byte[] { 2 }), fake.Content);
    }

    [Fact]
    public async Task Bad_token_gives_a_clear_message()
    {
        var ex = await Assert.ThrowsAsync<SyncException>(() => new GistRemote(new HttpClient(new FakeGitHub { Bad = true }), "nope").GetAsync());
        Assert.Contains("refused the token", ex.Message);
    }

    [Fact]
    public void Empty_token_is_rejected() => Assert.Throws<SyncException>(() => new GistRemote(new HttpClient(), " "));
}
