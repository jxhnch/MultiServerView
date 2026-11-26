using MultiServerView.Core.Ssh;

namespace MultiServerView.Core.Local;

/// <summary>
/// A shell of this PC in the terminal of the UI. The shell gets a real terminal of the system (Windows: pseudo console,
/// Linux: pty), so full-screen programs, colours, Ctrl+C and resizing work as usual.
/// </summary>
public sealed class LocalTerminalSession : ITerminalSession
{
    private readonly LocalShell _shell;
    private IPseudoTerminal? _terminal;
    private int _closedRaised;
    private volatile bool _disposed;

    public event Action<byte[]>? Data;
    public event Action<string?>? Closed;
    public event Action<string>? Info { add { } remove { } }

    public LocalTerminalSession(LocalShell shell) => _shell = shell;

    public Task ConnectAsync(uint columns, uint rows, CancellationToken ct = default) => Task.Run(() =>
    {
        var folder = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var cols = (int)Math.Clamp(columns, 2, 1000);
        var lines = (int)Math.Clamp(rows, 2, 1000);
        IPseudoTerminal terminal = OperatingSystem.IsWindows()
            ? new WindowsPseudoConsole(_shell, folder, cols, lines)
            : new UnixPseudoTerminal(_shell, folder, cols, lines);
        _terminal = terminal;
        new Thread(() => ReadLoop(terminal)) { IsBackground = true, Name = "local-terminal" }.Start();
    }, ct);

    private void ReadLoop(IPseudoTerminal terminal)
    {
        var buffer = new byte[8192];
        try
        {
            int n;
            while ((n = terminal.Output.Read(buffer, 0, buffer.Length)) > 0)
                Data?.Invoke(buffer.AsSpan(0, n).ToArray());
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or UnauthorizedAccessException)
        {
            // Linux reports the end of the shell as an input/output error
        }
        if (!_disposed && Interlocked.Exchange(ref _closedRaised, 1) == 0) Closed?.Invoke(null);
    }

    public void Write(byte[] data)
    {
        try
        {
            var input = _terminal?.Input;
            if (input == null) return;
            input.Write(data, 0, data.Length);
            input.Flush();
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // the shell is just ending; the read loop reports it
        }
    }

    public void Resize(uint columns, uint rows) => _terminal?.Resize((int)Math.Clamp(columns, 2, 1000), (int)Math.Clamp(rows, 2, 1000));

    public Task<string?> RunCommandAsync(string command, TimeSpan timeout) => Task.FromResult<string?>(null);
    public Task<string> DetectOsAsync() => Task.FromResult("");

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _terminal?.Dispose();
        _terminal = null;
    }
}

internal interface IPseudoTerminal : IDisposable
{
    Stream Input { get; }
    Stream Output { get; }
    void Resize(int columns, int rows);
}
