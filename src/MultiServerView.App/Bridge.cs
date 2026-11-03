using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using MultiServerView.Core;
using MultiServerView.Core.Import;
using MultiServerView.Core.Sftp;
using MultiServerView.Core.Ssh;
using MultiServerView.Core.Sync;
using MultiServerView.Core.Vnc;

namespace MultiServerView.App;

/// <summary>Was die Bridge vom RDP-Hosting (WinForms, UI-Thread) braucht.</summary>
internal interface IRdpController
{
    void Open(string sessionId, ServerEntry server, Rectangle bounds);
    void Layout(string sessionId, Rectangle bounds, bool visible);
    void Close(string sessionId);

    /// <summary>true = alle RDP-Fenster ausblenden (Overlay des UI liegt darüber), false = wieder einblenden.</summary>
    void SetSuspended(bool suspended);
}

/// <summary>
/// Nimmt Nachrichten vom UI entgegen ({ id, method, params }) und antwortet mit { id, result } oder { id, error }.
/// Zusätzlich schickt die Bridge Events ans UI ({ event, ... }): ssh.data, ssh.closed, rdp.status, sync.status, vault.changed.
/// Die Methoden-Namen sind identisch mit ui/src/bridge.ts.
/// </summary>
internal sealed class Bridge : IDisposable
{
    private readonly VaultStore _vault;
    private readonly SyncService _sync;
    private readonly AutoSync _auto;
    private readonly IRdpController _rdp;
    private readonly Action<object> _emit;
    private readonly HistoryStore _history;
    private readonly ConcurrentDictionary<string, SshPump> _ssh = new();
    private readonly ConcurrentDictionary<string, SftpSession> _sftp = new();
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _transfers = new();
    private readonly ConcurrentDictionary<string, string> _historyIds = new();
    private readonly PortForwardManager _forwards = new();
    private readonly WebSocketTcpProxy _vncProxy = new();

    public Bridge(VaultStore vault, SyncService sync, HistoryStore history, IRdpController rdp, Action<object> emit)
    {
        _vault = vault;
        _history = history;
        _forwards.StatusChanged += (id, running, error) =>
            emit(new { @event = "forward.status", ruleId = id, running, error });
        _sync = sync;
        _rdp = rdp;
        _emit = emit;
        _auto = new AutoSync(vault, sync, emit);
    }

