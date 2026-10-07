using UpdateHelper.Core.Updates;
using static UpdateHelper.Presentation.Tests.FakeBackend;

namespace UpdateHelper.Presentation.Tests;

public class TrayPolicyTests
{
    [Fact]
    public void Notifies_when_window_is_hidden()
        => Assert.Equal("发现 3 个更新，其中 2 个低风险。点击查看。",
            TrayPolicy.NotificationText([Update("A", UpdateTier.Low), Update("B", UpdateTier.Low),
                Update("C", UpdateTier.Careful), Update("D", UpdateTier.Ignored)], windowVisible: false));

    [Fact]
    public void Silent_when_window_is_visible()
        => Assert.Null(TrayPolicy.NotificationText([Update("A", UpdateTier.Low)], windowVisible: true));

    [Fact]
    public void Silent_when_nothing_needs_attention()
        => Assert.Null(TrayPolicy.NotificationText([Update("CS2", UpdateTier.Ignored)], windowVisible: false));

    [Theory]   // Review Focus 5
    [InlineData(true, false, true)]    // 托盘开着、不是"退出"：缩到托盘
    [InlineData(false, false, false)]  // 托盘关着：真正关闭
    [InlineData(true, true, false)]    // 托盘菜单点了"退出"：一定退出
    public void Close_behaviour(bool trayEnabled, bool exitRequested, bool hide)
        => Assert.Equal(hide, TrayPolicy.HideInsteadOfClose(trayEnabled, exitRequested));
}
