namespace MultiServerView.Core;

/// <summary>Alles, was im Vault liegt, per Id angesprochen wird und beim Cloud-Merge nach "neuester Stand gewinnt" behandelt wird.</summary>
public interface IVaultItem
{
    string Id { get; set; }
    DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>Wiederverwendbare Zugangsdaten (Benutzer + Passwort oder Key), die mehrere Hosts teilen.</summary>
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

/// <summary>Gespeicherter Befehl, den man mit einem Klick ins Terminal schicken kann.</summary>
public sealed class SnippetEntry : IVaultItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string Command { get; set; } = "";

    /// <summary>true = Enter wird gleich mitgesendet.</summary>
    public bool AutoRun { get; set; } = true;

    public DateTimeOffset UpdatedAt { get; set; }
}

public enum ForwardType
{
    /// <summary>Lokaler Port -> Ziel hinter dem SSH-Server (ssh -L).</summary>
    Local,

    /// <summary>Port auf dem SSH-Server -> Ziel auf diesem PC (ssh -R).</summary>
    Remote,

    /// <summary>Lokaler SOCKS5-Proxy über den SSH-Server (ssh -D).</summary>
    Dynamic
}

/// <summary>Eine Port-Weiterleitung über einen SSH-Host.</summary>
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
