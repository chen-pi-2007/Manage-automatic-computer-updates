using UpdateHelper.Core.Updates;

namespace UpdateHelper.Core.Tests;

public class AppVersionTests
{
    [Theory]
    [InlineData("3.1.12", "3.1.41", -1)]                       // 网易云
    [InlineData("9.9.20.36330", "9.9.33.52230", -1)]           // QQ
    [InlineData("12.1.0.23125", "12.1.0.24000", -1)]           // WPS
    [InlineData("2022.10", "2026.07-1", -1)]                   // Anaconda
    [InlineData("7.0.12", "7.2.20", -1)]                       // VirtualBox
    [InlineData("2.50.1", "2.55.0.5", -1)]                     // Git
    [InlineData("1.2", "1.2.0", 0)]                            // 末尾补 0
    [InlineData("1.10", "1.9", 1)]                             // 按数字比，不按字符串
    [InlineData("26.168.0830.0006", "26.173.0906.0008", -1)]   // 带前导 0
    [InlineData("1.0.0", "1.0.0-beta", 1)]                     // 正式版比预览版新
    [InlineData("2021.3.45f2c1", "2021.3.45f1", 1)]            // Unity 的后缀
    [InlineData("v1.2.3", "1.2.3", 0)]
    [InlineData("3.3.3-c7", "3.21.3.65535", -1)]               // Unity Hub
    public void Compares(string a, string b, int expected)
        => Assert.Equal(expected, Math.Sign(AppVersion.Parse(a)!.CompareTo(AppVersion.Parse(b))));

    [Theory]
    [InlineData("< 3.10.8")]
    [InlineData("> 1.8.10")]
    public void Winget_range_versions_are_approximate(string text)   // Review Focus 1
    {
        var v = AppVersion.Parse(text)!;
        Assert.True(v.IsApproximate);
        Assert.Equal(text, v.Original);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("abc")]
    [InlineData("beta.1")]
    [InlineData("<")]
    [InlineData("1234567890123456789012.1")]   // 数字太长
    public void Garbage_is_not_parsed(string? text)                  // Review Focus 1
        => Assert.Null(AppVersion.Parse(text));

    [Theory]
    [InlineData("8.0.3310.9", 8, 0)]
    [InlineData("2026.07-1", 2026, 7)]
    [InlineData("13", 13, 0)]
    public void Major_and_minor(string text, long major, long minor)
    {
        var v = AppVersion.Parse(text)!;
        Assert.Equal(major, v.Major);
        Assert.Equal(minor, v.Minor);
    }

    [Fact]
    public void Compare_to_null_is_greater()
        => Assert.True(AppVersion.Parse("1.0")!.CompareTo(null) > 0);
}
