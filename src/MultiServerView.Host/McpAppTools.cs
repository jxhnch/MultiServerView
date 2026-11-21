using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using MultiServerView.Core;
using MultiServerView.Core.Mcp;

namespace MultiServerView.Host;

/// <summary>
/// The actions of the app for AI tools: the AI can do what the user can do in the UI (manage hosts, keys, snippets,
/// identities and port forwards, transfer and change files on servers and on this PC, work in interactive terminals).
/// Almost every tool calls the same bridge method the UI uses, so behaviour stays identical.
/// Saved passwords, passphrases and private keys are never returned to the AI.
/// </summary>
internal sealed class McpAppTools : IMcpExtraTools
{
    private delegate Task<object?> Run(JsonObject args, CancellationToken ct);

    private readonly VaultStore _vault;
    private readonly Func<string, JsonElement, Task<object?>> _bridge;
    private readonly TerminalHub _terminals;
    private readonly ScreenHub _screens;
    private readonly Dictionary<string, (McpToolDef Def, Run Run)> _tools = new(StringComparer.Ordinal);

    // The MCP server can be switched to read-only and limited to some workspaces (settings, AI tools).
    private readonly Func<bool> _readOnly;
    private readonly Func<string, bool> _workspaceAllowed;
    private readonly Func<bool> _workspaceLimited;

    /// <summary>Tools that only look. In read-only mode nothing else is offered.</summary>
    private static readonly HashSet<string> ReadOnlyTools = new(StringComparer.Ordinal)
    {
        "get_host", "list_keys", "list_snippets", "list_identities", "list_forwards", "connection_history", "list_known_hosts",
        "file_info", "file_checksum", "local_list", "local_read_file", "list_proxmox", "list_proxmox_vms", "list_vmware_vms", "vm_status",
        "screen_list", "screen_screenshot", "list_terminals",
    };

    /// <summary>Tools that are not tied to one host. With a workspace limit they are not offered at all.</summary>
    private static readonly HashSet<string> GlobalTools = new(StringComparer.Ordinal)
    {
        "add_host", "list_keys", "generate_key", "save_key", "delete_key", "list_snippets", "save_snippet", "delete_snippet",
        "list_identities", "save_identity", "delete_identity", "list_forwards", "connection_history", "list_known_hosts", "remove_known_host",
        "local_list", "local_read_file", "local_write_file", "local_make_directory", "local_rename", "local_delete",
        "list_proxmox", "list_proxmox_vms", "list_vmware_vms", "vm_status", "list_terminals",
        "screen_list", "screen_screenshot", "screen_click", "screen_move", "screen_drag", "screen_scroll", "screen_type", "screen_key", "screen_close",
    };

    private bool Visible(string name) =>
        _tools.ContainsKey(name) && (!_readOnly() || ReadOnlyTools.Contains(name)) && (!_workspaceLimited() || !GlobalTools.Contains(name));

    public McpAppTools(VaultStore vault, Func<string, JsonElement, Task<object?>> bridge, Action<Action<object>> subscribeEvents, ScreenHub screens,
        Func<bool>? readOnly = null, Func<string, bool>? workspaceAllowed = null, Func<bool>? workspaceLimited = null)
    {
        _vault = vault;
        _screens = screens;
        _readOnly = readOnly ?? (() => false);
        _workspaceAllowed = workspaceAllowed ?? (_ => true);
        _workspaceLimited = workspaceLimited ?? (() => false);
        _bridge = bridge;
        _terminals = new TerminalHub(bridge, subscribeEvents);
        Register();
    }

    public IReadOnlyList<McpToolDef> Tools => _tools.Values.Where(t => Visible(t.Def.Name)).Select(t => t.Def).ToList();
    public bool Has(string name) => Visible(name);

    public async Task<McpToolOutput> CallAsync(string name, JsonObject args, CancellationToken ct)
    {
        if (!_vault.IsUnlocked) throw new InvalidOperationException("The vault is locked. Unlock MultiServerView first.");
        if (!Visible(name)) throw new InvalidOperationException(_readOnly() && !ReadOnlyTools.Contains(name)
            ? "The MCP server is in read-only mode. Turn it off in MultiServerView (AI tools) to use this tool."
            : "This tool is not available because the MCP server is limited to some workspaces.");
        var result = await _tools[name].Run(args, ct);
        return result switch
        {
            null => new McpToolOutput("Done."),
            McpToolOutput o => o,
            string s => new McpToolOutput(s),
            JsonNode n => new McpToolOutput(n.ToJsonString(Json.Options)),
            _ => new McpToolOutput(JsonSerializer.Serialize(result, Json.Options))
        };
    }

    // ------------------------------------------------------------------------------------------------ Registration

    private static JsonObject P(string type, string description) => new() { ["type"] = type, ["description"] = description };
    private static JsonObject Arr(string description) => new() { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" }, ["description"] = description };

    private void Add(string name, string description, JsonObject? props, string[]? required, Run run)
    {
        var schema = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = props ?? new JsonObject(),
            ["required"] = new JsonArray((required ?? []).Select(r => (JsonNode)JsonValue.Create(r)!).ToArray())
        };
        var readOnly = ReadOnlyTools.Contains(name);
        var destructive = !readOnly && (name.StartsWith("delete_", StringComparison.Ordinal) || name.StartsWith("remove_", StringComparison.Ordinal) || name is "local_delete" or "local_write_file" or "local_rename" or "rename_file" or "chmod_file" or "update_host" or "install_key");
        var annotations = new JsonObject { ["readOnlyHint"] = readOnly, ["destructiveHint"] = destructive, ["idempotentHint"] = readOnly, ["openWorldHint"] = true };
        _tools[name] = (new McpToolDef(name, description, schema, annotations), run);
    }

