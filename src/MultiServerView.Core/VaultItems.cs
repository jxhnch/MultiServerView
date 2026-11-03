namespace MultiServerView.Core;

/// <summary>Everything that lives in the vault, is addressed by id and is merged by "newest state wins" during sync.</summary>
public interface IVaultItem
{
    string Id { get; set; }
    DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>Reusable credentials (username + password or key) shared by several hosts.</summary>
public sealed class IdentityEntry : IVaultItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
    public AuthMethod Auth { get; set; } = AuthMethod.Password;
    public string KeyId { get; set; } = "";
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>A saved command that can be sent to the terminal with one click.</summary>
public sealed class SnippetEntry : IVaultItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string Command { get; set; } = "";

    /// <summary>true = Enter is sent right away as well.</summary>
    public bool AutoRun { get; set; } = true;

    public DateTimeOffset UpdatedAt { get; set; }
}

public enum ForwardType
{
    /// <summary>Local port -> target behind the SSH server (ssh -L).</summary>
    Local,

    /// <summary>Port on the SSH server -> target on this PC (ssh -R).</summary>
    Remote,

    /// <summary>Local SOCKS5 proxy through the SSH server (ssh -D).</summary>
    Dynamic
}

/// <summary>A port forward through an SSH host.</summary>
public sealed class PortForwardEntry : IVaultItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string ServerId { get; set; } = "";
    public ForwardType Type { get; set; } = ForwardType.Local;
    public string BindHost { get; set; } = "127.0.0.1";
    public int BindPort { get; set; }
    public string DestHost { get; set; } = "";
    public int DestPort { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>
/// A connection to a Proxmox VE server. The API token (like passwords) is only stored encrypted in the vault.
/// </summary>
public sealed class ProxmoxEntry : IVaultItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";

    /// <summary>Address with port, e.g. https://pve.example.com:8006</summary>
    public string Url { get; set; } = "";

    /// <summary>Token id in the format user@realm!tokenname</summary>
    public string TokenId { get; set; } = "";

    public string TokenSecret { get; set; } = "";

    /// <summary>
    /// SHA256 of the server certificate (pinned) if it is self-signed. Empty = the certificate must be trusted by the system.
    /// </summary>
    public string CertFingerprint { get; set; } = "";

    public DateTimeOffset UpdatedAt { get; set; }
}
