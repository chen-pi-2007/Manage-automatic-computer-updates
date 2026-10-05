using UpdateHelper.Core.Scanning;

namespace UpdateHelper.Core.Tests;

public class RegistryValuesTests
{
    [Theory]
    [InlineData("1.2.3", "1.2.3")]
    [InlineData("  ", null)]
    [InlineData(null, null)]
    public void AsString_trims_and_treats_blank_as_missing(string? input, string? expected)
        => Assert.Equal(expected, RegistryValues.AsString(input));

    [Fact]
    public void AsString_converts_dword_to_text()   // 版本号被存成 DWORD 的情况（Review Focus 1）
        => Assert.Equal("10", RegistryValues.AsString(10));

    [Fact]
    public void AsString_ignores_binary()
        => Assert.Null(RegistryValues.AsString(new byte[] { 1, 2 }));

    [Theory]
    [InlineData(1234, 1234L)]
    [InlineData("5678", 5678L)]   // 大小被存成字符串的情况（Review Focus 1）
    [InlineData("abc", null)]
    [InlineData(null, null)]
    public void AsLong_accepts_numbers_and_numeric_strings(object? input, long? expected)
        => Assert.Equal(expected, RegistryValues.AsLong(input));

    [Theory]
    [InlineData(1, true)]
    [InlineData(0, false)]
    [InlineData("1", true)]
    [InlineData(null, false)]
    public void AsFlag_reads_dword_booleans(object? input, bool expected)
        => Assert.Equal(expected, RegistryValues.AsFlag(input));

    private static Func<string, object?> Values(Dictionary<string, object?> d)
        => name => d.TryGetValue(name, out var v) ? v : null;

    [Fact]
    public void ToEntry_maps_all_fields()
    {
        var e = RegistryValues.ToEntry("QQ", UninstallHive.LocalMachine32, Values(new()
        {
            ["DisplayName"] = "QQ",
            ["DisplayVersion"] = "9.9.20.36330",
            ["Publisher"] = "腾讯科技(深圳)有限公司",
            ["InstallLocation"] = @"C:\Program Files\Tencent\QQNT\",
            ["UninstallString"] = "\"C:\\Program Files\\Tencent\\QQNT\\Uninstall.exe\"",
            ["SystemComponent"] = 0,
            ["EstimatedSize"] = 573440,
        }));

        Assert.NotNull(e);
        Assert.Equal("QQ", e!.DisplayName);
        Assert.Equal("9.9.20.36330", e.DisplayVersion);
        Assert.Equal(@"C:\Program Files\Tencent\QQNT\", e.InstallLocation);
        Assert.False(e.IsSystemComponent);
        Assert.False(e.IsUpdateOrPatch);
        Assert.Equal(573440L, e.EstimatedSizeKb);
    }

    [Fact]
    public void ToEntry_returns_null_without_display_name()
        => Assert.Null(RegistryValues.ToEntry("x", UninstallHive.CurrentUser, Values(new())));

    [Fact]
    public void ToEntry_marks_hidden_and_patch_entries()
    {
        var e = RegistryValues.ToEntry("KB1", UninstallHive.LocalMachine64, Values(new()
        {
            ["DisplayName"] = "Python 3.10.2 Core Interpreter (64-bit)",
            ["SystemComponent"] = 1,
            ["ParentKeyName"] = "Python310",
        }));
        Assert.True(e!.IsSystemComponent);
        Assert.True(e.IsUpdateOrPatch);
    }

    // 真实情况：QQ、WPS 的登记里 InstallLocation 是空的，只能从图标或卸载程序路径推断
    [Theory]
    [InlineData(@"C:\Program Files\Tencent\QQNT\QQ.exe,0", @"C:\Program Files\Tencent\QQNT\Uninstall.exe", @"C:\Program Files\Tencent\QQNT")]
    [InlineData(@"D:\WPS Office\ksolaunch.exe", @"D:\WPS Office\12.1.0.23125\utility\uninst.exe", @"D:\WPS Office")]
    [InlineData(null, "\"C:\\Apps\\Foo\\unins000.exe\" /S", @"C:\Apps\Foo")]
    [InlineData(@"C:\Windows\Installer\{GUID}\icon.ico", @"MsiExec.exe /X{GUID}", null)]          // Windows 目录不采用
    [InlineData(@"C:\ProgramData\Package Cache\{GUID}\setup.exe,0", null, null)]                  // 安装包缓存不采用
    [InlineData(@"D:\setup.exe", null, null)]                                                       // 磁盘根目录不采用
    public void ToEntry_infers_install_location_when_missing(string? icon, string? uninstall, string? expected)
    {
        var e = RegistryValues.ToEntry("x", UninstallHive.LocalMachine64, Values(new()
        {
            ["DisplayName"] = "X",
            ["DisplayIcon"] = icon,
            ["UninstallString"] = uninstall,
        }));
        Assert.Equal(expected, e!.InstallLocation, ignoreCase: true);
    }

    [Fact]
    public void ToEntry_keeps_explicit_install_location()
    {
        var e = RegistryValues.ToEntry("x", UninstallHive.LocalMachine64, Values(new()
        {
            ["DisplayName"] = "X",
            ["InstallLocation"] = @"E:\Real",
            ["DisplayIcon"] = @"C:\Other\x.exe",
        }));
        Assert.Equal(@"E:\Real", e!.InstallLocation);
    }

    [Fact]
    public void ToEntry_returns_null_when_key_is_unreadable()   // Review Focus 2
    {
        var e = RegistryValues.ToEntry("locked", UninstallHive.LocalMachine64,
            _ => throw new UnauthorizedAccessException());
        Assert.Null(e);
    }
}
