using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using MultiServerView.Core;
using MultiServerView.Core.Web;

namespace MultiServerView.Host;

/// <summary>
/// The "integrated browser": opens the web page of a device (firewall, router, Proxmox, printer ...) inside a tab.
/// Such pages often use a self-signed certificate, forbid being shown in a frame and need a login, so a small local proxy
/// (127.0.0.1 only, one per open tab) talks to the real server:
///  - the certificate is checked like an SSH host key: valid for the system, or the same as the one saved earlier (Known hosts);
///    a changed certificate is refused,
///  - frame headers are removed and addresses/cookies are rewritten so the page works under the local address,
///  - the saved user name and password are filled into the login page (or sent as HTTP basic authentication),
///  - web sockets (e.g. the Proxmox console) are passed on.
/// Access to the proxy needs a secret token that only this app knows.
/// </summary>
public sealed class WebProxyHub : IDisposable
{
    private readonly ConcurrentDictionary<string, WebProxySession> _sessions = new();
    private readonly Func<string, int, string, HostKeyTrust> _checkCertificate;

    public WebProxyHub(Func<string, int, string, HostKeyTrust> checkCertificate) => _checkCertificate = checkCertificate;

    /// <summary>
    /// Starts the proxy of a tab. <paramref name="connectHost"/>/<paramref name="connectPort"/> are where to connect to (they differ from
    /// the target when a jump host tunnel is used). Returns the address for the frame and the address for the system browser.
    /// </summary>
    public (string FrameUrl, string ExternalUrl) Open(string sessionId, WebTarget target, string connectHost, int connectPort, string username, string password)
    {
        Close(sessionId);
        var session = new WebProxySession(target, connectHost, connectPort, username, password, _checkCertificate);
        _sessions[sessionId] = session;
        return (session.FrameUrl(target.StartPath), session.EntryUrl(target.StartPath));
    }

    public void Close(string sessionId)
    {
        if (_sessions.TryRemove(sessionId, out var old)) old.Dispose();
    }

    public void Dispose()
    {
        foreach (var key in _sessions.Keys) Close(key);
    }
}

internal sealed class WebProxySession : IDisposable
{
    private const int MaxRewriteBytes = 12 * 1024 * 1024;
    private const int MaxReplayBytes = 8 * 1024 * 1024;
    private static readonly HashSet<string> SkipRequest = new(StringComparer.OrdinalIgnoreCase)
    {
        "Host", "Connection", "Keep-Alive", "Proxy-Connection", "Proxy-Authorization", "TE", "Trailer", "Transfer-Encoding", "Upgrade",
        "Accept-Encoding", "Cookie", "Content-Length", "Origin", "Referer", "Expect",
        "Sec-WebSocket-Key", "Sec-WebSocket-Version", "Sec-WebSocket-Extensions", "Sec-WebSocket-Protocol"
    };
    private static readonly HashSet<string> SkipResponse = new(StringComparer.OrdinalIgnoreCase)
    {
        "Connection", "Keep-Alive", "Transfer-Encoding", "Content-Length", "Content-Encoding", "Content-Type", "Set-Cookie", "Location", "Upgrade",
        "X-Frame-Options", "Content-Security-Policy", "Content-Security-Policy-Report-Only", "Strict-Transport-Security", "Public-Key-Pins",
        "Cross-Origin-Opener-Policy", "Cross-Origin-Embedder-Policy", "Cross-Origin-Resource-Policy", "Alt-Svc", "Report-To", "NEL"
    };

    private readonly WebTarget _target;
    private readonly string _connectHost;
    private readonly int _connectPort;
    private readonly string _username, _password;
    private readonly Func<string, int, string, HostKeyTrust> _checkCertificate;
    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly HttpClient _http;
    private readonly CookieContainer _jar = new();
    private readonly Uri _realUri;
    private readonly string _token = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
    private readonly int _port;
    private readonly string _proxyOrigin;
    private readonly string _cookiePrefix;
    /// <summary>Scripts added to every page: the cookie handling, then the automatic login.</summary>
    private readonly string _pageScripts;
    private volatile bool _basicActive;
    private volatile string _certificateProblem = "";

