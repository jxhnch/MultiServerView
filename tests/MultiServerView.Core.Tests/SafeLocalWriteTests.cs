using System.Text;
using MultiServerView.Core.Sftp;
using Xunit;

namespace MultiServerView.Core.Tests;

public class SafeLocalWriteTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("msv-safe-").FullName;

    public void Dispose() => Directory.Delete(_dir, true);

    [Fact]
    public async Task A_broken_download_keeps_the_old_file()
    {
        var file = Path.Combine(_dir, "data.txt");
        await File.WriteAllTextAsync(file, "old content");
        await Assert.ThrowsAsync<IOException>(() => SftpSession.SafeLocalWriteAsync(file, async s =>
        {
            await s.WriteAsync(Encoding.UTF8.GetBytes("half"));
            throw new IOException("connection lost");
        }));
        Assert.Equal("old content", await File.ReadAllTextAsync(file));
        Assert.Single(Directory.GetFiles(_dir));
    }

    [Fact]
    public async Task A_complete_download_replaces_or_creates_the_file()
    {
        var file = Path.Combine(_dir, "data.txt");
        await SftpSession.SafeLocalWriteAsync(file, async s => await s.WriteAsync(Encoding.UTF8.GetBytes("first")));
        await SftpSession.SafeLocalWriteAsync(file, async s => await s.WriteAsync(Encoding.UTF8.GetBytes("second")));
        Assert.Equal("second", await File.ReadAllTextAsync(file));
        Assert.Single(Directory.GetFiles(_dir));
    }
}
