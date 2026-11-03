using System.Globalization;
using System.Text.RegularExpressions;

namespace MultiServerView.Core.Vmware;

/// <summary>A VM from the VMware Workstation library (read from its .vmx file).</summary>
public sealed record VmwareVm(string VmxPath, string Name, string GuestOs, string Mac, string ConnectionType, bool Running, string? Ip)
{
    public override string ToString() => Name;
}

/// <summary>Last known DHCP entry of VMware (vmnetdhcp.leases) for a MAC address.</summary>
public sealed record VmwareLease(string Ip, DateTimeOffset Ends);

/// <summary>
/// Reads what VMware Workstation leaves on disk: the list of VMs (inventory.vmls), the settings of every VM (.vmx)
/// and the addresses given out by the internal DHCP server (vmnetdhcp.leases). VMware does not have to be running for this.
/// </summary>
public static class VmwareLibrary
{
    public static string DefaultInventoryPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VMware", "inventory.vmls");

    public static string DefaultVmFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Virtual Machines");

    public static string DefaultLeasesPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "VMware", "vmnetdhcp.leases");

    // ------------------------------------------------------------------------------------------------ Finding files

    /// <summary>
    /// All .vmx files: those of the library (inventory.vmls), plus everything in the given folders (including subfolders).
    /// Both together because the library does not contain every VM (e.g. never opened ones or ones copied by hand).
    /// </summary>
    public static IReadOnlyList<string> FindVmx(string? inventoryPath, IEnumerable<string> folders)
    {
        var found = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (inventoryPath != null && File.Exists(inventoryPath))
        {
            try
            {
                foreach (var path in ParseInventory(File.ReadAllText(inventoryPath)))
                    if (File.Exists(path)) found[Path.GetFullPath(path)] = path;
            }
            catch (IOException) { /* inventory locked right now: the folder search finds the VMs anyway */ }
        }

        foreach (var folder in folders)
        {
            if (!Directory.Exists(folder)) continue;
            try
            {
                foreach (var path in Directory.EnumerateFiles(folder, "*.vmx", SearchOption.AllDirectories))
                    found[Path.GetFullPath(path)] = path;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // skip folders without access
            }
        }
        return found.Values.OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Paths of the .vmx files from the inventory (vmlist2.config = "C:\...\x.vmx"). Empty entries are skipped.</summary>
    public static IEnumerable<string> ParseInventory(string text)
    {
        foreach (var (key, value) in ParseKeyValues(text))
            if (Regex.IsMatch(key, @"^vmlist\d+\.config$", RegexOptions.IgnoreCase) && value.Length > 0)
                yield return value;
    }

    // ------------------------------------------------------------------------------------------------ .vmx

    /// <summary>Reads a .vmx file. <paramref name="running"/> says whether the VM is running right now (that is not in the file).</summary>
    public static VmwareVm FromVmx(string path, string text, bool running = false, IReadOnlyDictionary<string, VmwareLease>? leases = null)
    {
        var v = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in ParseKeyValues(text)) v[key] = value;

        string Get(string key) => v.TryGetValue(key, out var s) ? s : "";

        var name = Get("displayName");
        if (name.Length == 0) name = Path.GetFileNameWithoutExtension(path);

        // First network card that really exists (if "present" is missing, it counts as present).
        var nic = Enumerable.Range(0, 10).FirstOrDefault(i => Get($"ethernet{i}.present").Length > 0
                                                             ? Get($"ethernet{i}.present").Equals("TRUE", StringComparison.OrdinalIgnoreCase)
                                                             : Get($"ethernet{i}.connectionType").Length > 0 || Get($"ethernet{i}.generatedAddress").Length > 0, -1);
        var mac = "";
        var connection = "";
        if (nic >= 0)
        {
            mac = NormalizeMac(Get($"ethernet{nic}.address") is { Length: > 0 } fixedMac ? fixedMac : Get($"ethernet{nic}.generatedAddress"));
            connection = Get($"ethernet{nic}.connectionType").ToLowerInvariant();
        }

        string? ip = null;
        if (mac.Length > 0 && leases != null && leases.TryGetValue(mac, out var lease)) ip = lease.Ip;
        return new VmwareVm(path, name, Get("guestOS"), mac, connection, running, ip);
    }

    /// <summary>"00:0C:29:AB:CD:EF" / "00-0c-…" -> "00:0c:29:ab:cd:ef" (for comparison).</summary>
    public static string NormalizeMac(string mac) => mac.Trim().Replace('-', ':').ToLowerInvariant();

    /// <summary>Operating system of the VM (guestOS = "ubuntu-64") -> protocol and operating system id for the icon.</summary>
    public static (Protocol Protocol, string Os) MapGuest(string guestOs)
    {
        var g = guestOs.ToLowerInvariant();
        if (g.StartsWith("win", StringComparison.Ordinal)) return (Protocol.Rdp, "windows");

        var os = g switch
        {
            _ when g.StartsWith("ubuntu", StringComparison.Ordinal) => "ubuntu",
            _ when g.StartsWith("debian", StringComparison.Ordinal) => "debian",
            _ when g.StartsWith("centos", StringComparison.Ordinal) => "centos",
            _ when g.StartsWith("rhel", StringComparison.Ordinal) => "redhat",
            _ when g.StartsWith("fedora", StringComparison.Ordinal) => "fedora",
            _ when g.StartsWith("rocky", StringComparison.Ordinal) => "rocky",
            _ when g.StartsWith("almalinux", StringComparison.Ordinal) => "almalinux",
            _ when g.StartsWith("opensuse", StringComparison.Ordinal) || g.StartsWith("sles", StringComparison.Ordinal) => "opensuse",
            _ when g.StartsWith("freebsd", StringComparison.Ordinal) => "freebsd",
            _ when g.StartsWith("darwin", StringComparison.Ordinal) => "macos",
            _ when g.Contains("linux", StringComparison.Ordinal) || g.StartsWith("oracle", StringComparison.Ordinal) => "linux",
            _ => ""
        };
        return (Protocol.Ssh, os);
    }

    // ------------------------------------------------------------------------------------------------ DHCP-Leases

    /// <summary>
    /// Reads the file of the VMware DHCP server (ISC format: "lease 192.168.40.128 { ends …; hardware ethernet 00:0c:…; }").
    /// Per MAC address the entry that expires last counts. Expired entries count too: VMware usually gives the same VM
    /// the same address again, so it is known for powered-off VMs as well.
    /// </summary>
    public static IReadOnlyDictionary<string, VmwareLease> ParseLeases(string text)
    {
        var result = new Dictionary<string, VmwareLease>();
        foreach (Match m in Regex.Matches(text, @"lease\s+(?<ip>\d+\.\d+\.\d+\.\d+)\s*\{(?<body>.*?)\}", RegexOptions.Singleline))
        {
            var body = m.Groups["body"].Value;
            var mac = Regex.Match(body, @"hardware\s+ethernet\s+(?<mac>[0-9a-fA-F:\-]+)\s*;");
            if (!mac.Success) continue;

            var ends = DateTimeOffset.MinValue;
            var e = Regex.Match(body, @"ends\s+\d+\s+(?<d>\d{4}/\d{2}/\d{2}\s+\d{2}:\d{2}:\d{2})\s*;");
            if (e.Success && DateTime.TryParseExact(e.Groups["d"].Value, "yyyy/MM/dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var dt))
                ends = new DateTimeOffset(dt, TimeSpan.Zero);

            var key = NormalizeMac(mac.Groups["mac"].Value);
            if (!result.TryGetValue(key, out var cur) || ends >= cur.Ends) result[key] = new VmwareLease(m.Groups["ip"].Value, ends);
        }
        return result;
    }

    public static IReadOnlyDictionary<string, VmwareLease> ReadLeases(string? path = null)
    {
        try
        {
            path ??= DefaultLeasesPath;
            return File.Exists(path) ? ParseLeases(File.ReadAllText(path)) : new Dictionary<string, VmwareLease>();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new Dictionary<string, VmwareLease>();
        }
    }

    // ------------------------------------------------------------------------------------------------ Textformat

    /// <summary>key = "value" lines (the inventory and .vmx have the same format). Comments and blank lines are skipped.</summary>
    public static IEnumerable<(string Key, string Value)> ParseKeyValues(string text)
    {
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim().TrimStart('\uFEFF');
            if (line.Length == 0 || line[0] == '#') continue;
            var eq = line.IndexOf('=');
            if (eq <= 0) continue;
            var key = line[..eq].Trim();
            var value = line[(eq + 1)..].Trim();
            if (value.Length >= 2 && value[0] == '"' && value[^1] == '"') value = value[1..^1];
            yield return (key, value);
        }
    }
}
