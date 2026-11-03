using System.Net.Sockets;
using System.Text;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace MultiServerView.Core.Ssh;

public sealed class SshConnectException : Exception
{
    public SshConnectException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>
/// Eine interaktive SSH-Shell (PTY). Daten kommen als rohe Bytes raus und gehen als rohe Bytes rein -
/// das Terminal selbst (xterm.js) sitzt im UI.
/// </summary>
public sealed class SshTerminalSession : IDisposable
{
    private readonly ServerEntry _server;
    private readonly KeyEntry? _key;
    private readonly Func<string, int, string, HostKeyTrust> _checkHostKey;
    private SshClient? _client;
    private ShellStream? _shell;
    private int _closedRaised;

    /// <summary>Neue Ausgabe vom Server.</summary>
    public event Action<byte[]>? Data;

    /// <summary>Sitzung wurde beendet (Grund oder null bei normalem Ende).</summary>
    public event Action<string?>? Closed;

    /// <summary>Wird einmalig beim ersten Kontakt mit einem Host gemeldet (Info für den Benutzer).</summary>
    public event Action<string>? Info;

    public SshTerminalSession(ServerEntry server, KeyEntry? key, Func<string, int, string, HostKeyTrust> checkHostKey)
    {
        _server = server;
        _key = key;
        _checkHostKey = checkHostKey;
    }

    public async Task ConnectAsync(uint columns, uint rows, CancellationToken ct = default)
    {
        var info = BuildConnectionInfo();
        var client = new SshClient(info) { KeepAliveInterval = TimeSpan.FromSeconds(30) };
        string? hostKeyError = null;

        client.HostKeyReceived += (_, e) =>
        {
            var fp = "SHA256:" + e.FingerPrintSHA256;
            switch (_checkHostKey(_server.Host, _server.Port, fp))
            {
                case HostKeyTrust.Trusted:
                    e.CanTrust = true;
                    break;
                case HostKeyTrust.NewlyTrusted:
                    e.CanTrust = true;
                    Info?.Invoke($"Neuer Host-Key gespeichert ({e.HostKeyName}) {fp}");
                    break;
                default:
                    e.CanTrust = false;
                    hostKeyError =
                        $"WARNUNG: Der Host-Key von {_server.Host} hat sich geändert ({fp}). " +
                        "Das kann ein Angriff sein oder der Server wurde neu aufgesetzt. " +
                        "Wenn du das erwartest: im Server-Dialog «Host-Key zurücksetzen».";
                    break;
            }
        };

        try
        {
            await Task.Run(() => client.Connect(), ct);
        }
        catch (Exception ex)
        {
            client.Dispose();
            throw new SshConnectException(hostKeyError ?? Describe(ex), ex);
        }

        _client = client;
        _shell = client.CreateShellStream("xterm-256color", columns, rows, 0, 0, 16384);
        _shell.DataReceived += (_, e) => Data?.Invoke(e.Data);
        _shell.Closed += (_, _) => RaiseClosed(null);
        _shell.ErrorOccurred += (_, e) => RaiseClosed(e.Exception.Message);
        client.ErrorOccurred += (_, e) => RaiseClosed(e.Exception.Message);
    }

    public void Write(byte[] data)
    {
        var shell = _shell;
        if (shell == null) return;
        try
        {
            shell.Write(data, 0, data.Length);
            shell.Flush();
        }
        catch (Exception ex) when (ex is ObjectDisposedException or SshException or IOException)
        {
            RaiseClosed(ex.Message);
        }
    }

    public void Resize(uint columns, uint rows)
    {
        try
        {
            _shell?.ChangeWindowSize(columns, rows, 0, 0);
        }
        catch (Exception ex) when (ex is ObjectDisposedException or SshException or InvalidOperationException)
        {
            // Fenstergrösse ändern ist nicht kritisch
        }
    }

    public void Dispose()
    {
        try { _shell?.Dispose(); } catch { /* egal beim Aufräumen */ }
        try { _client?.Dispose(); } catch { /* egal beim Aufräumen */ }
        _shell = null;
        _client = null;
    }

    private void RaiseClosed(string? reason)
    {
        if (Interlocked.Exchange(ref _closedRaised, 1) == 0) Closed?.Invoke(reason);
    }

    private ConnectionInfo BuildConnectionInfo()
    {
        var user = string.IsNullOrWhiteSpace(_server.Username) ? Environment.UserName : _server.Username;
        var methods = new List<AuthenticationMethod>();

        if (_server.Auth == AuthMethod.Key)
        {
            if (_key == null || string.IsNullOrWhiteSpace(_key.PrivateKey))
                throw new SshConnectException("Für diesen Server ist kein SSH-Key ausgewählt oder der Key fehlt.");
            methods.Add(new PrivateKeyAuthenticationMethod(user, LoadKey(_key)));
        }
        else
        {
            methods.Add(new PasswordAuthenticationMethod(user, _server.Password));

            // Viele Server nehmen das Passwort nur über "keyboard-interactive" an.
            var kbd = new KeyboardInteractiveAuthenticationMethod(user);
            kbd.AuthenticationPrompt += (_, e) =>
            {
                foreach (var prompt in e.Prompts) prompt.Response = _server.Password;
            };
            methods.Add(kbd);
        }

        return new ConnectionInfo(_server.Host, _server.Port > 0 ? _server.Port : 22, user, methods.ToArray())
        {
            Timeout = TimeSpan.FromSeconds(15)
        };
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

    private static string Describe(Exception ex) => ex switch
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
