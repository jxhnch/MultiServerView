using System.Net;
using System.Net.Sockets;
using MultiServerView.Core;
using MultiServerView.Host;

namespace MultiServerView.Linux;

/// <summary>Serves the built UI from a folder on 127.0.0.1 (random port). Nothing but static files.</summary>
internal sealed class StaticFileServer : IDisposable
{
    private static readonly Dictionary<string, string> Types = new(StringComparer.OrdinalIgnoreCase)
    {
        [".html"] = "text/html; charset=utf-8", [".js"] = "text/javascript; charset=utf-8", [".mjs"] = "text/javascript; charset=utf-8",
        [".css"] = "text/css; charset=utf-8", [".json"] = "application/json", [".svg"] = "image/svg+xml", [".png"] = "image/png",
        [".ico"] = "image/x-icon", [".woff"] = "font/woff", [".woff2"] = "font/woff2", [".ttf"] = "font/ttf", [".map"] = "application/json",
        [".wasm"] = "application/wasm", [".txt"] = "text/plain; charset=utf-8"
    };

    private readonly string _root;
    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _cts = new();

    public string Url { get; }

    public StaticFileServer(string root)
    {
        _root = Path.GetFullPath(root);
        var port = FreePort();
        Url = $"http://127.0.0.1:{port}/";
        _listener.Prefixes.Add(Url);
        _listener.Start();
        _ = Task.Run(LoopAsync);
    }

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    private async Task LoopAsync()
    {
        while (!_cts.IsCancellationRequested && _listener.IsListening)
        {
            HttpListenerContext ctx;
            try { ctx = await _listener.GetContextAsync(); }
            catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or InvalidOperationException) { return; }
            _ = Task.Run(() => Serve(ctx));
        }
    }

    private void Serve(HttpListenerContext ctx)
    {
        try
        {
            var path = Uri.UnescapeDataString(ctx.Request.Url!.AbsolutePath).TrimStart('/');
            if (path.Length == 0) path = "index.html";
            var full = Path.GetFullPath(Path.Combine(_root, path));
            // never leave the UI folder
            if (!full.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.Ordinal) || !File.Exists(full))
            {
                ctx.Response.StatusCode = 404;
                return;
            }
            var bytes = File.ReadAllBytes(full);
            ctx.Response.ContentType = Types.TryGetValue(Path.GetExtension(full), out var t) ? t : "application/octet-stream";
            ctx.Response.ContentLength64 = bytes.Length;
            ctx.Response.OutputStream.Write(bytes);
        }
        catch (Exception)
        {
            try { ctx.Response.StatusCode = 500; } catch (Exception) { /* connection gone */ }
        }
        finally
        {
            try { ctx.Response.Close(); } catch (Exception) { /* connection gone */ }
        }
    }

    public void Dispose()
    {
        try { _cts.Cancel(); } catch (ObjectDisposedException) { /* already disposed */ }
        try { _listener.Close(); } catch (Exception) { /* already closed */ }
        _cts.Dispose();
    }
}

/// <summary>Embedded RDP only exists in the Windows app. On Linux the RDP tab offers the external client instead.</summary>
internal sealed class NoEmbeddedRdp : IRdpController
{
    public void Open(string sessionId, ServerEntry server, System.Drawing.Rectangle bounds) =>
        throw new InvalidOperationException("RDP inside the app is only available in the Windows version. Use \"Open in RDP client\" (needs xfreerdp or Remmina).");

    public void Layout(string sessionId, System.Drawing.Rectangle bounds, bool visible) { }
    public void Close(string sessionId) { }
    public void SetSuspended(bool suspended) { }
}
