using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace MultiServerView.Core.Local;

/// <summary>A shell attached to a Windows pseudo console (ConPTY, Windows 10 1809 and later).</summary>
internal sealed class WindowsPseudoConsole : IPseudoTerminal
{
    private const uint ExtendedStartupInfoPresent = 0x00080000;
    private const int StartfUseStdHandles = 0x00000100;
    private static readonly IntPtr AttributePseudoConsole = 0x00020016;

    private readonly object _gate = new();
    private IntPtr _console;
    private IntPtr _process;

    public Stream Input { get; }
    public Stream Output { get; }

    public WindowsPseudoConsole(LocalShell shell, string folder, int columns, int rows)
    {
        if (!CreatePipe(out var inputRead, out var inputWrite, IntPtr.Zero, 0) || !CreatePipe(out var outputRead, out var outputWrite, IntPtr.Zero, 0))
            throw new Win32Exception();
        var attributes = IntPtr.Zero;
        try
        {
            var result = CreatePseudoConsole(new Coord((short)columns, (short)rows), inputRead, outputWrite, 0, out _console);
            if (result != 0) throw new InvalidOperationException($"The pseudo console could not be created (0x{result:X8}).");

            var size = IntPtr.Zero;
            InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);
            attributes = Marshal.AllocHGlobal(size);
            if (!InitializeProcThreadAttributeList(attributes, 1, 0, ref size)) throw new Win32Exception();
            if (!UpdateProcThreadAttribute(attributes, 0, AttributePseudoConsole, _console, IntPtr.Size, IntPtr.Zero, IntPtr.Zero)) throw new Win32Exception();

            var startup = new StartupInfoEx { AttributeList = attributes };
            startup.Info.Size = Marshal.SizeOf<StartupInfoEx>();
            // Empty standard handles: otherwise a redirected input or output of this program would be handed to the shell.
            startup.Info.Flags = StartfUseStdHandles;

            var command = new StringBuilder("\"" + shell.Path + "\"" + (shell.Arguments.Length > 0 ? " " + shell.Arguments : ""));
            if (!CreateProcessW(shell.Path, command, IntPtr.Zero, IntPtr.Zero, false, ExtendedStartupInfoPresent, IntPtr.Zero, folder, ref startup, out var process))
                throw new InvalidOperationException($"{shell.Name} could not be started: {new Win32Exception().Message}");
            CloseHandle(process.Thread);
            _process = process.Process;
        }
        catch
        {
            if (_console != IntPtr.Zero) ClosePseudoConsole(_console);
            _console = IntPtr.Zero;
            inputWrite.Dispose();
            outputRead.Dispose();
            throw;
        }
        finally
        {
            // the console has its own copies of these ends
            inputRead.Dispose();
            outputWrite.Dispose();
            if (attributes != IntPtr.Zero)
            {
                DeleteProcThreadAttributeList(attributes);
                Marshal.FreeHGlobal(attributes);
            }
        }

        Input = new FileStream(inputWrite, FileAccess.Write, 1);
        Output = new FileStream(outputRead, FileAccess.Read, 1);

        // When the shell ends, closing the console ends the output, which ends the read loop.
        var handle = _process;
        new Thread(() =>
        {
            WaitForSingleObject(handle, 0xFFFFFFFF);
            CloseConsole();
            CloseHandle(handle);
        }) { IsBackground = true, Name = "local-terminal-exit" }.Start();
    }

    public void Resize(int columns, int rows)
    {
        lock (_gate)
            if (_console != IntPtr.Zero) ResizePseudoConsole(_console, new Coord((short)columns, (short)rows));
    }

    private void CloseConsole()
    {
        IntPtr console;
        lock (_gate)
        {
            console = _console;
            _console = IntPtr.Zero;
        }
        // Closing ends the shell and everything started in it. It can wait for the output to be read, so no lock is held.
        if (console != IntPtr.Zero) ClosePseudoConsole(console);
    }

    public void Dispose()
    {
        Task.Run(() =>
        {
            CloseConsole();
            try { Input.Dispose(); } catch (IOException) { /* already closed */ }
        });
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct Coord(short x, short y)
    {
        public readonly short X = x;
        public readonly short Y = y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfo
    {
        public int Size;
        public IntPtr Reserved, Desktop, Title;
        public int X, Y, XSize, YSize, XCountChars, YCountChars, FillAttribute, Flags;
        public short ShowWindow, Reserved2Size;
        public IntPtr Reserved2, StdInput, StdOutput, StdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfoEx
    {
        public StartupInfo Info;
        public IntPtr AttributeList;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr Process, Thread;
        public int ProcessId, ThreadId;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CreatePipe(out SafeFileHandle read, out SafeFileHandle write, IntPtr attributes, int size);

    [DllImport("kernel32.dll")]
    private static extern int CreatePseudoConsole(Coord size, SafeFileHandle input, SafeFileHandle output, uint flags, out IntPtr console);

    [DllImport("kernel32.dll")]
    private static extern int ResizePseudoConsole(IntPtr console, Coord size);

    [DllImport("kernel32.dll")]
    private static extern void ClosePseudoConsole(IntPtr console);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, int flags, ref IntPtr size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, IntPtr attribute, IntPtr value, IntPtr size, IntPtr previous, IntPtr returnSize);

    [DllImport("kernel32.dll")]
    private static extern void DeleteProcThreadAttributeList(IntPtr list);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcessW(string application, StringBuilder commandLine, IntPtr processAttributes, IntPtr threadAttributes, bool inheritHandles,
        uint flags, IntPtr environment, string currentDirectory, ref StartupInfoEx startup, out ProcessInformation process);

    [DllImport("kernel32.dll")]
    private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);
}
