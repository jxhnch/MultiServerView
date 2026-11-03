using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;

namespace MultiServerView.Core.Vnc;

/// <summary>
/// Lokale Brücke für noVNC: nimmt eine WebSocket-Verbindung von der Oberfläche an
/// und reicht die Bytes 1:1 an einen TCP-Server (VNC) weiter und zurück.
/// Lauscht nur auf 127.0.0.1; jeder Eintrag ist per Einmal-Token geschützt, damit keine
/// fremde Webseite oder anderer Prozess die Brücke missbrauchen kann.
/// </summary>
public sealed class WebSocketTcpProxy : IDisposable
{
    private const string WsGuid = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";

    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentDictionary<string, (string host, int port, DateTime expires)> _tokens = new();
    private Task? _acceptLoop;

    public WebSocketTcpProxy()
    {
        _listener = new TcpListener(IPAddress.Loopback, 0);
    }

    public int Port { get; private set; }

    public void Start()
    {
        if (_acceptLoop != null) return;
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _acceptLoop = Task.Run(AcceptLoop);
    }

    /// <summary>Meldet ein Ziel an und gibt die WebSocket-URL zurück (einmal benutzbar, 30 s gültig).</summary>
    public string Register(string host, int port)
    {
        Start();
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
        _tokens[token] = (host, port, DateTime.UtcNow.AddSeconds(30));
        foreach (var (key, value) in _tokens)
            if (value.expires < DateTime.UtcNow) _tokens.TryRemove(key, out _);
        return $"ws://127.0.0.1:{Port}/{token}";
    }

    private async Task AcceptLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_cts.Token);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                return;
            }
            _ = Task.Run(() => HandleAsync(client));
        }
    }

    private async Task HandleAsync(TcpClient client)
    {
        using var _ = client;
        client.NoDelay = true;
        try
        {
            var stream = client.GetStream();
            var handshake = await ReadHandshakeAsync(stream, _cts.Token);
            if (handshake == null) return;

            var (token, wsKey, wantsBinary) = handshake.Value;
            if (!_tokens.TryRemove(token, out var target) || target.expires < DateTime.UtcNow)
            {
                await WriteAsync(stream, "HTTP/1.1 403 Forbidden\r\nConnection: close\r\nContent-Length: 0\r\n\r\n");
                return;
            }

            using var remote = new TcpClient { NoDelay = true };
            try
            {
                await remote.ConnectAsync(target.host, target.port, _cts.Token);
            }
            catch (Exception)
            {
                await WriteAsync(stream, "HTTP/1.1 502 Bad Gateway\r\nConnection: close\r\nContent-Length: 0\r\n\r\n");
                return;
            }

            var accept = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(wsKey + WsGuid)));
            var response = "HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\n" +
                           $"Sec-WebSocket-Accept: {accept}\r\n" +
                           (wantsBinary ? "Sec-WebSocket-Protocol: binary\r\n" : "") + "\r\n";
            await WriteAsync(stream, response);

            using var ws = WebSocket.CreateFromStream(stream, new WebSocketCreationOptions
            {
                IsServer = true,
                SubProtocol = wantsBinary ? "binary" : null,
                KeepAliveInterval = TimeSpan.FromSeconds(30)
            });

            await Pump(ws, remote.GetStream(), _cts.Token);
        }
        catch (Exception ex) when (ex is IOException or SocketException or WebSocketException or OperationCanceledException or ObjectDisposedException)
        {
            // Verbindung weg - nichts zu tun
        }
    }

    private static async Task Pump(WebSocket ws, NetworkStream tcp, CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);

        var wsToTcp = Task.Run(async () =>
        {
            var buffer = new byte[64 * 1024];
            while (!linked.IsCancellationRequested)
            {
                var r = await ws.ReceiveAsync(buffer, linked.Token);
                if (r.MessageType == WebSocketMessageType.Close) break;
                if (r.Count > 0) await tcp.WriteAsync(buffer.AsMemory(0, r.Count), linked.Token);
            }
        }, linked.Token);

        var tcpToWs = Task.Run(async () =>
        {
            var buffer = new byte[64 * 1024];
            while (!linked.IsCancellationRequested)
            {
                var n = await tcp.ReadAsync(buffer, linked.Token);
                if (n == 0) break;
                await ws.SendAsync(buffer.AsMemory(0, n), WebSocketMessageType.Binary, true, linked.Token);
            }
        }, linked.Token);

        try
        {
            await Task.WhenAny(wsToTcp, tcpToWs);
        }
        finally
        {
            linked.Cancel();
            try { await Task.WhenAll(wsToTcp, tcpToWs); } catch { /* beide Seiten sind eh am Ende */ }
            try
            {
                if (ws.State == WebSocketState.Open)
                    await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None);
            }
            catch { /* egal */ }
        }
    }

    /// <summary>Liest den HTTP-Upgrade-Request. Rückgabe: Token (aus dem Pfad), Sec-WebSocket-Key, Subprotokoll "binary" gewünscht.</summary>
    private static async Task<(string token, string key, bool binary)?> ReadHandshakeAsync(NetworkStream stream, CancellationToken ct)
    {
        var sb = new StringBuilder();
        var buf = new byte[1];
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));

        while (sb.Length < 8192)
        {
            var n = await stream.ReadAsync(buf, timeout.Token);
            if (n == 0) return null;
            sb.Append((char)buf[0]);
            if (sb.Length >= 4 && sb[^4] == '\r' && sb[^3] == '\n' && sb[^2] == '\r' && sb[^1] == '\n') break;
        }

        var lines = sb.ToString().Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length == 0) return null;

        var request = lines[0].Split(' ');
        if (request.Length < 2 || request[0] != "GET") return null;
        var token = request[1].Trim('/').Split('?')[0];

        string? key = null;
        var binary = false;
        foreach (var line in lines.Skip(1))
        {
            var idx = line.IndexOf(':');
            if (idx <= 0) continue;
            var name = line[..idx].Trim();
            var value = line[(idx + 1)..].Trim();
            if (name.Equals("Sec-WebSocket-Key", StringComparison.OrdinalIgnoreCase)) key = value;
            else if (name.Equals("Sec-WebSocket-Protocol", StringComparison.OrdinalIgnoreCase))
                binary = value.Split(',').Any(p => p.Trim().Equals("binary", StringComparison.OrdinalIgnoreCase));
        }

        return key == null ? null : (token, key, binary);
    }

    private static async Task WriteAsync(NetworkStream stream, string text)
    {
        var bytes = Encoding.ASCII.GetBytes(text);
        await stream.WriteAsync(bytes);
        await stream.FlushAsync();
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _listener.Stop(); } catch { /* egal */ }
        _cts.Dispose();
    }
}