    private void Register()
    {
        const string HostDoc = "Host name or id from list_hosts.";
        JsonObject Host() => P("string", HostDoc); // a JSON node can only have one parent

        // ------------------------------------------------------------ Hosts
        Add("get_host", "Show all settings of one saved host (the password is never shown, only whether one is saved).", new() { ["host"] = Host() }, ["host"],
            (a, _) => Task.FromResult<object?>(Redact(Node(FindServer(Req(a, "host"))))));

        var hostFields = () => new JsonObject
        {
            ["name"] = P("string", "Display name."),
            ["host"] = P("string", "Address (IP or DNS name). For TeamViewer: the TeamViewer ID. For serial: the port (COM3, /dev/ttyUSB0)."),
            ["protocol"] = P("string", "ssh, rdp, vnc, telnet, serial or teamviewer."),
            ["port"] = P("integer", "Port; the default of the protocol if omitted. For serial: the baud rate (default 9600)."),
            ["username"] = P("string", "User name (for Windows domains: DOMAIN\\user)."),
            ["password"] = P("string", "Password (stored encrypted in the vault)."),
            ["auth"] = P("string", "SSH only: password or key."),
            ["key"] = P("string", "Name or id of a saved SSH key (use with auth=key)."),
            ["jump_host"] = P("string", "Name or id of another SSH host to connect through."),
            ["identity"] = P("string", "Name or id of a saved identity (overrides user name and password)."),
            ["workspace"] = P("string", "Workspace (top level group)."),
            ["group"] = P("string", "Group."),
            ["tags"] = Arr("Tags."),
            ["notes"] = P("string", "Free notes."),
            ["favorite"] = P("boolean", "Mark as favorite.")
        };
        Add("add_host", "Add a new host (SSH, RDP, VNC or TeamViewer) to the vault.", hostFields(), ["name", "host"], async (a, _) =>
        {
            var s = new ServerEntry();
            ApplyHostFields(s, a, creating: true);
            return Redact(await Call("servers.save", s));
        });
        Add("update_host", "Change settings of a saved host. Only the given fields change.", Merge(new JsonObject { ["target"] = P("string", "Name or id of the host to change.") }, hostFields()), ["target"], async (a, _) =>
        {
            var s = FindServer(Req(a, "target")).Clone();
            ApplyHostFields(s, a, creating: false);
            return Redact(await Call("servers.save", s));
        });
        Add("delete_host", "Delete a host from the vault.", new() { ["host"] = Host() }, ["host"], async (a, _) =>
        {
            var s = FindServer(Req(a, "host"));
            await Call("servers.delete", new { id = s.Id });
            return $"Deleted {s.Name}.";
        });

        // ------------------------------------------------------------ Keys
        Add("list_keys", "List the SSH keys in the vault (name, type, public key, fingerprint; never the private key).", null, null, (_, _) => Call("keys.list", new { }));
        Add("generate_key", "Generate a new SSH key pair and save it in the vault. Returns the public key.",
            new() { ["name"] = P("string", "Name of the key."), ["type"] = P("string", "ed25519 (default) or rsa."), ["bits"] = P("integer", "RSA key size, default 3072."), ["comment"] = P("string", "Comment inside the public key.") },
            ["name"], (a, _) => Call("keys.generate", new { name = Req(a, "name"), type = Opt(a, "type"), bits = IntOr(a, "bits", 3072), comment = Opt(a, "comment") }));
        Add("save_key", "Import an existing private key into the vault.",
            new() { ["name"] = P("string", "Name of the key."), ["private_key"] = P("string", "The private key in PEM or OpenSSH format."), ["passphrase"] = P("string", "Passphrase of the key, if any.") },
            ["name", "private_key"], async (a, _) => Redact(await Call("keys.save", new { name = Req(a, "name"), privateKey = Req(a, "private_key"), passphrase = Opt(a, "passphrase") })));
        Add("install_key", "Install the public key of a saved key into ~/.ssh/authorized_keys of an SSH host (the host's saved login is used).",
            new() { ["host"] = Host(), ["key"] = P("string", "Name or id of the key."), ["use_for_host"] = P("boolean", "Also switch the host to key login (default false).") },
            ["host", "key"], async (a, _) =>
            {
                var s = FindServer(Req(a, "host"));
                await Call("keys.install", new { serverId = s.Id, keyId = FindKey(Req(a, "key")).Id, switchHost = Flag(a, "use_for_host") });
                return $"Key installed on {s.Name}.";
            });
        Add("delete_key", "Delete an SSH key from the vault.", new() { ["key"] = P("string", "Name or id of the key.") }, ["key"], async (a, _) =>
        {
            var k = FindKey(Req(a, "key"));
            await Call("keys.delete", new { id = k.Id });
            return $"Deleted key {k.Name}.";
        });

        // ------------------------------------------------------------ Snippets, identities
        Add("list_snippets", "List the saved command snippets.", null, null, (_, _) => Call("snippets.list", new { }));
        Add("save_snippet", "Create or change a command snippet (variables like {{name}} are allowed).",
            new() { ["name"] = P("string", "Name."), ["command"] = P("string", "The command text."), ["auto_run"] = P("boolean", "Send Enter right away (default true).") },
            ["name", "command"], (a, _) =>
            {
                var existing = _vault.ListSnippets().FirstOrDefault(x => x.Name.Equals(Req(a, "name"), StringComparison.OrdinalIgnoreCase));
                return Call("snippets.save", new { id = existing?.Id ?? Guid.NewGuid().ToString("N"), name = Req(a, "name"), command = Req(a, "command"), autoRun = a.ContainsKey("auto_run") ? Flag(a, "auto_run") : existing?.AutoRun ?? true });
            });
        Add("delete_snippet", "Delete a snippet.", new() { ["snippet"] = P("string", "Name or id.") }, ["snippet"], async (a, _) =>
        {
            var x = _vault.ListSnippets().FirstOrDefault(s => s.Id == Req(a, "snippet") || s.Name.Equals(Req(a, "snippet"), StringComparison.OrdinalIgnoreCase)) ?? throw new KeyNotFoundException("Snippet not found.");
            await Call("snippets.delete", new { id = x.Id });
            return $"Deleted snippet {x.Name}.";
        });
        Add("list_identities", "List the saved identities (shared logins). Passwords are never shown.", null, null, async (_, _) => Redact(await Call("identities.list", new { })));
        Add("save_identity", "Create or change an identity (a login that several hosts can share).",
            new() { ["name"] = P("string", "Name."), ["username"] = P("string", "User name."), ["password"] = P("string", "Password."), ["auth"] = P("string", "password (default) or key."), ["key"] = P("string", "Name or id of a key (auth=key).") },
            ["name", "username"], async (a, _) =>
            {
                var existing = _vault.ListIdentities().FirstOrDefault(x => x.Name.Equals(Req(a, "name"), StringComparison.OrdinalIgnoreCase));
                var key = Opt(a, "key").Length > 0 ? FindKey(Opt(a, "key")).Id : existing?.KeyId ?? "";
                var auth = Opt(a, "auth").Length > 0 ? Opt(a, "auth").ToLowerInvariant() : existing != null ? existing.Auth.ToString().ToLowerInvariant() : "password";
                return Redact(await Call("identities.save", new { id = existing?.Id ?? Guid.NewGuid().ToString("N"), name = Req(a, "name"), username = Req(a, "username"), password = a.ContainsKey("password") ? Opt(a, "password") : existing?.Password ?? "", auth, keyId = key }));
            });
        Add("delete_identity", "Delete an identity.", new() { ["identity"] = P("string", "Name or id.") }, ["identity"], async (a, _) =>
        {
            var x = _vault.ListIdentities().FirstOrDefault(s => s.Id == Req(a, "identity") || s.Name.Equals(Req(a, "identity"), StringComparison.OrdinalIgnoreCase)) ?? throw new KeyNotFoundException("Identity not found.");
            await Call("identities.delete", new { id = x.Id });
            return $"Deleted identity {x.Name}.";
        });

        // ------------------------------------------------------------ Port forwarding
        Add("list_forwards", "List the port forwarding rules and whether they are running.", null, null, (_, _) => Call("forwards.list", new { }));
        Add("save_forward", "Create or change a port forwarding rule over an SSH host.",
            new()
            {
                ["name"] = P("string", "Name of the rule."), ["host"] = P("string", "SSH host to tunnel through."),
                ["type"] = P("string", "local (-L), remote (-R) or dynamic (-D, SOCKS)."), ["bind_host"] = P("string", "Address to listen on, default 127.0.0.1."),
                ["bind_port"] = P("integer", "Port to listen on."), ["dest_host"] = P("string", "Target address (not for dynamic)."), ["dest_port"] = P("integer", "Target port (not for dynamic).")
            },
            ["name", "host", "type", "bind_port"], (a, _) =>
            {
                var existing = _vault.ListForwards().FirstOrDefault(x => x.Name.Equals(Req(a, "name"), StringComparison.OrdinalIgnoreCase));
                return Call("forwards.save", new
                {
                    id = existing?.Id ?? Guid.NewGuid().ToString("N"), name = Req(a, "name"), serverId = FindServer(Req(a, "host")).Id, type = Req(a, "type").ToLowerInvariant(),
                    bindHost = Opt(a, "bind_host").Length > 0 ? Opt(a, "bind_host") : "127.0.0.1", bindPort = IntOr(a, "bind_port", 0), destHost = Opt(a, "dest_host"), destPort = IntOr(a, "dest_port", 0)
                });
            });
        foreach (var (tool, method, past, verb) in new[] { ("start_forward", "forwards.start", "Started", "Start"), ("stop_forward", "forwards.stop", "Stopped", "Stop"), ("delete_forward", "forwards.delete", "Deleted", "Delete") })
            Add(tool, $"{verb} a port forwarding rule.", new() { ["rule"] = P("string", "Name or id of the rule.") }, ["rule"], async (a, _) =>
            {
                var r = _vault.ListForwards().FirstOrDefault(x => x.Id == Req(a, "rule") || x.Name.Equals(Req(a, "rule"), StringComparison.OrdinalIgnoreCase)) ?? throw new KeyNotFoundException("Rule not found.");
                FindServer(r.ServerId); // not allowed when its host is outside the allowed workspaces
                await Call(method, new { id = r.Id });
                return $"{past} {r.Name}.";
            });

        // ------------------------------------------------------------ History, known hosts
        Add("connection_history", "Recent connections (host, kind, time, success).", null, null, (_, _) => Call("history.list", new { }));
        Add("list_known_hosts", "List the trusted SSH host keys (fingerprints).", null, null, (_, _) => Call("knownhosts.list", new { }));
        Add("remove_known_host", "Forget a trusted SSH host key (for example after a server was reinstalled).", new() { ["id"] = P("string", "Entry id from list_known_hosts, usually host:port.") }, ["id"],
            async (a, _) => { await Call("knownhosts.remove", new { id = Req(a, "id") }); return "Removed."; });

        // ------------------------------------------------------------ Files on servers (SFTP)
        Add("upload_file", "Upload files or folders from this PC to a folder on an SSH host.",
            new() { ["host"] = Host(), ["local_paths"] = Arr("Paths on this PC."), ["remote_dir"] = P("string", "Target folder on the server.") }, ["host", "local_paths", "remote_dir"],
            (a, _) => WithSftp(Req(a, "host"), sid => Call("sftp.upload", new { sessionId = sid, transferId = NewId(), localPaths = Strings(a, "local_paths"), remoteDir = Req(a, "remote_dir") })));
        Add("download_file", "Download files or folders from an SSH host to a folder on this PC.",
            new() { ["host"] = Host(), ["remote_paths"] = Arr("Paths on the server."), ["local_dir"] = P("string", "Target folder on this PC.") }, ["host", "remote_paths", "local_dir"],
            (a, _) => WithSftp(Req(a, "host"), sid => Call("sftp.download", new { sessionId = sid, transferId = NewId(), remotePaths = Strings(a, "remote_paths"), localDir = Req(a, "local_dir") })));
        Add("make_directory", "Create a folder on an SSH host.", new() { ["host"] = Host(), ["path"] = P("string", "Folder path.") }, ["host", "path"],
            (a, _) => WithSftp(Req(a, "host"), sid => Call("sftp.mkdir", new { sessionId = sid, path = Req(a, "path") })));
        Add("rename_file", "Rename or move a file or folder on an SSH host.", new() { ["host"] = Host(), ["from"] = P("string", "Current path."), ["to"] = P("string", "New path.") }, ["host", "from", "to"],
            (a, _) => WithSftp(Req(a, "host"), sid => Call("sftp.rename", new { sessionId = sid, from = Req(a, "from"), to = Req(a, "to") })));
        Add("delete_file", "Delete a file or folder (recursively) on an SSH host. This cannot be undone.", new() { ["host"] = Host(), ["path"] = P("string", "Path.") }, ["host", "path"],
            (a, _) => WithSftp(Req(a, "host"), sid => Call("sftp.delete", new { sessionId = sid, path = Req(a, "path") })));
        Add("chmod_file", "Change permissions of files on an SSH host.", new() { ["host"] = Host(), ["paths"] = Arr("Paths."), ["mode"] = P("string", "Octal mode, e.g. 755."), ["recursive"] = P("boolean", "Apply to folder contents too.") }, ["host", "paths", "mode"],
            (a, _) => WithSftp(Req(a, "host"), sid => Call("sftp.chmod", new { sessionId = sid, paths = Strings(a, "paths"), mode = Convert.ToInt32(Req(a, "mode"), 8), recursive = Flag(a, "recursive") })));
        Add("file_info", "Size, permissions and time of a file or folder on an SSH host.", new() { ["host"] = Host(), ["path"] = P("string", "Path.") }, ["host", "path"],
            (a, _) => WithSftp(Req(a, "host"), sid => Call("sftp.stat", new { sessionId = sid, path = Req(a, "path") })));
        Add("file_checksum", "SHA-256 of a file on an SSH host.", new() { ["host"] = Host(), ["path"] = P("string", "Path.") }, ["host", "path"],
            (a, _) => WithSftp(Req(a, "host"), sid => Call("sftp.checksum", new { sessionId = sid, path = Req(a, "path") })));

        // ------------------------------------------------------------ Files on this PC
        Add("local_list", "List a folder on this PC (empty path = home folder).", new() { ["path"] = P("string", "Folder path.") }, null, (a, _) => Call("local.browse", new { path = Opt(a, "path") }));
        Add("local_read_file", "Read a text file on this PC.", new() { ["path"] = P("string", "File path."), ["max_bytes"] = P("integer", "Limit, default 1000000.") }, ["path"], (a, _) =>
        {
            var path = Req(a, "path");
            var max = Math.Clamp(IntOr(a, "max_bytes", 1_000_000), 1, 10_000_000);
            using var fs = File.OpenRead(path);
            var buffer = new byte[(int)Math.Min(fs.Length, max)];
            fs.ReadExactly(buffer);
            return Task.FromResult<object?>(Encoding.UTF8.GetString(buffer) + (fs.Length > max ? $"\n[... cut after {max} bytes of {fs.Length}]" : ""));
        });
        Add("local_write_file", "Write a text file on this PC (replaces it).", new() { ["path"] = P("string", "File path."), ["content"] = P("string", "New content.") }, ["path", "content"], (a, _) =>
        {
            var path = Req(a, "path");
            if (Path.GetDirectoryName(path) is { Length: > 0 } dir) Directory.CreateDirectory(dir);
            File.WriteAllText(path, Opt(a, "content"), new UTF8Encoding(false));
            return Task.FromResult<object?>("File written.");
        });
        Add("local_make_directory", "Create a folder on this PC.", new() { ["path"] = P("string", "Folder path.") }, ["path"], (a, _) => Call("local.mkdir", new { path = Req(a, "path") }));
        Add("local_rename", "Rename or move a file or folder on this PC.", new() { ["from"] = P("string", "Current path."), ["to"] = P("string", "New path.") }, ["from", "to"], (a, _) => Call("local.rename", new { from = Req(a, "from"), to = Req(a, "to") }));
        Add("local_delete", "Move files or folders on this PC to the recycle bin / trash.", new() { ["paths"] = Arr("Paths.") }, ["paths"], (a, _) => Call("local.delete", new { paths = Strings(a, "paths") }));

        // ------------------------------------------------------------ Proxmox, VMware
        Add("list_proxmox", "List the saved Proxmox connections.", null, null, (_, _) => Call("proxmox.list", new { }));
        Add("list_proxmox_vms", "List the VMs and containers of a Proxmox connection.", new() { ["connection"] = P("string", "Connection id from list_proxmox.") }, ["connection"], (a, _) => Call("proxmox.vms", new { id = Req(a, "connection") }));
        Add("list_vmware_vms", "List the VMware Workstation VMs on this PC.", null, null, (_, _) => Call("vmware.scan", new { }));

        Add("vm_status", "Power state (running, stopped, paused) of the hosts that were imported from Proxmox or VMware.", null, null, async (_, _) =>
        {
            var states = await Call("vms.status", new { }) as JsonObject ?? new JsonObject();
            var byId = _vault.List().ToDictionary(x => x.Id);
            return states.Select(kv => new { host = byId.TryGetValue(kv.Key, out var h) ? h.Name : kv.Key, id = kv.Key, state = kv.Value?.GetValue<string>() ?? "unknown" }).OrderBy(x => x.host).ToList();
        });
        Add("vm_power", "Start, shut down, restart, suspend or force-stop a VM that was imported from Proxmox or VMware (the host in MultiServerView stands for the VM).",
            new() { ["host"] = Host(), ["action"] = P("string", "start, shutdown (graceful), reboot, suspend or stop (forced, like pulling the plug).") }, ["host", "action"], async (a, _) =>
            {
                var vm = FindServer(Req(a, "host"));
                await Call("vms.power", new { hostId = vm.Id, action = Req(a, "action").ToLowerInvariant() });
                return $"{Req(a, "action")} sent to {vm.Name}. Call vm_status to see the new state.";
            });

        // ------------------------------------------------------------ Interactive terminals
        Add("terminal_open", "Open an interactive terminal on an SSH, Telnet or serial host (a real shell or device console: sudo, top, editors, installers, switch and router consoles). Returns a terminal id and the first output.",
            new() { ["host"] = Host(), ["cols"] = P("integer", "Width, default 120."), ["rows"] = P("integer", "Height, default 32.") }, ["host"],
            async (a, ct) => await _terminals.OpenAsync(FindServer(Req(a, "host")), IntOr(a, "cols", 120), IntOr(a, "rows", 32), ct));
        Add("terminal_send", "Type text into an open terminal. By default Enter is pressed afterwards. Then call terminal_read.",
            new() { ["terminal"] = P("string", "Terminal id."), ["text"] = P("string", "Text to type."), ["enter"] = P("boolean", "Press Enter after the text (default true).") }, ["terminal", "text"],
            async (a, ct) => await _terminals.SendAsync(Req(a, "terminal"), Opt(a, "text") + (a.ContainsKey("enter") && !Flag(a, "enter") ? "" : "\r"), ct));
        Add("terminal_key", "Press a special key in an open terminal: enter, tab, esc, backspace, up, down, left, right, ctrl+c, ctrl+d, ctrl+z, ctrl+l, ctrl+a, ctrl+e, ctrl+r.",
            new() { ["terminal"] = P("string", "Terminal id."), ["key"] = P("string", "Key name.") }, ["terminal", "key"],
            async (a, ct) => await _terminals.SendAsync(Req(a, "terminal"), TerminalHub.KeyBytes(Req(a, "key")), ct));
        Add("terminal_read", "Read what the terminal printed since the last read (waits until the output is quiet).",
            new() { ["terminal"] = P("string", "Terminal id."), ["wait_ms"] = P("integer", "How long to wait for output, default 800, max 30000."), ["max_chars"] = P("integer", "Limit, default 20000.") }, ["terminal"],
            async (a, ct) => await _terminals.ReadAsync(Req(a, "terminal"), Math.Clamp(IntOr(a, "wait_ms", 800), 0, 30_000), Math.Clamp(IntOr(a, "max_chars", 20_000), 100, 200_000), ct));
        Add("terminal_close", "Close an open terminal.", new() { ["terminal"] = P("string", "Terminal id.") }, ["terminal"], async (a, _) => await _terminals.CloseAsync(Req(a, "terminal")));
        Add("list_terminals", "List the terminals opened through MCP.", null, null, (_, _) => Task.FromResult<object?>(_terminals.List()));
        // ------------------------------------------------------------ Remote screens (RDP, VNC): see and operate like the user
        const string ScreenDoc = "Screen id from screen_open or screen_list.";
        JsonObject Screen() => P("string", ScreenDoc);
        JsonObject Xy(string what) => new() { ["x"] = P("integer", $"{what}: x in remote screen pixels (0 = left edge)."), ["y"] = P("integer", $"{what}: y in remote screen pixels (0 = top edge).") };
        JsonObject Follow() => P("boolean", "Return a screenshot after the action (default false).");

        Add("screen_open", "Open a remote desktop (RDP or VNC host) in a tab of the MultiServerView window and wait until it is connected. The window must be open and unlocked. Returns a screen id.",
            new() { ["host"] = Host() }, ["host"], async (a, ct) =>
            {
                var server = FindServer(Req(a, "host"));
                if (server.Protocol is not (Protocol.Rdp or Protocol.Vnc)) throw new InvalidOperationException($"\"{server.Name}\" is a {server.Protocol} host. Screens work for RDP and VNC hosts.");
                return await _screens.RequestAsync("open", new { serverId = server.Id }, TimeSpan.FromSeconds(60), ct);
            });
        Add("screen_list", "List the remote screens that are open in the window (id, host, kind, size, state).", null, null,
            async (_, ct) => await _screens.RequestAsync("list", new { }, TimeSpan.FromSeconds(15), ct));
        Add("screen_screenshot", "Take a screenshot of a remote screen. Coordinates for the other screen tools refer to this picture (its size is given in the text).",
            new() { ["screen"] = Screen() }, ["screen"], async (a, ct) => await Shot(a, 0, ct));
        Add("screen_click", "Click on a remote screen.", Merge(new JsonObject { ["screen"] = Screen(), ["button"] = P("string", "left (default), right or middle."), ["double"] = P("boolean", "Double click."), ["screenshot"] = Follow() }, Xy("Where")), ["screen", "x", "y"],
            async (a, ct) => await Act(a, "click", new { x = IntOr(a, "x", 0), y = IntOr(a, "y", 0), button = Opt(a, "button").Length > 0 ? Opt(a, "button") : "left", @double = Flag(a, "double") }, ct));
        Add("screen_move", "Move the mouse pointer on a remote screen (for hover menus and tooltips).", Merge(new JsonObject { ["screen"] = Screen(), ["screenshot"] = Follow() }, Xy("Where")), ["screen", "x", "y"],
            async (a, ct) => await Act(a, "move", new { x = IntOr(a, "x", 0), y = IntOr(a, "y", 0) }, ct));
        Add("screen_drag", "Press the left button at one point, move to another and release (drag and drop, selecting, resizing).",
            new() { ["screen"] = Screen(), ["from_x"] = P("integer", "Start x."), ["from_y"] = P("integer", "Start y."), ["to_x"] = P("integer", "End x."), ["to_y"] = P("integer", "End y."), ["screenshot"] = Follow() },
            ["screen", "from_x", "from_y", "to_x", "to_y"],
            async (a, ct) => await Act(a, "drag", new { fromX = IntOr(a, "from_x", 0), fromY = IntOr(a, "from_y", 0), toX = IntOr(a, "to_x", 0), toY = IntOr(a, "to_y", 0) }, ct));
        Add("screen_scroll", "Scroll the mouse wheel on a remote screen.", Merge(new JsonObject { ["screen"] = Screen(), ["amount"] = P("integer", "Scroll steps: positive = down, negative = up (default 3)."), ["screenshot"] = Follow() }, Xy("Where")), ["screen", "x", "y"],
            async (a, ct) => await Act(a, "scroll", new { x = IntOr(a, "x", 0), y = IntOr(a, "y", 0), amount = IntOr(a, "amount", 3) }, ct));
        Add("screen_type", "Type text on a remote screen (into the focused field). Click the field first. If the text does not arrive (some servers ignore unicode input), try method=keys.",
            new() { ["screen"] = Screen(), ["text"] = P("string", "Text to type."), ["method"] = P("string", "unicode (default, independent of the keyboard layout) or keys (key presses for a US keyboard layout; works with every server)."), ["screenshot"] = Follow() }, ["screen", "text"],
            async (a, ct) => await Act(a, "type", new { text = Opt(a, "text"), method = Opt(a, "method").Length > 0 ? Opt(a, "method") : "unicode" }, ct));
        Add("screen_key", "Press a key or a shortcut on a remote screen, e.g. enter, tab, esc, f5, ctrl+c, alt+tab, win+r, ctrl+alt+delete (several keys separated by spaces are pressed one after the other).",
            new() { ["screen"] = Screen(), ["keys"] = P("string", "Key or shortcut."), ["screenshot"] = Follow() }, ["screen", "keys"],
            async (a, ct) => await Act(a, "key", new { keys = Req(a, "keys") }, ct));
        Add("screen_close", "Close a remote screen tab.", new() { ["screen"] = Screen() }, ["screen"],
            async (a, ct) => await _screens.RequestAsync("close", new { screen = Req(a, "screen") }, TimeSpan.FromSeconds(15), ct));
    }

