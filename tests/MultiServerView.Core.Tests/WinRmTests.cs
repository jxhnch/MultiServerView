using System.Net;
using System.Text;
using System.Xml.Linq;
using MultiServerView.Core.Windows;
using Xunit;

namespace MultiServerView.Core.Tests;

/// <summary>Imitates the WinRM shell service of a Windows server (WS-Management): create shell, command, receive, signal, delete.</summary>
internal sealed class FakeWinRm : HttpMessageHandler
{
    private const string NsShell = "http://schemas.microsoft.com/wbem/wsman/1/windows/shell";
    public readonly List<string> Actions = new();
    public readonly List<string[]> CommandLines = new();
    public Func<string[], (string Out, string Err, int Exit)> Run = _ => ("", "", 0);
    public int TimeoutsBeforeOutput;
    public HttpStatusCode? ForceStatus;
    private (string Out, string Err, int Exit) _result;

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        if (ForceStatus is { } status) return Task.FromResult(new HttpResponseMessage(status));
        var doc = XDocument.Parse(request.Content!.ReadAsStringAsync(ct).GetAwaiter().GetResult());
        var action = doc.Descendants().First(e => e.Name.LocalName == "Action").Value;
        Actions.Add(action.Split('/').Last());
        var resource = doc.Descendants().First(e => e.Name.LocalName == "ResourceURI").Value;
        Assert.EndsWith("/windows/shell/cmd", resource);

        string body;
        switch (action.Split('/').Last())
        {
            case "Create":
                body = "<rsp:Shell xmlns:rsp=\"" + NsShell + "\"><rsp:ShellId>SHELL-1</rsp:ShellId></rsp:Shell>";
                break;
            case "Command":
                Assert.Equal("SHELL-1", doc.Descendants().First(e => e.Name.LocalName == "Selector").Value);
                var line = doc.Descendants().First(e => e.Name.LocalName == "CommandLine");
                var parts = line.Elements().Select(e => e.Value).ToArray();
                CommandLines.Add(parts);
                _result = Run(parts);
                body = "<rsp:CommandResponse xmlns:rsp=\"" + NsShell + "\"><rsp:CommandId>CMD-1</rsp:CommandId></rsp:CommandResponse>";
                break;
            case "Receive":
                if (TimeoutsBeforeOutput > 0)
                {
                    TimeoutsBeforeOutput--;
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)
                    {
                        Content = new StringContent("<s:Envelope xmlns:s=\"http://www.w3.org/2003/05/soap-envelope\"><s:Body><s:Fault><s:Detail><f:WSManFault xmlns:f=\"http://schemas.microsoft.com/wbem/wsman/1/wsmanfault\" Code=\"2150858793\"><f:Message>The WS-Management service cannot complete the operation within the time specified in OperationTimeout.</f:Message></f:WSManFault></s:Detail></s:Fault></s:Body></s:Envelope>")
                    });
                }
                string B64(string s) => Convert.ToBase64String(Encoding.UTF8.GetBytes(s));
                body = "<rsp:ReceiveResponse xmlns:rsp=\"" + NsShell + "\">" +
                       $"<rsp:Stream Name=\"stdout\" CommandId=\"CMD-1\">{B64(_result.Out)}</rsp:Stream>" +
                       $"<rsp:Stream Name=\"stderr\" CommandId=\"CMD-1\">{B64(_result.Err)}</rsp:Stream>" +
                       $"<rsp:CommandState CommandId=\"CMD-1\" State=\"{NsShell}/CommandState/Done\"><rsp:ExitCode>{_result.Exit}</rsp:ExitCode></rsp:CommandState></rsp:ReceiveResponse>";
                break;
            default: // Signal, Delete
                body = "<empty/>";
                break;
        }
        var envelope = "<s:Envelope xmlns:s=\"http://www.w3.org/2003/05/soap-envelope\"><s:Body>" + body + "</s:Body></s:Envelope>";
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(envelope, Encoding.UTF8, "application/soap+xml") });
    }
}

public class WinRmTests
{
    private static (WinRmClient Client, FakeWinRm Fake) NewClient()
    {
        var fake = new FakeWinRm();
        return (new WinRmClient(new HttpClient(fake), "https://win.example.com:5986/wsman"), fake);
    }

    private static string DecodeScript(string[] commandLine)
    {
        var encoded = commandLine[^1];
        return Encoding.Unicode.GetString(Convert.FromBase64String(encoded));
    }

    [Theory]
    [InlineData("win.example.com", 5986, "https://win.example.com:5986/wsman")]
    [InlineData("win.example.com", 5985, "http://win.example.com:5985/wsman")]
    [InlineData("10.0.0.5", 0, "https://10.0.0.5:5986/wsman")]
    public void Port_decides_between_https_and_http(string host, int port, string expected) =>
        Assert.Equal(expected, WinRmClient.EndpointFor(host, port));

