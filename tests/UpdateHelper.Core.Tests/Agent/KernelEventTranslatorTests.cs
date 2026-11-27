using UpdateHelper.Agent.Monitor;
using UpdateHelper.Core.Monitor;

namespace UpdateHelper.Core.Tests.Agent;

/// <summary>
/// 内核事件翻译：字段组合取自真机探针（Kernel-File / Kernel-Registry 清单提供程序）的实际输出。
/// </summary>
public class KernelEventTranslatorTests
{
    private const string Sid = "S-1-5-21-1-2-3-1001";
    private static readonly DateTimeOffset T = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static KernelEventTranslator New() =>
        new(p => (p.Replace(@"\Device\HarddiskVolume1", "C:"), null), Sid, pid => pid == 7 ? @"C:\x\child.exe" : "");

    [Fact]
    public void Create_new_file_becomes_create_event()
    {
        var e = Assert.IsType<FileEvent>(New().FileCreatedNew(5, @"\Device\HarddiskVolume1\a\new.dat", 0x05400060, T));
        Assert.Equal(@"C:\a\new.dat", e.Path);
        Assert.True(e.IsCreate);
    }

    [Fact]
    public void Created_directory_is_reported_as_directory()
    {
        // 目录也要报给 Core（不进足迹，只用来判断改名目标是否落在本次新建的目录里）
        var e = Assert.IsType<FileEvent>(New().FileCreatedNew(5, @"\Device\HarddiskVolume1\a\dir", 0x02000001, T));
        Assert.True(e.IsDirectory);
        Assert.Equal(@"C:\a\dir", e.Path);
        Assert.False(Assert.IsType<FileEvent>(New().FileCreatedNew(5, @"\Device\HarddiskVolume1\a\f.txt", 0x05400060, T)).IsDirectory);
    }

    [Fact]
    public void Rename_finds_old_name_from_the_open_with_same_file_object()
    {
        var t = New();
        t.FileOpened(0xAA, @"\Device\HarddiskVolume1\a\final.dat.tmp");
        var r = Assert.IsType<FileRenameEvent>(t.FileRenamed(5, 0xAA, @"\Device\HarddiskVolume1\a\final.dat", T));
        Assert.Equal(@"C:\a\final.dat.tmp", r.OldPath);
        Assert.Equal(@"C:\a\final.dat", r.NewPath);
    }

    [Fact]
    public void Rename_with_unknown_file_object_has_no_old_name()
    {
        var r = Assert.IsType<FileRenameEvent>(New().FileRenamed(5, 0xBB, @"\Device\HarddiskVolume1\a\final.dat", T));
        Assert.Null(r.OldPath);
    }