    // ------------------------------------------------------------------------------------------------ Helpers
    private async Task<object?> Shot(JsonObject a, int waitMs, CancellationToken ct)
    {
        if (waitMs > 0) await Task.Delay(waitMs, ct);
        var r = await _screens.RequestAsync("screenshot", new { screen = Req(a, "screen") }, TimeSpan.FromSeconds(30), ct);
        var png = r.GetProperty("png").GetString() ?? "";
        return new McpToolOutput($"Screenshot {r.GetProperty("width").GetInt32()}x{r.GetProperty("height").GetInt32()} pixels. Coordinates for clicks refer to this picture.", [new McpImage(png, "image/png")]);
    }

    /// <summary>Runs a screen action; optionally returns a screenshot afterwards.</summary>
    private async Task<object?> Act(JsonObject a, string op, object args, CancellationToken ct)
    {
        var payload = JsonSerializer.SerializeToNode(args, Json.Options)!.AsObject();
        payload["screen"] = Req(a, "screen");
        await _screens.RequestAsync(op, payload, TimeSpan.FromSeconds(60), ct);
        return Flag(a, "screenshot") ? await Shot(a, 400, ct) : "Done. Take a screenshot to see the result.";
    }


    private static string NewId() => Guid.NewGuid().ToString("N");

