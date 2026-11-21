using System.Net.Sockets;
using System.Text;

namespace MultiServerView.Core.Ssh;

/// <summary>
/// A Telnet connection (network devices, old servers). Speaks just enough of the protocol (RFC 854): it answers the
/// option negotiation, tells the other side the window size (NAWS) and the terminal type, and hands only the real
/// text to the terminal. Telnet is not encrypted: use it only in trusted networks.
/// </summary>
public sealed class TelnetTerminalSession : ITerminalSession
{
    private const byte IAC = 255, DONT = 254, DO = 253, WONT = 252, WILL = 251, SB = 250, SE = 240;
    private const byte OptEcho = 1, OptSuppressGoAhead = 3, OptTerminalType = 24, OptWindowSize = 31;

    private readonly string _host;
    private readonly int _port;
    private TcpClient? _tcp;
    private NetworkStream? _stream;
    private readonly CancellationTokenSource _cts = new();
    private readonly object _writeGate = new();
    private readonly HashSet<byte> _wePerform = new();   // options we said WILL to
    private readonly HashSet<byte> _theyPerform = new(); // options they said WILL to (we answered DO)
    private uint _columns = 80, _rows = 24;
    private int _closedRaised;

    public event Action<byte[]>? Data;
    public event Action<string?>? Closed;
    public event Action<string>? Info;

    public TelnetTerminalSession(string host, int port)
    {
        _host = host;
        _port = port > 0 ? port : 23;
    }

