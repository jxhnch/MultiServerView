namespace MultiServerView.Core;

public enum Protocol
{
    Ssh,
    Rdp,
    Vnc
}

/// <summary>Ein Server-Eintrag im Vault. Wird 1:1 als JSON mit dem UI ausgetauscht (camelCase).</summary>
public sealed class ServerEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string Host { get; set; } = "";
    public int Port { get; set; }
    public Protocol Protocol { get; set; } = Protocol.Ssh;
    public string Username { get; set; } = "";

    /// <summary>Liegt nur verschlüsselt im Vault (nie im Klartext auf der Platte oder in der Cloud).</summary>
    public string Password { get; set; } = "";

    public string Group { get; set; } = "";
    public List<string> Tags { get; set; } = new();
    public bool Favorite { get; set; }
    public DateTimeOffset? LastUsed { get; set; }
    public string Notes { get; set; } = "";

    public static int DefaultPort(Protocol protocol) => protocol switch
    {
        Protocol.Ssh => 22,
        Protocol.Rdp => 3389,
        Protocol.Vnc => 5900,
        _ => 0
    };
}
