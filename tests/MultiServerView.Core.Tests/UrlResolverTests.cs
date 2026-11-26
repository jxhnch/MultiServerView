using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using MultiServerView.Core.Proxmox;
using MultiServerView.Core.Web;
using Xunit;

namespace MultiServerView.Core.Tests;

public class UrlResolverTests
{
    /// <summary>A pretend network: only the listed "scheme:port" answer.</summary>
    private static Func<string, string, int, CancellationToken, Task<ProbeAnswer>> Network(params string[] open)
        => (scheme, _, port, _) => Task.FromResult(new ProbeAnswer(open.Contains($"{scheme}:{port}")));

    [Theory]
    [InlineData("https//fw.lan", "https://fw.lan")]
    [InlineData("https:/fw.lan", "https://fw.lan")]
    [InlineData("https:\\\\fw.lan", "https://fw.lan")]
    [InlineData("HTTP;//fw.lan:81/x", "http://fw.lan:81/x")]
    [InlineData("  https://fw.lan  ", "https://fw.lan")]
    [InlineData("https:fw.lan", "https://fw.lan")]
    [InlineData("httpserver.lan", "httpserver.lan")]
    [InlineData("http:8080", "http:8080")]
    public void Typing_mistakes_are_repaired(string typed, string expected) => Assert.Equal(expected, UrlResolver.Repair(typed));

    [Theory]
    [InlineData("fw.lan", "https:443", "https://fw.lan")]
    [InlineData("fw.lan", "http:80", "http://fw.lan")]
    [InlineData("fw.lan", "https:8006", "https://fw.lan:8006")]
    [InlineData("fw.lan:8443", "https:8443", "https://fw.lan:8443")]
    [InlineData("fw.lan:81", "http:81", "http://fw.lan:81")]
    [InlineData("http://fw.lan:8006", "https:8006", "https://fw.lan:8006")] // wrong scheme typed
    [InlineData("https://fw.lan/login?a=1", "https:8443", "https://fw.lan:8443/login?a=1")]
    [InlineData("http://fw.lan", "https:443", "https://fw.lan")]
    [InlineData("192.168.1.1", "https:443", "https://192.168.1.1")]
    [InlineData("[fd00::1]:8006", "https:8006", "https://[fd00::1]:8006")]
    public async Task The_form_that_answers_is_found(string typed, string open, string expected)
        => Assert.Equal(expected, await UrlResolver.FindAsync(typed, probe: Network(open)));

    [Fact]
    public async Task Https_wins_over_http()
        => Assert.Equal("https://fw.lan:8443", await UrlResolver.FindAsync("fw.lan", probe: Network("http:80", "https:8443")));

    [Theory]
    [InlineData("https://fw.lan")]
    [InlineData("https://fw.lan:443")]
    public async Task A_typed_https_never_becomes_http(string typed)
        => Assert.Null(await UrlResolver.FindAsync(typed, probe: Network("http:80", "http:443", "http:8080")));

    [Fact]
    public async Task A_saved_address_with_port_stays_on_that_port()
        => Assert.Null(await UrlResolver.FindAsync(UrlResolver.Exact("https://fw.lan"), probe: Network("https:8443", "http:80")));

    [Fact]
    public async Task A_port_next_to_the_address_counts_as_typed()
        => Assert.Equal("http://fw.lan:81", await UrlResolver.FindAsync("fw.lan", 81, Network("http:81", "https:443")));

    [Fact]
    public async Task A_redirect_to_https_on_the_same_host_is_followed()
    {
        Task<ProbeAnswer> Probe(string scheme, string host, int port, CancellationToken ct) => Task.FromResult(
            scheme == "http" && port == 80 ? new ProbeAnswer(true, "https://fw.lan:10443/") :
            new ProbeAnswer(scheme == "https" && port == 10443));
        Assert.Equal("https://fw.lan:10443", await UrlResolver.FindAsync("fw.lan", probe: Probe));
    }