    public async Task ConnectAsync(uint columns, uint rows, CancellationToken ct = default)
    {
        _columns = columns;
        _rows = rows;
        var tcp = new TcpClient { NoDelay = true };
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            await tcp.ConnectAsync(_host, _port, timeout.Token);
        }
        catch (Exception ex)
        {
            tcp.Dispose();
            throw new InvalidOperationException(ex is OperationCanceledException
                ? $"No answer from {_host}:{_port} (timeout)."
                : $"Could not connect to {_host}:{_port}: {ex.Message}", ex);
        }
        _tcp = tcp;
        _stream = tcp.GetStream();
        Info?.Invoke("Telnet is not encrypted: everything you type, including passwords, travels in clear text.");
        _ = Task.Run(() => ReadLoop(_cts.Token));
    }

    public void Write(byte[] data)
    {
        if (_stream == null) return;
        // 255 is "IAC" and has to be doubled; a bare Enter (CR) is followed by NUL (RFC 854)
        var outgoing = new List<byte>(data.Length + 4);
        for (var i = 0; i < data.Length; i++)
        {
            var b = data[i];
            outgoing.Add(b);
            if (b == IAC) outgoing.Add(IAC);
            else if (b == 13 && (i + 1 >= data.Length || data[i + 1] != 10)) outgoing.Add(0);
        }
        Send(outgoing.ToArray());
    }

    public void Resize(uint columns, uint rows)
    {
        _columns = columns;
        _rows = rows;
        if (_wePerform.Contains(OptWindowSize)) SendWindowSize();
    }

    public Task<string?> RunCommandAsync(string command, TimeSpan timeout) => Task.FromResult<string?>(null);
    public Task<string> DetectOsAsync() => Task.FromResult("");

    private void Send(byte[] bytes)
    {
        try
        {
            lock (_writeGate) _stream?.Write(bytes, 0, bytes.Length);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or SocketException or InvalidOperationException)
        {
            RaiseClosed(ex.Message);
        }
    }

    private void SendWindowSize()
    {
        // every byte of the size that equals IAC would have to be doubled; sizes are far below 255 columns/rows in practice
        // but a wide window can reach it, so double it properly
        var payload = new List<byte> { IAC, SB, OptWindowSize };
        foreach (var b in new[] { (byte)(_columns >> 8), (byte)_columns, (byte)(_rows >> 8), (byte)_rows })
        {
            payload.Add(b);
            if (b == IAC) payload.Add(IAC);
        }
        payload.Add(IAC);
        payload.Add(SE);
        Send(payload.ToArray());
    }

    private enum State { Data, Iac, Option, Sub, SubIac }

    private async Task ReadLoop(CancellationToken ct)
    {
        var buffer = new byte[8192];
        var state = State.Data;
        byte verb = 0;
        var sub = new List<byte>();
        var previousWasCr = false;
        try
        {
            var stream = _stream!;
            while (!ct.IsCancellationRequested)
            {
                var n = await stream.ReadAsync(buffer, ct);
                if (n == 0) break;
                var text = new List<byte>(n);
                for (var i = 0; i < n; i++)
                {
                    var b = buffer[i];
                    switch (state)
                    {
                        case State.Data:
                            if (b == IAC) state = State.Iac;
                            else if (previousWasCr && b == 0) previousWasCr = false; // CR NUL = a bare CR
                            else
                            {
                                text.Add(b);
                                previousWasCr = b == 13;
                            }
                            break;
                        case State.Iac:
                            if (b == IAC) { text.Add(IAC); state = State.Data; previousWasCr = false; }
                            else if (b is DO or DONT or WILL or WONT) { verb = b; state = State.Option; }
                            else if (b == SB) { sub.Clear(); state = State.Sub; }
                            else state = State.Data; // NOP, GA, ... are ignored
                            break;
                        case State.Option:
                            Negotiate(verb, b);
                            state = State.Data;
                            break;
                        case State.Sub:
                            if (b == IAC) state = State.SubIac;
                            else sub.Add(b);
                            break;
                        case State.SubIac:
                            if (b == SE) { SubNegotiation(sub); state = State.Data; }
                            else { sub.Add(b); state = State.Sub; } // IAC IAC inside = 255
                            break;
                    }
                }
                if (text.Count > 0) Data?.Invoke(text.ToArray());
            }
            RaiseClosed(null);
        }
        catch (OperationCanceledException) { /* closed by us */ }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or SocketException)
        {
            RaiseClosed(ct.IsCancellationRequested ? null : ex.Message);
        }
    }

    private void Negotiate(byte verb, byte option)
    {
        switch (verb)
        {
            case WILL: // they offer an option
                if (option is OptEcho or OptSuppressGoAhead)
                {
                    if (_theyPerform.Add(option)) Send(new[] { IAC, DO, option });
                }
                else Send(new[] { IAC, DONT, option });
                break;
            case DO: // they ask us to do an option
                if (option is OptTerminalType or OptWindowSize or OptSuppressGoAhead)
                {
                    if (_wePerform.Add(option))
                    {
                        Send(new[] { IAC, WILL, option });
                        if (option == OptWindowSize) SendWindowSize();
                    }
                }
                else Send(new[] { IAC, WONT, option });
                break;
            case WONT:
                if (_theyPerform.Remove(option)) Send(new[] { IAC, DONT, option });
                break;
            case DONT:
                if (_wePerform.Remove(option)) Send(new[] { IAC, WONT, option });
                break;
        }
    }

    private void SubNegotiation(List<byte> sub)
    {
        // terminal type: they ask (SEND = 1), we answer (IS = 0)
        if (sub.Count >= 2 && sub[0] == OptTerminalType && sub[1] == 1)
        {
            var name = Encoding.ASCII.GetBytes("xterm-256color");
            Send(new byte[] { IAC, SB, OptTerminalType, 0 }.Concat(name).Concat(new byte[] { IAC, SE }).ToArray());
        }
    }

    private void RaiseClosed(string? reason)
    {
        if (Interlocked.Exchange(ref _closedRaised, 1) == 0) Closed?.Invoke(reason);
    }

    public void Dispose()
    {
        try { _cts.Cancel(); } catch (ObjectDisposedException) { /* already disposed */ }
        try { _stream?.Dispose(); } catch { /* does not matter during cleanup */ }
        try { _tcp?.Dispose(); } catch { /* does not matter during cleanup */ }
        _stream = null;
        _tcp = null;
    }
}
