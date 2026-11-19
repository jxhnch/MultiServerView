using System.Net;
using System.Security.Cryptography;
using System.Text;
using MultiServerView.Core;
using MultiServerView.Core.Mcp;
using MultiServerView.Core.Sftp;
using MultiServerView.Core.Ssh;

namespace MultiServerView.Host;

/// <summary>
/// Local MCP server for AI tools (Claude Code, Claude Desktop, ...): listens on 127.0.0.1 only and needs the bearer token
/// shown in the settings. The tools work on the hosts of the unlocked vault.
/// </summary>
public sealed class McpService : IMcpBackend, IDisposable
{
    private readonly VaultStore _vault;
    private readonly Func<string, int, string, HostKeyTrust> _check;
    private readonly JumpResolver _jumps;
    private readonly McpHandler _handler;
    private readonly object _gate = new();
    private HttpListener? _listener;
    private CancellationTokenSource? _cts;
    private string _token = "";
    private int _port;

    public McpService(VaultStore vault, Func<string, int, string, HostKeyTrust> check, JumpResolver jumps)
    {
        _vault = vault;
        _check = check;
        _jumps = jumps;
        _handler = new McpHandler(this);
    }

    /// <summary>Last start error (e.g. port already in use), shown in the settings.</summary>
    public string? Error { get; private set; }

    public bool Running { get { lock (_gate) return _listener != null; } }

