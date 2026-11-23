namespace MultiServerView.Core.Web;

/// <summary>Where a "Web" host points to: scheme, host, port and the page to open first.</summary>
public sealed record WebTarget(string Scheme, string Host, int Port, string StartPath)
{
    public bool IsHttps => Scheme == "https";

    /// <summary>"https://host:port" (the port is left out when it is the default of the scheme).</summary>
    public string Origin => $"{Scheme}://{HostForUrl}{(Port == (IsHttps ? 443 : 80) ? "" : ":" + Port)}";

    private string HostForUrl => Host.Contains(':') && !Host.StartsWith('[') ? $"[{Host}]" : Host;

    /// <summary>
    /// The host field may hold a plain name ("fw.lan"), a name with a port setting, or a full address
    /// ("https://fw.lan:8443/login"). Without a scheme, https is used (except for the usual http ports 80, 8080, 8000).
    /// </summary>
    public static WebTarget Parse(string host, int port)
    {
        host = (host ?? "").Trim();
        if (host.Length == 0) throw new ArgumentException("The address of the web host is empty.");
        if (!host.Contains("://", StringComparison.Ordinal))
        {
            var scheme = port is 80 or 8080 or 8000 ? "http" : "https";
            host = $"{scheme}://{host}";
            if (port > 0 && !HasPort(host)) host += ":" + port;
        }
        if (!Uri.TryCreate(host, UriKind.Absolute, out var uri) || (uri.Scheme != "http" && uri.Scheme != "https") || uri.Host.Length == 0)
            throw new ArgumentException("The address of the web host is not a valid http or https address.");
        if (uri.UserInfo.Length > 0) throw new ArgumentException("Put the user name and password into the host's login fields, not into the address.");
        var path = uri.PathAndQuery;
        if (path.Length == 0 || path[0] != '/') path = "/" + path;
        return new WebTarget(uri.Scheme, uri.IdnHost, uri.Port, path);
    }

    public static WebTarget Parse(ServerEntry server) => Parse(server.Host, server.Port);

    private static bool HasPort(string url) => Uri.TryCreate(url, UriKind.Absolute, out var u) && !u.IsDefaultPort;
}
