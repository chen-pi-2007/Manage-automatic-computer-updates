using Microsoft.Win32;
using UpdateHelper.Agent.Monitor;
using UpdateHelper.Core.Monitor;

namespace UpdateHelper.Core.Tests.Agent;

/// <summary>
/// 复验探针的真实实现：只在临时目录和 HKCU 下的临时键里操作，测完删掉。不需要管理员。
/// </summary>
public sealed class RealFootprintProbeTests : IDisposable
{
    private readonly string _keyPath = @"Software\UpdateHelperTests\Probe-" + Guid.NewGuid().ToString("N");
    private readonly string _dir = Directory.CreateTempSubdirectory("uh-probe-").FullName;
    private readonly RealFootprintProbe _probe = new();

    public void Dispose()
    {
        try { Registry.CurrentUser.DeleteSubKeyTree(_keyPath, throwOnMissingSubKey: false); } catch (Exception) { }
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private static FootprintEntry Entry(FootprintEntryType type, string path) =>
        new(type, path, null, FootprintConfidence.Certain, 1, null, DateTimeOffset.Now, true);

    [Fact]
    public void Value_whose_name_contains_backslashes_is_found()
    {
        // SharedDLLs 一类：拿完整文件路径当值名
        using (var k = Registry.CurrentUser.CreateSubKey(_keyPath)) k.SetValue(@"C:\Program Files\Foo\a.dll", 1);
        var path = @"HKCU\" + _keyPath + @"\C:\Program Files\Foo\a.dll";
        Assert.True(_probe.StillExists(Entry(FootprintEntryType.RegistryValue, path)));
        Assert.False(_probe.StillExists(Entry(FootprintEntryType.RegistryValue, @"HKCU\" + _keyPath + @"\C:\nope.dll")));
    }

    [Fact]
    public void Default_value_path_ending_with_backslash_is_checked_as_the_default_value()
    {
        var path = @"HKCU\" + _keyPath + @"\";
        using (var k = Registry.CurrentUser.CreateSubKey(_keyPath)) { }
        Assert.False(_probe.StillExists(Entry(FootprintEntryType.RegistryValue, path)));   // 键在，默认值没设
        using (var k = Registry.CurrentUser.CreateSubKey(_keyPath)) k.SetValue("", "x");
        Assert.True(_probe.StillExists(Entry(FootprintEntryType.RegistryValue, path)));
    }

    [Fact]
    public void File_created_before_monitoring_existed_before()
    {
        var file = Path.Combine(_dir, "old.txt");
        File.WriteAllText(file, "x");
        File.SetCreationTimeUtc(file, DateTime.UtcNow.AddMinutes(-10));
        var since = DateTimeOffset.UtcNow.AddMinutes(-1);
        Assert.True(_probe.ExistedBefore(Entry(FootprintEntryType.File, file), since));

        var fresh = Path.Combine(_dir, "new.txt");
        File.WriteAllText(fresh, "y");
        Assert.False(_probe.ExistedBefore(Entry(FootprintEntryType.File, fresh), since));
        Assert.False(_probe.ExistedBefore(Entry(FootprintEntryType.RegistryKey, @"HKCU\Software"), since));   // 非文件不判断
    }
}