    public void Apply(AppSettings settings)
    {
        lock (_gate)
        {
            if (settings.McpEnabled && _listener != null && _port == settings.McpPort && _token == settings.McpToken) return;
            StopLocked();
            Error = null;
            if (!settings.McpEnabled || settings.McpToken.Length == 0) return;
            try
            {
                var listener = new HttpListener();
                listener.Prefixes.Add($"http://127.0.0.1:{settings.McpPort}/");
                try { listener.Prefixes.Add($"http://localhost:{settings.McpPort}/"); }
                catch (HttpListenerException) { /* 127.0.0.1 is already listening */ }
                listener.Start();
                var port = settings.McpPort;
                var token = settings.McpToken;
                var cts = new CancellationTokenSource();
                _listener = listener;
                _port = port;
                _token = token;
                _cts = cts;
                _ = Task.Run(() => LoopAsync(listener, port, token, cts.Token));
            }
            catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException)
            {
                Error = $"The MCP server could not start on port {settings.McpPort}: {ex.Message}";
            }
        }
    }

    public void Stop()
    {
        lock (_gate) StopLocked();
    }

    private void StopLocked()
    {
        _cts?.Cancel();
        try { _listener?.Close(); } catch (Exception) { /* already closed */ }
        _listener = null;
        _cts = null;
    }

    public void Dispose() => Stop();

    private async Task LoopAsync(HttpListener listener, int port, string token, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && listener.IsListening)
        {
            HttpListenerContext context;
            try { context = await listener.GetContextAsync(); }
            catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or InvalidOperationException) { return; }
            _ = Task.Run(() => HandleAsync(context, port, token, ct), ct);
        }
    }

    private async Task HandleAsync(HttpListenerContext context, int port, string token, CancellationToken ct)
    {
        var request = context.Request;
        var response = context.Response;
        try
        {
            // Not for browsers: no Origin header (blocks web pages talking to localhost), only our own host name.
            var hostOk = request.Headers["Host"] is { } h && (
                h.Equals($"127.0.0.1:{port}", StringComparison.OrdinalIgnoreCase) ||
                h.Equals($"localhost:{port}", StringComparison.OrdinalIgnoreCase) ||
                h.Equals($"[::1]:{port}", StringComparison.OrdinalIgnoreCase));
            if (!hostOk || request.Headers["Origin"] != null) { response.StatusCode = 403; return; }
            if (request.Url?.AbsolutePath.TrimEnd('/') != "/mcp") { response.StatusCode = 404; return; }
            if (!TokenOk(request.Headers["Authorization"], token)) { response.StatusCode = 401; response.AddHeader("WWW-Authenticate", "Bearer"); return; }
            if (request.HttpMethod != "POST") { response.AddHeader("Allow", "POST"); response.StatusCode = 405; return; }

            using var reader = new StreamReader(request.InputStream, Encoding.UTF8);
            var body = await reader.ReadToEndAsync(ct);
            if (body.Length > 5_000_000) { response.StatusCode = 413; return; }

            var answer = await _handler.HandleAsync(body, ct);
            if (answer == null) { response.StatusCode = 202; response.ContentLength64 = 0; return; }
            var bytes = Encoding.UTF8.GetBytes(answer);
            response.ContentType = "application/json; charset=utf-8";
            response.ContentLength64 = bytes.Length;
            await response.OutputStream.WriteAsync(bytes, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            try { response.StatusCode = 500; } catch (Exception) { /* connection already gone */ }
        }
        finally
        {
            try { response.Close(); } catch (Exception) { /* connection already gone */ }
        }
    }

    private static bool TokenOk(string? header, string token)
    {
        const string prefix = "Bearer ";
        if (header == null || !header.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;
        var given = Encoding.UTF8.GetBytes(header[prefix.Length..].Trim());
        return CryptographicOperations.FixedTimeEquals(given, Encoding.UTF8.GetBytes(token));
    }

    // ------------------------------------------------------------------------------------------------ Tools

    public Task<IReadOnlyList<McpHost>> ListHostsAsync(string query)
    {
        var hosts = Unlocked().List()
            .Where(s => query.Length == 0 || $"{s.Name} {s.Host} {s.Workspace} {s.Group} {string.Join(" ", s.Tags)}".Contains(query, StringComparison.OrdinalIgnoreCase))
            .Select(s => new McpHost(s.Id, s.Name, s.Host, s.Port, s.Protocol.ToString().ToLowerInvariant(), s.Workspace, s.Group, string.Join(",", s.Tags)))
            .ToList();
        return Task.FromResult<IReadOnlyList<McpHost>>(hosts);
    }

    public async Task<McpExecResult> RunCommandAsync(string host, string command, int timeoutSeconds, CancellationToken ct)
    {
        var (server, key) = Resolve(host);
        var r = await SshExec.RunAsync(server, key, _check, _jumps, command, TimeSpan.FromSeconds(timeoutSeconds), ct);
        return new McpExecResult(r.StdOut, r.StdErr, r.ExitCode, r.TimedOut);
    }

    public async Task<string> ReadFileAsync(string host, string path, CancellationToken ct)
    {
        var (sftp, home) = await OpenSftpAsync(host, ct);
        using (sftp) return (await sftp.ReadTextAsync(SftpSession.Normalize(home, path))).Content;
    }

    public async Task WriteFileAsync(string host, string path, string content, CancellationToken ct)
    {
        var (sftp, home) = await OpenSftpAsync(host, ct);
        using (sftp) await sftp.WriteTextAsync(SftpSession.Normalize(home, path), content, "\n", false, 0, 0, force: true);
    }

    public async Task<IReadOnlyList<McpFileEntry>> ListDirectoryAsync(string host, string path, CancellationToken ct)
    {
        var (sftp, _) = await OpenSftpAsync(host, ct);
        using (sftp)
        {
            var listing = await sftp.ListAsync(path);
            return listing.Entries.Select(e => new McpFileEntry(e.Name, e.IsDir, e.Size)).ToList();
        }
    }

    private async Task<(SftpSession Sftp, string Home)> OpenSftpAsync(string host, CancellationToken ct)
    {
        var (server, key) = Resolve(host);
        var sftp = new SftpSession(server, key, _check, _jumps);
        try
        {
            var home = await sftp.ConnectAsync(ct);
            return (sftp, home);
        }
        catch
        {
            sftp.Dispose();
            throw;
        }
    }

    private VaultStore Unlocked() => _vault.IsUnlocked ? _vault : throw new InvalidOperationException("The vault is locked. Unlock MultiServerView first.");

    private (ServerEntry Server, KeyEntry? Key) Resolve(string reference)
    {
        var all = Unlocked().List();
        var matches = all.Where(s => s.Id == reference).ToList();
        if (matches.Count == 0) matches = all.Where(s => s.Name.Equals(reference, StringComparison.OrdinalIgnoreCase)).ToList();
        if (matches.Count == 0) throw new KeyNotFoundException($"No host named \"{reference}\". Use list_hosts to see the available hosts.");
        if (matches.Count > 1) throw new InvalidOperationException($"\"{reference}\" matches {matches.Count} hosts. Use the id from list_hosts.");
        var server = _vault.FindResolved(matches[0].Id) ?? throw new KeyNotFoundException("Host not found.");
        if (server.Protocol != Protocol.Ssh) throw new InvalidOperationException($"\"{server.Name}\" is a {server.Protocol} host. Only SSH hosts are supported.");
        return (server, server.Auth == AuthMethod.Key ? _vault.FindKey(server.KeyId) : null);
    }
}
