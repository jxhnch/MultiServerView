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

    /// <summary>Text that is shown to the user may come from other people (imports, servers): no control characters.</summary>
    private static string Safe(string? text) => TerminalText.Sanitize(text);

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
            Console.Error.WriteLine($"No vault found at {Safe(vaultPath)}. Start MultiServerView once and create your vault, or use --data <folder>.");
            return 2;
        }

        var vault = new VaultStore(vaultPath);
        if (!Unlock(vault)) return 1;

        // the same order as in the app, including the places chosen there by drag and drop
        var servers = HostOrder.Sort(vault.List().Where(s => IsTerminalHost(s.Protocol)), vault.ListGroupOrder());
        if (servers.Count == 0)
        {
            Console.Error.WriteLine("There are no SSH, Telnet or serial hosts in the vault yet.");
            return 3;
        }

        var command = rest.Count > 0 ? rest[0] : "";
        if (command is "list" or "ls")
        {
            foreach (var s in servers) Console.WriteLine($"{Safe(s.Name),-28} {Describe(s)}{(s.Workspace.Length > 0 ? "  [" + Safe(s.Workspace) + "]" : "")}");
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
                Console.Error.WriteLine($"No server matches \"{Safe(query)}\". Use \"msv list\" to see all servers.");
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
        Protocol.Serial => $"serial {Safe(s.Host)} @ {s.Port} baud",
        Protocol.Telnet => $"telnet {Safe(s.Host)}:{s.Port}",
        _ => $"{(s.Username.Length > 0 ? Safe(s.Username) + "@" : "")}{Safe(s.Host)}{(s.Port is not 22 and > 0 ? ":" + s.Port : "")}"
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
        // The master password is only ever typed (or, for scripts, read from the first line of the input). It is not taken from
        // the environment or from arguments, so nothing can skip the question.
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var password = ReadSecret("Master password: ");
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
                var rows = Math.Max(3, Console.WindowHeight - 8);
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

    // Colours (true colour): a warm accent like Claude Code, soft greys for the rest.
    private const string Accent = "\u001b[38;2;217;119;87m";
    private const string Dim = "\u001b[38;2;140;140;150m";
    private const string Reset = "\u001b[0m";

    private static string Badge(Protocol p) => p switch
    {
        Protocol.Telnet => "\u001b[38;2;230;180;80m● telnet" + Reset,
        Protocol.Serial => "\u001b[38;2;120;200;200m● serial" + Reset,
        _ => "\u001b[38;2;110;200;130m● ssh   " + Reset
    };

    private static void Draw(IReadOnlyList<ServerEntry> matches, string query, int selected, int top, int rows)
    {
        var width = Math.Clamp(Console.WindowWidth - 1, 40, 110);
        var inner = width - 2;
        var sb = new StringBuilder();
        sb.Append($"{Esc}[H{Esc}[2J");

        // header box
        var title = " ✻ MultiServerView ";
        sb.Append($"{Accent}╭─{title}{new string('─', Math.Max(0, inner - title.Length - 1))}╮{Reset}\r\n");
        var hint = "Pick a server and press Enter";
        sb.Append($"{Accent}│{Reset} {hint}{new string(' ', Math.Max(0, inner - hint.Length - 1))}{Accent}│{Reset}\r\n");
        sb.Append($"{Accent}╰{new string('─', inner)}╯{Reset}\r\n");

        // search box
        var shownQuery = Safe(query);
        if (shownQuery.Length > inner - 6) shownQuery = shownQuery[^(inner - 6)..];
        var pad = Math.Max(0, inner - 4 - shownQuery.Length - 1);
        sb.Append($"{Dim}╭{new string('─', inner)}╮{Reset}\r\n");
        sb.Append($"{Dim}│{Reset} {Accent}>{Reset} {shownQuery}{Esc}[7m {Reset}{new string(' ', pad)}{Dim}│{Reset}\r\n");
        sb.Append($"{Dim}╰{new string('─', inner)}╯{Reset}\r\n");

        for (var i = top; i < Math.Min(matches.Count, top + rows); i++)
        {
            var s = matches[i];
            var shownName = Safe(s.Name);
            var name = shownName.Length > 24 ? shownName[..23] + "…" : shownName;
            var detail = Describe(s) + (s.Workspace.Length > 0 ? "  [" + Safe(s.Workspace) + "]" : "");
            var room = width - 4 - 25 - 9;
            if (detail.Length > room) detail = detail[..Math.Max(0, room - 1)] + "…";
            if (i == selected)
                sb.Append($" {Accent}❯{Reset} {Esc}[1m{name,-24}{Reset} {Badge(s.Protocol)} {Accent}{detail}{Reset}\r\n");
            else
                sb.Append($"   {name,-24} {Badge(s.Protocol)} {Dim}{detail}{Reset}\r\n");
        }
        if (matches.Count == 0) sb.Append($"{Dim}   nothing matches{Reset}\r\n");

        // footer on the last line
        var count = matches.Count == 0 ? "0" : $"{selected + 1}/{matches.Count}";
        sb.Append($"{Esc}[{Math.Max(1, Console.WindowHeight)};1H{Dim}  ↑↓ move · type to filter · Enter connect · Esc cancel · {count}{Reset}");
        Console.Out.Write(sb.ToString());
        Console.Out.Flush();
    }

    // ------------------------------------------------------------------------------------------------ Connect

    private static int Connect(VaultStore vault, ServerEntry server)
    {
        Console.Error.WriteLine($"{Accent}\u273b{Reset} Connecting to {Esc}[1m{Safe(server.Name)}{Reset} {Dim}({Describe(server)}){Reset}");
        var declinedHostKey = false;
        var known = vault.ListKnownHosts().ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);

        HostKeyTrust CheckHost(string host, int port, string fingerprint)
        {
            if (known.TryGetValue(VaultStore.HostKeyId(host, port), out var stored))
                return stored == fingerprint ? HostKeyTrust.Trusted : HostKeyTrust.Mismatch;
            Console.Error.WriteLine($"The authenticity of {Safe(host)}:{port} is not known yet.\r\nFingerprint: {Safe(fingerprint)}");
            if (Console.IsInputRedirected)
            {
                // A script or a pipe must never decide to trust a new server: the answer has to come from a person at the keyboard.
                Console.Error.WriteLine("Not trusted: connect once with MultiServerView or run msv in a terminal to confirm this host key.");
                declinedHostKey = true;
                return HostKeyTrust.Mismatch;
            }
            Console.Error.Write("Trust this host for this session? (Connect once in MultiServerView to remember it.) [y/N] ");
            var answer = Console.ReadKey(intercept: false).KeyChar;
            Console.Error.WriteLine();
            if (answer is 'y' or 'Y') return HostKeyTrust.Trusted;
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
            var typed = ReadSecret($"Password for {Safe(server.Username)}@{Safe(server.Host)}: ");
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
        session.Info += message => Console.Error.WriteLine($"{Esc}[2m{Safe(message)}{Esc}[0m");

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
            Console.Error.WriteLine(declinedHostKey ? "Cancelled: the host key was not trusted." : $"{Esc}[31m{Safe(ex.Message)}{Esc}[0m");
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
        Console.Error.WriteLine($"\r\n{Esc}[2mConnection to {Safe(server.Name)} closed{(closedReason != null ? ": " + Safe(closedReason) : ".")}{Esc}[0m");
        return closedReason == null ? 0 : 1;
    }
}
