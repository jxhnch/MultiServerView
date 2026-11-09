using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using MultiServerView.Core;
using MultiServerView.Core.Update;
using MultiServerView.Core.Import;
using MultiServerView.Core.Proxmox;
using MultiServerView.Core.Vmware;
using MultiServerView.Core.Sftp;
using MultiServerView.Core.Ssh;
using MultiServerView.Core.Sync;
using MultiServerView.Core.Vnc;

namespace MultiServerView.App;

/// <summary>What the bridge needs from the RDP hosting (WinForms, UI thread).</summary>
internal interface IRdpController
{
    void Open(string sessionId, ServerEntry server, Rectangle bounds);
    void Layout(string sessionId, Rectangle bounds, bool visible);
    void Close(string sessionId);

    /// <summary>true = hide all RDP windows (a UI overlay is on top), false = show them again.</summary>
    void SetSuspended(bool suspended);
}

/// <summary>
/// Receives messages from the UI ({ id, method, params }) and answers with { id, result } or { id, error }.
/// In addition the bridge sends events to the UI ({ event, ... }): ssh.data, ssh.closed, rdp.status, sync.status, vault.changed.
/// The method names are identical to ui/src/bridge.ts.
/// </summary>
internal sealed class Bridge : IDisposable
{
    private readonly VaultStore _vault;
    private readonly SyncService _sync;
    private readonly AutoSync _auto;
    private readonly IRdpController _rdp;
    private readonly Action<object> _emit;
    private readonly HistoryStore _history;
    private readonly AppSettingsStore _settings;
    private readonly Func<string, Task<string?>> _pickSavePath;
    private readonly Func<string, Task<string?>> _pickFolder;
    private readonly Func<string, bool> _window;
    private readonly ConcurrentDictionary<string, JumpTunnel> _tunnels = new();
    private readonly ConcurrentDictionary<string, SshPump> _ssh = new();
    private readonly ConcurrentDictionary<string, SftpSession> _sftp = new();
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _transfers = new();
    private readonly ConcurrentDictionary<string, string> _historyIds = new();
    private readonly PortForwardManager _forwards = new();
    private readonly WebSocketTcpProxy _vncProxy = new();
    private readonly McpService _mcp;

    public Bridge(VaultStore vault, SyncService sync, HistoryStore history, AppSettingsStore settings, IRdpController rdp, Action<object> emit,
        Func<string, Task<string?>> pickSavePath, Func<string, Task<string?>> pickFolder, Func<string, bool> window)
    {
        _pickFolder = pickFolder;
        _window = window;
        _vault = vault;
        _history = history;
        _settings = settings;
        _pickSavePath = pickSavePath;
        _forwards.StatusChanged += (id, running, error) =>
            emit(new { @event = "forward.status", ruleId = id, running, error });
        _sync = sync;
        _rdp = rdp;
        _emit = emit;
        _auto = new AutoSync(vault, sync, emit);
        _mcp = new McpService(vault, CheckHost, Jumps);
        _mcp.Apply(settings.Current);
    }

    /// <summary>Resolves jump hosts (including credentials from identities and keys) for connection chains.</summary>
    private (ServerEntry Server, KeyEntry? Key)? Jumps(string id)
    {
        var s = _vault.FindResolved(id);
        return s == null ? null : (s, s.Auth == AuthMethod.Key ? _vault.FindKey(s.KeyId) : null);
    }

    private HostKeyTrust CheckHost(string host, int port, string fp) => _vault.CheckHostKey(host, port, fp);

    /// <summary>Opens a jump tunnel for RDP/VNC if needed and returns the target that is actually to be connected.</summary>
    private async Task<ServerEntry> TunnelFor(string sessionId, ServerEntry server)
    {
        var tunnel = await JumpTunnel.OpenAsync(server, Jumps, CheckHost);
        if (tunnel == null) return server;
        if (_tunnels.TryRemove(sessionId, out var old)) old.Dispose();
        _tunnels[sessionId] = tunnel;
        var effective = server.Clone();
        effective.Host = tunnel.Host;
        effective.Port = tunnel.Port;
        return effective;
    }

