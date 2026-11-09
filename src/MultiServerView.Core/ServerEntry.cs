namespace MultiServerView.Core;

public enum Protocol
{
    Ssh,
    Rdp,
    Vnc,

    /// <summary>Remote support through the TeamViewer client; Host = TeamViewer ID.</summary>
    Teamviewer
}

public enum AuthMethod
{
    Password,
    Key
}

/// <summary>A server entry in the vault. Exchanged 1:1 as JSON with the UI (camelCase).</summary>
public sealed class ServerEntry : IVaultItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string Host { get; set; } = "";
    public int Port { get; set; }
    public Protocol Protocol { get; set; } = Protocol.Ssh;
    public string Username { get; set; } = "";

    /// <summary>Only stored encrypted in the vault (never in plain text on disk or in the synced vault).</summary>
    public string Password { get; set; } = "";

    /// <summary>SSH only: password or key.</summary>
    public AuthMethod Auth { get; set; } = AuthMethod.Password;

    /// <summary>Id of a <see cref="KeyEntry"/> (only with Auth = Key).</summary>
    public string KeyId { get; set; } = "";

    /// <summary>Optional: the connection goes through this SSH host (jump host / host chain). The jump host can have one itself.</summary>
    public string JumpHostId { get; set; } = "";

    /// <summary>Optional: credentials from an <see cref="IdentityEntry"/> (override username/password/key).</summary>
    public string IdentityId { get; set; } = "";

    /// <summary>Detected operating system ("debian", "ubuntu", "windows", ...). Set after the first SSH login.</summary>
    public string Os { get; set; } = "";

    /// <summary>
    /// Top level for sorting, e.g. a teacher or a customer: the UI shows only one workspace at a time.
    /// Empty = in no workspace. Lives in the same vault, so it is synced too.
    /// </summary>
    public string Workspace { get; set; } = "";

    /// <summary>
    /// Origin if the host was imported from Proxmox: "connection id:node:VM number". This lets the import recognize
    /// which VMs already exist. Empty = created by hand.
    /// </summary>
    public string ProxmoxRef { get; set; } = "";

    /// <summary>
    /// Origin if the host was imported from VMware Workstation: the path of the .vmx file in lowercase.
    /// Empty = not from VMware.
    /// </summary>
    public string VmwareRef { get; set; } = "";

    public string Group { get; set; } = "";
    public List<string> Tags { get; set; } = new();
    public bool Favorite { get; set; }
    public DateTimeOffset? LastUsed { get; set; }
    public string Notes { get; set; } = "";

    /// <summary>Set on every change. Basis for the sync merge (newest state wins).</summary>
    public DateTimeOffset UpdatedAt { get; set; }

    public ServerEntry Clone() => (ServerEntry)MemberwiseClone();

    public static int DefaultPort(Protocol protocol) => protocol switch
    {
        Protocol.Ssh => 22,
        Protocol.Rdp => 3389,
        Protocol.Vnc => 5900,
        _ => 0
    };
}

/// <summary>A private SSH key in the vault ("Keychain").</summary>
public sealed class KeyEntry : IVaultItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";

    /// <summary>Private key in plain text (PEM / OpenSSH). Only inside the encrypted vault.</summary>
    public string PrivateKey { get; set; } = "";

    public string Passphrase { get; set; } = "";

    /// <summary>Display helper, e.g. "ED25519", "RSA".</summary>
    public string Kind { get; set; } = "";

    public DateTimeOffset UpdatedAt { get; set; }
}
