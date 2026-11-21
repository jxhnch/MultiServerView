using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Xml.Linq;

namespace MultiServerView.Core.Windows;

public sealed record WinRmResult(string StdOut, string StdErr, int ExitCode, bool TimedOut);

public sealed class WinRmException : Exception
{
    public WinRmException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>
/// A small client for Windows Remote Management (WS-Management, "WinRS"): runs commands on a Windows server without SSH.
/// Works over HTTPS (port 5986, recommended) and over HTTP (port 5985) with basic authentication when the server allows
/// unencrypted traffic. Windows sign-in (NTLM/Negotiate) is done by the .NET HTTP stack. HTTP with NTLM needs message
/// encryption, which this client does not implement: use the HTTPS listener of WinRM for that.
/// Each command runs in a new PowerShell process (nothing is kept between commands except what the caller passes).
/// </summary>
public sealed class WinRmClient : IDisposable
{
    private const string NsSoap = "http://www.w3.org/2003/05/soap-envelope";
    private const string NsAddressing = "http://schemas.xmlsoap.org/ws/2004/08/addressing";
    private const string NsWsman = "http://schemas.dmtf.org/wbem/wsman/1/wsman.xsd";
    private const string NsShell = "http://schemas.microsoft.com/wbem/wsman/1/windows/shell";
    private const string ShellUri = "http://schemas.microsoft.com/wbem/wsman/1/windows/shell/cmd";
    private const string ActionCreate = "http://schemas.xmlsoap.org/ws/2004/09/transfer/Create";
    private const string ActionDelete = "http://schemas.xmlsoap.org/ws/2004/09/transfer/Delete";
    private const string ActionCommand = "http://schemas.microsoft.com/wbem/wsman/1/windows/shell/Command";
    private const string ActionReceive = "http://schemas.microsoft.com/wbem/wsman/1/windows/shell/Receive";
    private const string ActionSignal = "http://schemas.microsoft.com/wbem/wsman/1/windows/shell/Signal";
    private const string SignalTerminate = "http://schemas.microsoft.com/wbem/wsman/1/windows/shell/signal/terminate";
    private const string StateDone = "http://schemas.microsoft.com/wbem/wsman/1/windows/shell/CommandState/Done";

    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly string _endpoint;

    /// <summary>The address of the endpoint: port 5985 is plain HTTP, everything else HTTPS.</summary>
    public static string EndpointFor(string host, int port) => $"{(port == 5985 ? "http" : "https")}://{host}:{(port > 0 ? port : 5986)}/wsman";

    public WinRmClient(string host, int port, string username, string password)
    {
        _endpoint = EndpointFor(host, port);
        var user = username;
        var domain = "";
        var slash = username.IndexOf('\\');
        if (slash > 0)
        {
            domain = username[..slash];
            user = username[(slash + 1)..];
        }
        var handler = new HttpClientHandler
        {
            Credentials = new NetworkCredential(user, password, domain),
            PreAuthenticate = true,
            // Windows servers use self-signed certificates for WinRM by default; the user trusts the address he typed.
            ServerCertificateCustomValidationCallback = (_, _, _, _) => true
        };
        _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(90) };
        _ownsHttp = true;
    }

    /// <summary>For tests: use your own HTTP client (the endpoint is given as is).</summary>
    public WinRmClient(HttpClient http, string endpoint)
    {
        _http = http;
        _endpoint = endpoint;
    }

    /// <summary>Runs PowerShell code and returns what it printed. The code is handed over encoded, so quotes and special characters are safe.</summary>
    public Task<WinRmResult> RunPowerShellAsync(string script, TimeSpan timeout, CancellationToken ct = default)
    {
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(
            "$ProgressPreference='SilentlyContinue'; [Console]::OutputEncoding=[System.Text.Encoding]::UTF8; " + script));
        return RunAsync("powershell.exe", new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-EncodedCommand", encoded }, timeout, ct);
    }