    private void CloseTunnel(string sessionId)
    {
        if (_tunnels.TryRemove(sessionId, out var t)) t.Dispose();
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
            case "vault.summary":
                return _vault.Summary();
            case "vault.clear":
            {
                // Check the password first, then disconnect everything and clear.
                _vault.VerifyPassword(Str(p, "password"));
                CloseAllSessions();
                foreach (var sid in _sftp.Keys.ToList()) CloseSftp(sid);
                foreach (var tid in _tunnels.Keys.ToList()) CloseTunnel(tid);
                _forwards.StopAll();
                return _vault.Clear(Str(p, "password"));
            }
            case "vault.lock":
                CloseAllSessions();
                foreach (var sid in _sftp.Keys.ToList()) CloseSftp(sid);
                foreach (var tid in _tunnels.Keys.ToList()) CloseTunnel(tid);
                _forwards.StopAll();
                _vault.Lock();
                return null;

            // ---------------------------------------------------------- Server
            case "servers.list":
                return _vault.List();
            case "servers.save":
                return _vault.Upsert(p.Deserialize<ServerEntry>(Json.Options)
                                     ?? throw new ArgumentException("Invalid server."));
            case "servers.delete":
                _vault.Delete(Str(p, "id"));
                return null;
            case "servers.touch":
                return _vault.Touch(Str(p, "id"));
            case "hosts.forget":
            {
                var server = _vault.Find(Str(p, "id")) ?? throw new KeyNotFoundException("Server not found.");
                _vault.ForgetHostKey(server.Host, server.Port);
                return null;
            }
            case "import.mremoteng":
            {
                var mpw = Opt(p, "masterPassword");
                return ImportResult(MRemoteNgImporter.Parse(Str(p, "xml"), mpw.Length > 0 ? mpw : MRemoteNgCrypto.DefaultMasterPassword), Opt(p, "workspace"));
            }

            // ---------------------------------------------------------- Keys (private keys never leave the host towards the UI)
            case "keys.list":
                return _vault.ListKeys().Select(KeyDto).ToList();
            case "keys.save":
            {
                var key = p.Deserialize<KeyEntry>(Json.Options) ?? throw new ArgumentException("Invalid key.");
                if (string.IsNullOrWhiteSpace(key.Name)) throw new ArgumentException("Please give the key a name.");

                var existing = string.IsNullOrWhiteSpace(key.Id) ? null : _vault.FindKey(key.Id);
                if (existing != null && string.IsNullOrWhiteSpace(key.PrivateKey))
                {
                    // Only renamed: the key material stays as it is.
                    existing.Name = key.Name;
                    return KeyDto(_vault.UpsertKey(existing));
                }

                key.Kind = KeyTools.Inspect(key.PrivateKey, key.Passphrase);
                return KeyDto(_vault.UpsertKey(key));
            }
            case "keys.delete":
                _vault.DeleteKey(Str(p, "id"));
                return null;
            case "keys.generate":
            {
                var name = Str(p, "name").Trim();
                if (name.Length == 0) throw new ArgumentException("Please give the key a name.");
                var generated = KeyTools.Generate(Opt(p, "type"), Int(p, "bits", 3072), Opt(p, "comment"));
                return KeyDto(_vault.UpsertKey(new KeyEntry { Name = name, PrivateKey = generated.PrivateKey, Kind = generated.Kind }));
            }
            case "keys.install":
            {
                var key = _vault.FindKey(Str(p, "keyId")) ?? throw new KeyNotFoundException("Key not found.");
                var original = _vault.Find(Str(p, "serverId")) ?? throw new KeyNotFoundException("Server not found.");
                var server = WithTypedCredentials(_vault.FindResolved(original.Id)!, p, out var saveCredentials);
                var login = server.Auth == AuthMethod.Key ? _vault.FindKey(server.KeyId) : null;
                await KeyInstaller.InstallAsync(server, login, KeyTools.PublicKeyLine(key, "MultiServerView"), CheckHost, Jumps);
                saveCredentials?.Invoke();
                if (Bool(p, "switchHost") && string.IsNullOrEmpty(original.IdentityId))
                {
                    var updated = original.Clone();
                    updated.Auth = AuthMethod.Key;
                    updated.KeyId = key.Id;
                    _vault.Upsert(updated);
                }
                return null;
            }

            // ---------------------------------------------------------- SSH
            case "ssh.open":
                await OpenSshAsync(Str(p, "sessionId"), Str(p, "serverId"), (uint)Int(p, "cols", 80), (uint)Int(p, "rows", 24), p);
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
                var server = _vault.FindResolved(Str(p, "serverId")) ?? throw new KeyNotFoundException("Server not found.");
                var hid = _history.Begin(server, "vnc");
                ServerEntry target;
                try
                {
                    target = await TunnelFor(Opt(p, "sessionId").Length > 0 ? Opt(p, "sessionId") : server.Id, server);
                    _history.End(hid, true);
                }
                catch (Exception ex)
                {
                    _history.End(hid, false, Friendly(ex));
                    throw;
                }
                var url = _vncProxy.Register(target.Host, target.Port > 0 ? target.Port : 5900);
                return new { url, username = server.Username, password = server.Password };
            }

            case "vnc.close":
                CloseTunnel(Str(p, "sessionId"));
                return null;

