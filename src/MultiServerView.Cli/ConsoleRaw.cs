using System.Diagnostics;
using System.Runtime.InteropServices;

namespace MultiServerView.Cli;

/// <summary>
/// Puts the console into "raw" mode while a remote session runs: every key goes to the remote side untouched (arrows, Ctrl+C,
/// Tab, ...) and the escape sequences of the remote program are shown by the console. Restores everything afterwards.
/// </summary>
internal sealed class ConsoleRaw : IDisposable
{
    private const int StdInput = -10, StdOutput = -11;
    private const uint EnableProcessedInput = 0x1, EnableLineInput = 0x2, EnableEchoInput = 0x4, EnableVirtualTerminalInput = 0x200;
    private const uint EnableProcessedOutput = 0x1, EnableVirtualTerminalProcessing = 0x4, DisableNewlineAutoReturn = 0x8;

    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr GetStdHandle(int handle);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetConsoleMode(IntPtr handle, out uint mode);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetConsoleMode(IntPtr handle, uint mode);
    [DllImport("kernel32.dll")] private static extern uint GetConsoleCP();
    [DllImport("kernel32.dll")] private static extern uint GetConsoleOutputCP();
    [DllImport("kernel32.dll")] private static extern bool SetConsoleCP(uint codePage);
    [DllImport("kernel32.dll")] private static extern bool SetConsoleOutputCP(uint codePage);

    private uint _oldIn, _oldOut, _oldCp, _oldOutCp;
    private string? _unixSettings;
    private bool _active;

    /// <summary>true = this is a real terminal (not a pipe or a file).</summary>
    public static bool IsInteractive => !Console.IsInputRedirected && !Console.IsOutputRedirected;

    public void Enter()
    {
        if (OperatingSystem.IsWindows())
        {
            var input = GetStdHandle(StdInput);
            var output = GetStdHandle(StdOutput);
            GetConsoleMode(input, out _oldIn);
            GetConsoleMode(output, out _oldOut);
            _oldCp = GetConsoleCP();
            _oldOutCp = GetConsoleOutputCP();
            SetConsoleCP(65001);
            SetConsoleOutputCP(65001);
            SetConsoleMode(input, (_oldIn & ~(EnableProcessedInput | EnableLineInput | EnableEchoInput)) | EnableVirtualTerminalInput);
            SetConsoleMode(output, _oldOut | EnableProcessedOutput | EnableVirtualTerminalProcessing | DisableNewlineAutoReturn);
        }
        else
        {
            _unixSettings = Stty("-g")?.Trim();
            Stty("raw -echo");
        }
        _active = true;
    }

    private static string? Stty(string arguments)
    {
        try
        {
            // stty works on the terminal it inherits: stdin stays attached, only the answer is captured
            var info = new ProcessStartInfo("stty", arguments) { RedirectStandardOutput = true, UseShellExecute = false };
            using var process = Process.Start(info);
            var text = process?.StandardOutput.ReadToEnd();
            process?.WaitForExit(2000);
            return text;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return null;
        }
    }

    public void Dispose()
    {
        if (!_active) return;
        _active = false;
        if (OperatingSystem.IsWindows())
        {
            SetConsoleMode(GetStdHandle(StdInput), _oldIn);
            SetConsoleMode(GetStdHandle(StdOutput), _oldOut);
            SetConsoleCP(_oldCp);
            SetConsoleOutputCP(_oldOutCp);
        }
        else if (!string.IsNullOrEmpty(_unixSettings)) Stty(_unixSettings);
        else Stty("sane");
    }
}
