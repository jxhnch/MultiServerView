using System.Text;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace MultiServerView.Core.Ssh;

/// <summary>
/// An interactive SSH shell (PTY). Data comes out as raw bytes and goes in as raw bytes -
/// the terminal itself (xterm.js) lives in the UI.
/// </summary>
public sealed class SshTerminalSession : IDisposable
{
    private readonly ServerEntry _server;
    private readonly KeyEntry? _key;
    private readonly Func<string, int, string, HostKeyTrust> _checkHostKey;
    private readonly JumpResolver? _jumps;
    private JumpTunnel? _tunnel;
    private SshClient? _client;
    private ShellStream? _shell;
    private int _closedRaised;

    /// <summary>New output from the server.</summary>
    public event Action<byte[]>? Data;

    /// <summary>The session ended (reason, or null on a normal end).</summary>
    public event Action<string?>? Closed;

    /// <summary>Reported once on first contact with a host (info for the user).</summary>
    public event Action<string>? Info;

    public SshTerminalSession(ServerEntry server, KeyEntry? key, Func<string, int, string, HostKeyTrust> checkHostKey, JumpResolver? jumps = null)
    {
        _jumps = jumps;
        _server = server;
        _key = key;
        _checkHostKey = checkHostKey;
    }

    public async Task ConnectAsync(uint columns, uint rows, CancellationToken ct = default)
    {
        var tunnel = await JumpTunnel.OpenAsync(_server, _jumps, _checkHostKey, ct);
        var info = SshConnector.BuildConnectionInfo(_server, _key, tunnel?.Host, tunnel?.Port);
        var client = new SshClient(info) { KeepAliveInterval = SshConnector.KeepAlive };
        var hostKeyError = SshConnector.HookHostKey(client, _server, _checkHostKey, m => Info?.Invoke(m));

        try
        {
            await Task.Run(() => client.Connect(), ct);
        }
        catch (Exception ex)
        {
            client.Dispose();
            tunnel?.Dispose();
            throw new SshConnectException(hostKeyError() ?? SshConnector.Describe(ex), ex);
        }
        _tunnel = tunnel;

        _client = client;
        _shell = client.CreateShellStream("xterm-256color", columns, rows, 0, 0, 16384);
        _shell.DataReceived += (_, e) => Data?.Invoke(e.Data);
        _shell.Closed += (_, _) => RaiseClosed(null);
        _shell.ErrorOccurred += (_, e) => RaiseClosed(e.Exception.Message);
        client.ErrorOccurred += (_, e) => RaiseClosed(e.Exception.Message);
    }

    public void Write(byte[] data)
    {
        var shell = _shell;
        if (shell == null) return;
        try
        {
            shell.Write(data, 0, data.Length);
            shell.Flush();
        }
        catch (Exception ex) when (ex is ObjectDisposedException or SshException or IOException)
        {
            RaiseClosed(ex.Message);
        }
    }

    public void Resize(uint columns, uint rows)
    {
        try
        {
            _shell?.ChangeWindowSize(columns, rows, 0, 0);
        }
        catch (Exception ex) when (ex is ObjectDisposedException or SshException or InvalidOperationException)
        {
            // Resizing the window is not critical
        }
    }

    public void Dispose()
    {
        try { _shell?.Dispose(); } catch { /* does not matter during cleanup */ }
        try { _client?.Dispose(); } catch { /* does not matter during cleanup */ }
        try { _tunnel?.Dispose(); } catch { /* does not matter during cleanup */ }
        _shell = null;
        _client = null;
        _tunnel = null;
    }

    private void RaiseClosed(string? reason)
    {
        if (Interlocked.Exchange(ref _closedRaised, 1) == 0) Closed?.Invoke(reason);
    }

    /// <summary>Runs a command on a second channel of the running connection (e.g. to detect the operating system).</summary>
    public async Task<string?> RunCommandAsync(string command, TimeSpan timeout)
    {
        var client = _client;
        if (client == null) return null;
        try
        {
            return await Task.Run(() =>
            {
                using var cmd = client.CreateCommand(command);
                cmd.CommandTimeout = timeout;
                var stdout = cmd.Execute();
                // Also read the error channel: Windows servers report there that "cat" is unknown (that is how we recognize them).
                return string.IsNullOrWhiteSpace(cmd.Error) ? stdout : stdout + "\n" + cmd.Error;
            });
        }
        catch (Exception ex) when (ex is SshException or ObjectDisposedException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>Detects the operating system of the server ("debian", "ubuntu", "linux", "macos", ...) or "" on failure.</summary>
    public async Task<string> DetectOsAsync()
    {
        var output = await RunCommandAsync(OsDetector.ProbeCommand, TimeSpan.FromSeconds(5));
        return output == null ? "" : OsDetector.Parse(output);
    }

    /// <summary>Reads a private key (OpenSSH / PEM). Throws <see cref="SshConnectException"/> on errors.</summary>
    public static PrivateKeyFile LoadKey(KeyEntry key) => SshConnector.LoadKey(key);
}
