using Renci.SshNet;

namespace MultiServerView.Core.Ssh;

public sealed record ExecResult(string StdOut, string StdErr, int ExitCode, bool TimedOut);

/// <summary>Runs one command on a host over SSH (no terminal), through jump hosts if configured.</summary>
public static class SshExec
{
    public const int MaxOutputChars = 200_000;

    public static async Task<ExecResult> RunAsync(ServerEntry server, KeyEntry? key, Func<string, int, string, HostKeyTrust> check, JumpResolver? jumps,
        string command, TimeSpan timeout, CancellationToken ct = default)
    {
        using var tunnel = await JumpTunnel.OpenAsync(server, jumps, check, ct);
        using var client = new SshClient(SshConnector.BuildConnectionInfo(server, key, tunnel?.Host, tunnel?.Port)) { KeepAliveInterval = SshConnector.KeepAlive };
        var hostKeyError = SshConnector.HookHostKey(client, server, check, null);
        try
        {
            await Task.Run(() => client.Connect(), ct);
        }
        catch (Exception ex)
        {
            throw new SshConnectException(hostKeyError() ?? SshConnector.Describe(ex), ex);
        }

        using var cmd = client.CreateCommand(command);
        cmd.CommandTimeout = timeout;
        var run = cmd.BeginExecute();
        var finished = await Task.Run(() => run.AsyncWaitHandle.WaitOne(timeout), ct);
        if (!finished)
        {
            try { cmd.CancelAsync(); } catch (Exception) { /* connection closes below anyway */ }
            return new ExecResult(Trim(cmd.Result), Trim(cmd.Error), -1, true);
        }
        cmd.EndExecute(run);
        return new ExecResult(Trim(cmd.Result), Trim(cmd.Error), cmd.ExitStatus ?? 0, false);
    }

    private static string Trim(string? s) =>
        s == null ? "" : s.Length > MaxOutputChars ? s[..MaxOutputChars] + "\n[output truncated]" : s;
}
