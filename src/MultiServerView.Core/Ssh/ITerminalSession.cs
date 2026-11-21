namespace MultiServerView.Core.Ssh;

/// <summary>
/// An interactive text session for the terminal of the UI: SSH, Telnet or a serial port. Data comes out and goes in as raw bytes,
/// the terminal itself (xterm.js) lives in the UI.
/// </summary>
public interface ITerminalSession : IDisposable
{
    /// <summary>New output from the other side.</summary>
    event Action<byte[]>? Data;

    /// <summary>The session ended (reason, or null on a normal end).</summary>
    event Action<string?>? Closed;

    /// <summary>A note for the user (for example "first contact with this host").</summary>
    event Action<string>? Info;

    Task ConnectAsync(uint columns, uint rows, CancellationToken ct = default);
    void Write(byte[] data);
    void Resize(uint columns, uint rows);

    /// <summary>Runs a command on a second channel (SSH only, other sessions return null).</summary>
    Task<string?> RunCommandAsync(string command, TimeSpan timeout);

    /// <summary>Detects the operating system (SSH only, "" otherwise).</summary>
    Task<string> DetectOsAsync();
}
