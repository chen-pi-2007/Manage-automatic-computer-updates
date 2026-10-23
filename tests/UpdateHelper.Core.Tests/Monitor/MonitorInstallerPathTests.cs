using UpdateHelper.Core.Monitor;

namespace UpdateHelper.Core.Tests.Monitor;

public sealed class MonitorInstallerPathTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("uh-instpath-").FullName;
    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    private string Touch(string name)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, "x");
        return path;
    }

    [Theory]
    [InlineData("setup.exe")]
    [InlineData("SETUP.EXE")]
    [InlineData("pkg.msi")]
    [InlineData("Pkg.MSI")]
    public void Existing_local_exe_or_msi_is_accepted(string name) =>
        Assert.Null(MonitorInstallerPath.Validate(Touch(name)));

    [Theory]
    [InlineData("readme.txt")]
    [InlineData("script.bat")]
    [InlineData("run.ps1")]
    [InlineData("shortcut.lnk")]
    [InlineData("noext")]
    public void Other_extensions_are_rejected(string name)
    {
        var error = MonitorInstallerPath.Validate(Touch(name));
        Assert.False(string.IsNullOrEmpty(error));
    }

    [Fact]
    public void Missing_file_is_rejected() =>
        Assert.False(string.IsNullOrEmpty(MonitorInstallerPath.Validate(Path.Combine(_dir, "nope.exe"))));

    [Fact]
    public void Directory_named_like_an_exe_is_rejected()
    {
        var dir = Path.Combine(_dir, "fake.exe");
        Directory.CreateDirectory(dir);
        Assert.False(string.IsNullOrEmpty(MonitorInstallerPath.Validate(dir)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("setup.exe")]                       // 相对路径
    [InlineData(@"\server\share\setup.exe")]       // 网络共享
    [InlineData(@"\?\C:\setup.exe")]               // 设备路径前缀
    [InlineData("C:\bad\0name.exe")]
    public void Empty_relative_network_and_illegal_paths_are_rejected(string? path) =>
        Assert.False(string.IsNullOrEmpty(MonitorInstallerPath.Validate(path)));
}
