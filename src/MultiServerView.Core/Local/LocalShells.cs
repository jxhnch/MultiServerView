namespace MultiServerView.Core.Local;

/// <summary>A shell of this PC that can be opened in a terminal tab.</summary>
public sealed record LocalShell(string Id, string Name, string Path, string Arguments = "");

/// <summary>
/// The shells that can be opened: a fixed list with fixed places. The user interface only names one by its id; a program path never
/// comes from outside (not from the vault, not from an import or a sync), and nothing is searched in the PATH.
/// </summary>
public static class LocalShells
{
    public static IReadOnlyList<LocalShell> Available() => OperatingSystem.IsWindows() ? Windows() : Unix();

    public static LocalShell Find(string id)
        => Available().FirstOrDefault(s => s.Id == id) ?? throw new KeyNotFoundException("This shell is not available on this PC.");

    private static List<LocalShell> Windows()
    {
        var system = Environment.GetFolderPath(Environment.SpecialFolder.System);
        var programs = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        return Existing(
            new LocalShell("pwsh", "PowerShell 7", System.IO.Path.Combine(programs, "PowerShell", "7", "pwsh.exe"), "-NoLogo"),
            new LocalShell("powershell", "Windows PowerShell", System.IO.Path.Combine(system, "WindowsPowerShell", "v1.0", "powershell.exe"), "-NoLogo"),
            new LocalShell("cmd", "Command Prompt", System.IO.Path.Combine(system, "cmd.exe")),
            new LocalShell("wsl", "WSL", System.IO.Path.Combine(system, "wsl.exe"), "--cd ~"),
            new LocalShell("gitbash", "Git Bash", System.IO.Path.Combine(programs, "Git", "bin", "bash.exe"), "--login -i"));
    }

    private static List<LocalShell> Unix()
    {
        var list = Existing(
            new LocalShell("bash", "Bash", "/bin/bash"),
            new LocalShell("zsh", "Zsh", "/usr/bin/zsh"),
            new LocalShell("fish", "Fish", "/usr/bin/fish"),
            new LocalShell("sh", "sh", "/bin/sh"));
        // the login shell of the user first
        var preferred = System.IO.Path.GetFileName(Environment.GetEnvironmentVariable("SHELL") ?? "");
        return list.OrderByDescending(s => s.Id == preferred).ToList();
    }

    private static List<LocalShell> Existing(params LocalShell[] shells) => shells.Where(s => File.Exists(s.Path)).ToList();
}
