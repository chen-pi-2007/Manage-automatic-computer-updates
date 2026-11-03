using UpdateHelper.Core.Monitor;
using Xunit;

namespace UpdateHelper.Core.Tests.Monitor;

public class ProcessTreeTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static DateTimeOffset At(int s) => T0.AddSeconds(s);

    private static ProcessTree NewTree(int root = 100) => new(root, DelegateImages.Default);

    [Fact]
    public void Root_is_Tree()
    {
        var tree = NewTree();
        Assert.Equal(AttributionLevel.Tree, tree.Attribute(100, At(5)));
    }

    [Fact]
    public void Child_and_grandchild_are_Tree()
    {
        var tree = NewTree();
        tree.Observe(new ProcessStartEvent(200, 100, @"C:\a\child.exe", At(1)));
        tree.Observe(new ProcessStartEvent(300, 200, @"C:\a\grand.exe", At(2)));
        Assert.Equal(AttributionLevel.Tree, tree.Attribute(200, At(3)));
        Assert.Equal(AttributionLevel.Tree, tree.Attribute(300, At(3)));
    }

    [Fact]
    public void Unrelated_pid_is_Outside()
    {
        var tree = NewTree();
        tree.Observe(new ProcessStartEvent(400, 4, @"C:\x\other.exe", At(1)));
        Assert.Equal(AttributionLevel.Outside, tree.Attribute(400, At(2)));
        Assert.Equal(AttributionLevel.Outside, tree.Attribute(999, At(2)));
    }

    [Fact]
    public void Known_delegate_is_Delegated()
    {
        var tree = NewTree();
        tree.Observe(new ProcessStartEvent(500, 4, @"C:\Windows\System32\MsiExec.exe", At(1)));
        Assert.Equal(AttributionLevel.Delegated, tree.Attribute(500, At(2)));
    }

    [Fact]
    public void Pid_reuse_resolves_by_lifetime()
    {
        var tree = NewTree();
        tree.Observe(new ProcessStartEvent(200, 100, @"C:\a\old.exe", At(1)));
        tree.Observe(new ProcessStopEvent(200, At(10)));
        tree.Observe(new ProcessStartEvent(200, 4, @"C:\x\new.exe", At(20)));
        Assert.Equal(AttributionLevel.Tree, tree.Attribute(200, At(5)));
        Assert.Equal(AttributionLevel.Outside, tree.Attribute(200, At(25)));
    }

    [Fact]
    public void Reused_root_pid_after_root_stops_is_Outside()
    {
        var tree = NewTree();
        tree.Observe(new ProcessStopEvent(100, At(10)));
        tree.Observe(new ProcessStartEvent(100, 4, @"C:\x\reuse.exe", At(20)));
        tree.Observe(new ProcessStartEvent(600, 100, @"C:\x\reuse-child.exe", At(21)));
        Assert.Equal(AttributionLevel.Tree, tree.Attribute(100, At(5)));
        Assert.Equal(AttributionLevel.Outside, tree.Attribute(100, At(25)));
        Assert.Equal(AttributionLevel.Outside, tree.Attribute(600, At(25)));
    }

    [Fact]
    public void RootTreeHasExited_false_while_root_alive()
    {
        var tree = NewTree();
        // 根从未退出（也没有子进程）→ 整树未退出
        Assert.False(tree.RootTreeHasExited(At(5)));
    }

    [Fact]
    public void RootTreeHasExited_true_when_root_stops_without_children()
    {
        var tree = NewTree();
        tree.Observe(new ProcessStopEvent(100, At(2)));
        Assert.True(tree.RootTreeHasExited(At(3)));
        // 退出发生在 at 之后不算：at=1 时根还活着
        Assert.False(tree.RootTreeHasExited(At(1)));
    }

    [Fact]
    public void RootTreeHasExited_false_while_child_alive()
    {
        var tree = NewTree();
        tree.Observe(new ProcessStartEvent(200, 100, @"C:\a\child.exe", At(1)));
        tree.Observe(new ProcessStopEvent(100, At(2)));   // 根退了，但子进程还在
        Assert.False(tree.RootTreeHasExited(At(3)));
    }

    [Fact]
    public void RootTreeHasExited_true_when_child_stops_before_root()
    {
        var tree = NewTree();
        tree.Observe(new ProcessStartEvent(200, 100, @"C:\a\child.exe", At(1)));
        tree.Observe(new ProcessStartEvent(300, 200, @"C:\a\grand.exe", At(2)));
        tree.Observe(new ProcessStopEvent(300, At(4)));
        tree.Observe(new ProcessStopEvent(200, At(5)));
        tree.Observe(new ProcessStopEvent(100, At(6)));
        Assert.True(tree.RootTreeHasExited(At(7)));
        // 孙进程退了但子进程还在时，整树尚未退出
        Assert.False(tree.RootTreeHasExited(At(5)));
    }

    [Fact]
    public void RootTreeHasExited_not_misjudged_by_pid_reuse()
    {
        var tree = NewTree();
        tree.Observe(new ProcessStopEvent(100, At(10)));                             // 根退出
        tree.Observe(new ProcessStartEvent(100, 4, @"C:\x\reuse.exe", At(20)));      // 外来进程复用根 PID，仍存活
        tree.Observe(new ProcessStartEvent(600, 100, @"C:\x\reuse-child.exe", At(21)));
        // 复用根 PID 的进程不属于树，不应阻止“整树已退出”的判定
        Assert.True(tree.RootTreeHasExited(At(25)));
    }
}
