using Microsoft.Diagnostics.Tracing.Parsers;
using UpdateHelper.Agent.Monitor;

namespace UpdateHelper.Core.Tests.Agent;

/// <summary>
/// 采集器里【不依赖真实内核会话】就能验的部分：私有会话名常量、失败文案。
/// 真正的 ETW 并存采集只能真机验（由控制者带用户做），这里只守住"名字不是 NT Kernel Logger"
/// 与"失败提示不再误导用户去关 Process Monitor"这两条回归。
/// </summary>
public class EtwMonitorSessionTests
{
    // 监控会话必须用应用专属名，绝不能用 NT Kernel Logger——否则又会跟反作弊/游戏守护抢同一个传统会话。
    [Fact]
    public void Session_name_is_app_specific_and_not_nt_kernel_logger()
    {
        Assert.Equal("UpdateHelperMonitor", EtwMonitorSession.MonitorSessionName);
        Assert.NotEqual(EtwMonitorSession.MonitorSessionName, KernelTraceEventParser.KernelSessionName);
    }

    // 失败文案要说清原因，且不叫用户去关 Process Monitor（本会话与 NT Kernel Logger 无关）。
    [Fact]
    public void Unavailable_message_describes_private_session_failure_without_procmon()
    {
        var msg = EtwMonitorSession.UnavailableMessage("底层错误X");
        Assert.Contains("内核跟踪", msg);
        Assert.Contains("底层错误X", msg);      // 原始错误细节透传，便于排查
        Assert.Contains("重试", msg);
        Assert.DoesNotContain("Process Monitor", msg);
    }
}
