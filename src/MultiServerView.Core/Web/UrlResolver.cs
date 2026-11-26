using System.Net.Security;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;

namespace MultiServerView.Core.Web;

/// <summary>What answered at an address: nothing, or a web server (possibly with a redirect).</summary>
public sealed record ProbeAnswer(bool Answers, string Location = "");

/// <summary>
/// Finds the right form of an address somebody typed: http or https, with which port. The candidates are only tried on the typed
/// host. Safety rule: an address typed with "https://" is never turned into http, because a saved password would then travel in
/// clear text. Nothing but an empty "GET /" is sent while probing.
/// </summary>
public static partial class UrlResolver
{
    /// <summary>Ports tried when none was typed, in this order: every https one before any http one.</summary>
    private static readonly (string Scheme, int Port)[] WebCandidates = { ("https", 443), ("https", 8443), ("https", 8006), ("http", 80), ("http", 8080) };

    [GeneratedRegex(@"^\s*(https?)\s*(?::[\\/]*|;[\\/]*|[\\/]{2,})\s*(?=[^0-9\s\\/]|\d+\.|\[)", RegexOptions.IgnoreCase)]
    private static partial Regex SchemeTypo();

    [GeneratedRegex(@"^(?:\[[^\]]*\]|[^/?#:\[\]]+):(\d+)(?=$|[/?#])")]
    private static partial Regex TypedPortPattern();

    /// <summary>An address split into what was really typed. Scheme "" and Port 0 mean "not typed".</summary>
    public readonly record struct Typed(string Scheme, string Host, int Port, string Path);

    /// <summary>Repairs common typing mistakes ("https//fw", "http:\\fw", "https;//fw", blanks around).</summary>
    public static string Repair(string? text)
    {
        var value = (text ?? "").Trim();
        var match = SchemeTypo().Match(value);
        if (match.Success) value = match.Groups[1].Value.ToLowerInvariant() + "://" + value[match.Length..].TrimStart('/', '\\');
        return value;
    }

    public static bool TrySplit(string? text, out Typed typed)
    {
        typed = default;
        var value = Repair(text);
        if (value.Length == 0 || value.Any(char.IsWhiteSpace)) return false;
        var scheme = "";
        var mark = value.IndexOf("://", StringComparison.Ordinal);
        var rest = value;
        if (mark >= 0)
        {
            scheme = value[..mark].ToLowerInvariant();
            if (scheme is not ("http" or "https")) return false;
            rest = value[(mark + 3)..];
        }
        var portMatch = TypedPortPattern().Match(rest);
        var port = 0;
        if (portMatch.Success && (!int.TryParse(portMatch.Groups[1].Value, out port) || port is <= 0 or > 65535)) return false;
        if (!Uri.TryCreate("https://" + rest, UriKind.Absolute, out var uri) || uri.Host.Length == 0 || uri.UserInfo.Length > 0) return false;
        var path = uri.PathAndQuery;
        typed = new Typed(scheme, uri.Host, port, path == "/" ? "" : path);
        return true;
    }

    /// <summary>The addresses to try, best first.</summary>
    public static IReadOnlyList<(string Scheme, int Port)> Candidates(Typed typed)
    {
        var list = new List<(string, int)>();
        if (typed.Port > 0)
        {
            // The port is given: only the scheme can be wrong. https is always tried first (a typed "http" is upgraded if possible).
            list.Add(("https", typed.Port));
            if (typed.Scheme != "https") list.Add(("http", typed.Port));
        }
        else if (typed.Scheme == "https") list.AddRange(WebCandidates.Where(c => c.Scheme == "https"));
        else if (typed.Scheme == "http") list.AddRange(new[] { ("http", 80), ("http", 8080), ("https", 443), ("https", 8443), ("https", 8006) });
        else list.AddRange(WebCandidates);
        return list;
    }

    /// <summary>"scheme://host[:port]/path"; the port is left out when it is the usual one of the scheme.</summary>
    public static string Compose(string scheme, string host, int port, string path)
        => $"{scheme}://{host}{(port == (scheme == "https" ? 443 : 80) ? "" : ":" + port)}{path}";

