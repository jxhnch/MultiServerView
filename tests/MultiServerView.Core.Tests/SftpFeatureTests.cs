using MultiServerView.Core.Sftp;
using Xunit;

namespace MultiServerView.Core.Tests;

public class LocalFsFeatureTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "msv-lfs-" + Guid.NewGuid().ToString("N"));

    public LocalFsFeatureTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
    }

    [Fact]
    public void Create_file_and_rename_refuse_to_overwrite()
    {
        var a = Path.Combine(_dir, "a.txt");
        var b = Path.Combine(_dir, "b.txt");
        LocalFs.CreateFile(a);
        Assert.True(File.Exists(a));
        Assert.Throws<IOException>(() => LocalFs.CreateFile(a));

        File.WriteAllText(b, "x");
        Assert.Throws<IOException>(() => LocalFs.Rename(a, b));
        Assert.True(File.Exists(a));

        LocalFs.Rename(a, Path.Combine(_dir, "c.txt"));
        Assert.True(File.Exists(Path.Combine(_dir, "c.txt")));
        Assert.False(File.Exists(a));
    }

    [Fact]
    public void Rename_works_for_folders()
    {
        var d = Path.Combine(_dir, "d1");
        Directory.CreateDirectory(d);
        LocalFs.Rename(d, Path.Combine(_dir, "d2"));
        Assert.True(Directory.Exists(Path.Combine(_dir, "d2")));
    }

    [Fact]
    public async Task Stat_sums_up_folders()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "sub", "deep"));
        File.WriteAllBytes(Path.Combine(_dir, "sub", "one.bin"), new byte[100]);
        File.WriteAllBytes(Path.Combine(_dir, "sub", "deep", "two.bin"), new byte[250]);

        var stat = await LocalFs.StatAsync(Path.Combine(_dir, "sub"));
        Assert.True(stat.IsDir);
        Assert.Equal(350, stat.Size);
        Assert.Equal(2, stat.Items);

        var file = await LocalFs.StatAsync(Path.Combine(_dir, "sub", "one.bin"));
        Assert.False(file.IsDir);
        Assert.Equal(100, file.Size);
        await Assert.ThrowsAsync<FileNotFoundException>(() => LocalFs.StatAsync(Path.Combine(_dir, "missing")));
    }

    [Fact]
    public async Task Checksum_is_sha256()
    {
        var f = Path.Combine(_dir, "abc.txt");
        File.WriteAllText(f, "abc");
        Assert.Equal("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", await LocalFs.ChecksumAsync(f));
    }
}