            // ---------------------------------------------------------- RDP
            case "rdp.open":
            {
                var server = _vault.FindResolved(Str(p, "serverId")) ?? throw new KeyNotFoundException("Server not found.");
                var sessionId = Str(p, "sessionId");
                var hid = _history.Begin(server, "rdp");
                try
                {
                    var effective = await TunnelFor(sessionId, server);
                    _rdp.Open(sessionId, effective, Bounds(p));
                    _history.End(hid, true);
                }
                catch (Exception ex)
                {
                    CloseTunnel(sessionId);
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
                CloseTunnel(Str(p, "sessionId"));
                return null;
            case "rdp.suspend":
                _rdp.SetSuspended(Bool(p, "suspended"));
                return null;
            case "rdp.external":
            {
                // Fallback: normal Windows window (mstsc). You enter the credentials there yourself.
                var server = _vault.FindResolved(Str(p, "serverId")) ?? throw new KeyNotFoundException("Server not found.");
                Process.Start(new ProcessStartInfo("mstsc.exe", $"/v:{server.Host}:{server.Port}") { UseShellExecute = true });
                return null;
            }

            case "update.check":
            {
                var current = AppVersion;
                var info = await UpdateChecker.CheckAsync(_http, current);
                return new { current, available = info != null, version = info?.Version ?? "", notes = info?.Notes ?? "", pageUrl = info?.PageUrl ?? "", canInstall = info?.SetupUrl != null && IsInstalled() };
            }
            case "update.install":
            {
                var info = await UpdateChecker.CheckAsync(_http, AppVersion);
                if (info?.SetupUrl == null || !UpdateChecker.IsGitHubUrl(info.SetupUrl)) throw new InvalidOperationException("No installer found for the new version. Download it from the releases page.");
                if (!IsInstalled()) throw new InvalidOperationException("This is the portable version. Download the new ZIP from the releases page.");
                var file = Path.Combine(Path.GetTempPath(), "MultiServerView-Setup-" + info.Version + ".exe");
                using (var response = await _http.GetAsync(info.SetupUrl, HttpCompletionOption.ResponseHeadersRead))
                {
                    response.EnsureSuccessStatusCode();
                    await using var fs = File.Create(file);
                    await response.Content.CopyToAsync(fs);
                }
                // Inno Setup: silent, closes this app, starts the new version when done.
                Process.Start(new ProcessStartInfo(file, "/SILENT /CLOSEAPPLICATIONS /RESTARTAPPLICATIONS /NOCANCEL") { UseShellExecute = true });
                _window("close");
                return null;
            }
            case "teamviewer.open":
            {
                // Hands the ID and password to the installed TeamViewer client (no embedded session).
                var server = _vault.FindResolved(Str(p, "serverId")) ?? throw new KeyNotFoundException("Server not found.");
                var exe = FindTeamViewer() ?? throw new InvalidOperationException("TeamViewer is not installed on this PC (https://www.teamviewer.com).");
                var id = new string(server.Host.Where(char.IsDigit).ToArray());
                if (id.Length < 8) throw new ArgumentException("Please enter the TeamViewer ID of the partner (digits only).");
                var info = new ProcessStartInfo(exe) { UseShellExecute = false };
                info.ArgumentList.Add("-i");
                info.ArgumentList.Add(id);
                if (server.Password.Length > 0)
                {
                    info.ArgumentList.Add("--Password");
                    info.ArgumentList.Add(server.Password);
                }
                Process.Start(info);
                return null;
            }

            // ---------------------------------------------------------- Identities
            case "identities.list":
                return _vault.ListIdentities().Select(IdentityDto).ToList();
            case "identities.save":
            {
                var identity = p.Deserialize<IdentityEntry>(Json.Options) ?? throw new ArgumentException("Invalid identity.");
                if (string.IsNullOrWhiteSpace(identity.Name)) throw new ArgumentException("Please give the identity a name.");
                var existing = string.IsNullOrWhiteSpace(identity.Id) ? null : _vault.FindIdentity(identity.Id);
                // Empty password field = unchanged (saved passwords never go to the UI).
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
                var snippet = p.Deserialize<SnippetEntry>(Json.Options) ?? throw new ArgumentException("Invalid snippet.");
                if (string.IsNullOrWhiteSpace(snippet.Name) || string.IsNullOrWhiteSpace(snippet.Command))
                    throw new ArgumentException("Name and command are required.");
                return _vault.UpsertSnippet(snippet);
            }
            case "snippets.delete":
                _vault.DeleteSnippet(Str(p, "id"));
                return null;

            // ---------------------------------------------------------- Port forwards
            case "forwards.list":
            {
                var running = _forwards.RunningIds.ToHashSet();
                return _vault.ListForwards().Select(f => new { rule = f, running = running.Contains(f.Id) }).ToList();
            }
            case "forwards.save":
            {
                var rule = p.Deserialize<PortForwardEntry>(Json.Options) ?? throw new ArgumentException("Invalid rule.");
                if (_vault.Find(rule.ServerId) == null) throw new ArgumentException("Please choose an SSH host.");
                if (rule.BindPort is < 1 or > 65535) throw new ArgumentException("The port must be between 1 and 65535.");
                if (rule.Type != ForwardType.Dynamic && (string.IsNullOrWhiteSpace(rule.DestHost) || rule.DestPort is < 1 or > 65535))
                    throw new ArgumentException("Target address and target port are missing.");
                if (string.IsNullOrWhiteSpace(rule.Name)) rule.Name = $"{rule.Type} {rule.BindPort}";
                _forwards.Stop(rule.Id); // a changed rule does not keep running with the old state
                return _vault.UpsertForward(rule);
            }
            case "forwards.delete":
                _forwards.Stop(Str(p, "id"));
                _vault.DeleteForward(Str(p, "id"));
                return null;
            case "forwards.start":
            {
                var rule = _vault.FindForward(Str(p, "id")) ?? throw new KeyNotFoundException("Rule not found.");
                var server = _vault.FindResolved(rule.ServerId) ?? throw new KeyNotFoundException("The SSH host of this rule no longer exists.");
                var key = server.Auth == AuthMethod.Key ? _vault.FindKey(server.KeyId) : null;
                var hid = _history.Begin(server, "forward");
                try
                {
                    await _forwards.StartAsync(rule, server, key, CheckHost, Jumps);
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

            // ---------------------------------------------------------- Known hosts, history
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
                var server = _vault.FindResolved(Str(p, "serverId")) ?? throw new KeyNotFoundException("Server not found.");
                server = WithTypedCredentials(server, p, out var saveCredentials);
                var key = server.Auth == AuthMethod.Key ? _vault.FindKey(server.KeyId) : null;
                var hid = _history.Begin(server, "sftp");
                var session = new SftpSession(server, key, CheckHost, Jumps);
                try
                {
                    var home = await session.ConnectAsync();
                    saveCredentials?.Invoke();
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
            case "sftp.readText":
            {
                var file = await Sftp(p).ReadTextAsync(Str(p, "path"));
                return new { content = file.Content, lineEnding = file.LineEnding, bom = file.Bom, size = file.Size, modified = file.Modified };
            }
            case "sftp.writeText":
            {
                var saved = await Sftp(p).WriteTextAsync(Str(p, "path"), Str(p, "content"), Str(p, "lineEnding"), Bool(p, "bom"),
                    Long(p, "expectedModified"), Long(p, "expectedSize"), Bool(p, "force"));
                return new { modified = saved.Modified, size = saved.Size };
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

            // ---------------------------------------------------------- Settings, password, backup
            case "settings.get":
                return _settings.Current;
            case "settings.save":
            {
                var saved = _settings.Save(p.Deserialize<AppSettings>(Json.Options) ?? throw new ArgumentException("Invalid settings."));
                _mcp.Apply(saved);
                return saved;
            }
            case "mcp.status":
                return new { running = _mcp.Running, error = _mcp.Error };
            case "vault.changePassword":
            {
                _vault.ChangePassword(Str(p, "oldPassword"), Str(p, "newPassword"));
                if (!_sync.Settings.IsConfigured) return new { cloud = "none" };
                try
                {
                    await _sync.ForceUploadAsync();
                    return new { cloud = "updated" };
                }
                catch (Exception ex)
                {
                    return new { cloud = "failed", message = Friendly(ex) };
                }
            }
            case "vault.export":
            {
                var path = await _pickSavePath($"MultiServerView-Backup-{DateTime.Now:yyyy-MM-dd}.msv");
                if (path == null) return new { saved = false };
                _vault.ExportTo(path);
                return new { saved = true, path };
            }
            case "vault.info":
                return new { path = _vault.FilePath, version = typeof(Bridge).Assembly.GetName().Version?.ToString(3) ?? "" };
            case "vault.reveal":
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{_vault.FilePath}\"") { UseShellExecute = true });
                return null;
            case "sync.adoptPassword":
                await _sync.AdoptCloudPasswordAsync(Str(p, "password"));
                return null;
            case "sync.forceUpload":
                await _sync.ForceUploadAsync();
                return null;

            // ---------------------------------------------------------- Further imports
            case "import.sshconfig":
                return ImportResult(SshConfigImporter.Parse(Str(p, "text")), Opt(p, "workspace"));
            case "import.putty":
                return ImportResult(Opt(p, "text").Length > 0 ? PuttyImporter.ParseReg(Str(p, "text")) : PuttyImporter.FromSessions(ReadPuttyRegistry()), Opt(p, "workspace"));
            case "import.csv":
                return ImportResult(CsvImporter.Parse(Str(p, "text")), Opt(p, "workspace"));

            // ---------------------------------------------------------- VMware Workstation
            case "vmware.scan":
            {
                var folders = new List<string> { VmwareLibrary.DefaultVmFolder };
                if (Opt(p, "folder").Length > 0) folders.Add(Opt(p, "folder"));
                var vmrun = Vmrun.Find();
                var have = _vault.List().Select(s => s.VmwareRef).Where(r => r.Length > 0).ToHashSet();
                var vms = (await ReadVmwareVmsAsync(VmwareLibrary.FindVmx(VmwareLibrary.DefaultInventoryPath, folders), vmrun)).Select(vm =>
                {
                    var (protocol, os) = VmwareLibrary.MapGuest(vm.GuestOs);
                    return new
                    {
                        vmx = vm.VmxPath, name = vm.Name, guestOs = vm.GuestOs, os, protocol = protocol.ToString().ToLowerInvariant(),
                        connection = vm.ConnectionType, running = vm.Running, ip = vm.Ip,
                        imported = have.Contains(VmwareImporter.Ref(vm.VmxPath))
                    };
                }).ToList();
                return new { vmrun = vmrun != null, vms };
            }
            case "vmware.import":
            {
                var paths = p.TryGetProperty("vms", out var arr) && arr.ValueKind == JsonValueKind.Array
                    ? arr.EnumerateArray().Select(e => e.GetString() ?? "").Where(x => x.Length > 0).ToList()
                    : new List<string>();
                if (paths.Count == 0) throw new ArgumentException("Please choose at least one VM.");

                var vmrun = Vmrun.Find();
                var vms = await ReadVmwareVmsAsync(paths, vmrun);
                var have = _vault.List().Select(s => s.VmwareRef).Where(r => r.Length > 0).ToHashSet();
                var result = await VmwareImporter.BuildAsync(vms, new VmwareImportOptions(Opt(p, "workspace"), Opt(p, "identityId")), have, vmrun);
                return new { imported = _vault.UpsertMany(result.Servers), noIp = result.NoIp, skipped = result.Skipped };
            }
            case "vmware.removeImported":
            {
                var ids = _vault.List().Where(s => s.VmwareRef.Length > 0).Select(s => s.Id).ToList();
                return new { removed = _vault.DeleteMany(ids) };
            }

            // ---------------------------------------------------------- Proxmox
            case "proxmox.list":
                return _vault.ListProxmox().Select(ProxmoxDto).ToList();
            case "proxmox.probe":
            {
                var cert = await ProxmoxClient.ProbeAsync(Str(p, "url"));
                return new { fingerprint = cert.Fingerprint, subject = cert.Subject, notAfter = cert.NotAfter, trusted = cert.TrustedBySystem };
            }
            case "proxmox.test":
            {
                using var client = ProxmoxClient.Create(ProxmoxFromParams(p));
                var version = await client.VersionAsync();
                var vms = await client.ListVmsAsync();
                return new { version, vms = vms.Count };
            }
            case "proxmox.save":
            {
                var entry = ProxmoxFromParams(p);
                if (string.IsNullOrWhiteSpace(entry.Name)) entry.Name = new Uri(ProxmoxClient.NormalizeUrl(entry.Url)).Host;
                return ProxmoxDto(_vault.UpsertProxmox(entry));
            }
            case "proxmox.delete":
                _vault.DeleteProxmox(Str(p, "id"));
                return null;
            case "proxmox.vms":
            {
                var entry = _vault.FindProxmox(Str(p, "id")) ?? throw new KeyNotFoundException("Connection not found.");
                using var client = ProxmoxClient.Create(entry);
                var have = ImportedRefs();
                return (await client.ListVmsAsync()).Select(v => new
                {
                    node = v.Node, vmid = v.VmId, type = v.Type, name = v.Name, status = v.Status, pool = v.Pool, tags = v.Tags, template = v.Template,
                    imported = have.Contains(ProxmoxImporter.Ref(entry.Id, v.Node, v.VmId))
                }).ToList();
            }
            case "proxmox.removeImported":
            {
                var id = Str(p, "id");
                var ids = _vault.List().Where(s => s.ProxmoxRef.StartsWith(id + ":", StringComparison.Ordinal)).Select(s => s.Id).ToList();
                return new { removed = _vault.DeleteMany(ids) };
            }
            case "proxmox.import":
            {
                var entry = _vault.FindProxmox(Str(p, "id")) ?? throw new KeyNotFoundException("Connection not found.");
                var picked = p.TryGetProperty("vms", out var arr) && arr.ValueKind == JsonValueKind.Array
                    ? arr.EnumerateArray().Select(e => (Node: Str(e, "node"), VmId: Long(e, "vmid"))).ToHashSet()
                    : new HashSet<(string, long)>();
                if (picked.Count == 0) throw new ArgumentException("Please choose at least one VM.");

                using var client = ProxmoxClient.Create(entry);
                var vms = (await client.ListVmsAsync()).Where(v => picked.Contains((v.Node, v.VmId))).ToList();
                var groupBy = Opt(p, "groupBy") switch { "pool" => ProxmoxGroupBy.Pool, "node" => ProxmoxGroupBy.Node, _ => ProxmoxGroupBy.None };
                var options = new ProxmoxImportOptions(Opt(p, "workspace"), Bool(p, "workspaceFromPool"), Opt(p, "identityId"), groupBy);
                var result = await ProxmoxImporter.BuildAsync(client, entry.Id, vms, options, ImportedRefs());
                var imported = _vault.UpsertMany(result.Servers);
                return new { imported, noIp = result.NoIp, skipped = result.Skipped };
            }

            // ---------------------------------------------------------- Cloud
            case "sync.get":
                return SyncDto(_sync.Settings);
            case "sync.save":
            {
                var cfg = ReadSyncConfig(p);
                _sync.Save(cfg);
                return SyncDto(cfg);
            }
            case "window.command":
                return new { maximized = _window(Str(p, "command")) };
            case "sync.pickFolder":
                return new { path = await _pickFolder("Choose sync folder (e.g. a folder in OneDrive or iCloud Drive)") };
            case "dialog.pickFolder":
                return new { path = await _pickFolder(Opt(p, "title").Length > 0 ? Opt(p, "title") : "Choose folder") };
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
                _emit(new { @event = "sync.status", state = "off", message = "Sync disconnected" });
                return null;
            case "sync.downloadInitial":
                await _sync.DownloadInitialAsync(ReadSyncConfig(p, keepStoredPassword: false));
                return null;

            default:
                throw new InvalidOperationException($"Unknown method: {method}");
        }
    }

    // ================================================================== SSH

    private async Task OpenSshAsync(string sessionId, string serverId, uint cols, uint rows, JsonElement p)
    {
        CloseSsh(sessionId); // reconnect with the same id

        var server = _vault.FindResolved(serverId) ?? throw new KeyNotFoundException("Server not found.");
        server = WithTypedCredentials(server, p, out var saveCredentials);
        var key = server.Auth == AuthMethod.Key ? _vault.FindKey(server.KeyId) : null;
        var hid = _history.Begin(server, "ssh");

        var session = new SshTerminalSession(server, key, CheckHost, Jumps);
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
            saveCredentials?.Invoke();
        }
        catch (Exception ex)
        {
            _ssh.TryRemove(sessionId, out _);
            pump.Dispose();
            _history.End(hid, false, Friendly(ex));
            throw;
        }

        // Detect the operating system in the background (for the icon); errors do not matter.
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

    /// <summary>Bundles many small SSH outputs into a few messages (every ~6 ms) so the UI is not overwhelmed.</summary>
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

    // ================================================================== Helpers

    /// <summary>Takes over imported hosts, skips duplicates (same name, host, port, protocol) and redirects jump host references.</summary>
    private object ImportResult(Core.Import.ImportResult result, string workspace = "")
    {
        var existing = _vault.List();
        var remap = new Dictionary<string, string>();
        var fresh = new List<ServerEntry>();
        foreach (var s in result.Servers)
        {
            var dup = existing.FirstOrDefault(e => e.Name == s.Name && e.Host == s.Host && e.Port == s.Port && e.Protocol == s.Protocol);
            if (dup != null) remap[s.Id] = dup.Id;
            else fresh.Add(s);
        }
        // If a workspace is selected in the UI, hosts without a workspace of their own end up there.
        foreach (var s in fresh)
            if (s.Workspace.Length == 0) s.Workspace = workspace.Trim();
        foreach (var s in fresh)
            if (s.JumpHostId.Length > 0 && remap.TryGetValue(s.JumpHostId, out var mapped)) s.JumpHostId = mapped;

        var keysImported = 0;
        var keysSkipped = 0;
        if (result.KeyFiles != null)
        {
            var byFile = new Dictionary<string, KeyEntry>(StringComparer.OrdinalIgnoreCase);
            foreach (var s in fresh)
            {
                if (!result.KeyFiles.TryGetValue(s.Id, out var file)) continue;
                try
                {
                    var path = Environment.ExpandEnvironmentVariables(file.StartsWith("~") ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), file[1..].TrimStart('/', (char)92)) : file);
                    if (!byFile.TryGetValue(path, out var key))
                    {
                        var text = File.ReadAllText(path);
                        var kind = KeyTools.Inspect(text, "");
                        key = _vault.UpsertKey(new KeyEntry { Name = Path.GetFileName(path), PrivateKey = text, Kind = kind });
                        byFile[path] = key;
                        keysImported++;
                    }
                    s.Auth = AuthMethod.Key;
                    s.KeyId = key.Id;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SshConnectException or ArgumentException)
                {
                    keysSkipped++; // missing file, protected by a passphrase or not an OpenSSH key: the host just keeps password login
                }
            }
        }

        var imported = _vault.UpsertMany(fresh);
        var passwordsFailed = result.PasswordsFailed;
        return new { imported, skipped = result.Skipped, duplicates = result.Servers.Count - fresh.Count, keysImported, keysSkipped, passwordsFailed };
    }

