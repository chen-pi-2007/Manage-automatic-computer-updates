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
    [InlineData(@"HKCU\SOFTWARE\Classes")]
    [InlineData(@"HKCU\SOFTWARE\Classes\.foo")]
    [InlineData(@"HKLM\SOFTWARE\WOW6432Node\Classes")]
    [InlineData(@"HKLM\SOFTWARE\WOW6432Node\Classes\CLSID\X")]
    [InlineData(@"HKLM\SOFTWARE\WOW6432Node\Policies")]
    [InlineData(@"HKLM\SOFTWARE\WOW6432Node\Policies\X")]
    [InlineData(@"HKCU\SOFTWARE\WOW6432Node\Classes")]
    [InlineData(@"HKCU\SOFTWARE\WOW6432Node\Classes\X")]
    [InlineData(@"HKCU\SOFTWARE\WOW6432Node\Policies")]
    [InlineData(@"HKCU\SOFTWARE\WOW6432Node\Policies\X")]
    public void Registry_classes_policies_subtrees_protected(string key) =>
        Assert.True(SystemPaths.IsProtectedRegistryKey(key));

    private static string Local => Env(Environment.SpecialFolder.LocalApplicationData);

    [Fact]
    public void WindowsApps_subtree_protected()
    {
        var wa = Path.Combine(Env(Environment.SpecialFolder.ProgramFiles), "WindowsApps");
        Assert.True(SystemPaths.IsProtectedDirectory(wa));
        Assert.True(SystemPaths.IsProtectedDirectory(Path.Combine(wa, "Some.App_1.0")));
    }

    [Fact]
    public void Microsoft_containers_subtree_protected_vendor_allowed()
    {
        string[] roots =
        [
            Path.Combine(Local, "Microsoft"),
            Path.Combine(Env(Environment.SpecialFolder.ApplicationData), "Microsoft"),
            Path.Combine(Env(Environment.SpecialFolder.CommonApplicationData), "Microsoft"),
            Path.Combine(Env(Environment.SpecialFolder.CommonProgramFiles), "Microsoft Shared"),
            Path.Combine(Env(Environment.SpecialFolder.CommonProgramFilesX86), "Microsoft Shared"),
        ];
        foreach (var r in roots)
        {
            Assert.True(SystemPaths.IsProtectedDirectory(r), r);
            Assert.True(SystemPaths.IsProtectedDirectory(Path.Combine(r, "Sub")), r);
        }
        Assert.False(SystemPaths.IsProtectedDirectory(Path.Combine(Local, "SomeVendor")));
    }

    [Fact]
    public void Temp_and_LocalPrograms_roots_protected_subdirs_allowed()
    {
        var temp = Path.GetTempPath();
        Assert.True(SystemPaths.IsProtectedDirectory(temp));
        Assert.False(SystemPaths.IsProtectedDirectory(Path.Combine(temp, "VendorTemp")));
        var programs = Path.Combine(Local, "Programs");
        Assert.True(SystemPaths.IsProtectedDirectory(programs));
        Assert.False(SystemPaths.IsProtectedDirectory(Path.Combine(programs, "Vendor")));
    }

    [Fact]
    public void Media_folders_roots_protected()
    {
        Assert.True(SystemPaths.IsProtectedDirectory(Env(Environment.SpecialFolder.MyPictures)));
        Assert.True(SystemPaths.IsProtectedDirectory(Env(Environment.SpecialFolder.MyVideos)));
        Assert.True(SystemPaths.IsProtectedDirectory(Env(Environment.SpecialFolder.MyMusic)));
    }

    [Theory]
    [InlineData(@"HKCU\Software\Tencent")]
    [InlineData(@"HKLM\SOFTWARE\Kingsoft\WPS")]
    [InlineData(@"HKLM\SOFTWARE\WOW6432Node\Kingsoft")]
    public void Registry_vendor_keys_not_protected(string key) =>
        Assert.False(SystemPaths.IsProtectedRegistryKey(key));
}
