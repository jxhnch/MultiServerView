namespace MultiServerView.Core;

public enum Protocol
{
    Ssh,
    Rdp,
    Vnc
}

public enum AuthMethod
{
    Password,
    Key
}

/// <summary>Ein Server-Eintrag im Vault. Wird 1:1 als JSON mit dem UI ausgetauscht (camelCase).</summary>
public sealed class ServerEntry : IVaultItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string Host { get; set; } = "";
    public int Port { get; set; }
    public Protocol Protocol { get; set; } = Protocol.Ssh;
    public string Username { get; set; } = "";

    /// <summary>Liegt nur verschlüsselt im Vault (nie im Klartext auf der Platte oder in der Cloud).</summary>
    public string Password { get; set; } = "";

    /// <summary>Nur SSH: Passwort oder Key.</summary>
    public AuthMethod Auth { get; set; } = AuthMethod.Password;

    /// <summary>Id eines <see cref="KeyEntry"/> (nur bei Auth = Key).</summary>
    public string KeyId { get; set; } = "";

    /// <summary>Optional: Verbindung läuft über diesen SSH-Host (Jump Host / Host-Chain). Der Jump-Host kann selbst wieder einen haben.</summary>
    public string JumpHostId { get; set; } = "";

    /// <summary>Optional: Zugangsdaten aus einer <see cref="IdentityEntry"/> (überschreiben Benutzer/Passwort/Key).</summary>
    public string IdentityId { get; set; } = "";

    /// <summary>Erkanntes Betriebssystem ("debian", "ubuntu", "windows", ...). Wird nach dem ersten SSH-Login gesetzt.</summary>
    public string Os { get; set; } = "";

    public string Group { get; set; } = "";
    public List<string> Tags { get; set; } = new();
    public bool Favorite { get; set; }
    public DateTimeOffset? LastUsed { get; set; }
    public string Notes { get; set; } = "";

    /// <summary>Wird bei jeder Änderung gesetzt. Grundlage für den Cloud-Merge (neuester Stand gewinnt).</summary>
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

/// <summary>Ein privater SSH-Schlüssel im Vault ("Keychain").</summary>
public sealed class KeyEntry : IVaultItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";

    /// <summary>Privater Schlüssel im Klartext (PEM / OpenSSH). Nur im verschlüsselten Vault.</summary>
    public string PrivateKey { get; set; } = "";

    public string Passphrase { get; set; } = "";

    /// <summary>Anzeigehilfe, z. B. "ED25519", "RSA".</summary>
    public string Kind { get; set; } = "";

    public DateTimeOffset UpdatedAt { get; set; }
}
