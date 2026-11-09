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
    }
}
