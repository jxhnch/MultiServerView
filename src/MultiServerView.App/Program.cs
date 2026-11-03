namespace MultiServerView.App;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        // Selbsttest (für die automatische Prüfung auf Windows): MultiServerView.exe --smoke <Ausgabeordner>
        string? smokeDir = null;
        var i = Array.IndexOf(args, "--smoke");
        if (i >= 0)
        {
            smokeDir = i + 1 < args.Length ? args[i + 1] : Path.Combine(Path.GetTempPath(), "msv-smoke");
            Directory.CreateDirectory(smokeDir);
            // Eigene, leere Datenordner - die echten Daten bleiben unberührt.
            Environment.SetEnvironmentVariable("MSV_DATA_DIR", Path.Combine(smokeDir, "data"));
        }

        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm(smokeDir));
    }
}
