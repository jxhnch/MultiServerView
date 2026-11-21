using System.IO.Ports;

namespace MultiServerView.Core.Ssh;

/// <summary>
/// A serial console (network devices, routers, embedded boards): 8 data bits, no parity, 1 stop bit, no flow control.
/// Host = port name (COM3, /dev/ttyUSB0), Port = baud rate.
/// </summary>
public sealed class SerialTerminalSession : ITerminalSession
{
    private readonly string _portName;
    private readonly int _baud;
    private SerialPort? _port;
    private readonly CancellationTokenSource _cts = new();
    private int _closedRaised;

    public event Action<byte[]>? Data;
    public event Action<string?>? Closed;
    public event Action<string>? Info { add { } remove { } }

    public SerialTerminalSession(string portName, int baud)
    {
        _portName = portName.Trim();
        _baud = baud > 0 ? baud : 9600;
    }

    public static string[] PortNames()
    {
        try { return SerialPort.GetPortNames().OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToArray(); }
        catch (Exception) { return Array.Empty<string>(); }
    }

    public Task ConnectAsync(uint columns, uint rows, CancellationToken ct = default)
    {
        if (_portName.Length == 0) throw new InvalidOperationException("Please enter the serial port (for example COM3 or /dev/ttyUSB0).");
        var port = new SerialPort(_portName, _baud, Parity.None, 8, StopBits.One) { Handshake = Handshake.None, ReadTimeout = SerialPort.InfiniteTimeout };
        try
        {
            port.Open();
            // Raise DTR and RTS like a normal terminal program (many consoles wait for it). Virtual ports do not have these lines.
            try { port.DtrEnable = true; port.RtsEnable = true; }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException) { /* not available: fine */ }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or InvalidOperationException or ArgumentException)
        {
            port.Dispose();
            var hint = ex is UnauthorizedAccessException ? " (in use by another program, or no permission: on Linux your user needs the group \"dialout\")" : "";
            throw new InvalidOperationException($"Could not open {_portName}: {ex.Message}{hint}", ex);
        }
        _port = port;
        _ = Task.Run(() => ReadLoop(port, _cts.Token));
        return Task.CompletedTask;
    }

    private async Task ReadLoop(SerialPort port, CancellationToken ct)
    {
        var buffer = new byte[4096];
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var n = await port.BaseStream.ReadAsync(buffer, ct);
                if (n == 0)
                {
                    await Task.Delay(20, ct);
                    if (!port.IsOpen) break;
                    continue;
                }
                Data?.Invoke(buffer.AsSpan(0, n).ToArray());
            }
        }
        catch (OperationCanceledException) { /* closed by us */ }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException or ObjectDisposedException)
        {
            if (!ct.IsCancellationRequested) RaiseClosed("The serial port was removed or closed (" + ex.Message + ").");
            return;
        }
        if (!ct.IsCancellationRequested) RaiseClosed(null);
    }

    public void Write(byte[] data)
    {
        try { _port?.Write(data, 0, data.Length); }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or TimeoutException or UnauthorizedAccessException)
        {
            RaiseClosed(ex.Message);
        }
    }

    public void Resize(uint columns, uint rows) { /* a serial line has no window size */ }
    public Task<string?> RunCommandAsync(string command, TimeSpan timeout) => Task.FromResult<string?>(null);
    public Task<string> DetectOsAsync() => Task.FromResult("");

    private void RaiseClosed(string? reason)
    {
        if (Interlocked.Exchange(ref _closedRaised, 1) == 0) Closed?.Invoke(reason);
    }

    public void Dispose()
    {
        try { _cts.Cancel(); } catch (ObjectDisposedException) { /* already disposed */ }
        try { _port?.Dispose(); } catch { /* does not matter during cleanup */ }
        _port = null;
    }
}