    private async Task<object?> Call(string method, object args)
    {
        var element = JsonSerializer.SerializeToElement(args, Json.Options);
        var result = await _bridge(method, element);
        return result == null ? null : JsonSerializer.SerializeToNode(result, Json.Options);
    }

    private async Task<object?> WithSftp(string host, Func<string, Task<object?>> use)
    {
        var server = FindServer(host);
        if (server.Protocol != Protocol.Ssh) throw new InvalidOperationException($"\"{server.Name}\" is a {server.Protocol} host. File tools need an SSH host.");
        var sid = "mcp-sftp-" + NewId();
        await Call("sftp.open", new { sessionId = sid, serverId = server.Id });
        try { return await use(sid); }
        finally { try { await Call("sftp.close", new { sessionId = sid }); } catch (Exception) { /* already closed */ } }
    }

    private ServerEntry FindServer(string reference)
    {
        var all = _vault.List().Where(s => _workspaceAllowed(s.Workspace)).ToList();
        var matches = all.Where(s => s.Id == reference).ToList();
        if (matches.Count == 0) matches = all.Where(s => s.Name.Equals(reference, StringComparison.OrdinalIgnoreCase)).ToList();
        if (matches.Count == 0) throw new KeyNotFoundException($"No host named \"{reference}\". Use list_hosts to see the available hosts.");
        if (matches.Count > 1) throw new InvalidOperationException($"\"{reference}\" matches {matches.Count} hosts. Use the id from list_hosts.");
        return matches[0];
    }