    public async Task<string> HandleAsync(string requestJson)
    {
        var id = 0;
        try
        {
            using var doc = JsonDocument.Parse(requestJson);
            var root = doc.RootElement;
            id = root.GetProperty("id").GetInt32();
            var method = root.GetProperty("method").GetString() ?? "";
            var p = root.TryGetProperty("params", out var pp) ? pp : default;

            var result = await DispatchAsync(method, p);
            return JsonSerializer.Serialize(new { id, result }, Json.Options);
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { id, error = Friendly(ex) }, Json.Options);
        }
    }

    private async Task<object?> DispatchAsync(string method, JsonElement p)
    {
        switch (method)
        {
            // ---------------------------------------------------------- Vault
            case "vault.status":
                return new { exists = _vault.Exists, unlocked = _vault.IsUnlocked };
            case "vault.create":
                _vault.Create(Str(p, "password"));
                return null;
            case "vault.unlock":
                _vault.Unlock(Str(p, "password"));
                _auto.RunSoon();
                return null;
            case "vault.lock":
                CloseAllSessions();
                foreach (var sid in _sftp.Keys.ToList()) CloseSftp(sid);
                _forwards.StopAll();
                _vault.Lock();
                return null;

            // ---------------------------------------------------------- Server
            case "servers.list":
                return _vault.List();
            case "servers.save":
                return _vault.Upsert(p.Deserialize<ServerEntry>(Json.Options)
                                     ?? throw new ArgumentException("Ungültiger Server."));
            case "servers.delete":
                _vault.Delete(Str(p, "id"));
                return null;
            case "servers.touch":
                return _vault.Touch(Str(p, "id"));
            case "hosts.forget":
            {
                var server = _vault.Find(Str(p, "id")) ?? throw new KeyNotFoundException("Server nicht gefunden.");
                _vault.ForgetHostKey(server.Host, server.Port);
                return null;
            }
            case "import.mremoteng":
            {
                var result = MRemoteNgImporter.Parse(Str(p, "xml"));
                var imported = _vault.UpsertMany(result.Servers);
                return new { imported, skipped = result.Skipped };
            }

            // ---------------------------------------------------------- Keys (private Keys verlassen den Host nie Richtung UI)
            case "keys.list":
                return _vault.ListKeys().Select(KeyDto).ToList();
            case "keys.save":
            {
                var key = p.Deserialize<KeyEntry>(Json.Options) ?? throw new ArgumentException("Ungültiger Key.");
                if (string.IsNullOrWhiteSpace(key.Name)) throw new ArgumentException("Bitte dem Key einen Namen geben.");

                var existing = string.IsNullOrWhiteSpace(key.Id) ? null : _vault.FindKey(key.Id);
                if (existing != null && string.IsNullOrWhiteSpace(key.PrivateKey))
                {
                    // Nur umbenannt: Schlüsselmaterial bleibt wie es ist.
                    existing.Name = key.Name;
                    return KeyDto(_vault.UpsertKey(existing));
                }

                key.Kind = KeyTools.Inspect(key.PrivateKey, key.Passphrase);
                return KeyDto(_vault.UpsertKey(key));
            }
            case "keys.delete":
                _vault.DeleteKey(Str(p, "id"));
                return null;

            // ---------------------------------------------------------- SSH
            case "ssh.open":
                await OpenSshAsync(Str(p, "sessionId"), Str(p, "serverId"), (uint)Int(p, "cols", 80), (uint)Int(p, "rows", 24));
                return null;
            case "ssh.input":
                if (_ssh.TryGetValue(Str(p, "sessionId"), out var inputTarget))
                    inputTarget.Session.Write(Convert.FromBase64String(Str(p, "data")));
                return null;
            case "ssh.resize":
                if (_ssh.TryGetValue(Str(p, "sessionId"), out var resizeTarget))
                    resizeTarget.Session.Resize((uint)Int(p, "cols", 80), (uint)Int(p, "rows", 24));
                return null;
            case "ssh.close":
                CloseSsh(Str(p, "sessionId"));
                return null;

            // ---------------------------------------------------------- VNC
            case "vnc.open":
            {
                var server = _vault.FindResolved(Str(p, "serverId")) ?? throw new KeyNotFoundException("Server nicht gefunden.");
                var hid = _history.Begin(server, "vnc");
                _history.End(hid, true);
                var url = _vncProxy.Register(server.Host, server.Port > 0 ? server.Port : 5900);
                return new { url, username = server.Username, password = server.Password };
            }

            // ---------------------------------------------------------- RDP
            case "rdp.open":
            {
                var server = _vault.FindResolved(Str(p, "serverId")) ?? throw new KeyNotFoundException("Server nicht gefunden.");
                var sessionId = Str(p, "sessionId");
                var hid = _history.Begin(server, "rdp");
                try
                {
                    _rdp.Open(sessionId, server, Bounds(p));
                    _history.End(hid, true);
                }
                catch (Exception ex)
                {
                    _history.End(hid, false, Friendly(ex));
                    throw;
                }
                return null;
            }
            case "rdp.layout":
                _rdp.Layout(Str(p, "sessionId"), Bounds(p), Bool(p, "visible"));
                return null;
            case "rdp.close":
                _rdp.Close(Str(p, "sessionId"));
                return null;
            case "rdp.suspend":
                _rdp.SetSuspended(Bool(p, "suspended"));
                return null;
            case "rdp.external":
            {
                // Fallback: normales Windows-Fenster (mstsc). Zugangsdaten gibt man dort selbst ein.
                var server = _vault.FindResolved(Str(p, "serverId")) ?? throw new KeyNotFoundException("Server nicht gefunden.");
                Process.Start(new ProcessStartInfo("mstsc.exe", $"/v:{server.Host}:{server.Port}") { UseShellExecute = true });
                return null;
            }

            // ---------------------------------------------------------- Identities
            case "identities.list":
                return _vault.ListIdentities().Select(IdentityDto).ToList();
            case "identities.save":
            {
                var identity = p.Deserialize<IdentityEntry>(Json.Options) ?? throw new ArgumentException("Ungültige Identity.");
                if (string.IsNullOrWhiteSpace(identity.Name)) throw new ArgumentException("Bitte der Identity einen Namen geben.");
                var existing = string.IsNullOrWhiteSpace(identity.Id) ? null : _vault.FindIdentity(identity.Id);
                // Leeres Passwortfeld = unverändert (gespeicherte Passwörter gehen nie ans UI).
                if (existing != null && identity.Password.Length == 0 && identity.Auth == AuthMethod.Password) identity.Password = existing.Password;
                return IdentityDto(_vault.UpsertIdentity(identity));
            }
            case "identities.delete":
                _vault.DeleteIdentity(Str(p, "id"));
                return null;

            // ---------------------------------------------------------- Snippets
            case "snippets.list":
                return _vault.ListSnippets();
            case "snippets.save":
            {
                var snippet = p.Deserialize<SnippetEntry>(Json.Options) ?? throw new ArgumentException("Ungültiges Snippet.");
                if (string.IsNullOrWhiteSpace(snippet.Name) || string.IsNullOrWhiteSpace(snippet.Command))
                    throw new ArgumentException("Name und Befehl sind Pflicht.");
                return _vault.UpsertSnippet(snippet);
            }
            case "snippets.delete":
                _vault.DeleteSnippet(Str(p, "id"));
                return null;

            // ---------------------------------------------------------- Port-Weiterleitungen
            case "forwards.list":
            {
                var running = _forwards.RunningIds.ToHashSet();
                return _vault.ListForwards().Select(f => new { rule = f, running = running.Contains(f.Id) }).ToList();
            }
            case "forwards.save":
            {
                var rule = p.Deserialize<PortForwardEntry>(Json.Options) ?? throw new ArgumentException("Ungültige Regel.");
                if (_vault.Find(rule.ServerId) == null) throw new ArgumentException("Bitte einen SSH-Host auswählen.");
                if (rule.BindPort is < 1 or > 65535) throw new ArgumentException("Der Port muss zwischen 1 und 65535 liegen.");
                if (rule.Type != ForwardType.Dynamic && (string.IsNullOrWhiteSpace(rule.DestHost) || rule.DestPort is < 1 or > 65535))
                    throw new ArgumentException("Ziel-Adresse und Ziel-Port fehlen.");
                if (string.IsNullOrWhiteSpace(rule.Name)) rule.Name = $"{rule.Type} {rule.BindPort}";
                _forwards.Stop(rule.Id); // geänderte Regel läuft nicht mit altem Stand weiter
                return _vault.UpsertForward(rule);
            }
            case "forwards.delete":
                _forwards.Stop(Str(p, "id"));
                _vault.DeleteForward(Str(p, "id"));
                return null;
            case "forwards.start":
            {
                var rule = _vault.FindForward(Str(p, "id")) ?? throw new KeyNotFoundException("Regel nicht gefunden.");
                var server = _vault.FindResolved(rule.ServerId) ?? throw new KeyNotFoundException("Der SSH-Host dieser Regel existiert nicht mehr.");
                var key = server.Auth == AuthMethod.Key ? _vault.FindKey(server.KeyId) : null;
                var hid = _history.Begin(server, "forward");
                try
                {
                    await _forwards.StartAsync(rule, server, key, (h, port, fp) => _vault.CheckHostKey(h, port, fp));
                    _history.End(hid, true, $"{rule.Type} {rule.BindPort}");
                }
                catch (Exception ex)
                {
                    _history.End(hid, false, Friendly(ex));
                    throw;
                }
                return null;
            }
            case "forwards.stop":
                _forwards.Stop(Str(p, "id"));
                return null;

            // ---------------------------------------------------------- Known Hosts, Verlauf
            case "knownhosts.list":
                return _vault.ListKnownHosts().Select(kv => new { id = kv.Key, fingerprint = kv.Value }).ToList();
            case "knownhosts.remove":
                _vault.RemoveKnownHost(Str(p, "id"));
                return null;
            case "history.list":
                return _history.List();
            case "history.clear":
                _history.Clear();
                return null;

            // ---------------------------------------------------------- SFTP
            case "sftp.open":
            {
                var sessionId = Str(p, "sessionId");
                CloseSftp(sessionId);
                var server = _vault.FindResolved(Str(p, "serverId")) ?? throw new KeyNotFoundException("Server nicht gefunden.");
                var key = server.Auth == AuthMethod.Key ? _vault.FindKey(server.KeyId) : null;
                var hid = _history.Begin(server, "sftp");
                var session = new SftpSession(server, key, (h, port, fp) => _vault.CheckHostKey(h, port, fp));
                try
                {
                    var home = await session.ConnectAsync();
                    _sftp[sessionId] = session;
                    _historyIds[sessionId] = hid;
                    return new { home };
                }
                catch (Exception ex)
                {
                    session.Dispose();
                    _history.End(hid, false, Friendly(ex));
                    throw;
                }
            }
            case "sftp.list":
                return await Sftp(p).ListAsync(Opt(p, "path"));
            case "sftp.mkdir":
                await Sftp(p).MakeDirAsync(Str(p, "path"));
                return null;
            case "sftp.rename":
                await Sftp(p).RenameAsync(Str(p, "from"), Str(p, "to"));
                return null;
            case "sftp.delete":
                await Sftp(p).DeleteAsync(Str(p, "path"));
                return null;
            case "sftp.upload":
            case "sftp.download":
            {
                var sessionId = Str(p, "sessionId");
                var session = Sftp(p);
                var transferId = Str(p, "transferId");
                var cts = new CancellationTokenSource();
                _transfers[transferId] = cts;
                try
                {
                    var upload = method == "sftp.upload";
                    foreach (var path in Strings(p, upload ? "localPaths" : "remotePaths"))
                    {
                        void Report(TransferProgress tp) => _emit(new { @event = "sftp.progress", sessionId, transferId, name = tp.Name, done = tp.Done, total = tp.Total, finished = tp.Finished });
                        if (upload) await session.UploadAsync(path, Str(p, "remoteDir"), Report, cts.Token);
                        else await session.DownloadAsync(path, Str(p, "localDir"), Report, cts.Token);
                    }
                }
                finally
                {
                    _transfers.TryRemove(transferId, out _);
                    cts.Dispose();
                }
                return null;
            }
            case "sftp.cancel":
                if (_transfers.TryGetValue(Str(p, "transferId"), out var toCancel)) toCancel.Cancel();
                return null;
            case "sftp.close":
                CloseSftp(Str(p, "sessionId"));
                return null;
            case "local.browse":
                return LocalFs.Browse(Opt(p, "path").Length == 0 ? LocalFs.Home() : Opt(p, "path"));
            case "local.roots":
                return LocalFs.Browse("");
            case "local.mkdir":
                LocalFs.MakeDir(Str(p, "path"));
                return null;

            // ---------------------------------------------------------- Cloud
            case "sync.get":
                return SyncDto(_sync.Settings);
            case "sync.save":
            {
                var cfg = ReadSyncConfig(p);
                _sync.Save(cfg);
                return SyncDto(cfg);
            }
            case "sync.test":
                await _sync.TestConnectionAsync(ReadSyncConfig(p));
                return null;
            case "sync.signUp":
                return new { signedIn = await _sync.SignUpAsync(ReadSyncConfig(p)) };
            case "sync.now":
            {
                var r = await _auto.RunAsync();
                return new { outcome = r.Outcome.ToString(), at = r.At };
            }
            case "sync.disconnect":
                _sync.Disconnect();
                _emit(new { @event = "sync.status", state = "off", message = "Cloud getrennt" });
                return null;
            case "sync.downloadInitial":
                await _sync.DownloadInitialAsync(ReadSyncConfig(p, keepStoredPassword: false));
                return null;

            default:
                throw new InvalidOperationException($"Unbekannte Methode: {method}");
        }
    }

    // ================================================================== SSH

    private async Task OpenSshAsync(string sessionId, string serverId, uint cols, uint rows)
    {
        CloseSsh(sessionId); // Reconnect mit gleicher Id

        var server = _vault.FindResolved(serverId) ?? throw new KeyNotFoundException("Server nicht gefunden.");
        var key = server.Auth == AuthMethod.Key ? _vault.FindKey(server.KeyId) : null;
        var hid = _history.Begin(server, "ssh");

        var session = new SshTerminalSession(server, key, (host, port, fp) => _vault.CheckHostKey(host, port, fp));
        var pump = new SshPump(session, chunk => _emit(new { @event = "ssh.data", sessionId, data = Convert.ToBase64String(chunk) }));
        session.Info += message => _emit(new { @event = "ssh.info", sessionId, message });
        session.Closed += reason =>
        {
            pump.Flush();
            if (_ssh.TryRemove(sessionId, out var gone) && ReferenceEquals(gone, pump)) pump.Dispose();
            _history.End(hid, reason == null, reason ?? "");
            _emit(new { @event = "ssh.closed", sessionId, reason });
        };

        _ssh[sessionId] = pump;
        try
        {
            await session.ConnectAsync(cols, rows);
        }
        catch (Exception ex)
        {
            _ssh.TryRemove(sessionId, out _);
            pump.Dispose();
            _history.End(hid, false, Friendly(ex));
            throw;
        }

        // Betriebssystem im Hintergrund erkennen (für das Icon); Fehler sind egal.
        _ = Task.Run(async () =>
        {
            var os = await session.DetectOsAsync();
            if (os.Length > 0) _vault.SetOs(server.Id, os);
        });
    }

    private void CloseSsh(string sessionId)
    {
        if (_ssh.TryRemove(sessionId, out var pump)) pump.Dispose();
    }

    private void CloseAllSessions()
    {
        foreach (var id in _ssh.Keys.ToList()) CloseSsh(id);
    }

    /// <summary>Bündelt viele kleine SSH-Ausgaben zu wenigen Nachrichten (alle ~6 ms), damit das UI nicht überrollt wird.</summary>
    private sealed class SshPump : IDisposable
    {
        private readonly Action<byte[]> _send;
        private readonly MemoryStream _buffer = new();
        private readonly object _gate = new();
        private bool _scheduled;
        private bool _disposed;

        public SshTerminalSession Session { get; }

        public SshPump(SshTerminalSession session, Action<byte[]> send)
        {
            Session = session;
            _send = send;
            session.Data += OnData;
        }

        private void OnData(byte[] data)
        {
            lock (_gate)
            {
                if (_disposed) return;
                _buffer.Write(data, 0, data.Length);
                if (_scheduled) return;
                _scheduled = true;
            }
            _ = Task.Delay(6).ContinueWith(_ => Flush());
        }

        public void Flush()
        {
            byte[] chunk;
            lock (_gate)
            {
                chunk = _buffer.ToArray();
                _buffer.SetLength(0);
                _scheduled = false;
            }
            if (chunk.Length > 0) _send(chunk);
        }

        public void Dispose()
        {
            lock (_gate) _disposed = true;
            Session.Dispose();
        }
    }

    // ================================================================== Hilfen

    private SftpSession Sftp(JsonElement p) =>
        _sftp.TryGetValue(Str(p, "sessionId"), out var s) ? s : throw new InvalidOperationException("Die SFTP-Verbindung ist nicht offen.");

    private void CloseSftp(string sessionId)
    {
        if (_sftp.TryRemove(sessionId, out var s)) s.Dispose();
        if (_historyIds.TryRemove(sessionId, out var hid)) _history.End(hid, true);
    }

    private static List<string> Strings(JsonElement p, string name) =>
        p.ValueKind == JsonValueKind.Object && p.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array
            ? v.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString() ?? "").ToList()
            : throw new ArgumentException($"Parameter fehlt: {name}");

    private static object IdentityDto(IdentityEntry i) => new
    {
        id = i.Id,
        name = i.Name,
        username = i.Username,
        hasPassword = i.Password.Length > 0,
        auth = i.Auth,
        keyId = i.KeyId,
        updatedAt = i.UpdatedAt
    };

    private static object KeyDto(KeyEntry k) => new
    {
        id = k.Id,
        name = k.Name,
        kind = k.Kind,
        hasPassphrase = k.Passphrase.Length > 0,
        updatedAt = k.UpdatedAt
    };

    private static object SyncDto(SyncSettings s) => new
    {
        configured = s.IsConfigured,
        url = s.Url,
        anonKey = s.AnonKey,
        email = s.Email,
        hasPassword = s.Password.Length > 0,
        lastSync = s.LastSync
    };

    private SyncSettings ReadSyncConfig(JsonElement p, bool keepStoredPassword = true)
    {
        var stored = _sync.Settings;
        var password = Opt(p, "password");
        return new SyncSettings
        {
            Url = Str(p, "url").Trim(),
            AnonKey = Str(p, "anonKey").Trim(),
            Email = Str(p, "email").Trim(),
            // Leeres Passwortfeld = "unverändert" (das gespeicherte wird nie ans UI geschickt).
            Password = password.Length > 0 ? password : keepStoredPassword ? stored.Password : "",
            LastSync = stored.LastSync
        };
    }

    private static Rectangle Bounds(JsonElement p) =>
        new(Int(p, "x", 0), Int(p, "y", 0), Math.Max(Int(p, "w", 640), 1), Math.Max(Int(p, "h", 480), 1));

    private static string Str(JsonElement p, string name) =>
        p.ValueKind == JsonValueKind.Object && p.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? ""
            : throw new ArgumentException($"Parameter fehlt: {name}");

    private static string Opt(JsonElement p, string name) =>
        p.ValueKind == JsonValueKind.Object && p.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? ""
            : "";

    private static int Int(JsonElement p, string name, int fallback) =>
        p.ValueKind == JsonValueKind.Object && p.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number
            ? (int)Math.Round(v.GetDouble())
            : fallback;

    private static bool Bool(JsonElement p, string name) =>
        p.ValueKind == JsonValueKind.Object && p.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    internal static string Friendly(Exception ex) => ex switch
    {
        WrongPasswordException => "Falsches Passwort.",
        SshConnectException or SyncException => ex.Message,
        Renci.SshNet.Common.SftpPathNotFoundException => "Pfad nicht gefunden.",
        Renci.SshNet.Common.SftpPermissionDeniedException => "Zugriff verweigert.",
        OperationCanceledException => "Abgebrochen.",
        Renci.SshNet.Common.SshException or IOException or UnauthorizedAccessException => ex.Message,
        ArgumentException or InvalidOperationException or KeyNotFoundException => ex.Message,
        System.Security.Cryptography.CryptographicException => "Die Vault-Datei ist ungültig oder beschädigt.",
        _ => "Unerwarteter Fehler: " + ex.Message
    };

    public void Dispose()
    {
        _auto.Dispose();
        _forwards.Dispose();
        foreach (var t in _transfers.Values) t.Cancel();
        foreach (var id in _sftp.Keys.ToList()) CloseSftp(id);
        CloseAllSessions();
        _vncProxy.Dispose();
    }
}
