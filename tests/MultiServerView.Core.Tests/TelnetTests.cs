using System.Net;
using System.Net.Sockets;
using System.Text;
using MultiServerView.Core;
using MultiServerView.Core.Ssh;
using Xunit;

namespace MultiServerView.Core.Tests;

public class TelnetTests
{
    private const byte IAC = 255, DONT = 254, DO = 253, WONT = 252, WILL = 251, SB = 250, SE = 240;

    /// <summary>Waits until <paramref name="check"/> is true (or fails after a few seconds).</summary>
    private static async Task WaitFor(Func<bool> check, string what)
    {
        for (var i = 0; i < 100 && !check(); i++) await Task.Delay(50);
        Assert.True(check(), "Timed out waiting for: " + what);
    }

    private static (TcpListener Listener, int Port) Listen()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return (listener, ((IPEndPoint)listener.LocalEndpoint).Port);
    }

    [Fact]
    public async Task Negotiation_is_answered_and_only_the_text_reaches_the_terminal()
    {
        var (listener, port) = Listen();
        using var _ = new Disposer(listener.Stop);
        var received = new List<byte>(); // what the server gets from the client
        var serverTask = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            var stream = client.GetStream();
            // WILL ECHO, DO TERMINAL-TYPE, DO NAWS, DO (unknown 99), then text with a doubled 255 and a CR NUL
            await stream.WriteAsync(new byte[] { IAC, WILL, 1, IAC, DO, 24, IAC, DO, 31, IAC, DO, 99 });
            await stream.WriteAsync(new byte[] { IAC, SB, 24, 1, IAC, SE }); // "send me your terminal type"
            await stream.WriteAsync(Encoding.ASCII.GetBytes("login: "));
            await stream.WriteAsync(new byte[] { 0xC3, 0xA4, IAC, IAC, 13, 0, (byte)'x' }); // "ä", 255, CR (NUL removed), x
            var buffer = new byte[4096];
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (DateTime.UtcNow < deadline)
            {
                if (!stream.DataAvailable) { await Task.Delay(20); continue; }
                var n = await stream.ReadAsync(buffer);
                if (n == 0) break;
                lock (received) received.AddRange(buffer.AsSpan(0, n).ToArray());
            }
        });

        var text = new List<byte>();
        using var session = new TelnetTerminalSession("127.0.0.1", port);
        session.Data += d => { lock (text) text.AddRange(d); };
        await session.ConnectAsync(100, 30);

        await WaitFor(() => { lock (text) return text.Count >= 7 + 5; }, "the text from the server");
        string Text() { lock (text) return Encoding.Latin1.GetString(text.ToArray()); }
        Assert.Equal("login: Ã¤ÿ\rx", Text()); // no negotiation bytes inside, 255 once, CR NUL became CR

        byte[] Got() { lock (received) return received.ToArray(); }
        await WaitFor(() => Got().Length >= 3 + 3 + 3 + 9 + 3 + (4 + 14 + 2), "the answers of the client");
        var got = Got();
        bool Has(params byte[] sequence) => got.AsSpan().IndexOf(sequence) >= 0;
        Assert.True(Has(IAC, DO, 1), "agrees to server echo");
        Assert.True(Has(IAC, WILL, 24), "offers the terminal type");
        Assert.True(Has(IAC, WILL, 31), "offers the window size");
        Assert.True(Has(IAC, SB, 31, 0, 100, 0, 30, IAC, SE), "tells the window size 100x30");
        Assert.True(Has(IAC, WONT, 99), "refuses what it does not know");
        Assert.True(Has(new byte[] { IAC, SB, 24, 0 }.Concat(Encoding.ASCII.GetBytes("xterm-256color")).Concat(new byte[] { IAC, SE }).ToArray()), "answers the terminal type");
    }

    [Fact]
    public async Task Typed_text_is_escaped_and_resizing_updates_the_window_size()
    {
        var (listener, port) = Listen();
        using var _ = new Disposer(listener.Stop);
        var received = new List<byte>();
        var serverTask = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            var stream = client.GetStream();
            await stream.WriteAsync(new byte[] { IAC, DO, 31 });
            var buffer = new byte[4096];
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (DateTime.UtcNow < deadline)
            {
                if (!stream.DataAvailable) { await Task.Delay(20); continue; }
                var n = await stream.ReadAsync(buffer);
                if (n == 0) break;
                lock (received) received.AddRange(buffer.AsSpan(0, n).ToArray());
            }
        });

        using var session = new TelnetTerminalSession("127.0.0.1", port);
        await session.ConnectAsync(80, 24);
        byte[] Got() { lock (received) return received.ToArray(); }
        await WaitFor(() => Got().Length >= 3 + 9, "the window size negotiation");

        session.Write(new byte[] { (byte)'a', 255, 13 });   // a, 255 doubled, a lone CR followed by NUL
        session.Write(new byte[] { 13, 10 });               // CR LF stays as it is
        session.Resize(120, 40);
        await WaitFor(() => Got().Length >= 12 + 5 + 2 + 9, "the typed text and the new size");
        var got = Got();
        bool Has(params byte[] sequence) => got.AsSpan().IndexOf(sequence) >= 0;
        Assert.True(Has((byte)'a', 255, 255, 13, 0), "255 doubled and CR NUL");
        Assert.True(Has(13, 10), "CR LF unchanged");
        Assert.True(Has(IAC, SB, 31, 0, 120, 0, 40, IAC, SE), "new window size");
    }

    [Fact]
    public async Task Closing_the_connection_raises_Closed_and_a_wrong_address_gives_a_clear_error()
    {
        var (listener, port) = Listen();
        var closed = new TaskCompletionSource<string?>();
        var accept = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            await client.GetStream().WriteAsync(Encoding.ASCII.GetBytes("bye"));
        });
        using (var session = new TelnetTerminalSession("127.0.0.1", port))
        {
            session.Closed += r => closed.TrySetResult(r);
            await session.ConnectAsync(80, 24);
            Assert.Null(await closed.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        }
        listener.Stop();

        using var unreachable = new TelnetTerminalSession("127.0.0.1", port); // nobody listens any more
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => unreachable.ConnectAsync(80, 24));
        Assert.Contains("Could not connect", ex.Message);
    }

    [Fact]
    public void Default_ports_and_protocol_names_for_the_new_host_types()
    {
        Assert.Equal(23, ServerEntry.DefaultPort(Protocol.Telnet));
        Assert.Equal(9600, ServerEntry.DefaultPort(Protocol.Serial));
        var json = System.Text.Json.JsonSerializer.Serialize(new ServerEntry { Protocol = Protocol.Telnet }, Json.Options);
        Assert.Contains("\"protocol\":\"telnet\"", json);
        Assert.Equal(Protocol.Serial, System.Text.Json.JsonSerializer.Deserialize<ServerEntry>("{\"protocol\":\"serial\"}", Json.Options)!.Protocol);
    }

    private sealed class Disposer(Action action) : IDisposable
    {
        public void Dispose() => action();
    }
}