    private KeyEntry FindKey(string reference)
    {
        var all = _vault.ListKeys();
        return all.FirstOrDefault(k => k.Id == reference) ?? all.FirstOrDefault(k => k.Name.Equals(reference, StringComparison.OrdinalIgnoreCase))
               ?? throw new KeyNotFoundException($"No key named \"{reference}\". Use list_keys.");
    }

    private void ApplyHostFields(ServerEntry s, JsonObject a, bool creating)
    {
        if (a.ContainsKey("name")) s.Name = Req(a, "name");
        if (a.ContainsKey("host")) s.Host = Req(a, "host");
        if (a.ContainsKey("protocol"))
            s.Protocol = Enum.TryParse<Protocol>(Opt(a, "protocol"), true, out var proto) ? proto : throw new ArgumentException("protocol must be ssh, rdp, vnc, telnet, serial or teamviewer.");
        if (a.ContainsKey("port")) s.Port = IntOr(a, "port", 0);
        else if (creating) s.Port = ServerEntry.DefaultPort(s.Protocol);
        if (a.ContainsKey("username")) s.Username = Opt(a, "username");
        if (a.ContainsKey("password")) s.Password = Opt(a, "password");
        if (a.ContainsKey("auth")) s.Auth = Enum.TryParse<AuthMethod>(Opt(a, "auth"), true, out var auth) ? auth : throw new ArgumentException("auth must be password or key.");
        if (a.ContainsKey("key")) { s.KeyId = Opt(a, "key").Length == 0 ? "" : FindKey(Opt(a, "key")).Id; if (s.KeyId.Length > 0) s.Auth = AuthMethod.Key; }
        if (a.ContainsKey("jump_host")) s.JumpHostId = Opt(a, "jump_host").Length == 0 ? "" : FindServer(Opt(a, "jump_host")).Id;
        if (a.ContainsKey("identity"))
            s.IdentityId = Opt(a, "identity").Length == 0 ? "" : (_vault.ListIdentities().FirstOrDefault(i => i.Id == Opt(a, "identity") || i.Name.Equals(Opt(a, "identity"), StringComparison.OrdinalIgnoreCase))
                ?? throw new KeyNotFoundException($"No identity named \"{Opt(a, "identity")}\".")).Id;
        if (a.ContainsKey("workspace")) s.Workspace = Opt(a, "workspace");
        if (a.ContainsKey("group")) s.Group = Opt(a, "group");
        if (a.ContainsKey("tags")) s.Tags = Strings(a, "tags");
        if (a.ContainsKey("notes")) s.Notes = Opt(a, "notes");
        if (a.ContainsKey("favorite")) s.Favorite = Flag(a, "favorite");
        if (s.Name.Length == 0 || s.Host.Length == 0) throw new ArgumentException("name and host are required.");
    }

