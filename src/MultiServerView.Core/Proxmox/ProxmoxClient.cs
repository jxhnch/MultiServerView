using System.Net;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MultiServerView.Core.Proxmox;

public sealed class ProxmoxException : Exception
{
    public ProxmoxException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>A VM or a container from <c>/cluster/resources</c>.</summary>
public sealed record ProxmoxVm(string Node, long VmId, string Type, string Name, string Status, string Pool, IReadOnlyList<string> Tags, bool Template)
{
    public bool IsContainer => Type == "lxc";
}

/// <summary>What the app needs to know about a guest to turn it into a host.</summary>
public sealed record ProxmoxGuest(string? Ip, Protocol Protocol, string Os);

/// <summary>Certificate of a Proxmox server, as shown on the first connection.</summary>
public sealed record ProxmoxCertificate(string Fingerprint, string Subject, DateTime NotAfter, bool TrustedBySystem);

/// <summary>
/// Minimal client for the Proxmox VE API (read-only): check the connection, list VMs and containers, determine IP and operating system
/// of a guest. Authentication via API token (<c>Authorization: PVEAPIToken=…</c>), so without a ticket or CSRF token.
/// </summary>
public sealed class ProxmoxClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly string _baseUrl;

    public ProxmoxClient(HttpClient http, string url, string tokenId, string tokenSecret, bool ownsHttp = false)
    {
        _http = http;
        _ownsHttp = ownsHttp;
        _baseUrl = NormalizeUrl(url) + "/api2/json";
        _http.DefaultRequestHeaders.Authorization = null;
        _http.DefaultRequestHeaders.Remove("Authorization");
        _http.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", $"PVEAPIToken={tokenId.Trim()}={tokenSecret.Trim()}");
    }

