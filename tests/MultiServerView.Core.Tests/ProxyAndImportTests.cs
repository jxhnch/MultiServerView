using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using MultiServerView.Core;
using MultiServerView.Core.Import;
using MultiServerView.Core.Ssh;
using MultiServerView.Core.Vnc;
using Xunit;

namespace MultiServerView.Core.Tests;

public class ProxyTests
{
    /// <summary>Small TCP server that sends everything back (echo) - a stand-in for a VNC server.</summary>
    private static (TcpListener listener, int port) StartEcho()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        _ = Task.Run(async () =>
        {
            while (true)
            {
                TcpClient c;
                try { c = await listener.AcceptTcpClientAsync(); } catch { return; }
                _ = Task.Run(async () =>
                {
                    using var _ = c;
                    var s = c.GetStream();
                    var buf = new byte[4096];
                    int n;
                    while ((n = await s.ReadAsync(buf)) > 0) await s.WriteAsync(buf.AsMemory(0, n));
                });
            }
        });
        return (listener, ((IPEndPoint)listener.LocalEndpoint).Port);
    }

    [Fact]
    public async Task Websocket_bytes_reach_tcp_server_and_come_back()
    {
        var (echo, port) = StartEcho();
        using var proxy = new WebSocketTcpProxy();
        var url = proxy.Register("127.0.0.1", port);

        using var ws = new ClientWebSocket();
        ws.Options.AddSubProtocol("binary");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await ws.ConnectAsync(new Uri(url), cts.Token);
        Assert.Equal("binary", ws.SubProtocol);

        var payload = RandomNumberGenerator.GetBytes(50_000);
        await ws.SendAsync(payload, WebSocketMessageType.Binary, true, cts.Token);

        var received = new List<byte>();
        var buf = new byte[8192];
        while (received.Count < payload.Length)
        {
            var r = await ws.ReceiveAsync(buf, cts.Token);
            received.AddRange(buf.AsSpan(0, r.Count).ToArray());
        }

        Assert.Equal(payload, received.ToArray());
        echo.Stop();
    }

    [Fact]
    public async Task Unknown_or_reused_token_is_rejected()
    {
        var (echo, port) = StartEcho();
        using var proxy = new WebSocketTcpProxy();
        var url = proxy.Register("127.0.0.1", port);

        using (var first = new ClientWebSocket())
        {
            await first.ConnectAsync(new Uri(url), CancellationToken.None); // once works
        }

        using var second = new ClientWebSocket();
        await Assert.ThrowsAnyAsync<WebSocketException>(() => second.ConnectAsync(new Uri(url), CancellationToken.None));

        using var forged = new ClientWebSocket();
        await Assert.ThrowsAnyAsync<WebSocketException>(() =>
            forged.ConnectAsync(new Uri($"ws://127.0.0.1:{proxy.Port}/deadbeef"), CancellationToken.None));
        echo.Stop();
    }

    [Fact]
    public async Task Unreachable_target_fails_the_handshake()
    {
        var free = new TcpListener(IPAddress.Loopback, 0);
        free.Start();
        var deadPort = ((IPEndPoint)free.LocalEndpoint).Port;
        free.Stop();

        using var proxy = new WebSocketTcpProxy();
        var url = proxy.Register("127.0.0.1", deadPort);

        using var ws = new ClientWebSocket();
        await Assert.ThrowsAnyAsync<WebSocketException>(() => ws.ConnectAsync(new Uri(url), CancellationToken.None));
    }
}

public class ImporterTests
{
    private const string Sample = """
        <?xml version="1.0" encoding="utf-8"?>
        <Connections Name="Connections" Export="false" EncryptionEngine="AES" BlockCipherMode="GCM" KdfIterations="1000" FullFileEncryption="false" ConfVersion="2.6">
          <Node Name="Production" Type="Container" Expanded="true">
            <Node Name="web-01" Type="Connection" Descr="Frontend" Hostname="10.0.0.5" Protocol="SSH2" Port="2222" Username="deploy" Password="encrypted" />
            <Node Name="ad-01" Type="Connection" Hostname="ad.corp.local" Protocol="RDP" Port="3389" Username="admin" Domain="CORP" />
            <Node Name="Subfolder" Type="Container">
              <Node Name="vnc-box" Type="Connection" Hostname="10.0.0.9" Protocol="VNC" Port="5900" />
            </Node>
          </Node>
          <Node Name="Web interface" Type="Connection" Hostname="router.local" Protocol="HTTP" Port="80" />
          <Node Name="no-port" Type="Connection" Hostname="h.local" Protocol="RDP" />
        </Connections>
        """;

    [Fact]
    public void Imports_folders_as_groups_and_maps_protocols()
    {
        var result = MRemoteNgImporter.Parse(Sample);

        Assert.Equal(4, result.Servers.Count);
        Assert.Equal(1, result.Skipped); // HTTP

        var web = result.Servers.Single(s => s.Name == "web-01");
        Assert.Equal(Protocol.Ssh, web.Protocol);
        Assert.Equal(2222, web.Port);
        Assert.Equal("Production", web.Group);
        Assert.Equal("Frontend", web.Notes);
        Assert.Equal("", web.Password); // an undecryptable password is dropped, not imported as garbage

        var ad = result.Servers.Single(s => s.Name == "ad-01");
        Assert.Equal("CORP\\admin", ad.Username);

        Assert.Equal("Production / Subfolder", result.Servers.Single(s => s.Name == "vnc-box").Group);
        Assert.Equal(3389, result.Servers.Single(s => s.Name == "no-port").Port);
    }

    [Fact]
    public void Rejects_other_files()
    {
        Assert.Throws<ArgumentException>(() => MRemoteNgImporter.Parse("<html/>"));
        Assert.Throws<ArgumentException>(() => MRemoteNgImporter.Parse("not xml"));
    }
}

public class KeyToolsTests
{
    [Fact]
    public void Detects_rsa_key_type()
    {
        using var rsa = RSA.Create(2048);
        var pem = rsa.ExportRSAPrivateKeyPem();

        Assert.Equal("RSA", KeyTools.Inspect(pem, ""));
    }

    [Fact]
    public void Rejects_non_keys_and_putty_keys()
    {
        Assert.Throws<SshConnectException>(() => KeyTools.Inspect("hallo", ""));
        Assert.Throws<SshConnectException>(() => KeyTools.Inspect("PuTTY-User-Key-File-3: ssh-ed25519 PRIVATE KEY", ""));
    }
}
