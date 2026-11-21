using System.Text;
using MultiServerView.Core.Ssh;

namespace MultiServerView.Core.Windows;

/// <summary>
/// A PowerShell prompt on a Windows server through WinRM (no SSH needed). It is a "line" terminal: you type a line, press Enter,
/// the command runs on the server and its output is shown. The current folder is kept between commands; variables are not
/// (every line runs in a new PowerShell). Programs that need the keyboard while they run (editors, "more") do not work.
/// </summary>
public sealed class WinRmTerminalSession : ITerminalSession
{
    private const string CwdMarker = "__MSVCWD__";

    private readonly WinRmClient _client;
    private readonly string _host;
    private readonly StringBuilder _line = new();
    private readonly SemaphoreSlim _running = new(1, 1);
    private readonly List<string> _history = new();
    private string _cwd = "";
    private int _closedRaised;
    private bool _busy;
    private bool _disposed;

    public event Action<byte[]>? Data;
    public event Action<string?>? Closed;
    public event Action<string>? Info;

    public WinRmTerminalSession(string host, int port, string username, string password)
    {
        _host = host;
        _client = new WinRmClient(host, port, username, password);
    }

    /// <summary>For tests.</summary>
    public WinRmTerminalSession(WinRmClient client, string host)
    {
        _client = client;
        _host = host;
    }

    public async Task ConnectAsync(uint columns, uint rows, CancellationToken ct = default)
    {
        // The first command proves that host, port and sign-in are right and tells the folder to start in.
        var first = await _client.RunPowerShellAsync("Write-Output ($env:COMPUTERNAME + '|' + (Get-Location).Path)", TimeSpan.FromSeconds(60), ct);
        var parts = first.StdOut.Trim().Split('|', 2);
        if (first.ExitCode != 0 || parts.Length != 2)
            throw new WinRmException("The server answered, but PowerShell did not run: " + (first.StdErr.Length > 0 ? first.StdErr.Trim() : first.StdOut.Trim()));
        _cwd = parts[1].Trim();
        Info?.Invoke($"Connected to {parts[0].Trim()} through WinRM. Every line runs in its own PowerShell; the folder is kept.");
        Prompt();
    }

    public void Write(byte[] data)
    {
        if (_disposed) return;
        var text = Encoding.UTF8.GetString(data);
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '\u001b')
            {
                // arrow keys and other escape sequences: not supported in a line terminal, skip them
                if (i + 1 < text.Length && text[i + 1] == '[')
                {
                    i += 2;
                    while (i < text.Length && !char.IsLetter(text[i]) && text[i] != '~') i++;
                }
                continue;
            }
            if (_busy)
            {
                if (c == '\u0003') Emit("^C\r\n"); // the running command cannot be interrupted yet
                continue;
            }
            switch (c)
            {
                case '\r':
                case '\n':
                    if (c == '\n' && i > 0 && text[i - 1] == '\r') break;
                    var command = _line.ToString();
                    _line.Clear();
                    Emit("\r\n");
                    _ = Task.Run(() => RunLineAsync(command));
                    break;
                case '\u007f':
                case '\b':
                    if (_line.Length > 0)
                    {
                        _line.Length--;
                        Emit("\b \b");
                    }
                    break;
                case '\u0003':
                    _line.Clear();
                    Emit("^C\r\n");
                    Prompt();
                    break;
                case '\u000c':
                    Emit("\u001b[2J\u001b[H");
                    Prompt();
                    Emit(_line.ToString());
                    break;
                default:
                    if (c >= ' ')
                    {
                        _line.Append(c);
                        Emit(c.ToString());
                    }
                    break;
            }
        }
    }

    public void Resize(uint columns, uint rows) { /* the output is just text lines */ }
    public Task<string?> RunCommandAsync(string command, TimeSpan timeout) => Task.FromResult<string?>(null);
    public Task<string> DetectOsAsync() => Task.FromResult("windows");

    private async Task RunLineAsync(string command)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            Prompt();
            return;
        }
        await _running.WaitAsync();
        _busy = true;
        try
        {
            var quotedCwd = _cwd.Replace("'", "''");
            var script = $"Set-Location -LiteralPath '{quotedCwd}' -ErrorAction SilentlyContinue; try {{ {command}\n}} finally {{ Write-Output ('{CwdMarker}' + (Get-Location).Path) }}";
            var result = await _client.RunPowerShellAsync(script, TimeSpan.FromMinutes(10));
            var output = result.StdOut;
            var marker = output.LastIndexOf(CwdMarker, StringComparison.Ordinal);
            if (marker >= 0)
            {
                _cwd = output[(marker + CwdMarker.Length)..].Trim();
                output = output[..marker];
            }
            Emit(ToTerminal(output));
            if (result.StdErr.Length > 0) Emit("\u001b[31m" + ToTerminal(result.StdErr.TrimEnd('\r', '\n') + "\n") + "\u001b[0m");
            if (result.TimedOut) Emit("\u001b[33m(stopped after 10 minutes)\u001b[0m\r\n");
        }
        catch (WinRmException ex)
        {
            Emit("\u001b[31m" + ToTerminal(ex.Message) + "\u001b[0m\r\n");
            RaiseClosed(ex.Message);
            return;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            RaiseClosed(ex.Message);
            return;
        }
        finally
        {
            _busy = false;
            _running.Release();
        }
        Prompt();
    }

    private static string ToTerminal(string text) => text.Replace("\r\n", "\n").Replace("\n", "\r\n");

    private void Prompt() => Emit($"\u001b[32mPS {_cwd}>\u001b[0m ");

    private void Emit(string text)
    {
        if (!_disposed && text.Length > 0) Data?.Invoke(Encoding.UTF8.GetBytes(text));
    }

    private void RaiseClosed(string? reason)
    {
        if (Interlocked.Exchange(ref _closedRaised, 1) == 0) Closed?.Invoke(reason);
    }

    public void Dispose()
    {
        _disposed = true;
        _client.Dispose();
    }
}
