using System.Diagnostics;
using System.Text.RegularExpressions;

namespace MultiServerView.Core.Vmware;

/// <summary>What the app wants to know from VMware "live": which VMs are running and which address they currently have.</summary>
public interface IVmrun
{
    Task<IReadOnlyList<string>> ListRunningAsync(CancellationToken ct = default);

    /// <summary>IPv4 address of the running VM according to VMware Tools, or null if it does not report one (yet).</summary>
    Task<string?> GetGuestIpAsync(string vmxPath, CancellationToken ct = default);
}

/// <summary>Drives <c>vmrun.exe</c>, the command-line program of VMware Workstation (located in the installation folder).</summary>
public sealed class Vmrun : IVmrun
{
    private readonly string _exe;

    public Vmrun(string exe) => _exe = exe;

    /// <summary>Looks for vmrun.exe in the usual installation folders. null = VMware Workstation is not installed.</summary>
    public static Vmrun? Find()
    {
        foreach (var root in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles) })
        {
            if (string.IsNullOrEmpty(root)) continue;
            var exe = Path.Combine(root, "VMware", "VMware Workstation", "vmrun.exe");
            if (File.Exists(exe)) return new Vmrun(exe);
        }
        return null;
    }

    public async Task<IReadOnlyList<string>> ListRunningAsync(CancellationToken ct = default)
    {
        var (ok, output) = await RunAsync(new[] { "-T", "ws", "list" }, TimeSpan.FromSeconds(10), ct);
        if (!ok) return Array.Empty<string>();
        return ParseList(output);
    }

    public async Task<string?> GetGuestIpAsync(string vmxPath, CancellationToken ct = default)
    {
        var (ok, output) = await RunAsync(new[] { "-T", "ws", "getGuestIPAddress", vmxPath }, TimeSpan.FromSeconds(8), ct);
        return ok ? ParseIp(output) : null;
    }

    /// <summary>"Total running VMs: 2" followed by one path per line.</summary>
    public static IReadOnlyList<string> ParseList(string output) =>
        output.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith("Total running VMs", StringComparison.OrdinalIgnoreCase)).ToList();

    /// <summary>Only accepts a usable IPv4 address (VMware otherwise answers with "Error: …" or "unknown").</summary>
    public static string? ParseIp(string output)
    {
        var m = Regex.Match(output, @"\b(\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3})\b");
        return m.Success && Proxmox.ProxmoxClient.UsableIpv4(m.Groups[1].Value) ? m.Groups[1].Value : null;
    }

    private async Task<(bool Ok, string Output)> RunAsync(string[] args, TimeSpan timeout, CancellationToken ct)
    {
        var info = new ProcessStartInfo(_exe)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var a in args) info.ArgumentList.Add(a);

        try
        {
            using var process = Process.Start(info);
            if (process == null) return (false, "");
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);
            var stdout = process.StandardOutput.ReadToEndAsync(cts.Token);
            try
            {
                await process.WaitForExitAsync(cts.Token);
                return (process.ExitCode == 0, await stdout);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(true); } catch (Exception) { /* already exited */ }
                return (false, "");
            }
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return (false, "");
        }
    }
}