    /// <summary>Builds a client for a saved connection (with a pinned certificate if one is stored).</summary>
    public static ProxmoxClient Create(ProxmoxEntry entry)
    {
        var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (_, cert, _, errors) =>
                errors == SslPolicyErrors.None
                || (entry.CertFingerprint.Length > 0 && cert != null && string.Equals(FingerprintOf(cert), Clean(entry.CertFingerprint), StringComparison.OrdinalIgnoreCase))
        };
        var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };
        return new ProxmoxClient(http, entry.Url, entry.TokenId, entry.TokenSecret, ownsHttp: true);
    }

    /// <summary>
    /// Reads the certificate of the server without signing in. <see cref="ProxmoxCertificate.TrustedBySystem"/> is true if
    /// Windows already trusts it (e.g. Let's Encrypt) - then no pinned fingerprint is needed.
    /// </summary>
    public static async Task<ProxmoxCertificate> ProbeAsync(string url, CancellationToken ct = default)
    {
        X509Certificate2? seen = null;
        var trusted = false;
        var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (_, cert, _, errors) =>
            {
                if (cert != null) seen = new X509Certificate2(cert);
                trusted = errors == SslPolicyErrors.None;
                return true; // only look, send nothing
            }
        };
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
        try
        {
            using var _ = await http.GetAsync(NormalizeUrl(url) + "/api2/json/version", ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            throw new ProxmoxException(Describe(ex, url), ex);
        }
        if (seen == null) throw new ProxmoxException("The server did not send a certificate (check https:// and port 8006).");
        return new ProxmoxCertificate(Group(FingerprintOf(seen)), seen.Subject, seen.NotAfter, trusted);
    }

    public static string NormalizeUrl(string url)
    {
        var u = url.Trim().TrimEnd('/');
        if (u.Length == 0) throw new ProxmoxException("Please enter the address of the Proxmox server, e.g. https://pve.example.com:8006");
        if (!u.Contains("://", StringComparison.Ordinal)) u = "https://" + u;
        if (!Uri.TryCreate(u, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            throw new ProxmoxException("The address must start with https://.");
        // Without a port we use the Proxmox default: 8006.
        return uri.IsDefaultPort ? $"https://{uri.Host}:8006" : $"https://{uri.Host}:{uri.Port}";
    }

    // ---------------------------------------------------------------------------------------------------- Queries

    /// <summary>Checks address, certificate and token. Returns the Proxmox version.</summary>
    public async Task<string> VersionAsync(CancellationToken ct = default)
    {
        var data = await GetAsync("/version", ct);
        return data?["version"]?.GetValue<string>() ?? "";
    }

    /// <summary>All VMs and containers the token may see (templates are marked).</summary>
    public async Task<IReadOnlyList<ProxmoxVm>> ListVmsAsync(CancellationToken ct = default)
    {
        var data = await GetAsync("/cluster/resources?type=vm", ct);
        var list = new List<ProxmoxVm>();
        if (data is JsonArray items)
        {
            foreach (var item in items)
            {
                var type = Str(item, "type");
                if (type is not ("qemu" or "lxc")) continue;
                if (!long.TryParse(Raw(item, "vmid"), out var vmid)) continue;
                var tags = Str(item, "tags").Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                list.Add(new ProxmoxVm(Str(item, "node"), vmid, type, Str(item, "name") is { Length: > 0 } n ? n : $"{type}-{vmid}",
                    Str(item, "status"), Str(item, "pool"), tags, Raw(item, "template") == "1"));
            }
        }
        return list.OrderBy(v => v.Pool, StringComparer.OrdinalIgnoreCase).ThenBy(v => v.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Determines the operating system, protocol and - if possible - the IP address of a guest.</summary>
    public async Task<ProxmoxGuest> DescribeAsync(ProxmoxVm vm, CancellationToken ct = default)
    {
        JsonNode? config = null;
        try { config = await GetAsync($"/nodes/{vm.Node}/{vm.Type}/{vm.VmId}/config", ct); }
        catch (ProxmoxException) { /* without the configuration we know less, importing still works */ }

        var ostype = Str(config, "ostype").ToLowerInvariant();
        var windows = !vm.IsContainer && IsWindowsType(ostype);
        var os = windows ? "windows" : vm.IsContainer ? ContainerOs(ostype) : "";
        var protocol = windows ? Protocol.Rdp : Protocol.Ssh;

        string? ip = null;
        var running = vm.Status == "running";
        if (running)
        {
            try { ip = vm.IsContainer ? await ContainerIpAsync(vm, ct) : await AgentIpAsync(vm, ct); }
            catch (ProxmoxException) { /* no guest agent or similar */ }
        }
        ip ??= StaticIp(config, vm.IsContainer);
        return new ProxmoxGuest(ip, protocol, os);
    }

    private async Task<string?> AgentIpAsync(ProxmoxVm vm, CancellationToken ct)
    {
        var data = await GetAsync($"/nodes/{vm.Node}/qemu/{vm.VmId}/agent/network-get-interfaces", ct);
        var interfaces = (data?["result"] as JsonArray) ?? (data as JsonArray);
        if (interfaces == null) return null;

        foreach (var nic in interfaces)
        {
            var name = Str(nic, "name");
            if (name == "lo" || IsVirtualNic(name)) continue;
            if (nic?["ip-addresses"] is not JsonArray addrs) continue;
            foreach (var a in addrs)
                if (Str(a, "ip-address-type") == "ipv4" && UsableIpv4(Str(a, "ip-address"))) return Str(a, "ip-address");
        }
        return null;
    }

    private async Task<string?> ContainerIpAsync(ProxmoxVm vm, CancellationToken ct)
    {
        var data = await GetAsync($"/nodes/{vm.Node}/lxc/{vm.VmId}/interfaces", ct);
        if (data is not JsonArray nics) return null;
        foreach (var nic in nics)
        {
            if (Str(nic, "name") == "lo") continue;
            var ip = Str(nic, "inet").Split('/')[0];
            if (UsableIpv4(ip)) return ip;
        }
        return null;
    }

    // ---------------------------------------------------------------------------------------------------- Helpers (public for tests)

    public static bool IsWindowsType(string ostype) =>
        ostype.StartsWith("win", StringComparison.Ordinal) || ostype.StartsWith("w2k", StringComparison.Ordinal)
        || ostype is "wxp" or "wvista";

    public static string ContainerOs(string ostype) => ostype switch
    {
        "debian" or "ubuntu" or "centos" or "fedora" or "alpine" or "opensuse" or "nixos" => ostype,
        "archlinux" => "arch",
        "" or "unmanaged" => "",
        _ => "linux"
    };

    /// <summary>Static IP from the VM/container configuration (<c>ip=192.168.1.5/24</c>, <c>ipconfig0</c>); DHCP yields nothing.</summary>
    public static string? StaticIp(JsonNode? config, bool container)
    {
        if (config is not JsonObject obj) return null;
        foreach (var (key, value) in obj)
        {
            var isNic = container ? key.StartsWith("net", StringComparison.Ordinal) : key.StartsWith("ipconfig", StringComparison.Ordinal);
            if (!isNic) continue;
            foreach (var part in (value?.GetValue<string>() ?? "").Split(','))
            {
                var kv = part.Split('=', 2);
                if (kv.Length == 2 && kv[0] == "ip")
                {
                    var ip = kv[1].Split('/')[0];
                    if (UsableIpv4(ip)) return ip;
                }
            }
        }
        return null;
    }

    public static bool UsableIpv4(string ip) =>
        IPAddress.TryParse(ip, out var a) && a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
        && !ip.StartsWith("127.", StringComparison.Ordinal) && !ip.StartsWith("169.254.", StringComparison.Ordinal) && ip != "0.0.0.0";

    private static bool IsVirtualNic(string name) =>
        name.StartsWith("docker", StringComparison.OrdinalIgnoreCase) || name.StartsWith("br-", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("veth", StringComparison.OrdinalIgnoreCase) || name.StartsWith("virbr", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("cni", StringComparison.OrdinalIgnoreCase) || name.StartsWith("flannel", StringComparison.OrdinalIgnoreCase);

    public static string FingerprintOf(X509Certificate cert) => Convert.ToHexString(SHA256.HashData(cert.GetRawCertData()));

    /// <summary>"AB:CD:…" -> "ABCD…"</summary>
    public static string Clean(string fingerprint) => fingerprint.Replace(":", "").Replace(" ", "").Trim().ToUpperInvariant();

    /// <summary>"ABCD…" -> "AB:CD:…" (the way Proxmox shows it in its interface).</summary>
    public static string Group(string hex) => string.Join(':', Enumerable.Range(0, hex.Length / 2).Select(i => hex.Substring(i * 2, 2)));

    // ---------------------------------------------------------------------------------------------------- HTTP

    private async Task<JsonNode?> GetAsync(string path, CancellationToken ct)
    {
        HttpResponseMessage response;
        try
        {
            response = await _http.GetAsync(_baseUrl + path, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            throw new ProxmoxException(Describe(ex, _baseUrl), ex);
        }

        using (response)
        {
            if (response.StatusCode == HttpStatusCode.Unauthorized)
                throw new ProxmoxException("Sign-in refused: the token ID or secret is wrong (format: user@realm!tokenname).");
            if (response.StatusCode == HttpStatusCode.Forbidden)
                throw new ProxmoxException("No permission. The token lacks a role, e.g. PVEAuditor (Datacenter → Permissions).");
            if (!response.IsSuccessStatusCode)
            {
                var reason = response.ReasonPhrase;
                throw new ProxmoxException($"Proxmox answered with {(int)response.StatusCode} {reason}.");
            }

            var text = await response.Content.ReadAsStringAsync(ct);
            try
            {
                return JsonNode.Parse(text)?["data"];
            }
            catch (JsonException ex)
            {
                throw new ProxmoxException("This is not a Proxmox API (the answer is not JSON). Are address and port correct?", ex);
            }
        }
    }

    private static string Describe(Exception ex, string url)
    {
        var inner = ex;
        while (inner.InnerException != null) inner = inner.InnerException;
        var text = inner.Message;
        if (text.Contains("certificate", StringComparison.OrdinalIgnoreCase) || text.Contains("SSL", StringComparison.Ordinal) || text.Contains("remote certificate", StringComparison.OrdinalIgnoreCase))
            return "The certificate of the server is not trusted. It has changed or was not confirmed yet (edit the connection → check the certificate).";
        if (ex is TaskCanceledException) return "Timeout: " + url + " does not answer.";
        return "No connection to " + url + ": " + text;
    }

    private static string Str(JsonNode? node, string name) => node?[name] is JsonValue v && v.TryGetValue<string>(out var s) ? s : Raw(node, name);

    private static string Raw(JsonNode? node, string name) => node?[name] is { } n ? n.ToString().Trim('"') : "";

    public void Dispose()
    {
        if (_ownsHttp) _http.Dispose();
    }
}
