namespace MultiServerView.App;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        // Self-test (for the automatic check on Windows): MultiServerView.exe --smoke <output folder>
        string? smokeDir = null;
        var i = Array.IndexOf(args, "--smoke");
        if (i >= 0)
        {
            smokeDir = i + 1 < args.Length ? args[i + 1] : Path.Combine(Path.GetTempPath(), "msv-smoke");
            Directory.CreateDirectory(smokeDir);
            // Separate, empty data folders - the real data stays untouched.
            Environment.SetEnvironmentVariable("MSV_DATA_DIR", Path.Combine(smokeDir, "data"));
        }

        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm(smokeDir));
    }
}