    private static JsonObject Merge(JsonObject first, JsonObject second)
    {
        foreach (var (k, v) in second) first[k] = v?.DeepClone();
        return first;
    }

    private static JsonNode? Node(object o) => JsonSerializer.SerializeToNode(o, Json.Options);

    private static readonly string[] SecretNames = ["password", "passphrase", "privateKey", "apiToken", "tokenSecret", "secret"];

    /// <summary>Removes saved passwords, passphrases and private keys from anything that goes to the AI.</summary>
    internal static object? Redact(object? value)
    {
        if (value is not JsonNode node) return value;
        Strip(node);
        return node;
    }

    private static void Strip(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject o:
                foreach (var name in SecretNames)
                {
                    if (!o.ContainsKey(name)) continue;
                    var has = o[name] is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrEmpty(s);
                    o.Remove(name);
                    if (name == "password") o["hasPassword"] = has;
                }
                foreach (var child in o.Select(kv => kv.Value).ToList()) Strip(child);
                break;
            case JsonArray arr:
                foreach (var child in arr) Strip(child);
                break;
        }
    }

    private static string Opt(JsonObject a, string key)
    {
        if (!a.TryGetPropertyValue(key, out var n) || n is not JsonValue v) return "";
        return v.TryGetValue<string>(out var s) ? s : v.ToString();
    }

    private static string Req(JsonObject a, string key) => Opt(a, key) is { Length: > 0 } v ? v : throw new ArgumentException($"{key} is required.");

    private static int IntOr(JsonObject a, string key, int fallback)
    {
        if (!a.TryGetPropertyValue(key, out var n) || n is not JsonValue v) return fallback;
        if (v.TryGetValue<int>(out var i)) return i;
        if (v.TryGetValue<double>(out var d)) return (int)d;
        return v.TryGetValue<string>(out var s) && int.TryParse(s, out var parsed) ? parsed : fallback;
    }

    private static bool Flag(JsonObject a, string key) =>
        a.TryGetPropertyValue(key, out var n) && n is JsonValue v && (v.TryGetValue<bool>(out var b) ? b : v.TryGetValue<string>(out var s) && s.Equals("true", StringComparison.OrdinalIgnoreCase));

    private static List<string> Strings(JsonObject a, string key) =>
        a.TryGetPropertyValue(key, out var n) && n is JsonArray arr ? arr.Select(x => x?.GetValue<string>() ?? "").Where(x => x.Length > 0).ToList()
        : Opt(a, key).Length > 0 ? [Opt(a, key)] : [];
}

