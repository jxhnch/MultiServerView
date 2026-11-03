using System.Net.Sockets;
using System.Text;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace MultiServerView.Core.Ssh;

public sealed class SshConnectException : Exception
{
    public SshConnectException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>Shared building blocks for everything that sets up an SSH connection (terminal, SFTP, port forwarding).</summary>
public static class SshConnector
{
    /// <summary>Global connection settings (from the app settings).</summary>
    public static TimeSpan ConnectTimeout { get; set; } = TimeSpan.FromSeconds(15);
    public static TimeSpan KeepAlive { get; set; } = TimeSpan.FromSeconds(30);

    public static ConnectionInfo BuildConnectionInfo(ServerEntry server, KeyEntry? key, string? hostOverride = null, int? portOverride = null)
    {
        var user = string.IsNullOrWhiteSpace(server.Username) ? Environment.UserName : server.Username;
        var methods = new List<AuthenticationMethod>();

        if (server.Auth == AuthMethod.Key)
        {
            if (key == null || string.IsNullOrWhiteSpace(key.PrivateKey))
                throw new SshConnectException("No SSH key is selected for this host, or the key is missing.");
            methods.Add(new PrivateKeyAuthenticationMethod(user, LoadKey(key)));
        }
        else
        {
            methods.Add(new PasswordAuthenticationMethod(user, server.Password));

            // Many servers only accept the password via "keyboard-interactive".
            var kbd = new KeyboardInteractiveAuthenticationMethod(user);
            kbd.AuthenticationPrompt += (_, e) =>
            {
                foreach (var prompt in e.Prompts) prompt.Response = server.Password;
            };
            methods.Add(kbd);
        }

        return new ConnectionInfo(hostOverride ?? server.Host, portOverride ?? (server.Port > 0 ? server.Port : 22), user, methods.ToArray())
        {
            Timeout = ConnectTimeout
        };
    }

    /// <summary>
    /// Attaches the host key check (trust on first use) to a client.
    /// After a failure, the returned delegate provides the understandable warning (or null).
    /// </summary>
    public static Func<string?> HookHostKey(BaseClient client, ServerEntry server,
        Func<string, int, string, HostKeyTrust> check, Action<string>? info)
    {
        string? error = null;
        client.HostKeyReceived += (_, e) =>
        {
            var fp = "SHA256:" + e.FingerPrintSHA256;
            switch (check(server.Host, server.Port, fp))
            {
                case HostKeyTrust.Trusted:
                    e.CanTrust = true;
                    break;
                case HostKeyTrust.NewlyTrusted:
                    e.CanTrust = true;
                    info?.Invoke($"New host key stored ({e.HostKeyName}) {fp}");
                    break;
                default:
                    e.CanTrust = false;
                    error =
                        $"WARNING: The host key of {server.Host} has changed ({fp}). " +
                        "This can be an attack, or the server was set up again. " +
                        "If you expect this: use \"Reset host key\" in the host panel.";
                    break;
            }
        };
        return () => error;
    }

    /// <summary>Reads a private key (OpenSSH / PEM). Throws <see cref="SshConnectException"/> on errors.</summary>
    public static PrivateKeyFile LoadKey(KeyEntry key)
    {
        try
        {
            using var ms = new MemoryStream(Encoding.UTF8.GetBytes(key.PrivateKey.Trim() + "\n"));
            return string.IsNullOrEmpty(key.Passphrase) ? new PrivateKeyFile(ms) : new PrivateKeyFile(ms, key.Passphrase);
        }
        catch (Exception ex) when (ex is SshException or InvalidOperationException or ArgumentException or FormatException
                                       or System.Security.Cryptography.CryptographicException)
        {
            throw new SshConnectException("The SSH key could not be read (wrong passphrase or unsupported format).", ex);
        }
    }

    public static string Describe(Exception ex) => ex switch
    {
        SshAuthenticationException => "Authentication failed (wrong username, password or key).",
        SshOperationTimeoutException => "Connection timed out.",
        SocketException se => se.SocketErrorCode switch
        {
            SocketError.ConnectionRefused => "Connection refused (is SSH running on this port?).",
            SocketError.HostNotFound or SocketError.NoData => "Host not found.",
            SocketError.TimedOut => "Connection timed out.",
            _ => "Network error: " + se.Message
        },
        SshConnectionException => "Connection failed: " + ex.Message,
        _ => ex.Message
    };
}
