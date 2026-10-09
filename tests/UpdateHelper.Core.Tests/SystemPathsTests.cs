using UpdateHelper.Core.Uninstall;

namespace UpdateHelper.Core.Tests;

public sealed class SystemPathsTests
{
    private static string Env(Environment.SpecialFolder f) => Environment.GetFolderPath(f);

    [Fact]
    public void Program_files_root_protected_but_vendor_subdir_allowed()
    {
        var pf = Env(Environment.SpecialFolder.ProgramFiles);
        Assert.True(SystemPaths.IsProtectedDirectory(pf));
        Assert.False(SystemPaths.IsProtectedDirectory(Path.Combine(pf, "Foo")));
    }

    [Fact]
    public void Drive_root_protected() => Assert.True(SystemPaths.IsProtectedDirectory(@"D:\"));

    [Fact]
    public void Windows_whole_subtree_protected()
    {
        var win = Env(Environment.SpecialFolder.Windows);
        Assert.True(SystemPaths.IsProtectedDirectory(win));
        Assert.True(SystemPaths.IsProtectedDirectory(Path.Combine(win, @"System32\drivers")));
    }

    [Fact]
    public void Appdata_roots_protected_but_vendor_dirs_allowed()
    {
        var local = Env(Environment.SpecialFolder.LocalApplicationData);
        var roaming = Env(Environment.SpecialFolder.ApplicationData);
        Assert.True(SystemPaths.IsProtectedDirectory(local));
        Assert.True(SystemPaths.IsProtectedDirectory(roaming));
        Assert.False(SystemPaths.IsProtectedDirectory(Path.Combine(local, "Vendor")));
        Assert.False(SystemPaths.IsProtectedDirectory(Path.Combine(roaming, "Vendor")));
    }

    [Fact]
    public void User_folders_protected()
    {
        var profile = Env(Environment.SpecialFolder.UserProfile);
        Assert.True(SystemPaths.IsProtectedDirectory(Path.Combine(profile, "Downloads")));
        Assert.True(SystemPaths.IsProtectedDirectory(Path.Combine(profile, @"AppData\LocalLow")));
        Assert.True(SystemPaths.IsProtectedDirectory(Env(Environment.SpecialFolder.Desktop)));
        Assert.True(SystemPaths.IsProtectedDirectory(Env(Environment.SpecialFolder.MyDocuments)));
        Assert.True(SystemPaths.IsProtectedDirectory(@"C:\Users\Public"));
    }

    [Theory]
    [InlineData(@"HKLM\SYSTEM\X")]
    [InlineData(@"HKLM\SOFTWARE")]
    [InlineData(@"HKLM\SOFTWARE\Microsoft\Anything")]
    [InlineData(@"HKCU\Software")]
    [InlineData(@"HKCR\Anything")]
    [InlineData(@"HKLM\SOFTWARE\WOW6432Node")]
    [InlineData(@"HKLM\SOFTWARE\WOW6432Node\Microsoft\X")]
    [InlineData(@"HKLM\SOFTWARE\Classes\X")]
    [InlineData(@"HKLM\SOFTWARE\Policies")]
    public void Registry_system_keys_protected(string key) =>
        Assert.True(SystemPaths.IsProtectedRegistryKey(key));

    [Theory]
    [InlineData(@"HKCU\Software\Tencent")]
    [InlineData(@"HKLM\SOFTWARE\Kingsoft\WPS")]
    [InlineData(@"HKLM\SOFTWARE\WOW6432Node\Kingsoft")]
    public void Registry_vendor_keys_not_protected(string key) =>
        Assert.False(SystemPaths.IsProtectedRegistryKey(key));
}
