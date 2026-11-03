using Renci.SshNet;

namespace MultiServerView.Core.Ssh;

/// <summary>Adds a public key to ~/.ssh/authorized_keys of a Unix server (like ssh-copy-id).</summary>
public static class KeyInstaller
{
    public static async Task InstallAsync(ServerEntry server, KeyEntry? loginKey, string publicKeyLine,
        Func<string, int, string, HostKeyTrust> checkHostKey, JumpResolver? jumps, CancellationToken ct = default)
    {
        var line = publicKeyLine.Trim();
        if (line.Length == 0 || line.Any(c => c is '\'' or '\n' or '\r'))
            throw new SshConnectException("The public key is invalid.");

        var tunnel = await JumpTunnel.OpenAsync(server, jumps, checkHostKey, ct);
        var info = SshConnector.BuildConnectionInfo(server, loginKey, tunnel?.Host, tunnel?.Port);
        using var client = new SshClient(info);
        var hostKeyError = SshConnector.HookHostKey(client, server, checkHostKey, null);
        try
        {
            await Task.Run(() => client.Connect(), ct);
            var command =
                "mkdir -p ~/.ssh && chmod 700 ~/.ssh && touch ~/.ssh/authorized_keys && chmod 600 ~/.ssh/authorized_keys && " +
                $"(grep -qxF '{line}' ~/.ssh/authorized_keys || echo '{line}' >> ~/.ssh/authorized_keys)";
            var result = await Task.Run(() => client.RunCommand(command), ct);
            if (result.ExitStatus != 0)
                throw new SshConnectException("The key could not be installed: " +
                                              (string.IsNullOrWhiteSpace(result.Error) ? "Exit-Code " + result.ExitStatus : result.Error.Trim()));
        }
        catch (SshConnectException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new SshConnectException(hostKeyError() ?? SshConnector.Describe(ex), ex);
        }
        finally
        {
            client.Disconnect();
            tunnel?.Dispose();
        }
    }
}
