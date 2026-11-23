using MultiServerView.Core.Web;
using Xunit;

namespace MultiServerView.Core.Tests;

public class WebTests
{
    [Theory]
    [InlineData("fw.lan", 0, "https://fw.lan", 443, "/")]
    [InlineData("fw.lan", 8006, "https://fw.lan:8006", 8006, "/")]
    [InlineData("192.168.1.1", 80, "http://192.168.1.1", 80, "/")]
    [InlineData("https://fw.lan:8443/login?x=1", 443, "https://fw.lan:8443", 8443, "/login?x=1")]
    [InlineData("http://printer.lan/status", 0, "http://printer.lan", 80, "/status")]
    public void Target_is_parsed(string host, int port, string origin, int resultPort, string path)
    {
        var t = WebTarget.Parse(host, port);
        Assert.Equal(origin, t.Origin);
        Assert.Equal(resultPort, t.Port);
        Assert.Equal(path, t.StartPath);
    }

    [Theory]
    [InlineData("")]
    [InlineData("ftp://x.lan")]
    [InlineData("https://user:pw@fw.lan")]
    public void Bad_targets_are_refused(string host) => Assert.Throws<ArgumentException>(() => WebTarget.Parse(host, 0));

    [Fact]
    public void Origins_in_text_and_json_are_replaced()
    {
        var text = WebRewriter.ReplaceOrigin("<a href=\"https://fw.lan:8443/x\">{\"u\":\"https:\\/\\/fw.lan:8443\\/y\"}", "https://fw.lan:8443", "http://127.0.0.1:5000");
        Assert.Contains("http://127.0.0.1:5000/x", text);
        Assert.Contains("http:\\/\\/127.0.0.1:5000\\/y", text);
        Assert.DoesNotContain("fw.lan", text);
    }

    [Fact]
    public void Redirects_to_the_device_stay_in_the_proxy()
    {
        Assert.Equal("http://127.0.0.1:5000/login", WebRewriter.RewriteLocation("https://fw.lan/login", "https://fw.lan", "http://127.0.0.1:5000"));
        Assert.Equal("https://other.example/x", WebRewriter.RewriteLocation("https://other.example/x", "https://fw.lan", "http://127.0.0.1:5000"));
    }

    [Fact]
    public void Cookies_lose_domain_secure_and_samesite_none()
        => Assert.Equal("sid=1; Path=/; HttpOnly", WebRewriter.RewriteSetCookie("sid=1; Domain=fw.lan; Path=/; Secure; HttpOnly; SameSite=None"));

    [Fact]
    public void Login_script_keeps_the_password_inside_a_string()
    {
        var script = WebRewriter.BuildAutoLoginScript("adm\"in", "</script><b>x");
        Assert.DoesNotContain("</script><b>", script);
        Assert.StartsWith("<script>", script);
        Assert.Equal("", WebRewriter.BuildAutoLoginScript("", ""));
    }

    [Fact]
    public void Script_is_put_after_the_head_tag()
        => Assert.Equal("<html><head ><script>1</script><title>t</title></head>", WebRewriter.InjectIntoHtml("<html><head ><title>t</title></head>", "<script>1</script>"));
}
