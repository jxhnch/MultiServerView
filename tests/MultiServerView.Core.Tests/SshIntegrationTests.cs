using System.Text;
using MultiServerView.Core;
using MultiServerView.Core.Ssh;
using Xunit;

namespace MultiServerView.Core.Tests;

/// <summary>
/// Real SSH connections against a local sshd. They only run if MSV_SSH_TEST_DIR is set
/// (folder with userkey, userkey_pp = passphrase "pp-secret"; server 127.0.0.1:2222, user msvtest / testpw-123).
/// Without the variable these tests do nothing - so `dotnet test` stays green everywhere.
/// </summary>
public class SshIntegrationTests
{
    private static string? Dir => Environment.GetEnvironmentVariable("MSV_SSH_TEST_DIR");

    private static ServerEntry Server(string password = "testpw-123") => new()
    {
        Name = "local", Host = "127.0.0.1", Port = 2222, Username = "msvtest", Password = password
    };

    private static async Task<string> RunAsync(SshTerminalSession s, string command)
    {
        var output = new StringBuilder();
        var done = new TaskCompletionSource();
        s.Data += d =>
        {
            lock (output)
            {
                output.Append(Encoding.UTF8.GetString(d));
                if (output.ToString().Contains("RESULT:")) done.TrySetResult();
            }
        };
        await s.ConnectAsync(120, 30);
        s.Write(Encoding.UTF8.GetBytes(command + "\n"));
        await done.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await Task.Delay(200);
        lock (output) return output.ToString();
    }

    private static Func<string, int, string, HostKeyTrust> Accept() =>
        (_, _, _) => HostKeyTrust.NewlyTrusted;

    [Fact]
    public async Task Password_login_runs_a_command()
    {
        if (Dir == null) return;
        using var s = new SshTerminalSession(Server(), null, Accept());
        var output = await RunAsync(s, "echo RESULT:$((6*7))");
        Assert.Contains("RESULT:42", output);
    }

    [Fact]
    public async Task Wrong_password_gives_friendly_error()
    {
        if (Dir == null) return;
        using var s = new SshTerminalSession(Server("wrong"), null, Accept());
        var ex = await Assert.ThrowsAsync<SshConnectException>(() => s.ConnectAsync(80, 24));
        Assert.Contains("Authentication failed", ex.Message);
    }

    [Fact]
    public async Task Unreachable_port_gives_friendly_error()
    {
        if (Dir == null) return;
        var server = Server();
        server.Port = 1;
        using var s = new SshTerminalSession(server, null, Accept());
        var ex = await Assert.ThrowsAsync<SshConnectException>(() => s.ConnectAsync(80, 24));
        Assert.Contains("refused", ex.Message);
    }

    [Fact]
    public async Task Ed25519_openssh_key_login_works()
    {
        if (Dir == null) return;
        var key = new KeyEntry { Name = "k", PrivateKey = File.ReadAllText(Path.Combine(Dir, "userkey")) };
        Assert.Equal("ED25519", KeyTools.Inspect(key.PrivateKey, ""));

        var server = Server("");
        server.Auth = AuthMethod.Key;
        using var s = new SshTerminalSession(server, key, Accept());
        var output = await RunAsync(s, "echo RESULT:key-$(whoami)");
        Assert.Contains("RESULT:key-msvtest", output);
    }

    [Fact]
    public async Task Rsa_key_with_passphrase_login_works_and_wrong_passphrase_fails()
    {
        if (Dir == null) return;
        var pem = File.ReadAllText(Path.Combine(Dir, "userkey_pp"));

        Assert.Equal("RSA", KeyTools.Inspect(pem, "pp-secret"));
        Assert.Throws<SshConnectException>(() => KeyTools.Inspect(pem, "wrong"));

        var server = Server("");
        server.Auth = AuthMethod.Key;
        using var s = new SshTerminalSession(server, new KeyEntry { PrivateKey = pem, Passphrase = "pp-secret" }, Accept());
        var output = await RunAsync(s, "echo RESULT:rsa-ok");
        Assert.Contains("RESULT:rsa-ok", output);
    }

    [Fact]
    public async Task Changed_host_key_is_rejected_with_warning()
    {
        if (Dir == null) return;
        using var s = new SshTerminalSession(Server(), null, (_, _, _) => HostKeyTrust.Mismatch);
        var ex = await Assert.ThrowsAsync<SshConnectException>(() => s.ConnectAsync(80, 24));
        Assert.Contains("Host-Key", ex.Message);
    }

    [Fact]
    public async Task Resize_and_close_do_not_throw()
    {
        if (Dir == null) return;
        using var s = new SshTerminalSession(Server(), null, Accept());
        string? closedReason = "not-closed";
        var closed = new TaskCompletionSource();
        s.Closed += r => { closedReason = r; closed.TrySetResult(); };
        await s.ConnectAsync(80, 24);
        s.Resize(200, 50);
        s.Write(Encoding.UTF8.GetBytes("exit\n"));
        await closed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.NotEqual("not-closed", closedReason);
    }
}