    /// <summary>The same address with the port always written out.</summary>
    public static string Exact(string address)
        => TrySplit(address, out var t) && t.Scheme.Length > 0
            ? $"{t.Scheme}://{t.Host}:{(t.Port > 0 ? t.Port : t.Scheme == "https" ? 443 : 80)}{t.Path}"
            : address;

    /// <summary>
    /// The first candidate that answers, or null when nothing does. A device that redirects from http to https on itself is
    /// followed to https.
    /// </summary>
    public static async Task<(string Scheme, int Port)?> ResolveAsync(
        string host, IReadOnlyList<(string Scheme, int Port)> candidates,
        Func<string, string, int, CancellationToken, Task<ProbeAnswer>> probe, CancellationToken ct = default)
    {
        // all at once (a closed port can take seconds to fail), but the order of the list decides
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var running = candidates.Select(c => probe(c.Scheme, host, c.Port, stop.Token)).ToList();
        try
        {
            for (var i = 0; i < candidates.Count; i++)
            {
                var answer = await running[i];
                if (!answer.Answers) continue;
                var (scheme, port) = candidates[i];
                if (scheme == "http" && Uri.TryCreate(answer.Location, UriKind.Absolute, out var next) && next.Scheme == "https"
                    && string.Equals(next.Host, host, StringComparison.OrdinalIgnoreCase)
                    && (await probe("https", host, next.Port, ct)).Answers)
                    return ("https", next.Port);
                return (scheme, port);
            }
            return null;
        }
        finally
        {
            stop.Cancel();
            try { await Task.WhenAll(running); }
            catch (OperationCanceledException) { /* the ones that were not needed */ }
        }
    }

    /// <summary>
    /// https answers when the TLS handshake works (the certificate is not judged here: nothing secret is sent, and the real
    /// connection checks it again). http answers when an HTTP status line comes back.
    /// </summary>
    public static async Task<ProbeAnswer> ProbeAsync(string scheme, string host, int port, CancellationToken ct = default)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(TimeSpan.FromSeconds(3));
        try
        {
            using var client = new TcpClient();
            var name = host.Trim('[', ']');
            await client.ConnectAsync(name, port, limit.Token);
            if (scheme == "https")
            {
                await using var tls = new SslStream(client.GetStream(), false, (_, _, _, _) => true);
                await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = name }, limit.Token);
                return new ProbeAnswer(true);
            }
            var stream = client.GetStream();
            var request = Encoding.ASCII.GetBytes($"GET / HTTP/1.1\r\nHost: {host}:{port}\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(request, limit.Token);
            var buffer = new byte[4096];
            var length = 0;
            while (length < buffer.Length)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(length), limit.Token);
                if (read == 0) break;
                length += read;
                if (Encoding.ASCII.GetString(buffer, 0, length).Contains("\r\n\r\n", StringComparison.Ordinal)) break;
            }
            var head = Encoding.ASCII.GetString(buffer, 0, length);
            if (!head.StartsWith("HTTP/", StringComparison.Ordinal)) return new ProbeAnswer(false);
            var location = head.Split("\r\n").FirstOrDefault(l => l.StartsWith("Location:", StringComparison.OrdinalIgnoreCase));
            return new ProbeAnswer(true, location?[9..].Trim() ?? "");
        }
        catch (Exception ex) when (ex is SocketException or IOException or System.Security.Authentication.AuthenticationException or ArgumentException
                                   || ex is OperationCanceledException && !ct.IsCancellationRequested)
        {
            return new ProbeAnswer(false);
        }
    }

    /// <summary>
    /// The corrected form of an address, found by trying it out. null = nothing answered (or the text is no address), so keep
    /// what was typed. <paramref name="fallbackPort"/> is a port set next to the address (0 = none chosen).
    /// </summary>
    public static async Task<string?> FindAsync(string? text, int fallbackPort = 0,
        Func<string, string, int, CancellationToken, Task<ProbeAnswer>>? probe = null, CancellationToken ct = default)
    {
        if (!TrySplit(text, out var typed)) return null;
        if (typed.Port == 0 && fallbackPort is > 0 and <= 65535) typed = typed with { Port = fallbackPort };
        var found = await ResolveAsync(typed.Host, Candidates(typed), probe ?? ProbeAsync, ct);
        return found is { } f ? Compose(f.Scheme, typed.Host, f.Port, typed.Path) : null;
    }
}
