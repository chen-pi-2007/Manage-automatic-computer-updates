using UpdateHelper.Core.Uninstall;

namespace UpdateHelper.Core.Tests;

public sealed class RealLeftoverProbesTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("uh-probe-").FullName;
    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    [Fact]
    public void Directory_exists_and_size()
    {
        File.WriteAllText(Path.Combine(_dir, "a.txt"), new string('x', 1000));
        Directory.CreateDirectory(Path.Combine(_dir, "sub"));
        File.WriteAllText(Path.Combine(_dir, "sub", "b.txt"), new string('y', 500));
        var probe = new RealFileProbe();

        Assert.True(probe.DirectoryExists(_dir));
        Assert.False(probe.DirectoryExists(Path.Combine(_dir, "nope")));
        Assert.Equal(1500, probe.DirectorySize(_dir));   // 递归求和
    }

    [Fact]
    public void Child_directories_listed()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "c1"));
        Directory.CreateDirectory(Path.Combine(_dir, "c2"));
        var kids = new RealFileProbe().GetChildDirectories(_dir);
        Assert.Equal(2, kids.Count);
    }

    [Fact]
    public void Nonexistent_dir_size_is_null_not_throw()
    {
        Assert.Null(new RealFileProbe().DirectorySize(Path.Combine(_dir, "ghost")));
        Assert.Empty(new RealFileProbe().GetChildDirectories(Path.Combine(_dir, "ghost")));
    }

    [Fact]
    public void Registry_key_exists()
    {
        var probe = new RealRegistryProbe();
        Assert.True(probe.KeyExists(@"HKLM\SOFTWARE\Microsoft\Windows"));
        Assert.False(probe.KeyExists(@"HKCU\Software\UpdateHelperDefinitelyMissing12345"));
        Assert.False(probe.KeyExists("not a key"));   // 不抛
    }
}
