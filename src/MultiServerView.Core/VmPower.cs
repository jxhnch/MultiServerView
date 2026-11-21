namespace MultiServerView.Core;

/// <summary>Power state of a VM as the app shows it.</summary>
public static class VmPower
{
    public const string Running = "running";
    public const string Stopped = "stopped";
    public const string Paused = "paused";
    public const string Unknown = "unknown";

    /// <summary>Proxmox: running, stopped, paused (qemu), suspended.</summary>
    public static string FromProxmox(string status) => status.ToLowerInvariant() switch
    {
        "running" => Running,
        "stopped" => Stopped,
        "paused" or "suspended" or "prelaunch" => Paused,
        _ => Unknown
    };

    /// <summary>"connection:node:vmid" of an imported Proxmox host.</summary>
    public static bool TryParseProxmoxRef(string reference, out string connection, out string node, out long vmId)
    {
        connection = node = "";
        vmId = 0;
        var parts = reference.Split(':');
        if (parts.Length != 3 || !long.TryParse(parts[2], out vmId)) return false;
        connection = parts[0];
        node = parts[1];
        return connection.Length > 0 && node.Length > 0;
    }
}