    /// <summary>Runs a program (with arguments) in a command shell and collects its output.</summary>
    public async Task<WinRmResult> RunAsync(string command, string[] arguments, TimeSpan timeout, CancellationToken ct = default)
    {
        var shellId = await CreateShellAsync(ct);
        try
        {
            var commandId = await StartCommandAsync(shellId, command, arguments, ct);
            var stdout = new StringBuilder();
            var stderr = new StringBuilder();
            var stdoutBytes = new List<byte>();
            var stderrBytes = new List<byte>();
            var exit = -1;
            var done = false;
            var deadline = DateTime.UtcNow + timeout;
            while (!done)
            {
                if (DateTime.UtcNow > deadline)
                {
                    await TryAsync(() => SignalAsync(shellId, commandId, CancellationToken.None));
                    stdout.Append(Encoding.UTF8.GetString(stdoutBytes.ToArray()));
                    stderr.Append(Encoding.UTF8.GetString(stderrBytes.ToArray()));
                    return new WinRmResult(stdout.ToString(), stderr.ToString(), -1, true);
                }
                ct.ThrowIfCancellationRequested();
                var reply = await SendAsync(ActionReceive, shellId, "<rsp:Receive><rsp:DesiredStream CommandId=\"" + commandId + "\">stdout stderr</rsp:DesiredStream></rsp:Receive>", ct, allowOperationTimeout: true);
                if (reply == null) continue; // nothing new within the wait time: ask again
                foreach (var stream in reply.Descendants(XName.Get("Stream", NsShell)))
                {
                    var bytes = Convert.FromBase64String(stream.Value.Trim());
                    if ((string?)stream.Attribute("Name") == "stderr") stderrBytes.AddRange(bytes);
                    else stdoutBytes.AddRange(bytes);
                }
                var state = reply.Descendants(XName.Get("CommandState", NsShell)).FirstOrDefault();
                if (state != null && (string?)state.Attribute("State") == StateDone)
                {
                    done = true;
                    var code = state.Element(XName.Get("ExitCode", NsShell))?.Value;
                    if (int.TryParse(code, out var parsed)) exit = parsed;
                }
            }
            await TryAsync(() => SignalAsync(shellId, commandId, CancellationToken.None));
            return new WinRmResult(Encoding.UTF8.GetString(stdoutBytes.ToArray()), CleanClixml(Encoding.UTF8.GetString(stderrBytes.ToArray())), exit, false);
        }
        finally
        {
            await TryAsync(() => DeleteShellAsync(shellId));
        }
    }

    /// <summary>PowerShell reports errors on stderr as "#&lt; CLIXML ..." when it is not run in a console: show plain text instead.</summary>
    public static string CleanClixml(string text)
    {
        if (!text.StartsWith("#< CLIXML", StringComparison.Ordinal)) return text;
        try
        {
            var xml = text[(text.IndexOf('\n') + 1)..].Trim();
            var doc = XDocument.Parse(xml);
            var lines = doc.Descendants().Where(e => e.Name.LocalName == "S" && (string?)e.Attribute("S") == "Error").Select(e => e.Value.Replace("_x000D__x000A_", "\n").Replace("_x000D_", "").Replace("_x000A_", "\n"));
            var result = string.Concat(lines).Trim();
            return result.Length > 0 ? result : text;
        }
        catch (Exception ex) when (ex is System.Xml.XmlException or InvalidOperationException) { return text; }
    }

    // ------------------------------------------------------------------------------------------------ Protocol

    private async Task<string> CreateShellAsync(CancellationToken ct)
    {
        var body = "<rsp:Shell><rsp:InputStreams>stdin</rsp:InputStreams><rsp:OutputStreams>stdout stderr</rsp:OutputStreams></rsp:Shell>";
        var reply = await SendAsync(ActionCreate, null, body, ct, options: "<w:OptionSet><w:Option Name=\"WINRS_NOPROFILE\">FALSE</w:Option><w:Option Name=\"WINRS_CODEPAGE\">65001</w:Option></w:OptionSet>");
        return reply?.Descendants(XName.Get("ShellId", NsShell)).FirstOrDefault()?.Value
               ?? throw new WinRmException("The server did not open a shell.");
    }

    private async Task<string> StartCommandAsync(string shellId, string command, string[] arguments, CancellationToken ct)
    {
        var body = new StringBuilder("<rsp:CommandLine><rsp:Command>").Append(Escape(command)).Append("</rsp:Command>");
        foreach (var argument in arguments) body.Append("<rsp:Arguments>").Append(Escape(argument)).Append("</rsp:Arguments>");
        body.Append("</rsp:CommandLine>");
        var reply = await SendAsync(ActionCommand, shellId, body.ToString(), ct, options: "<w:OptionSet><w:Option Name=\"WINRS_CONSOLEMODE_STDIN\">TRUE</w:Option><w:Option Name=\"WINRS_SKIP_CMD_SHELL\">FALSE</w:Option></w:OptionSet>");
        return reply?.Descendants(XName.Get("CommandId", NsShell)).FirstOrDefault()?.Value
               ?? throw new WinRmException("The server did not start the command.");
    }

    private Task SignalAsync(string shellId, string commandId, CancellationToken ct) =>
        SendAsync(ActionSignal, shellId, "<rsp:Signal CommandId=\"" + commandId + "\"><rsp:Code>" + SignalTerminate + "</rsp:Code></rsp:Signal>", ct);

