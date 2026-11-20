using System.Net.Sockets;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace MultiServerView.Core.Ssh;

public sealed record ExecResult(string StdOut, string StdErr, int ExitCode, bool TimedOut);

/// <summary>Runs commands on a host over SSH (no terminal), through jump hosts if configured.</summary>
public static class SshExec
{
    public const int MaxOutputChars = 200_000;

    public static async Task<ExecResult> RunAsync(ServerEntry server, KeyEntry? key, Func<string, int, string, HostKeyTrust> check, JumpResolver? jumps,
        string command, TimeSpan timeout, CancellationToken ct = default)
    {
        using var session = await SshExecSession.ConnectAsync(server, key, check, jumps, ct);
        return await session.RunAsync(command, timeout, ct);
    }

    public static async Task<ExecResult> RunOnClientAsync(SshClient client, string command, TimeSpan timeout, CancellationToken ct = default)
    {
        using var cmd = client.CreateCommand(command);
        cmd.CommandTimeout = timeout;
        var run = cmd.BeginExecute();
        var wait = await Task.Run(() => WaitHandle.WaitAny(new[] { run.AsyncWaitHandle, ct.WaitHandle }, timeout), ct);
        if (wait == 1)
        {
            try { cmd.CancelAsync(); } catch (Exception) { /* connection may already be broken */ }
            ct.ThrowIfCancellationRequested();
        }
        if (wait == WaitHandle.WaitTimeout)
        {
            try { cmd.CancelAsync(); } catch (Exception) { /* connection may already be broken */ }
            return new ExecResult(Trim(cmd.Result), Trim(cmd.Error), -1, true);
        }
        try
        {
            cmd.EndExecute(run);
        }
        catch (SshOperationTimeoutException)
        {
            return new ExecResult(Trim(cmd.Result), Trim(cmd.Error), -1, true);
        }
        return new ExecResult(Trim(cmd.Result), Trim(cmd.Error), cmd.ExitStatus ?? 0, false);
    }

    /// <summary>True when an exception indicates a broken transport/connection that may succeed on a fresh reconnect.</summary>
    public static bool IsConnectionError(Exception ex)
    {
        for (var cur = ex; cur != null; cur = cur.InnerException)
        {
            switch (cur)
            {
                case SshAuthenticationException:
                case SshOperationTimeoutException:
                case SshPassPhraseNullOrEmptyException:
                case SftpPathNotFoundException:
                case SftpPermissionDeniedException:
                    return false;
                case SshConnectionException:
                case SocketException:
                case ObjectDisposedException:
                case IOException:
                case SshException:
                    return true;
            }
        }
        return false;
    }

    private static string Trim(string? s) =>
        s == null ? "" : s.Length > MaxOutputChars ? s[..MaxOutputChars] + "\n[output truncated]" : s;
}

/// <summary>A reusable SSH command execution connection (including its jump tunnel, if any).</summary>
public sealed class SshExecSession : IDisposable
{
    private SshClient? _client;
    private JumpTunnel? _tunnel;

    private SshExecSession(SshClient client, JumpTunnel? tunnel)
    {
        _client = client;
        _tunnel = tunnel;
    }

    public bool IsConnected => _client?.IsConnected == true && (_tunnel == null || _tunnel.IsConnected);

    public static async Task<SshExecSession> ConnectAsync(ServerEntry server, KeyEntry? key, Func<string, int, string, HostKeyTrust> check,
        JumpResolver? jumps, CancellationToken ct = default)
    {
        var tunnel = await JumpTunnel.OpenAsync(server, jumps, check, ct);
        SshClient? client = null;
        try
        {
            client = new SshClient(SshConnector.BuildConnectionInfo(server, key, tunnel?.Host, tunnel?.Port)) { KeepAliveInterval = SshConnector.KeepAlive };
            var hostKeyError = SshConnector.HookHostKey(client, server, check, null);
            try
            {
                await Task.Run(() => client.Connect(), ct);
            }
            catch (Exception ex)
            {
                throw new SshConnectException(hostKeyError() ?? SshConnector.Describe(ex), ex);
            }
            return new SshExecSession(client, tunnel);
        }
        catch
        {
            try { client?.Dispose(); } catch { /* does not matter during cleanup */ }
            try { tunnel?.Dispose(); } catch { /* does not matter during cleanup */ }
            throw;
        }
    }

    public Task<ExecResult> RunAsync(string command, TimeSpan timeout, CancellationToken ct = default)
    {
        var client = _client ?? throw new InvalidOperationException("The SSH connection is not open.");
        return SshExec.RunOnClientAsync(client, command, timeout, ct);
    }

    public void Disconnect()
    {
        try { _client?.Disconnect(); } catch { /* does not matter during cleanup */ }
    }

    public void Dispose()
    {
        try { _client?.Dispose(); } catch { /* does not matter during cleanup */ }
        try { _tunnel?.Dispose(); } catch { /* does not matter during cleanup */ }
        _client = null;
        _tunnel = null;
    }
}
