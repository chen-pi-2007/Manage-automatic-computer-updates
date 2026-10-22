using UpdateHelper.Core.Monitor;

namespace UpdateHelper.Core.Tests.Monitor;

public class LocationClassifierTests
{
    // 用固定基准，避免测试依赖本机真实的 Program Files / 用户目录。
    private static readonly KnownFolders F = new(
        ProgramFiles: @"C:\Program Files",
        ProgramFilesX86: @"C:\Program Files (x86)",
        ProgramData: @"C:\ProgramData",
        LocalAppData: @"C:\Users\u\AppData\Local",
        RoamingAppData: @"C:\Users\u\AppData\Roaming",
        UserTemp: @"C:\Users\u\AppData\Local\Temp",
        Windows: @"C:\Windows");

    [Theory]
    // 驻留位置 -> Keep
    [InlineData(@"C:\Program Files\Foo\a.exe", LocationClass.Keep)]
    [InlineData(@"C:\Program Files (x86)\Foo\a.exe", LocationClass.Keep)]
    [InlineData(@"C:\ProgramData\Foo\cfg.ini", LocationClass.Keep)]
    [InlineData(@"C:\Users\u\AppData\Local\Foo\a.dll", LocationClass.Keep)]
    [InlineData(@"C:\Users\u\AppData\Roaming\Foo\a.cfg", LocationClass.Keep)]
    [InlineData(@"c:\PROGRAM FILES\foo\A.EXE", LocationClass.Keep)] // 大小写不敏感
    // 噪声 -> Drop（优先于白名单：Temp 在 LocalAppData 之下）
    [InlineData(@"C:\Users\u\AppData\Local\Temp\x\setup.tmp", LocationClass.Drop)]
    [InlineData(@"C:\Windows\Temp\a.tmp", LocationClass.Drop)]
    [InlineData(@"C:\Windows\Prefetch\FOO.pf", LocationClass.Drop)]
    [InlineData(@"C:\ProgramData\Microsoft\Windows\WER\ReportQueue\r.wer", LocationClass.Drop)]
    [InlineData(@"C:\Program Files\Foo\trace.etl", LocationClass.Drop)]
    // 模糊 -> Ambiguous
    [InlineData(@"C:\MyTool\data.bin", LocationClass.Ambiguous)]
    // 前缀必须按目录边界匹配，Program Files2 不是 Program Files
    [InlineData(@"C:\Program Files2\a.exe", LocationClass.Ambiguous)]
    [InlineData(@"C:\Windows\System32\x.dll", LocationClass.Ambiguous)]
    public void Classify_buckets_paths(string path, LocationClass expected)
        => Assert.Equal(expected, LocationClassifier.Classify(path, F));

    [Fact]
    public void Classify_default_overload_does_not_throw()
        => _ = LocationClassifier.Classify(@"D:\x\y.bin");

    [Fact]
    public void ToWin32_maps_device_path()
    {
        var map = new Dictionary<string, string> { [@"\Device\HarddiskVolume3"] = "C:" };
        Assert.Equal(@"C:\PF\a", PathNormalizer.ToWin32(@"\Device\HarddiskVolume3\PF\a", map));
    }

    [Fact]
    public void ToWin32_device_match_respects_boundary_and_case()
    {
        var map = new Dictionary<string, string>
        {
            [@"\Device\HarddiskVolume1"] = "C:",
            [@"\Device\HarddiskVolume12"] = "D:",
        };
        Assert.Equal(@"D:\a", PathNormalizer.ToWin32(@"\device\harddiskvolume12\a", map));
        Assert.Equal(@"C:\a", PathNormalizer.ToWin32(@"\Device\HarddiskVolume1\a", map));
    }

    [Fact]
    public void ToWin32_passthrough_win32()
        => Assert.Equal(@"C:\a\b", PathNormalizer.ToWin32(@"C:\a\b", new Dictionary<string, string>()));

    [Fact]
    public void ToWin32_unknown_device_returned_as_is()
        => Assert.Equal(@"\Device\Unknown\a", PathNormalizer.ToWin32(@"\Device\Unknown\a", new Dictionary<string, string>()));
}
