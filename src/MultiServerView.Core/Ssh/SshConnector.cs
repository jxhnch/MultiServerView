using System.Net.Sockets;
using System.Text;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace MultiServerView.Core.Ssh;

public sealed class SshConnectException : Exception
{
    public SshConnectException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>Gemeinsame Bausteine für alles, was eine SSH-Verbindung aufbaut (Terminal, SFTP, Port-Weiterleitung).</summary>
public static class SshConnector
{
    public static ConnectionInfo BuildConnectionInfo(ServerEntry server, KeyEntry? key)
    {
        var user = string.IsNullOrWhiteSpace(server.Username) ? Environment.UserName : server.Username;
        var methods = new List<AuthenticationMethod>();

        if (server.Auth == AuthMethod.Key)
        {
            if (key == null || string.IsNullOrWhiteSpace(key.PrivateKey))
                throw new SshConnectException("Für diesen Host ist kein SSH-Key ausgewählt oder der Key fehlt.");
            methods.Add(new PrivateKeyAuthenticationMethod(user, LoadKey(key)));
        }
        else
        {
            methods.Add(new PasswordAuthenticationMethod(user, server.Password));

            // Viele Server nehmen das Passwort nur über "keyboard-interactive" an.
            var kbd = new KeyboardInteractiveAuthenticationMethod(user);
            kbd.AuthenticationPrompt += (_, e) =>
            {
                foreach (var prompt in e.Prompts) prompt.Response = server.Password;
            };
            methods.Add(kbd);
        }

        return new ConnectionInfo(server.Host, server.Port > 0 ? server.Port : 22, user, methods.ToArray())
        {
            Timeout = TimeSpan.FromSeconds(15)
        };
    }

    /// <summary>
    /// Hängt die Host-Key-Prüfung (Trust on first use) an einen Client.
    /// Der zurückgegebene Delegate liefert nach einem Fehlschlag die verständliche Warnung (oder null).
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
                    info?.Invoke($"Neuer Host-Key gespeichert ({e.HostKeyName}) {fp}");
                    break;
                default:
                    e.CanTrust = false;
                    error =
                        $"WARNUNG: Der Host-Key von {server.Host} hat sich geändert ({fp}). " +
                        "Das kann ein Angriff sein oder der Server wurde neu aufgesetzt. " +
                        "Wenn du das erwartest: im Host-Panel «Host-Key zurücksetzen».";
                    break;
            }
        };
        return () => error;
    }

    /// <summary>Liest einen privaten Key (OpenSSH / PEM). Wirft <see cref="SshConnectException"/> bei Fehlern.</summary>
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
            throw new SshConnectException("Der SSH-Key konnte nicht gelesen werden (falsche Passphrase oder nicht unterstütztes Format).", ex);
        }
    }

    public static string Describe(Exception ex) => ex switch
    {
        SshAuthenticationException => "Anmeldung fehlgeschlagen (Benutzer, Passwort oder Key stimmen nicht).",
        SshOperationTimeoutException => "Zeitüberschreitung beim Verbinden.",
        SocketException se => se.SocketErrorCode switch
        {
            SocketError.ConnectionRefused => "Verbindung abgelehnt (läuft SSH auf diesem Port?).",
            SocketError.HostNotFound or SocketError.NoData => "Host nicht gefunden.",
            SocketError.TimedOut => "Zeitüberschreitung beim Verbinden.",
            _ => "Netzwerkfehler: " + se.Message
        },
        SshConnectionException => "Verbindung fehlgeschlagen: " + ex.Message,
        _ => ex.Message
    };
}