    [Fact]
    public void Opening_existing_key_is_not_a_create_but_later_set_value_gets_its_path()
    {
        var t = New();
        // 先有一个绝对路径打开的基键（HKCU 根），再相对它打开已有子键（Disposition=2 / OpenKey 为 0）
        Assert.Null(t.KeyOpenedOrCreated(5, 0, 0x10, 0, 0, null, @"\REGISTRY\USER\" + Sid, T));
        Assert.Null(t.KeyOpenedOrCreated(5, 0x10, 0x20, 0, 2, "", @"Software\Pre", T));

        var set = Assert.IsType<RegistryEvent>(t.ValueSet(5, 0x20, 0, "Touched", 4, 4, 4, 4, T));
        Assert.Equal(@"HKCU\Software\Pre", set.KeyPath);
        Assert.Equal("Touched", set.ValueName);
        Assert.False(set.IsNew);   // 有旧值（类型/大小非 0）→ 改写，不是新值
    }

    [Fact]
    public void Really_created_key_produces_create_event_and_new_value_is_marked_new()
    {
        var t = New();
        t.KeyOpenedOrCreated(5, 0, 0x10, 0, 0, null, @"\REGISTRY\MACHINE\SOFTWARE", T);
        var created = Assert.IsType<RegistryEvent>(t.KeyOpenedOrCreated(5, 0x10, 0x30, 0, 1, "", "Vendor", T));
        Assert.Equal(MonitorEventKind.RegistryCreate, created.Kind);
        Assert.Equal(@"HKLM\SOFTWARE\Vendor", created.KeyPath);

        var set = Assert.IsType<RegistryEvent>(t.ValueSet(5, 0x30, 0, "Installed", 4, 4, 0, 0, T));
        Assert.True(set.IsNew);
    }

    [Fact]
    public void Created_key_under_unknown_base_is_reported_with_empty_path_not_dropped()
    {
        var created = Assert.IsType<RegistryEvent>(New().KeyOpenedOrCreated(5, 0x99, 0x40, 0, 1, "", "Orphan", T));
        Assert.Equal("", created.KeyPath);   // Core 据此标不完整
    }

    [Fact]
    public void Failed_operations_produce_nothing()
    {
        var t = New();
        Assert.Null(t.KeyOpenedOrCreated(5, 0, 0x10, unchecked((int)0xC0000034), 1, null, @"\REGISTRY\MACHINE\X", T));
        Assert.Null(t.ValueSet(5, 0x10, unchecked((int)0xC0000022), "v", 4, 4, 0, 0, T));
    }

    [Fact]
    public void Background_activity_moderator_value_is_ignored()
    {
        Assert.Null(New().ValueSet(5, 0x77, 0, @"\Device\HarddiskVolume1\x\a.exe", 3, 24, 0, 0, T));
    }

    [Fact]
    public void Device_path_value_name_is_kept_when_its_key_is_known()
    {
        var t = New();
        t.KeyOpenedOrCreated(5, 0, 0x10, 0, 0, null, @"\REGISTRY\MACHINE\SOFTWARE\Vendor", T);
        var set = Assert.IsType<RegistryEvent>(t.ValueSet(5, 0x10, 0, @"\Device\X", 1, 8, 0, 0, T));
        Assert.Equal(@"HKLM\SOFTWARE\Vendor", set.KeyPath);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Default_value_is_a_value_with_empty_name_never_a_key(string? eventValueName)
    {
        var t = New();
        t.KeyOpenedOrCreated(5, 0, 0x10, 0, 0, null, @"\REGISTRY\MACHINE\SOFTWARE\Existing", T);
        var set = Assert.IsType<RegistryEvent>(t.ValueSet(5, 0x10, 0, eventValueName, 1, 8, 0, 0, T));
        Assert.Equal("", set.ValueName);   // 空串 = 默认值；null 才表示"针对键"
        var del = Assert.IsType<RegistryEvent>(t.ValueDeleted(5, 0x10, 0, eventValueName, T));
        Assert.Equal("", del.ValueName);
    }

    [Fact]
    public void Empty_reg_none_value_is_marked_uncertain()
    {
        var t = New();
        t.KeyOpenedOrCreated(5, 0, 0x10, 0, 0, null, @"\REGISTRY\MACHINE\SOFTWARE\Classes\.txt\OpenWithProgids", T);
        var set = Assert.IsType<RegistryEvent>(t.ValueSet(5, 0x10, 0, "Foo.txt", 0, 0, 0, 0, T));
        Assert.True(set.IsNew);
        Assert.True(set.IsNewUncertain);   // "原本不存在"与"原本就是空 REG_NONE"分不清
        var normal = Assert.IsType<RegistryEvent>(t.ValueSet(5, 0x10, 0, "Bar", 1, 8, 0, 0, T));
        Assert.False(normal.IsNewUncertain);
    }

    [Fact]
    public void Rename_still_finds_old_name_after_table_rotation()
    {
        var t = New();
        t.FileOpened(0xAA, @"\Device\HarddiskVolume1\a\final.dat.tmp");
        for (ulong i = 1; i <= 100_000; i++) t.FileOpened(0x1000_0000 + i, @"\Device\HarddiskVolume1\noise");   // 写满一代，触发轮换
        var r = Assert.IsType<FileRenameEvent>(t.FileRenamed(5, 0xAA, @"\Device\HarddiskVolume1\a\final.dat", T));
        Assert.Equal(@"C:\a\final.dat.tmp", r.OldPath);
    }

    [Fact]
    public void Process_start_uses_child_pid_and_image_lookup()
    {
        var p = Assert.IsType<ProcessStartEvent>(New().ProcessStarted(7, 5, T));
        Assert.Equal(7, p.Pid);
        Assert.Equal(5, p.ParentPid);
        Assert.Equal(@"C:\x\child.exe", p.ImagePath);
    }

    [Theory]
    [InlineData(@"\REGISTRY\MACHINE\SOFTWARE\X", @"HKLM\SOFTWARE\X")]
    [InlineData(@"\REGISTRY\USER\" + Sid + @"\Software\X", @"HKCU\Software\X")]
    [InlineData(@"\REGISTRY\USER\S-1-5-18\Software\X", @"HKU\S-1-5-18\Software\X")]
    [InlineData(null, "")]
    public void Reg_path_maps_kernel_paths_to_hives(string? kernel, string expected)
    {
        Assert.Equal(expected, KernelEventTranslator.RegPath(kernel, Sid));
    }
}
