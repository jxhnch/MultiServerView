using System.Text;
using MultiServerView.Core;
using MultiServerView.Core.Ssh;

namespace MultiServerView.Cli;

/// <summary>
/// msv - opens the servers of your MultiServerView vault from any terminal (PowerShell, cmd, bash).
///   msv            pick a server from a list (type to filter) and connect
///   msv web        connect to "web" right away, or pick from the matches
///   msv list       print the servers
/// The vault is only read (nothing is saved), so it can be used while the app is running.
/// </summary>
internal static class Program
{
    private const string Esc = "\u001b";

    private static int Main(string[] args)
    {
        try
        {
            return Run(args);
        }
        catch (OperationCanceledException)
        {
            return 130;
        }
    }

    private static int Run(string[] args)
    {
        var dataDir = Environment.GetEnvironmentVariable("MSV_DATA_DIR");
        var rest = new List<string>();
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] is "--data" && i + 1 < args.Length) dataDir = args[++i];
            else rest.Add(args[i]);
        }
        if (rest.Count > 0 && rest[0] is "-h" or "--help" or "help" or "/?") return Help();
        if (rest.Count > 0 && rest[0] is "-v" or "--version" or "version")
        {
            Console.WriteLine("msv " + (typeof(Program).Assembly.GetName().Version?.ToString(3) ?? ""));
            return 0;
        }

        if (string.IsNullOrWhiteSpace(dataDir))
            dataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MultiServerView");
        var vaultPath = Path.Combine(dataDir, "vault.msv");
        if (!File.Exists(vaultPath))
        {
            Console.Error.WriteLine($"No vault found at {vaultPath}. Start MultiServerView once and create your vault, or use --data <folder>.");
            return 2;
        }

        var vault = new VaultStore(vaultPath);
        if (!Unlock(vault)) return 1;

        var servers = vault.List().Where(s => IsTerminalHost(s.Protocol)).OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToList();
        if (servers.Count == 0)
        {
            Console.Error.WriteLine("There are no SSH, Telnet or serial hosts in the vault yet.");
            return 3;
        }

        var command = rest.Count > 0 ? rest[0] : "";
        if (command is "list" or "ls")
        {
            foreach (var s in servers) Console.WriteLine($"{s.Name,-28} {Describe(s)}{(s.Workspace.Length > 0 ? "  [" + s.Workspace + "]" : "")}");
            return 0;
        }

        var query = string.Join(' ', rest);
        ServerEntry? chosen;
        if (query.Length > 0)
        {
            var exact = servers.Where(s => s.Name.Equals(query, StringComparison.OrdinalIgnoreCase)).ToList();
            var matches = exact.Count == 1 ? exact : Filter(servers, query);
            if (matches.Count == 0)
            {
                Console.Error.WriteLine($"No server matches \"{query}\". Use \"msv list\" to see all servers.");
                return 4;
            }
            chosen = matches.Count == 1 ? matches[0] : Pick(matches, query);
        }
        else chosen = Pick(servers, "");

        if (chosen == null) return 130;
        var resolved = vault.FindResolved(chosen.Id) ?? chosen;
        return Connect(vault, resolved);
    }

    private static int Help()
    {
        Console.WriteLine("""
            msv - open the servers of your MultiServerView vault from any terminal

              msv               pick a server from a list (type to filter, Enter connects, Esc cancels)
              msv <name>        connect to <name> right away (or pick from the matches)
              msv list          print all servers
              msv --data <dir>  use another data folder (default: the folder of the app)

            Works for SSH, Telnet and serial hosts. Asks for your master password; nothing is saved, so it is safe to use while MultiServerView is open.
            """);
        return 0;
    }

    private static bool IsTerminalHost(Protocol p) => p is Protocol.Ssh or Protocol.Telnet or Protocol.Serial;

    private static string Describe(ServerEntry s) => s.Protocol switch
    {
        Protocol.Serial => $"serial {s.Host} @ {s.Port} baud",
        Protocol.Telnet => $"telnet {s.Host}:{s.Port}",
        _ => $"{(s.Username.Length > 0 ? s.Username + "@" : "")}{s.Host}{(s.Port is not 22 and > 0 ? ":" + s.Port : "")}"
    };

    private static List<ServerEntry> Filter(IReadOnlyList<ServerEntry> servers, string query)
    {
        var words = query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return servers.Where(s =>
        {
            var hay = $"{s.Name} {s.Host} {s.Username} {s.Workspace} {s.Group} {string.Join(' ', s.Tags)} {s.Os} {s.Protocol}".ToLowerInvariant();
            return words.All(w => hay.Contains(w.ToLowerInvariant()));
        }).ToList();
    }

    // ------------------------------------------------------------------------------------------------ Master password

    private static bool Unlock(VaultStore vault)
    {
        var fromEnvironment = Environment.GetEnvironmentVariable("MSV_MASTER_PASSWORD");
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var password = attempt == 0 && !string.IsNullOrEmpty(fromEnvironment) ? fromEnvironment : ReadSecret("Master password: ");
            if (password == null) return false;
            try
            {
                vault.Unlock(password);
                return true;
            }
            catch (WrongPasswordException)
            {
                Console.Error.WriteLine("Wrong password.");
            }
        }
        return false;
    }

    /// <summary>Reads a line without showing it. null = cancelled (Esc or end of input).</summary>
    private static string? ReadSecret(string prompt)
    {
        Console.Error.Write(prompt);
        if (Console.IsInputRedirected)
        {
            var line = Console.In.ReadLine();
            Console.Error.WriteLine();
            return line;
        }
        var sb = new StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter)
            {
                Console.Error.WriteLine();
                return sb.ToString();
            }
            if (key.Key == ConsoleKey.Escape || (key.Key == ConsoleKey.C && key.Modifiers.HasFlag(ConsoleModifiers.Control)))
            {
                Console.Error.WriteLine();
                return null;
            }
            if (key.Key == ConsoleKey.Backspace) { if (sb.Length > 0) sb.Length--; }
            else if (!char.IsControl(key.KeyChar)) sb.Append(key.KeyChar);
        }
    }

    // ------------------------------------------------------------------------------------------------ Picker

    private static ServerEntry? Pick(IReadOnlyList<ServerEntry> servers, string initialQuery)
    {
        if (!ConsoleRaw.IsInteractive)
        {
            Console.Error.WriteLine("This needs a real terminal. Use \"msv <name>\" or \"msv list\" in scripts.");
            return null;
        }
        var query = new StringBuilder(initialQuery);
        var selected = 0;
        var top = 0;
        Console.Out.Write($"{Esc}[?1049h{Esc}[?25l"); // own screen, hide the cursor
        try
        {
            while (true)
            {
                var matches = query.Length == 0 ? servers.ToList() : Filter(servers, query.ToString());
                selected = Math.Clamp(selected, 0, Math.Max(0, matches.Count - 1));
                var rows = Math.Max(3, Console.WindowHeight - 4);
                if (selected < top) top = selected;
                if (selected >= top + rows) top = selected - rows + 1;
                Draw(matches, query.ToString(), selected, top, rows);

                var key = Console.ReadKey(intercept: true);
                switch (key.Key)
                {
                    case ConsoleKey.Enter:
                        return matches.Count > 0 ? matches[selected] : null;
                    case ConsoleKey.Escape:
                        return null;
                    case ConsoleKey.C when key.Modifiers.HasFlag(ConsoleModifiers.Control):
                        return null;
                    case ConsoleKey.UpArrow: selected = Math.Max(0, selected - 1); break;
                    case ConsoleKey.DownArrow: selected = Math.Min(matches.Count - 1, selected + 1); break;
                    case ConsoleKey.PageUp: selected = Math.Max(0, selected - rows); break;
                    case ConsoleKey.PageDown: selected = Math.Min(matches.Count - 1, selected + rows); break;
                    case ConsoleKey.Home: selected = 0; break;
                    case ConsoleKey.End: selected = Math.Max(0, matches.Count - 1); break;
                    case ConsoleKey.Backspace:
                        if (query.Length > 0) query.Length--;
                        selected = 0;
                        break;
                    default:
                        if (!char.IsControl(key.KeyChar))
                        {
                            query.Append(key.KeyChar);
                            selected = 0;
                        }
                        break;
                }
            }
        }
        finally
        {
            Console.Out.Write($"{Esc}[?25h{Esc}[?1049l");
        }
    }

    private static void Draw(IReadOnlyList<ServerEntry> matches, string query, int selected, int top, int rows)
    {
        var width = Math.Max(40, Console.WindowWidth - 1);
        var sb = new StringBuilder();
        sb.Append($"{Esc}[H{Esc}[2J");
        sb.Append($"{Esc}[1mMultiServerView{Esc}[0m  {Esc}[2m{matches.Count} server(s) · type to filter · ↑↓ · Enter connects · Esc cancels{Esc}[0m\r\n");
        sb.Append($"{Esc}[36m>{Esc}[0m {query}{Esc}[7m {Esc}[0m\r\n\r\n");
        for (var i = top; i < Math.Min(matches.Count, top + rows); i++)
        {
            var s = matches[i];
            var name = s.Name.Length > 26 ? s.Name[..25] + "…" : s.Name;
            var line = $"{name,-27} {Describe(s)}{(s.Workspace.Length > 0 ? "  [" + s.Workspace + "]" : "")}";
            if (line.Length > width - 3) line = line[..(width - 4)] + "…";
            sb.Append(i == selected ? $"{Esc}[7m ▸ {line}{Esc}[0m\r\n" : $"   {line}\r\n");
        }
        if (matches.Count == 0) sb.Append($"{Esc}[2m   nothing matches{Esc}[0m\r\n");
        Console.Out.Write(sb.ToString());
        Console.Out.Flush();
    }

    // ------------------------------------------------------------------------------------------------ Connect

    private static int Connect(VaultStore vault, ServerEntry server)
    {
        Console.Error.WriteLine($"{Esc}[2mConnecting to {server.Name} ({Describe(server)}) …{Esc}[0m");
        var declinedHostKey = false;
        var known = vault.ListKnownHosts().ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);

        HostKeyTrust CheckHost(string host, int port, string fingerprint)
        {
            if (known.TryGetValue(VaultStore.HostKeyId(host, port), out var stored))
                return stored == fingerprint ? HostKeyTrust.Trusted : HostKeyTrust.Mismatch;
            Console.Error.WriteLine($"The authenticity of {host}:{port} is not known yet.\r\nFingerprint: {fingerprint}");
            Console.Error.Write("Trust this host for this session? (Connect once in MultiServerView to remember it.) [y/N] ");
            var answer = Console.IsInputRedirected ? Console.In.ReadLine() : Console.ReadKey(intercept: false).KeyChar.ToString();
            Console.Error.WriteLine();
            if (answer is { Length: > 0 } && char.ToLowerInvariant(answer[0]) == 'y') return HostKeyTrust.Trusted;
            declinedHostKey = true;
            return HostKeyTrust.Mismatch;
        }

        (ServerEntry Server, KeyEntry? Key)? Jumps(string id)
        {
            var s = vault.FindResolved(id);
            return s == null ? null : (s, s.Auth == AuthMethod.Key ? vault.FindKey(s.KeyId) : null);
        }

        if (server.Protocol == Protocol.Ssh && server.Auth == AuthMethod.Password && server.Password.Length == 0)
        {
            var typed = ReadSecret($"Password for {server.Username}@{server.Host}: ");
            if (typed == null) return 130;
            server = server.Clone();
            server.Password = typed;
        }

        ITerminalSession session = server.Protocol switch
        {
            Protocol.Telnet => new TelnetTerminalSession(server.Host, server.Port),
            Protocol.Serial => new SerialTerminalSession(server.Host, server.Port),
            _ => new SshTerminalSession(server, server.Auth == AuthMethod.Key ? vault.FindKey(server.KeyId) : null, CheckHost, Jumps)
        };

        var closed = new ManualResetEventSlim();
        string? closedReason = null;
        session.Closed += reason => { closedReason = reason; closed.Set(); };
        session.Info += message => Console.Error.WriteLine($"{Esc}[2m{message}{Esc}[0m");

        var output = Console.OpenStandardOutput();
        session.Data += data =>
        {
            try
            {
                output.Write(data, 0, data.Length);
                output.Flush();
            }
            catch (IOException) { /* the terminal went away */ }
        };

        try
        {
            var columns = (uint)Math.Max(20, Console.IsOutputRedirected ? 80 : Console.WindowWidth);
            var rows = (uint)Math.Max(5, Console.IsOutputRedirected ? 24 : Console.WindowHeight);
            session.ConnectAsync(columns, rows).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            session.Dispose();
            Console.Error.WriteLine(declinedHostKey ? "Cancelled: the host key was not trusted." : $"{Esc}[31m{ex.Message}{Esc}[0m");
            return 1;
        }

        using var raw = new ConsoleRaw();
        if (ConsoleRaw.IsInteractive) raw.Enter();

        // keyboard -> server
        var input = Console.OpenStandardInput();
        var reader = new Thread(() =>
        {
            var buffer = new byte[4096];
            try
            {
                int n;
                while ((n = input.Read(buffer, 0, buffer.Length)) > 0) session.Write(buffer.AsSpan(0, n).ToArray());
            }
            catch (IOException) { /* input closed */ }
            // The input ended (a script or a pipe, not a key): give the server a moment to finish and print what is still coming.
            if (!closed.Wait(TimeSpan.FromSeconds(6))) closed.Set();
        }) { IsBackground = true };
        reader.Start();

        // window size -> server
        if (ConsoleRaw.IsInteractive)
        {
            var resizer = new Thread(() =>
            {
                int w = Console.WindowWidth, h = Console.WindowHeight;
                while (!closed.IsSet)
                {
                    Thread.Sleep(300);
                    try
                    {
                        if (Console.WindowWidth != w || Console.WindowHeight != h)
                        {
                            w = Console.WindowWidth;
                            h = Console.WindowHeight;
                            session.Resize((uint)w, (uint)h);
                        }
                    }
                    catch (IOException) { return; }
                }
            }) { IsBackground = true };
            resizer.Start();
        }

        closed.Wait();
        raw.Dispose();
        session.Dispose();
        Console.Error.WriteLine($"\r\n{Esc}[2mConnection to {server.Name} closed{(closedReason != null ? ": " + closedReason : ".")}{Esc}[0m");
        return closedReason == null ? 0 : 1;
    }
}