    [Fact]
    public async Task A_command_runs_through_create_command_receive_signal_and_delete()
    {
        var (client, fake) = NewClient();
        fake.Run = _ => ("hello \u00E4\r\n", "", 0);
        var result = await client.RunPowerShellAsync("Write-Output 'hello'", TimeSpan.FromSeconds(30));
        Assert.Equal(new[] { "Create", "Command", "Receive", "Signal", "Delete" }, fake.Actions);
        Assert.Equal("hello \u00E4\r\n", result.StdOut);
        Assert.Equal(0, result.ExitCode);
        Assert.False(result.TimedOut);

        var line = fake.CommandLines.Single();
        Assert.Equal("powershell.exe", line[0]);
        Assert.Contains("-EncodedCommand", line);
        Assert.Contains("Write-Output 'hello'", DecodeScript(line)); // the script is handed over encoded, so no quoting problems
    }

    [Fact]
    public async Task Exit_code_and_errors_are_returned_and_clixml_is_turned_into_text()
    {
        var (client, fake) = NewClient();
        fake.Run = _ => ("", "#< CLIXML\r\n<Objs Version=\"1.1.0.1\" xmlns=\"http://schemas.microsoft.com/powershell/2004/04\"><S S=\"Error\">boom_x000D__x000A_</S></Objs>", 1);
        var result = await client.RunPowerShellAsync("throw 'boom'", TimeSpan.FromSeconds(30));
        Assert.Equal(1, result.ExitCode);
        Assert.Equal("boom", result.StdErr);
    }

    [Fact]
    public async Task A_receive_that_ran_into_the_wait_time_is_asked_again()
    {
        var (client, fake) = NewClient();
        fake.TimeoutsBeforeOutput = 2;
        fake.Run = _ => ("late", "", 0);
        var result = await client.RunPowerShellAsync("Start-Sleep 30", TimeSpan.FromSeconds(30));
        Assert.Equal("late", result.StdOut);
        Assert.Equal(3, fake.Actions.Count(a => a == "Receive"));
    }

    [Fact]
    public async Task A_refused_sign_in_explains_what_to_check()
    {
        var (client, fake) = NewClient();
        fake.ForceStatus = HttpStatusCode.Unauthorized;
        var ex = await Assert.ThrowsAsync<WinRmException>(() => client.RunPowerShellAsync("1", TimeSpan.FromSeconds(5)));
        Assert.Contains("Sign-in refused", ex.Message);
        Assert.Contains("5986", ex.Message);
    }

    [Fact]
    public async Task The_terminal_runs_a_line_and_keeps_the_folder()
    {
        var (client, fake) = NewClient();
        var scripts = new List<string>();
        fake.Run = line =>
        {
            var script = DecodeScript(line);
            scripts.Add(script);
            if (script.Contains("COMPUTERNAME")) return ("SRV01|C:\\Users\\admin\r\n", "", 0);
            if (script.Contains("cd Windows")) return ("out-1\r\n__MSVCWD__C:\\Windows\r\n", "", 0);
            return ("out-2\r\n__MSVCWD__C:\\Windows\r\n", "", 0);
        };
        using var session = new WinRmTerminalSession(client, "win.example.com");
        var output = new StringBuilder();
        session.Data += d => { lock (output) output.Append(Encoding.UTF8.GetString(d)); };
        string Out() { lock (output) return output.ToString(); }

        await session.ConnectAsync(80, 24);
        Assert.Contains("PS C:\\Users\\admin>", Out());

        session.Write(Encoding.UTF8.GetBytes("cd Windows\r"));
        for (var i = 0; i < 100 && !Out().Contains("PS C:\\Windows>"); i++) await Task.Delay(30);
        Assert.Contains("out-1", Out());
        Assert.DoesNotContain("__MSVCWD__", Out()); // the marker is an internal detail
        Assert.Contains("PS C:\\Windows>", Out());

        session.Write(Encoding.UTF8.GetBytes("dir\r"));
        for (var i = 0; i < 100 && !Out().Contains("out-2"); i++) await Task.Delay(30);
        Assert.Contains("out-2", Out());
        Assert.Contains("Set-Location -LiteralPath 'C:\\Windows'", scripts.Last()); // the folder of the last line is the start of the next
    }

    [Fact]
    public async Task Typing_is_echoed_and_backspace_corrects()
    {
        var (client, fake) = NewClient();
        fake.Run = line => DecodeScript(line).Contains("COMPUTERNAME") ? ("SRV|C:\\\r\n", "", 0) : ("", "", 0);
        using var session = new WinRmTerminalSession(client, "x");
        var output = new StringBuilder();
        session.Data += d => { lock (output) output.Append(Encoding.UTF8.GetString(d)); };
        await session.ConnectAsync(80, 24);
        session.Write(Encoding.UTF8.GetBytes("abc"));
        session.Write(new byte[] { 0x7f });
        string Out() { lock (output) return output.ToString(); }
        Assert.EndsWith("abc\b \b", Out());
        session.Write(new byte[] { 0x03 }); // Ctrl+C throws the line away
        Assert.Contains("^C", Out());
    }
}