internal sealed record TermRead(string Output, bool Closed, string? Reason);

/// <summary>Interactive SSH shells opened through MCP: collects the output until the AI reads it.</summary>
internal sealed class TerminalHub
{
    private sealed class Term
    {
        public string Host = "";
        public readonly StringBuilder Pending = new();
        public bool Closed;
        public string? Reason;
        public DateTime LastData = DateTime.UtcNow;
        public readonly object Gate = new();
    }

    private readonly Func<string, JsonElement, Task<object?>> _bridge;
    private readonly Dictionary<string, Term> _terms = new();
    private static readonly Regex Ansi = new(@"\x1B\[[0-?]*[ -/]*[@-~]|\x1B\][^\x07\x1B]*(\x07|\x1B\\)|\x1B[()][0-9A-B]|\x1B[=>]", RegexOptions.Compiled);

    public TerminalHub(Func<string, JsonElement, Task<object?>> bridge, Action<Action<object>> subscribe)
    {
        _bridge = bridge;
        subscribe(OnEvent);
    }

    private void OnEvent(object payload)
    {
        JsonNode? node;
        try { node = JsonSerializer.SerializeToNode(payload, Json.Options); }
        catch (Exception) { return; }
        var kind = node?["event"]?.GetValue<string>();
        var id = node?["sessionId"]?.GetValue<string>();
        if (id == null || !id.StartsWith("mcp-term-", StringComparison.Ordinal)) return;
        Term? t;
        lock (_terms) _terms.TryGetValue(id, out t);
        if (t == null) return;
        lock (t.Gate)
        {
            if (kind == "ssh.data" && node!["data"]?.GetValue<string>() is { } b64)
            {
                t.Pending.Append(Encoding.UTF8.GetString(Convert.FromBase64String(b64)));
                t.LastData = DateTime.UtcNow;
            }
            else if (kind == "ssh.closed")
            {
                t.Closed = true;
                t.Reason = node!["reason"]?.GetValue<string>();
            }
        }
    }