    [Fact]
    public async Task A_redirect_to_another_host_is_not_followed()
    {
        Task<ProbeAnswer> Probe(string scheme, string host, int port, CancellationToken ct) => Task.FromResult(
            scheme == "http" && port == 80 ? new ProbeAnswer(true, "https://elsewhere.example/") : new ProbeAnswer(false));
        Assert.Equal("http://fw.lan", await UrlResolver.FindAsync("fw.lan", probe: Probe));
    }

    [Theory]
    [InlineData("")]
    [InlineData("two words")]
    [InlineData("ftp://fw.lan")]
    [InlineData("user:secret@fw.lan")]
    [InlineData("fw.lan:99999")]
    public async Task Unusable_text_is_left_alone(string typed)
        => Assert.Null(await UrlResolver.FindAsync(typed, probe: Network("https:443")));

    [Theory]
    [InlineData("pve.lan", "https:8006", "https://pve.lan:8006")]
    [InlineData("pve.lan", "https:443", "https://pve.lan:443")]
    [InlineData("http://pve.lan:8006/", "https:8006", "https://pve.lan:8006")]
    [InlineData("https//pve.lan:8006/#v1:0", "https:8006", "https://pve.lan:8006")]
    [InlineData("pve.lan", "https:1", "https://pve.lan:8006")] // nothing answers: the usual form
    public async Task The_proxmox_address_is_completed(string typed, string open, string expected)
        => Assert.Equal(expected, await ProxmoxClient.FindAddressAsync(typed, Network(open)));

    [Theory]
    [InlineData("pve.lan", "https://pve.lan:8006")]
    [InlineData("https://pve.lan", "https://pve.lan:8006")]
    [InlineData("https://pve.lan:443", "https://pve.lan:443")]
    [InlineData("pve.lan:8443", "https://pve.lan:8443")]
    public void A_typed_proxmox_port_is_kept(string typed, string expected) => Assert.Equal(expected, ProxmoxClient.NormalizeUrl(typed));

    [Fact]
    public async Task A_real_https_server_with_a_self_signed_certificate_is_found_even_when_http_was_typed()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=device", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var created = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        // Windows needs the private key in a loaded form
        using var certificate = new X509Certificate2(created.Export(X509ContentType.Pfx));
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var serving = Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    using var client = await listener.AcceptTcpClientAsync();
                    await using var tls = new SslStream(client.GetStream());
                    try { await tls.AuthenticateAsServerAsync(certificate); }
                    catch (Exception ex) when (ex is System.Security.Authentication.AuthenticationException or IOException) { /* a plain http request */ }
                }
            }
            catch (Exception ex) when (ex is SocketException or ObjectDisposedException or IOException or InvalidOperationException) { /* test is over */ }
        });
        try
        {
            Assert.True((await UrlResolver.ProbeAsync("https", "127.0.0.1", port)).Answers);
            Assert.Equal($"https://127.0.0.1:{port}", await UrlResolver.FindAsync($"http://127.0.0.1:{port}"));
        }
        finally
        {
            listener.Stop();
            await serving;
        }
    }

    [Fact]
    public async Task A_real_http_server_is_recognised_and_is_no_https()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var serving = Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    using var client = await listener.AcceptTcpClientAsync();
                    var stream = client.GetStream();
                    var buffer = new byte[2048];
                    var read = await stream.ReadAsync(buffer);
                    if (read == 0 || buffer[0] != (byte)'G') continue; // a TLS hello: just hang up
                    await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 302 Found\r\nLocation: /login\r\nConnection: close\r\n\r\n"));
                }
            }
            catch (Exception ex) when (ex is SocketException or ObjectDisposedException or IOException or InvalidOperationException) { /* test is over */ }
        });
        try
        {
            Assert.False((await UrlResolver.ProbeAsync("https", "127.0.0.1", port)).Answers);
            var answer = await UrlResolver.ProbeAsync("http", "127.0.0.1", port);
            Assert.True(answer.Answers);
            Assert.Equal("/login", answer.Location);
            Assert.Equal($"http://127.0.0.1:{port}", await UrlResolver.FindAsync($"127.0.0.1:{port}"));
        }
        finally
        {
            listener.Stop();
            await serving;
        }
    }
}
