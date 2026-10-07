using System.Text;
using UpdateHelper.Presentation.Settings;

namespace UpdateHelper.Presentation.Tests;

public sealed class SettingsStoreTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("uh-settings-").FullName;
    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    private string PathOf(string name = "settings.json") => Path.Combine(_dir, "sub", name);

    [Fact]
    public void Missing_file_gives_defaults()
    {
        var s = new SettingsStore(PathOf()).Load();
        Assert.Equal(new AppSettings(), s);
        Assert.Equal(UpdateMode.NotifyOnly, s.Mode);   // spec 第 6 节：默认只提醒
        Assert.Equal(3, s.ObservationDays);
        Assert.Equal(24, s.CheckIntervalHours);
        Assert.True(s.TrayEnabled);
    }

    [Fact]
    public void Saved_settings_are_loaded_back()
    {
        var store = new SettingsStore(PathOf());   // 目录不存在也能保存
        var wanted = new AppSettings { Mode = UpdateMode.Tiered, ObservationDays = 7, CheckIntervalHours = 6, TrayEnabled = false };
        store.Save(wanted);
        Assert.Equal(wanted, store.Load());
        Assert.Contains("\"Tiered\"", File.ReadAllText(PathOf()));   // 模式按名字保存，文件可读
    }

    [Theory]   // Review Focus 4
    [InlineData("这不是 JSON")]
    [InlineData("{\"Mode\": \"NoSuchMode\"}")]
    [InlineData("[1, 2, 3]")]
    [InlineData("")]
    public void Corrupt_file_gives_defaults(string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(PathOf())!);
        File.WriteAllText(PathOf(), content, Encoding.UTF8);
        Assert.Equal(new AppSettings(), new SettingsStore(PathOf()).Load());
    }

    [Theory]   // Review Focus 4：数值越界时限制到范围内
    [InlineData(-5, 0, 0, 1)]
    [InlineData(999, 30, 1000, 168)]
    [InlineData(10, 10, 12, 12)]
    public void Out_of_range_numbers_are_clamped(int days, int expectedDays, int hours, int expectedHours)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(PathOf())!);
        File.WriteAllText(PathOf(), $"{{\"ObservationDays\": {days}, \"CheckIntervalHours\": {hours}}}");
        var s = new SettingsStore(PathOf()).Load();
        Assert.Equal(expectedDays, s.ObservationDays);
        Assert.Equal(expectedHours, s.CheckIntervalHours);
    }

    [Fact]
    public void Unknown_fields_are_ignored_and_missing_fields_use_defaults()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(PathOf())!);
        File.WriteAllText(PathOf(), "{\"Mode\": \"FullAuto\", \"SomethingNew\": 1}");
        var s = new SettingsStore(PathOf()).Load();
        Assert.Equal(UpdateMode.FullAuto, s.Mode);
        Assert.Equal(3, s.ObservationDays);
    }

    [Fact]
    public void Save_leaves_no_temporary_file()
    {
        new SettingsStore(PathOf()).Save(new AppSettings());
        Assert.Equal(new[] { "settings.json" }, Directory.GetFiles(Path.GetDirectoryName(PathOf())!).Select(Path.GetFileName));
    }
}
