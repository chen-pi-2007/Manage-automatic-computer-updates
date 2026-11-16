using UpdateHelper.Agent.Monitor;

namespace UpdateHelper.Core.Tests.Agent;

public class RegistryObjectTableTests
{
    [Fact]
    public void Chains_relative_opens_to_full_path()
    {
        var t = new RegistryObjectTable();
        Assert.Equal(@"\REGISTRY\MACHINE", t.Record(0, 1, 0, null, @"\REGISTRY\MACHINE"));
        Assert.Equal(@"\REGISTRY\MACHINE\SOFTWARE\A", t.Record(1, 2, 0, "", @"SOFTWARE\A"));
        Assert.Equal(@"\REGISTRY\MACHINE\SOFTWARE\A\B", t.Record(2, 3, 0, null, "B"));
        Assert.Equal(@"\REGISTRY\MACHINE\SOFTWARE\A\B", t.Resolve(3));
    }

    [Fact]
    public void Event_base_name_wins_over_table()
    {
        var t = new RegistryObjectTable();
        Assert.Equal(@"\REGISTRY\MACHINE\X\Y", t.Record(42, 5, 0, @"\REGISTRY\MACHINE\X", "Y"));
    }

    [Fact]
    public void Unknown_base_yields_null_and_clears_stale_mapping_of_reused_address()
    {
        var t = new RegistryObjectTable();
        t.Record(0, 9, 0, null, @"\REGISTRY\MACHINE\OLD");
        Assert.Null(t.Record(777, 9, 0, null, "Something"));   // 同一地址现在指向一个说不清路径的键
        Assert.Null(t.Resolve(9));                              // 不能再拿旧路径冒充
    }

    [Fact]
    public void Failed_open_is_not_recorded()
    {
        var t = new RegistryObjectTable();
        Assert.Null(t.Record(0, 9, unchecked((int)0xC0000034), null, @"\REGISTRY\MACHINE\X"));
        Assert.Null(t.Resolve(9));
    }

    [Fact]
    public void Generation_rotation_keeps_keys_still_in_use()
    {
        var t = new RegistryObjectTable(generationSize: 2);
        t.Record(0, 1, 0, null, @"\REGISTRY\A");
        t.Record(0, 2, 0, null, @"\REGISTRY\B");
        t.Record(0, 3, 0, null, @"\REGISTRY\C");   // 轮换：1、2 降为上一代
        Assert.Equal(@"\REGISTRY\A", t.Resolve(1)); // 命中上一代 → 提回当前代
        t.Record(0, 4, 0, null, @"\REGISTRY\D");    // 再轮换：2 被淘汰，1 因刚用过而保留
        Assert.Equal(@"\REGISTRY\A", t.Resolve(1));
        Assert.Null(t.Resolve(2));
    }
}
