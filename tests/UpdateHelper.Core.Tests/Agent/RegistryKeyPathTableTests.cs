using UpdateHelper.Agent.Monitor;

namespace UpdateHelper.Core.Tests.Agent;

/// <summary>
/// KCB 句柄 → 键全路径 兜底表的纯逻辑单测（不依赖真实内核会话）。
/// 复现真机冒烟的核心场景：内核 SetValue 事件只带值名、键靠 KCB 句柄，
/// 当事件自带 KeyName 为空时，用先前 Create/KCBCreate 记下的 句柄→全路径 回查补回键路径。
/// </summary>
public class RegistryKeyPathTableTests
{
    private const string UninstallKey =
        @"\REGISTRY\MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\MyApp";

    // 常态：事件自带了键全路径（TraceEvent 已解析成功），原样返回，不碰兜底表。
    [Fact]
    public void Resolve_prefers_event_key_name_when_present()
    {
        var t = new RegistryKeyPathTable();
        Assert.Equal(UninstallKey, t.Resolve(keyHandle: 0x1234, eventKeyName: UninstallKey));
    }

    // 关键修复场景：Create 记下 句柄→全路径后，同句柄上 KeyName 为空的 SetValue 能解析出键全路径。
    [Fact]
    public void Resolve_falls_back_to_recorded_path_when_event_key_name_empty()
    {
        var t = new RegistryKeyPathTable();
        t.Record(0x1234, UninstallKey);                       // 相当于 RegistryCreate(KCB=0x1234, 全路径)
        Assert.Equal(UninstallKey, t.Resolve(0x1234, ""));    // 相当于 SetValue(KCB=0x1234, KeyName 空)
        Assert.Equal(UninstallKey, t.Resolve(0x1234, null));
    }

    // 模拟一串事件：Create(KCB,路径) → SetValue(KCB,值名,KeyName空) → 解析出的键路径正确。
    [Fact]
    public void Create_then_setvalue_sequence_resolves_full_key_path()
    {
        var t = new RegistryKeyPathTable();

        // Create 事件：句柄 + 全路径都在
        ulong kcb = 0xABCD;
        t.Record(kcb, UninstallKey);

        // 随后两个 SetValue：DisplayName / Installed，内核事件只给值名，KeyName 为空
        var displayNameKey = t.Resolve(kcb, "");
        var installedKey = t.Resolve(kcb, "");

        Assert.Equal(UninstallKey, displayNameKey);
        Assert.Equal(UninstallKey, installedKey);
    }

    // 未知句柄、空名、空句柄：绝不瞎猜，返回空串 / 不记录。
    [Fact]
    public void Resolve_unknown_handle_returns_empty()
    {
        var t = new RegistryKeyPathTable();
        Assert.Equal("", t.Resolve(0x9999, ""));
        Assert.Equal("", t.Resolve(0, ""));
    }

    [Fact]
    public void Record_ignores_zero_handle_and_empty_name()
    {
        var t = new RegistryKeyPathTable();
        t.Record(0, UninstallKey);     // 空句柄：忽略
        t.Record(0x1, "");             // 空名：忽略
        t.Record(0x1, null);
        Assert.Equal("", t.Resolve(0, ""));
        Assert.Equal("", t.Resolve(0x1, ""));
    }

    // 句柄复用：内核把同一 KCB 句柄复用给新键时，新 Record 后写覆盖旧映射。
    [Fact]
    public void Record_last_write_wins_for_reused_handle()
    {
        var t = new RegistryKeyPathTable();
        t.Record(0x55, @"\REGISTRY\MACHINE\SOFTWARE\Old");
        t.Record(0x55, @"\REGISTRY\MACHINE\SOFTWARE\New");
        Assert.Equal(@"\REGISTRY\MACHINE\SOFTWARE\New", t.Resolve(0x55, ""));
    }

    // KCBDelete 后忘掉映射：再回查即为未知。
    [Fact]
    public void Forget_removes_mapping()
    {
        var t = new RegistryKeyPathTable();
        t.Record(0x77, UninstallKey);
        t.Forget(0x77);
        Assert.Equal("", t.Resolve(0x77, ""));
    }
}
