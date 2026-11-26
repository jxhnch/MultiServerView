using System.Text;
using MultiServerView.Core.Local;
using Xunit;

namespace MultiServerView.Core.Tests;

public class LocalTerminalTests
{
    [Fact]
    public void Only_listed_shells_can_be_opened()
    {
        Assert.NotEmpty(LocalShells.Available());
        Assert.All(LocalShells.Available(), s => Assert.True(Path.IsPathRooted(s.Path)));
        Assert.Throws<KeyNotFoundException>(() => LocalShells.Find("/bin/rm"));
        Assert.Throws<KeyNotFoundException>(() => LocalShells.Find(""));
    }

    [Fact]
    public async Task A_shell_runs_commands_can_be_resized_and_reports_its_end()
    {
        var shell = LocalShells.Find(OperatingSystem.IsWindows() ? "cmd" : "sh");
        using var session = new LocalTerminalSession(shell);
        var output = new StringBuilder();
        var closed = new TaskCompletionSource();
        session.Data += d => { lock (output) output.Append(Encoding.UTF8.GetString(d)); };
        session.Closed += _ => closed.TrySetResult();

        await session.ConnectAsync(100, 30);
        session.Resize(120, 40);
        // the result is not part of the typed line, so finding it proves the command ran
        var command = OperatingSystem.IsWindows() ? "set /a 41000+999\r" : "echo $((41000+999))\r";
        session.Write(Encoding.UTF8.GetBytes(command));

        string Text() { lock (output) return output.ToString(); }
        for (var i = 0; i < 150 && !Text().Contains("41999"); i++) await Task.Delay(100);
        Assert.Contains("41999", Text());

        session.Write(Encoding.UTF8.GetBytes("exit\r"));
        Assert.Same(closed.Task, await Task.WhenAny(closed.Task, Task.Delay(15000)));
    }
}
