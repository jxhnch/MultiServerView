using System.Collections;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace MultiServerView.Core.Local;

/// <summary>
/// A shell attached to a Linux pseudo terminal. The shell is started with posix_spawn in a new session: opening the terminal
/// as the first thing there makes it the controlling terminal, so Ctrl+C and job control work. (fork() is not safe in .NET.)
/// </summary>
internal sealed class UnixPseudoTerminal : IPseudoTerminal
{
    private const string LibC = "libc.so.6";
    private const int ReadWrite = 0x2, NoControllingTty = 0x100, CloseOnExec = 0x80000;
    private const short SpawnSetSigDefault = 0x04, SpawnSetSigMask = 0x08, SpawnSetSid = 0x80;
    private const nuint SetWindowSize = 0x5414;
    private const int SigHup = 1, SigKill = 9;

    private readonly SafeFileHandle _master;
    private readonly int _pid;
    private volatile bool _exited;

    public Stream Input { get; }
    public Stream Output { get; }

    public UnixPseudoTerminal(LocalShell shell, string folder, int columns, int rows)
    {
        var fd = posix_openpt(ReadWrite | NoControllingTty | CloseOnExec);
        if (fd < 0) throw new InvalidOperationException("No terminal could be opened (posix_openpt).");
        _master = new SafeFileHandle(fd, ownsHandle: true);
        var name = new byte[256];
        if (grantpt(fd) != 0 || unlockpt(fd) != 0 || ptsname_r(fd, name, (nuint)name.Length) != 0)
        {
            _master.Dispose();
            throw new InvalidOperationException("No terminal could be opened (ptsname).");
        }
        var slave = Encoding.UTF8.GetString(name, 0, Math.Max(0, Array.IndexOf(name, (byte)0)));
        Resize(columns, rows);

        // generously sized room for the opaque structures of the C library
        var actions = Marshal.AllocHGlobal(1024);
        var attributes = Marshal.AllocHGlobal(1024);
        var allSignals = Marshal.AllocHGlobal(256);
        var noSignals = Marshal.AllocHGlobal(256);
        var strings = new List<IntPtr>();
        try
        {
            posix_spawn_file_actions_init(actions);
            posix_spawn_file_actions_addopen(actions, 0, slave, ReadWrite, 0);
            posix_spawn_file_actions_adddup2(actions, 0, 1);
            posix_spawn_file_actions_adddup2(actions, 0, 2);
            posix_spawn_file_actions_addchdir_np(actions, folder);
            posix_spawnattr_init(attributes);
            // the runtime ignores some signals; the shell must start with the normal behaviour
            sigfillset(allSignals);
            sigemptyset(noSignals);
            posix_spawnattr_setsigdefault(attributes, allSignals);
            posix_spawnattr_setsigmask(attributes, noSignals);
            posix_spawnattr_setflags(attributes, SpawnSetSid | SpawnSetSigDefault | SpawnSetSigMask);

            var arguments = new List<string> { shell.Path };
            arguments.AddRange(shell.Arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries));
            var environment = Environment.GetEnvironmentVariables().Cast<DictionaryEntry>()
                .Where(e => (string)e.Key is not ("TERM" or "COLUMNS" or "LINES"))
                .Select(e => $"{e.Key}={e.Value}")
                .Append("TERM=xterm-256color")
                .ToList();

            var error = posix_spawn(out _pid, shell.Path, actions, attributes, Pointers(arguments, strings), Pointers(environment, strings));
            posix_spawn_file_actions_destroy(actions);
            posix_spawnattr_destroy(attributes);
            if (error != 0)
            {
                _master.Dispose();
                throw new InvalidOperationException($"{shell.Name} could not be started (error {error}).");
            }
        }
        finally
        {
            Marshal.FreeHGlobal(actions);
            Marshal.FreeHGlobal(attributes);
            Marshal.FreeHGlobal(allSignals);
            Marshal.FreeHGlobal(noSignals);
            foreach (var s in strings) Marshal.FreeCoTaskMem(s);
        }

        // one stream for both directions; the handle is closed by this class
        var stream = new FileStream(new SafeFileHandle(fd, ownsHandle: false), FileAccess.Read, 1);
        Output = stream;
        Input = new FileStream(new SafeFileHandle(fd, ownsHandle: false), FileAccess.Write, 1);

        new Thread(() =>
        {
            waitpid(_pid, out _, 0);
            _exited = true;
        }) { IsBackground = true, Name = "local-terminal-exit" }.Start();
    }

    private static IntPtr[] Pointers(List<string> values, List<IntPtr> owned)
    {
        var result = new IntPtr[values.Count + 1]; // the last one stays zero
        for (var i = 0; i < values.Count; i++)
        {
            result[i] = Marshal.StringToCoTaskMemUTF8(values[i]);
            owned.Add(result[i]);
        }
        return result;
    }

    public void Resize(int columns, int rows)
    {
        if (_master.IsClosed) return;
        var size = new WindowSize { Rows = (ushort)rows, Columns = (ushort)columns };
        ioctl((int)_master.DangerousGetHandle(), SetWindowSize, ref size);
    }

    public void Dispose()
    {
        if (!_exited) kill(-_pid, SigHup);
        Task.Run(async () =>
        {
            for (var i = 0; i < 20 && !_exited; i++) await Task.Delay(100);
            if (!_exited) kill(-_pid, SigKill);
            await Task.Delay(100);
            _master.Dispose();
        });
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowSize
    {
        public ushort Rows, Columns, PixelWidth, PixelHeight;
    }

    [DllImport(LibC)] private static extern int posix_openpt(int flags);
    [DllImport(LibC)] private static extern int grantpt(int fd);
    [DllImport(LibC)] private static extern int unlockpt(int fd);
    [DllImport(LibC)] private static extern int ptsname_r(int fd, byte[] buffer, nuint length);
    [DllImport(LibC)] private static extern int ioctl(int fd, nuint request, ref WindowSize size);
    [DllImport(LibC)] private static extern int kill(int pid, int signal);
    [DllImport(LibC)] private static extern int waitpid(int pid, out int status, int options);
    [DllImport(LibC)] private static extern int sigfillset(IntPtr set);
    [DllImport(LibC)] private static extern int sigemptyset(IntPtr set);
    [DllImport(LibC)] private static extern int posix_spawn_file_actions_init(IntPtr actions);
    [DllImport(LibC)] private static extern int posix_spawn_file_actions_destroy(IntPtr actions);
    [DllImport(LibC)] private static extern int posix_spawn_file_actions_addopen(IntPtr actions, int fd, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags, int mode);
    [DllImport(LibC)] private static extern int posix_spawn_file_actions_adddup2(IntPtr actions, int fd, int newFd);
    [DllImport(LibC)] private static extern int posix_spawn_file_actions_addchdir_np(IntPtr actions, [MarshalAs(UnmanagedType.LPUTF8Str)] string path);
    [DllImport(LibC)] private static extern int posix_spawnattr_init(IntPtr attributes);
    [DllImport(LibC)] private static extern int posix_spawnattr_destroy(IntPtr attributes);
    [DllImport(LibC)] private static extern int posix_spawnattr_setflags(IntPtr attributes, short flags);
    [DllImport(LibC)] private static extern int posix_spawnattr_setsigdefault(IntPtr attributes, IntPtr set);
    [DllImport(LibC)] private static extern int posix_spawnattr_setsigmask(IntPtr attributes, IntPtr set);

    [DllImport(LibC)]
    private static extern int posix_spawn(out int pid, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, IntPtr actions, IntPtr attributes, IntPtr[] argv, IntPtr[] envp);
}