    public WebProxySession(WebTarget target, string connectHost, int connectPort, string username, string password, Func<string, int, string, HostKeyTrust> checkCertificate)
    {
        _target = target;
        _connectHost = connectHost;
        _connectPort = connectPort;
        _username = username;
        _password = password;
        _checkCertificate = checkCertificate;
        _realUri = new Uri(target.Origin + "/");
        _cookiePrefix = WebRewriter.CookiePrefix(target.Origin);
        _pageScripts = WebRewriter.BuildCookieScript(_cookiePrefix) + WebRewriter.BuildAutoLoginScript(username, password);

        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            UseProxy = false,
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            ConnectTimeout = TimeSpan.FromSeconds(15)
        };
        handler.SslOptions.TargetHost = target.Host;
        handler.SslOptions.RemoteCertificateValidationCallback = (_, certificate, _, errors) => ValidateCertificate(certificate, errors);
        _http = new HttpClient(handler, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };

        for (var attempt = 0; ; attempt++)
        {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            _port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            try
            {
                _listener.Prefixes.Add($"http://127.0.0.1:{_port}/");
                _listener.Start();
                break;
            }
            catch (HttpListenerException) when (attempt < 5)
            {
                _listener.Prefixes.Clear();
            }
        }
        _proxyOrigin = $"http://127.0.0.1:{_port}";
        _ = Task.Run(AcceptLoop);
    }

    /// <summary>Address for the frame inside the app (the cookie is set by the entry address).</summary>
    public string FrameUrl(string path) => EntryUrl(path);

    /// <summary>First address to open: it checks the token, sets the access cookie and goes on to the real page.</summary>
    public string EntryUrl(string path) => $"{_proxyOrigin}/__msv/{_token}/?to={Uri.EscapeDataString(path)}";

    public void Dispose()
    {
        try { _cts.Cancel(); } catch (ObjectDisposedException) { /* already disposed */ }
        try { _listener.Close(); } catch (Exception) { /* already closed */ }
        _http.Dispose();
        _cts.Dispose();
    }

    // ------------------------------------------------------------------------------------------------ certificate

    private string AccessCookieName => "msvauth" + _port;

    private bool ValidateCertificate(X509Certificate? certificate, System.Net.Security.SslPolicyErrors errors)
    {
        if (certificate == null) return false;
        if (errors == System.Net.Security.SslPolicyErrors.None) return true;
        var fingerprint = "SHA256:" + Convert.ToBase64String(SHA256.HashData(certificate.GetRawCertData())).TrimEnd('=');
        var trust = _checkCertificate("web/" + _target.Host, _target.Port, fingerprint);
        if (trust == HostKeyTrust.Mismatch)
        {
            _certificateProblem = $"The certificate of {_target.Host} is different from the one saved earlier. This can mean that someone sits between you and the device. If you changed the certificate on purpose, remove the entry \"web/{_target.Host}:{_target.Port}\" on the Known hosts page and open the page again.";
            return false;
        }
        return true;
    }

    // ------------------------------------------------------------------------------------------------ requests

    private async Task AcceptLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try { ctx = await _listener.GetContextAsync(); }
            catch (Exception) { return; }
            _ = Task.Run(() => HandleAsync(ctx));
        }
    }

    private static bool SameSecret(string a, string b)
        => a.Length == b.Length && CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(a), Encoding.ASCII.GetBytes(b));

    private async Task HandleAsync(HttpListenerContext ctx)
    {
        try
        {
            var request = ctx.Request;
            // Only requests for the exact local address (no DNS rebinding) and from this page itself (no other web site).
            if (request.Headers["Host"] != $"127.0.0.1:{_port}") { Refuse(ctx, "HOST", "the request did not use the local address of this tab"); return; }
            var origin = request.Headers["Origin"];
            if (!string.IsNullOrEmpty(origin) && origin != _proxyOrigin && origin != "null") { Refuse(ctx, "ORIGIN", "the request came from another web page"); return; }

            var path = request.RawUrl ?? "/";
            var entry = $"/__msv/{_token}/";
            if (path.StartsWith("/__msv/", StringComparison.Ordinal))
            {
                if (!path.StartsWith(entry, StringComparison.Ordinal)) { Refuse(ctx, "TOKEN", "the address of this tab is out of date. Reload the tab"); return; }
                var to = "/";
                var q = path.IndexOf("?to=", StringComparison.Ordinal);
                if (q >= 0) to = Uri.UnescapeDataString(path[(q + 4)..]);
                if (to.Length == 0 || to[0] != '/' || to.StartsWith("//", StringComparison.Ordinal)) to = "/";
                // Windows shows the page inside a window of another origin: the cookie has to be allowed in such a frame.
                var attributes = OperatingSystem.IsWindows() ? "SameSite=None; Secure" : "SameSite=Strict";
                ctx.Response.AppendHeader("Set-Cookie", $"{AccessCookieName}={_token}; Path=/; HttpOnly; {attributes}");
                ctx.Response.StatusCode = 302;
                ctx.Response.RedirectLocation = to;
                ctx.Response.Close();
                return;
            }

            var cookies = ParseCookies(request.Headers["Cookie"]);
            if (!cookies.TryGetValue(AccessCookieName, out var presented) || !SameSecret(presented, _token))
            {
                Refuse(ctx, "COOKIE", "the built-in browser of this system did not send back the access cookie of the tab. "
                    + "Use the globe button at the top right to open the page in your normal browser instead");
                return;
            }

            if (request.IsWebSocketRequest) await ProxyWebSocketAsync(ctx, cookies);
            else await ProxyHttpAsync(ctx, cookies);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            try { Fail(ctx, 502, "Could not reach the page: " + (string.IsNullOrEmpty(_certificateProblem) ? Describe(ex) : _certificateProblem)); }
            catch (Exception) { /* connection already gone */ }
        }
    }

    private static string Describe(Exception ex)
    {
        for (var e = ex; e != null; e = e.InnerException)
            if (e is System.Security.Authentication.AuthenticationException or SocketException or IOException) return e.Message;
        return ex.InnerException?.Message ?? ex.Message;
    }

    /// <summary>
    /// The app's own "no", clearly marked as such: the device was never asked. The code names the check that failed, so a
    /// report ("it said access denied") can be traced.
    /// </summary>
    private static void Refuse(HttpListenerContext ctx, string code, string reason)
        => Fail(ctx, 403, $"MultiServerView blocked this request itself, the device was not contacted: {reason}. (Code MSV-{code})");

    private static void Fail(HttpListenerContext ctx, int status, string message)
    {
        var html = $"<!doctype html><meta charset=utf-8><title>{status}</title><body style=\"font:15px system-ui;margin:3rem;color:#444\"><h2>{status}</h2><p>{WebUtility.HtmlEncode(message)}</p>";
        var bytes = Encoding.UTF8.GetBytes(html);
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = "text/html; charset=utf-8";
        ctx.Response.ContentLength64 = bytes.Length;
        ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
        ctx.Response.Close();
    }

    private static Dictionary<string, string> ParseCookies(string? header)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrEmpty(header)) return map;
        foreach (var part in header.Split(';'))
        {
            var eq = part.IndexOf('=');
            if (eq <= 0) continue;
            map[part[..eq].Trim()] = part[(eq + 1)..].Trim();
        }
        return map;
    }

    /// <summary>Cookies for the real server: what the server gave us (authoritative) plus what the page itself set.</summary>
    private string UpstreamCookies(Dictionary<string, string> fromBrowser)
    {
        var merged = ParseCookies(_jar.GetCookieHeader(_realUri));
        foreach (var kv in WebRewriter.OwnCookies(fromBrowser, _cookiePrefix))
            if (!merged.ContainsKey(kv.Key)) merged[kv.Key] = kv.Value;
        return string.Join("; ", merged.Select(kv => kv.Key + "=" + kv.Value));
    }

    private string BasicHeader() => "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(_username + ":" + _password));

    private Uri UpstreamUri(string pathAndQuery, bool webSocket = false)
    {
        var scheme = webSocket ? (_target.IsHttps ? "wss" : "ws") : _target.Scheme;
        var host = _connectHost.Contains(':') && !_connectHost.StartsWith('[') ? $"[{_connectHost}]" : _connectHost;
        return new Uri($"{scheme}://{host}:{_connectPort}{pathAndQuery}");
    }

    private string Unproxy(string value) => value.Replace(_proxyOrigin, _target.Origin, StringComparison.OrdinalIgnoreCase);

    private HttpRequestMessage BuildRequest(HttpListenerRequest request, Dictionary<string, string> cookies, byte[]? body)
    {
        var message = new HttpRequestMessage(new HttpMethod(request.HttpMethod), UpstreamUri(request.RawUrl ?? "/"));
        message.Version = HttpVersion.Version11;
        message.Headers.Host = _target.Origin[(_target.Scheme.Length + 3)..];
        HttpContent? content = null;
        if (body != null) content = new ByteArrayContent(body);
        else if (request.HasEntityBody) content = new StreamContent(request.InputStream);
        message.Content = content;
        foreach (var name in request.Headers.AllKeys)
        {
            if (name == null || SkipRequest.Contains(name)) continue;
            var value = request.Headers[name] ?? "";
            if (!message.Headers.TryAddWithoutValidation(name, value)) content?.Headers.TryAddWithoutValidation(name, value);
        }
        if (content != null && request.ContentType != null) content.Headers.TryAddWithoutValidation("Content-Type", request.ContentType);
        var origin = request.Headers["Origin"];
        if (!string.IsNullOrEmpty(origin) && origin != "null") message.Headers.TryAddWithoutValidation("Origin", _target.Origin);
        var referer = request.Headers["Referer"];
        if (!string.IsNullOrEmpty(referer)) message.Headers.TryAddWithoutValidation("Referer", Unproxy(referer));
        var cookie = UpstreamCookies(cookies);
        if (cookie.Length > 0) message.Headers.TryAddWithoutValidation("Cookie", cookie);
        if (_basicActive && request.Headers["Authorization"] == null) message.Headers.TryAddWithoutValidation("Authorization", BasicHeader());
        return message;
    }

    private async Task ProxyHttpAsync(HttpListenerContext ctx, Dictionary<string, string> cookies)
    {
        var request = ctx.Request;
        // A small body is kept in memory so the request can be repeated once with basic authentication.
        byte[]? body = null;
        if (request.HasEntityBody && request.ContentLength64 is >= 0 and <= MaxReplayBytes)
        {
            using var buffer = new MemoryStream();
            await request.InputStream.CopyToAsync(buffer, _cts.Token);
            body = buffer.ToArray();
        }

        var message = BuildRequest(request, cookies, body);
        var response = await _http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, _cts.Token);
        if (response.StatusCode == HttpStatusCode.Unauthorized && !_basicActive && _password.Length > 0
            && response.Headers.WwwAuthenticate.Any(h => h.Scheme.Equals("Basic", StringComparison.OrdinalIgnoreCase))
            && (body != null || !request.HasEntityBody))
        {
            response.Dispose();
            _basicActive = true;
            response = await _http.SendAsync(BuildRequest(request, cookies, body), HttpCompletionOption.ResponseHeadersRead, _cts.Token);
        }

        using (response)
        {
            var output = ctx.Response;
            output.StatusCode = (int)response.StatusCode;

            if (response.Headers.TryGetValues("Set-Cookie", out var setCookies))
            {
                foreach (var raw in setCookies)
                {
                    try { _jar.SetCookies(_realUri, raw); } catch (CookieException) { /* a broken cookie of the device is ignored */ }
                    output.AppendHeader("Set-Cookie", WebRewriter.RewriteSetCookie(raw, _cookiePrefix));
                }
            }
            if (response.Headers.Location != null)
                output.RedirectLocation = WebRewriter.RewriteLocation(response.Headers.Location.OriginalString, _target.Origin, _proxyOrigin);
            foreach (var header in response.Headers.Concat(response.Content.Headers))
            {
                if (SkipResponse.Contains(header.Key)) continue;
                foreach (var value in header.Value)
                {
                    try { output.AppendHeader(header.Key, value); } catch (ArgumentException) { /* restricted header */ }
                }
            }

            var contentType = response.Content.Headers.ContentType?.ToString();
            var noBody = request.HttpMethod == "HEAD" || output.StatusCode is 204 or 304 or 205;
            if (contentType != null) output.ContentType = contentType;
            if (noBody)
            {
                output.Close();
                return;
            }

            var length = response.Content.Headers.ContentLength;
            if (WebRewriter.IsRewritable(contentType) && (length == null || length <= MaxRewriteBytes))
            {
                var bytes = await response.Content.ReadAsByteArrayAsync(_cts.Token);
                var charset = response.Content.Headers.ContentType?.CharSet?.Trim('"');
                Encoding encoding;
                try { encoding = string.IsNullOrEmpty(charset) ? Encoding.Latin1 : Encoding.GetEncoding(charset); }
                catch (ArgumentException) { encoding = Encoding.Latin1; }
                var text = WebRewriter.ReplaceOrigin(encoding.GetString(bytes), _target.Origin, _proxyOrigin);
                if (WebRewriter.IsHtml(contentType)) text = WebRewriter.InjectIntoHtml(text, _pageScripts);
                var result = encoding.GetBytes(text);
                output.ContentLength64 = result.Length;
                await output.OutputStream.WriteAsync(result, _cts.Token);
                output.Close();
                return;
            }

            if (length != null) output.ContentLength64 = length.Value;
            else output.SendChunked = true;
            await using var stream = await response.Content.ReadAsStreamAsync(_cts.Token);
            await stream.CopyToAsync(output.OutputStream, _cts.Token);
            output.Close();
        }
    }

    // ------------------------------------------------------------------------------------------------ web sockets

    private async Task ProxyWebSocketAsync(HttpListenerContext ctx, Dictionary<string, string> cookies)
    {
        var request = ctx.Request;
        using var upstream = new ClientWebSocket();
        upstream.Options.RemoteCertificateValidationCallback = (_, certificate, _, errors) => ValidateCertificate(certificate, errors);
        var wanted = request.Headers["Sec-WebSocket-Protocol"];
        if (!string.IsNullOrEmpty(wanted))
            foreach (var sub in wanted.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) upstream.Options.AddSubProtocol(sub);
        upstream.Options.SetRequestHeader("Origin", _target.Origin);
        var cookie = UpstreamCookies(cookies);
        if (cookie.Length > 0) upstream.Options.SetRequestHeader("Cookie", cookie);
        if (_basicActive && request.Headers["Authorization"] == null) upstream.Options.SetRequestHeader("Authorization", BasicHeader());
        var authorization = request.Headers["Authorization"];
        if (authorization != null) upstream.Options.SetRequestHeader("Authorization", authorization);

        using var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        connectTimeout.CancelAfter(TimeSpan.FromSeconds(15));
        try { await upstream.ConnectAsync(UpstreamUri(request.RawUrl ?? "/", webSocket: true), connectTimeout.Token); }
        catch (Exception ex) when (ex is WebSocketException or HttpRequestException or OperationCanceledException)
        {
            Fail(ctx, 502, string.IsNullOrEmpty(_certificateProblem) ? "The device refused the connection." : _certificateProblem);
            return;
        }

        var accepted = await ctx.AcceptWebSocketAsync(upstream.SubProtocol);
        var browser = accepted.WebSocket;
        using var both = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        var up = Pump(browser, upstream, both);
        var down = Pump(upstream, browser, both);
        await Task.WhenAny(up, down);
        both.Cancel();
        try { await Task.WhenAll(up, down); } catch (Exception) { /* closed */ }
    }

    private static async Task Pump(WebSocket from, WebSocket to, CancellationTokenSource stop)
    {
        var buffer = new byte[64 * 1024];
        try
        {
            while (!stop.IsCancellationRequested && from.State == WebSocketState.Open)
            {
                var received = await from.ReceiveAsync(buffer, stop.Token);
                if (received.MessageType == WebSocketMessageType.Close)
                {
                    if (to.State == WebSocketState.Open) await to.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None);
                    break;
                }
                if (to.State != WebSocketState.Open) break;
                await to.SendAsync(new ArraySegment<byte>(buffer, 0, received.Count), received.MessageType, received.EndOfMessage, stop.Token);
            }
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or ObjectDisposedException or IOException)
        {
            // one side is gone
        }
        finally
        {
            stop.Cancel();
        }
    }
}