    private Task<object?> Call(string method, object args) => _bridge(method, JsonSerializer.SerializeToElement(args, Json.Options));

    public async Task<object?> OpenAsync(ServerEntry server, int cols, int rows, CancellationToken ct)
    {
        if (server.Protocol is not (Protocol.Ssh or Protocol.Telnet or Protocol.Serial)) throw new InvalidOperationException($"\"{server.Name}\" is a {server.Protocol} host. Terminals work for SSH, Telnet and serial hosts.");
        var id = "mcp-term-" + Guid.NewGuid().ToString("N")[..8];
        var term = new Term { Host = server.Name };
        lock (_terms) _terms[id] = term;
        try
        {
            await Call("ssh.open", new { sessionId = id, serverId = server.Id, cols, rows });
        }
        catch
        {
            lock (_terms) _terms.Remove(id);
            throw;
        }
        var first = await ReadAsync(id, 1500, 20_000, ct);
        var read = (TermRead)first!;
        return new { terminal = id, host = server.Name, output = read.Output, closed = read.Closed };
    }

    public async Task<object?> SendAsync(string id, string text, CancellationToken ct)
    {
        _ = Get(id);
        await Call("ssh.input", new { sessionId = id, data = Convert.ToBase64String(Encoding.UTF8.GetBytes(text)) });
        return "Sent. Call terminal_read to see the result.";
    }

    public async Task<object?> ReadAsync(string id, int waitMs, int maxChars, CancellationToken ct)
    {
        var t = Get(id);
        var deadline = DateTime.UtcNow.AddMilliseconds(waitMs);
        // wait until there is output and it has been quiet for a moment (or the time is over)
        while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            lock (t.Gate)
            {
                if (t.Closed) break;
                if (t.Pending.Length > 0 && (DateTime.UtcNow - t.LastData).TotalMilliseconds > 250) break;
            }
            await Task.Delay(50, ct);
        }
        string text;
        bool closed;
        string? reason;
        lock (t.Gate)
        {
            text = t.Pending.ToString();
            t.Pending.Clear();
            closed = t.Closed;
            reason = t.Reason;
        }
        text = Ansi.Replace(text, "").Replace("\r\n", "\n").Replace("\r", "\n");
        if (text.Length > maxChars) text = "[... earlier output cut]\n" + text[^maxChars..];
        return new TermRead(text, closed, closed ? reason : null);
    }

    public async Task<object?> CloseAsync(string id)
    {
        _ = Get(id);
        try { await Call("ssh.close", new { sessionId = id }); } catch (Exception) { /* already closed */ }
        lock (_terms) _terms.Remove(id);
        return "Closed.";
    }

    public object List()
    {
        lock (_terms) return _terms.Select(kv => new { terminal = kv.Key, host = kv.Value.Host, closed = kv.Value.Closed }).ToList();
    }

    private Term Get(string id)
    {
        lock (_terms) return _terms.TryGetValue(id, out var t) ? t : throw new KeyNotFoundException($"No open terminal \"{id}\". Use terminal_open.");
    }

    public static string KeyBytes(string key) => key.Trim().ToLowerInvariant() switch
    {
        "enter" or "return" => "\r",
        "tab" => "\t",
        "esc" or "escape" => "\x1b",
        "backspace" => "\x7f",
        "up" => "\x1b[A",
        "down" => "\x1b[B",
        "right" => "\x1b[C",
        "left" => "\x1b[D",
        "ctrl+c" => "\x03",
        "ctrl+d" => "\x04",
        "ctrl+z" => "\x1a",
        "ctrl+l" => "\x0c",
        "ctrl+a" => "\x01",
        "ctrl+e" => "\x05",
        "ctrl+r" => "\x12",
        _ => throw new ArgumentException($"Unknown key \"{key}\".")
    };
}
