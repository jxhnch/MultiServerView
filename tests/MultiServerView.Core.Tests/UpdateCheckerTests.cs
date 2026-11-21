using MultiServerView.Core.Update;
using Xunit;

namespace MultiServerView.Core.Tests;

public class UpdateCheckerTests
{
    private const string Release = """
        { "tag_name": "v1.2.0", "draft": false, "prerelease": false, "html_url": "https://github.com/jxhnch/MultiServerView/releases/tag/v1.2.0", "body": "notes",
          "assets": [
            { "name": "MultiServerView-1.2.0-portable-win-x64.zip", "browser_download_url": "https://github.com/jxhnch/MultiServerView/releases/download/v1.2.0/a.zip" },
            { "name": "MultiServerView-Setup-1.2.0.exe", "browser_download_url": "https://github.com/jxhnch/MultiServerView/releases/download/v1.2.0/MultiServerView-Setup-1.2.0.exe" } ] }
        """;

    [Fact]
    public void Newer_release_is_found_with_its_setup_asset()
    {
        var info = UpdateChecker.Parse(Release, "1.1.0")!;
        Assert.Equal("1.2.0", info.Version);
        Assert.EndsWith("Setup-1.2.0.exe", info.SetupUrl);
    }

    [Theory]
    [InlineData("1.2.0")]
    [InlineData("1.10.0")]
    public void Same_or_newer_installed_version_gives_no_update(string current) =>
        Assert.Null(UpdateChecker.Parse(Release, current));

    [Fact]
    public void Versions_compare_numerically_not_as_text() => Assert.True(UpdateChecker.IsNewer("1.10.0", "1.9.3"));

    [Fact]
    public void Prerelease_and_foreign_download_hosts_are_ignored()
    {
        Assert.Null(UpdateChecker.Parse(Release.Replace("\"prerelease\": false", "\"prerelease\": true"), "1.0.0"));
        var evil = Release.Replace("https://github.com/jxhnch/MultiServerView/releases/download/v1.2.0/MultiServerView-Setup-1.2.0.exe", "https://evil.example.com/Setup.exe");
        Assert.Null(UpdateChecker.Parse(evil, "1.0.0")!.SetupUrl);

        var foreignRepo = Release.Replace("https://github.com/jxhnch/MultiServerView/releases/download/v1.2.0/MultiServerView-Setup-1.2.0.exe", "https://github.com/attacker/fake/releases/download/v1.2.0/Setup.exe");
        Assert.Null(UpdateChecker.Parse(foreignRepo, "1.0.0")!.SetupUrl);
    }

    [Theory]
    [InlineData("1.2.0")]
    [InlineData("1.2.0-beta.1")]
    public void SanitizeVersionForFileName_accepts_normal_versions(string v) =>
        Assert.Equal(v, UpdateChecker.SanitizeVersionForFileName(v));

    [Theory]
    [InlineData("")]
    [InlineData("1.2.0/../../evil")]
    [InlineData("1.2.0\\evil")]
    [InlineData("1.2.0..evil")]
    public void SanitizeVersionForFileName_rejects_traversal(string v) =>
        Assert.Throws<InvalidOperationException>(() => UpdateChecker.SanitizeVersionForFileName(v));

    [Fact]
    public void Parses_checksums_asset_and_extracts_sha256_entries()
    {
        var withSums = Release.Replace("] }", ", { \"name\": \"SHA256SUMS.txt\", \"browser_download_url\": \"https://github.com/jxhnch/MultiServerView/releases/download/v1.2.0/SHA256SUMS.txt\" } ] }");
        var info = UpdateChecker.Parse(withSums, "1.1.0")!;
        Assert.Equal("MultiServerView-Setup-1.2.0.exe", info.SetupName);
        Assert.EndsWith("SHA256SUMS.txt", info.ChecksumsUrl);

        var hash = new string('a', 64);
        var manifest = $"{hash.ToUpperInvariant()}  MultiServerView-Setup-1.2.0.exe\n{new string('b', 64)} *other.zip\n";
        Assert.Equal(hash, UpdateChecker.ParseSha256Sum(manifest, "MultiServerView-Setup-1.2.0.exe"));
        Assert.Equal(new string('b', 64), UpdateChecker.ParseSha256Sum(manifest, "other.zip"));
        Assert.Null(UpdateChecker.ParseSha256Sum(manifest, "missing.exe"));
        Assert.Null(UpdateChecker.ParseSha256Sum("not-a-hash  MultiServerView-Setup-1.2.0.exe\n", "MultiServerView-Setup-1.2.0.exe"));
    }

    [Fact]
    public async Task CheckAsync_returns_null_on_404_and_throws_on_rate_limit_or_error()
    {
        using var notFound = new HttpClient(new StubHandler(System.Net.HttpStatusCode.NotFound));
        Assert.Null(await UpdateChecker.CheckAsync(notFound, "1.0.0"));

        using var rateLimited = new HttpClient(new StubHandler(System.Net.HttpStatusCode.Forbidden));
        var rlEx = await Assert.ThrowsAsync<InvalidOperationException>(() => UpdateChecker.CheckAsync(rateLimited, "1.0.0"));
        Assert.Contains("rate limit", rlEx.Message, StringComparison.OrdinalIgnoreCase);

        using var serverError = new HttpClient(new StubHandler(System.Net.HttpStatusCode.InternalServerError));
        var seEx = await Assert.ThrowsAsync<InvalidOperationException>(() => UpdateChecker.CheckAsync(serverError, "1.0.0"));
        Assert.Contains("500", seEx.Message);
    }

    private sealed class StubHandler(System.Net.HttpStatusCode status) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(status));
    }
}
