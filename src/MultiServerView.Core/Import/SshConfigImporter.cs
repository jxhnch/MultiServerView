namespace MultiServerView.Core.Import;

/// <summary>
/// Reads an OpenSSH configuration (~/.ssh/config). Every concrete "Host" entry becomes an SSH host.
/// Taken over: HostName, User, Port, ProxyJump (as a jump host if the jump host is imported as well).
/// Not taken over: wildcard entries (Host *), Match blocks, IdentityFile (import keys individually), ProxyCommand.
/// </summary>
public static class SshConfigImporter
{
    private sealed class Block
    {
        public string Alias = "";
        public string? HostName, User, ProxyJump;
        public int? Port;
    }

    public static ImportResult Parse(string text)
    {
        var blocks = new List<Block>();
        var defaults = new Block();     // "Host *"
        Block? current = null;
        var inDefaults = false;
        var skipped = 0;

        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;

            var (key, value) = Split(line);
            if (key.Equals("Host", StringComparison.OrdinalIgnoreCase))
            {
                var aliases = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                var concrete = aliases.FirstOrDefault(a => !a.Contains('*') && !a.Contains('?') && !a.StartsWith('!'));
                inDefaults = aliases.Contains("*");
                if (concrete != null)
                {
                    current = new Block { Alias = concrete };
                    blocks.Add(current);
                }
                else
                {
                    current = null;
                    if (!inDefaults) skipped++;
                }
                continue;
            }
            if (key.Equals("Match", StringComparison.OrdinalIgnoreCase))
            {
                current = null;
                inDefaults = false;
                skipped++;
                continue;
            }

            var target = current ?? (inDefaults ? defaults : null);
            if (target == null) continue;
            switch (key.ToLowerInvariant())
            {
                case "hostname": target.HostName ??= value; break;
                case "user": target.User ??= value; break;
                case "port": if (int.TryParse(value, out var p)) target.Port ??= p; break;
                case "proxyjump": target.ProxyJump ??= value.Split(',')[0].Trim(); break;
            }
        }

        var servers = blocks.Select(b => new ServerEntry
        {
            Name = b.Alias,
            Host = b.HostName ?? b.Alias,
            Port = b.Port ?? defaults.Port ?? 22,
            Protocol = Protocol.Ssh,
            Username = b.User ?? defaults.User ?? ""
        }).ToList();

        // ProxyJump: point to another imported host (by alias or HostName)
        for (var i = 0; i < blocks.Count; i++)
        {
            var jump = blocks[i].ProxyJump ?? defaults.ProxyJump;
            if (string.IsNullOrWhiteSpace(jump) || jump.Equals("none", StringComparison.OrdinalIgnoreCase)) continue;
            var name = jump.Contains('@') ? jump[(jump.IndexOf('@') + 1)..] : jump;
            name = name.Contains(':') ? name[..name.LastIndexOf(':')] : name;
            var match = servers.FirstOrDefault(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                        ?? servers.FirstOrDefault(s => s.Host.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (match != null && match != servers[i]) servers[i].JumpHostId = match.Id;
        }

        return new ImportResult(servers, skipped);
    }

    private static (string key, string value) Split(string line)
    {
        var i = line.IndexOfAny(new[] { ' ', '\t', '=' });
        if (i < 0) return (line, "");
        return (line[..i], line[(i + 1)..].Trim().TrimStart('=').Trim().Trim('"'));
    }
}
