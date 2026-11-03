namespace MultiServerView.Core.Ssh;

/// <summary>Erkennt das Betriebssystem aus /etc/os-release (+ "---" + uname -s).</summary>
public static class OsDetector
{
    private static readonly Dictionary<string, string> Known = new(StringComparer.OrdinalIgnoreCase)
    {
        ["debian"] = "debian", ["ubuntu"] = "ubuntu", ["raspbian"] = "raspberrypi", ["centos"] = "centos",
        ["fedora"] = "fedora", ["rhel"] = "redhat", ["rocky"] = "rocky", ["almalinux"] = "almalinux",
        ["arch"] = "arch", ["manjaro"] = "manjaro", ["alpine"] = "alpine", ["opensuse"] = "opensuse",
        ["opensuse-leap"] = "opensuse", ["opensuse-tumbleweed"] = "opensuse", ["sles"] = "opensuse",
        ["amzn"] = "amazon", ["nixos"] = "nixos", ["kali"] = "kali", ["linuxmint"] = "mint", ["pop"] = "pop",
        ["gentoo"] = "gentoo", ["void"] = "void", ["freebsd"] = "freebsd", ["openbsd"] = "openbsd",
        ["proxmox"] = "proxmox"
    };

    public static string Parse(string output)
    {
        // Windows-Server mit OpenSSH: cmd.exe versteht "cat" und "uname" nicht.
        if (output.Contains("not recognized as an internal or external command", StringComparison.OrdinalIgnoreCase)
            || output.Contains("nicht als interner oder externer Befehl", StringComparison.OrdinalIgnoreCase)
            || output.Contains("is not recognized as the name of a cmdlet", StringComparison.OrdinalIgnoreCase))
            return "windows";

        string? id = null;
        string? idLike = null;
        var uname = "";
        var afterSeparator = false;

        foreach (var raw in output.Split('\n'))
        {
            var line = raw.Trim();
            if (line == "---")
            {
                afterSeparator = true;
                continue;
            }
            if (afterSeparator)
            {
                if (line.Length > 0 && uname.Length == 0) uname = line;
                continue;
            }
            if (line.StartsWith("ID=", StringComparison.Ordinal)) id = Unquote(line[3..]);
            else if (line.StartsWith("ID_LIKE=", StringComparison.Ordinal)) idLike = Unquote(line[8..]);
        }

        if (id != null && Known.TryGetValue(id, out var known)) return known;
        if (idLike != null)
            foreach (var part in idLike.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                if (Known.TryGetValue(part, out var like)) return like;

        return uname.ToLowerInvariant() switch
        {
            "linux" => "linux",
            "darwin" => "macos",
            "freebsd" => "freebsd",
            "openbsd" => "openbsd",
            "" => "",
            _ => "linux"
        };
    }

    private static string Unquote(string value) => value.Trim().Trim('"', '\'');
}