    private Task DeleteShellAsync(string shellId) => SendAsync(ActionDelete, shellId, "", CancellationToken.None);

    private static string Escape(string text) => System.Security.SecurityElement.Escape(text) ?? "";

    private static async Task TryAsync(Func<Task> action)
    {
        try { await action(); }
        catch (Exception ex) when (ex is WinRmException or HttpRequestException or TaskCanceledException) { /* cleaning up: does not matter */ }
    }

    /// <summary>Sends one WS-Management message. Returns the body of the answer (null when a Receive ran into the wait time).</summary>
    private async Task<XElement?> SendAsync(string action, string? shellId, string body, CancellationToken ct, string options = "", bool allowOperationTimeout = false)
    {
        var selector = shellId == null ? "" : "<w:SelectorSet><w:Selector Name=\"ShellId\">" + shellId + "</w:Selector></w:SelectorSet>";
        var xml =
            "<env:Envelope xmlns:env=\"" + NsSoap + "\" xmlns:a=\"" + NsAddressing + "\" xmlns:w=\"" + NsWsman + "\" xmlns:rsp=\"" + NsShell + "\" xmlns:p=\"http://schemas.microsoft.com/wbem/wsman/1/wsman.xsd\">" +
            "<env:Header>" +
            "<a:To>" + Escape(_endpoint) + "</a:To>" +
            "<a:ReplyTo><a:Address mustUnderstand=\"true\">http://schemas.xmlsoap.org/ws/2004/08/addressing/role/anonymous</a:Address></a:ReplyTo>" +
            "<w:MaxEnvelopeSize mustUnderstand=\"true\">153600</w:MaxEnvelopeSize>" +
            "<a:MessageID>uuid:" + Guid.NewGuid().ToString().ToUpperInvariant() + "</a:MessageID>" +
            "<w:Locale mustUnderstand=\"false\" xml:lang=\"en-US\"/><p:DataLocale mustUnderstand=\"false\" xml:lang=\"en-US\"/>" +
            "<w:OperationTimeout>PT20S</w:OperationTimeout>" +
            "<w:ResourceURI mustUnderstand=\"true\">" + ShellUri + "</w:ResourceURI>" +
            "<a:Action mustUnderstand=\"true\">" + action + "</a:Action>" +
            selector + options +
            "</env:Header><env:Body>" + body + "</env:Body></env:Envelope>";

        HttpResponseMessage response;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, _endpoint) { Content = new StringContent(xml, Encoding.UTF8, "application/soap+xml") };
            request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse("application/soap+xml;charset=UTF-8");
            response = await _http.SendAsync(request, ct);
        }
        catch (HttpRequestException ex)
        {
            throw new WinRmException(Describe(ex), ex);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new WinRmException("No answer from " + _endpoint + " (timeout). Is WinRM switched on and is the port open?", ex);
        }

        using (response)
        {
            var text = await response.Content.ReadAsStringAsync(ct);
            if (response.StatusCode == HttpStatusCode.Unauthorized)
                throw new WinRmException("Sign-in refused: wrong user name or password, or the server does not allow this kind of sign-in over this connection. For a local account use .\\user or COMPUTER\\user; for HTTP (port 5985) basic authentication must be enabled on the server, HTTPS (port 5986) is recommended.");
            if (!response.IsSuccessStatusCode)
            {
                XElement? fault = null;
                try { fault = XDocument.Parse(text).Root; } catch (System.Xml.XmlException) { /* not XML */ }
                var code = fault?.Descendants().FirstOrDefault(e => e.Name.LocalName == "WSManFault")?.Attribute("Code")?.Value;
                // "operation timed out": a Receive without new output - that is normal while a command is running
                if (allowOperationTimeout && code == "2150858793") return null;
                var reason = fault?.Descendants().FirstOrDefault(e => e.Name.LocalName == "Message")?.Value;
                throw new WinRmException($"The server answered {(int)response.StatusCode}" + (string.IsNullOrWhiteSpace(reason) ? "." : ": " + reason.Trim()));
            }
            try
            {
                return XDocument.Parse(text).Root?.Descendants(XName.Get("Body", NsSoap)).FirstOrDefault() ?? new XElement("empty");
            }
            catch (System.Xml.XmlException ex)
            {
                throw new WinRmException("This is not a WinRM server (the answer is not XML). Are address and port correct?", ex);
            }
        }
    }

    private static string Describe(Exception ex)
    {
        var inner = ex;
        while (inner.InnerException != null) inner = inner.InnerException;
        return "No connection: " + inner.Message + " (WinRM listens on port 5986 for HTTPS and 5985 for HTTP; it has to be switched on on the server).";
    }

    public void Dispose()
    {
        if (_ownsHttp) _http.Dispose();
    }
}