    /// <summary>Reads saved PuTTY sessions directly from the Windows registry.</summary>
    private static IEnumerable<PuttySession> ReadPuttyRegistry()
    {
        using var root = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\SimonTatham\PuTTY\Sessions");
        if (root == null) throw new InvalidOperationException("No saved PuTTY sessions were found on this PC.");
        foreach (var name in root.GetSubKeyNames())
        {
            using var k = root.OpenSubKey(name);
            if (k == null) continue;
            yield return new PuttySession(
                Uri.UnescapeDataString(name),
                k.GetValue("HostName") as string ?? "",
                k.GetValue("PortNumber") is int port ? port : 22,
                k.GetValue("Protocol") as string ?? "ssh",
                k.GetValue("UserName") as string ?? "");
        }
    }

    private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromMinutes(5) };

    private static string AppVersion => typeof(Bridge).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    /// <summary>Installed by the setup wizard (the uninstaller sits next to the exe) - otherwise it is the portable ZIP.</summary>
    private static bool IsInstalled() => File.Exists(Path.Combine(AppContext.BaseDirectory, "unins000.exe"));

    private static string? FindTeamViewer()
    {
        foreach (var root in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles) })
        {
            if (string.IsNullOrEmpty(root)) continue;
            var exe = Path.Combine(root, "TeamViewer", "TeamViewer.exe");
            if (File.Exists(exe)) return exe;
        }
        return null;
    }

    private SftpSession Sftp(JsonElement p) =>
        _sftp.TryGetValue(Str(p, "sessionId"), out var s) ? s : throw new InvalidOperationException("The SFTP connection is not open.");

    private void CloseSftp(string sessionId)
    {
        if (_sftp.TryRemove(sessionId, out var s)) s.Dispose();
        if (_historyIds.TryRemove(sessionId, out var hid)) _history.End(hid, true);
    }

    private static List<string> Strings(JsonElement p, string name) =>
        p.ValueKind == JsonValueKind.Object && p.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array
            ? v.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString() ?? "").ToList()
            : throw new ArgumentException($"Missing parameter: {name}");

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

    /// <summary>
    /// After a failed sign-in the UI asks again (like PuTTY) and sends user/password along.
    /// Only applies to this connection; with "save" it is stored in the host after a successful connection.
    /// </summary>
    private ServerEntry WithTypedCredentials(ServerEntry server, JsonElement p, out Action? saveAfterSuccess)
    {
        saveAfterSuccess = null;
        var password = Opt(p, "password");
        var user = Opt(p, "username").Trim();
        if (password.Length == 0 && user.Length == 0) return server;

        var typed = server.Clone();
        if (user.Length > 0) typed.Username = user;
        if (password.Length > 0)
        {
            typed.Password = password;
            typed.Auth = AuthMethod.Password;
        }

        if (Bool(p, "save"))
        {
            var id = server.Id;
            saveAfterSuccess = () =>
            {
                var stored = _vault.Find(id);
                if (stored == null || !string.IsNullOrEmpty(stored.IdentityId)) return;
                var updated = stored.Clone();
                if (user.Length > 0) updated.Username = user;
                if (password.Length > 0 && stored.Auth == AuthMethod.Password) updated.Password = password;
                _vault.Upsert(updated);
            };
        }
        return typed;
    }

    private static string? PublicKeyOf(KeyEntry k)
    {
        try { return KeyTools.PublicKeyLine(k); }
        catch (Exception) { return null; }
    }

    /// <summary>Reads the .vmx files: settings, whether the VM is running (vmrun, otherwise the lock file) and its address from the DHCP entry.</summary>
    private static async Task<List<VmwareVm>> ReadVmwareVmsAsync(IEnumerable<string> paths, IVmrun? vmrun)
    {
        var running = vmrun != null ? (await vmrun.ListRunningAsync()).Select(VmwareImporter.Ref).ToHashSet() : new HashSet<string>();
        var leases = VmwareLibrary.ReadLeases();
        var list = new List<VmwareVm>();
        foreach (var path in paths)
        {
            try
            {
                var isRunning = vmrun != null ? running.Contains(VmwareImporter.Ref(path)) : Directory.Exists(path + ".lck");
                list.Add(VmwareLibrary.FromVmx(path, File.ReadAllText(path), isRunning, leases));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // skip unreadable VM
            }
        }
        return list;
    }

    private static object ProxmoxDto(ProxmoxEntry e) => new
    {
        id = e.Id,
        name = e.Name,
        url = e.Url,
        tokenId = e.TokenId,
        hasSecret = e.TokenSecret.Length > 0,
        certFingerprint = e.CertFingerprint.Length > 0 ? ProxmoxClient.Group(ProxmoxClient.Clean(e.CertFingerprint)) : "",
        updatedAt = e.UpdatedAt
    };

    /// <summary>Which Proxmox VMs already exist as hosts (origin "connection:node:VM number").</summary>
    private HashSet<string> ImportedRefs() =>
        _vault.List().Select(s => s.ProxmoxRef).Where(r => r.Length > 0).ToHashSet();

    /// <summary>
    /// Reads a connection from the parameters. With "id" missing values (above all the secret, which never goes to the UI)
    /// are filled in from the saved connection.
    /// </summary>
    private ProxmoxEntry ProxmoxFromParams(JsonElement p)
    {
        var stored = Opt(p, "id").Length > 0 ? _vault.FindProxmox(Opt(p, "id")) : null;
        var secret = Opt(p, "tokenSecret");
        return new ProxmoxEntry
        {
            Id = stored?.Id ?? "",
            Name = Opt(p, "name").Trim(),
            Url = Str(p, "url").Trim(),
            TokenId = Str(p, "tokenId").Trim(),
            TokenSecret = secret.Length > 0 ? secret.Trim() : stored?.TokenSecret ?? "",
            CertFingerprint = ProxmoxClient.Clean(Opt(p, "certFingerprint"))
        };
    }

    private static object KeyDto(KeyEntry k) => new
    {
        id = k.Id,
        name = k.Name,
        kind = k.Kind,
        publicKey = PublicKeyOf(k) ?? "",
        fingerprint = PublicKeyOf(k) is { } pk ? KeyTools.Fingerprint(pk) : "",
        hasPassphrase = k.Passphrase.Length > 0,
        updatedAt = k.UpdatedAt
    };

    private static object SyncDto(SyncSettings s) => new
    {
        configured = s.IsConfigured,
        mode = s.Mode,
        folder = s.Folder,
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
            Mode = Opt(p, "mode") is SyncSettings.ModeFolder or SyncSettings.ModeWebDav ? Opt(p, "mode") : SyncSettings.ModeSupabase,
            Folder = Opt(p, "folder").Trim(),
            Url = Opt(p, "url").Trim(),
            AnonKey = Opt(p, "anonKey").Trim(),
            Email = Opt(p, "email").Trim(),
            // Empty password field = "unchanged" (the saved one is never sent to the UI).
            Password = password.Length > 0 ? password : keepStoredPassword ? stored.Password : "",
            LastSync = stored.LastSync
        };
    }

    private static Rectangle Bounds(JsonElement p) =>
        new(Int(p, "x", 0), Int(p, "y", 0), Math.Max(Int(p, "w", 640), 1), Math.Max(Int(p, "h", 480), 1));

    private static string Str(JsonElement p, string name) =>
        p.ValueKind == JsonValueKind.Object && p.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? ""
            : throw new ArgumentException($"Missing parameter: {name}");

    private static string Opt(JsonElement p, string name) =>
        p.ValueKind == JsonValueKind.Object && p.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? ""
            : "";

    private static int Int(JsonElement p, string name, int fallback) =>
        p.ValueKind == JsonValueKind.Object && p.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number
            ? (int)Math.Round(v.GetDouble())
            : fallback;

    private static long Long(JsonElement p, string name) =>
        p.ValueKind == JsonValueKind.Object && p.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt64() : 0;

    private static bool Bool(JsonElement p, string name) =>
        p.ValueKind == JsonValueKind.Object && p.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    internal static string Friendly(Exception ex) => ex switch
    {
        WrongPasswordException => "Wrong password.",
        SshConnectException or SyncException => ex.Message,
        Renci.SshNet.Common.SftpPathNotFoundException => "Path not found.",
        Renci.SshNet.Common.SftpPermissionDeniedException => "Permission denied.",
        OperationCanceledException => "Cancelled.",
        Renci.SshNet.Common.SshException or IOException or UnauthorizedAccessException => ex.Message,
        ArgumentException or InvalidOperationException or KeyNotFoundException => ex.Message,
        System.Security.Cryptography.CryptographicException => "The vault file is invalid or damaged.",
        _ => "Unexpected error: " + ex.Message
    };

    public void Dispose()
    {
        _auto.Dispose();
        _mcp.Dispose();
        _forwards.Dispose();
        foreach (var id in _tunnels.Keys.ToList()) CloseTunnel(id);
        foreach (var t in _transfers.Values) t.Cancel();
        foreach (var id in _sftp.Keys.ToList()) CloseSftp(id);
        CloseAllSessions();
        _vncProxy.Dispose();
    }
}
