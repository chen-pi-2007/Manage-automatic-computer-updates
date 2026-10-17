using UpdateHelper.Core.Agent;

namespace UpdateHelper.Core.Tests;

public sealed class AgentVersionTests
{
    // App 侧（GetStatus 的 expectedVersion）和 Agent 侧（Deploy 写进 agent-version.txt 的版本）都从这一个来源取。
    // 两个进程加载的是同一个 Core.dll，所以这个版本必然一致——不会再出现 App 比 Agent 新、GetStatus 恒 Outdated、免确认更新静默失效。
    [Fact]
    public void Current_is_the_shared_core_assembly_version()
    {
        var core = typeof(AgentPaths).Assembly.GetName().Version!.ToString(3);
        Assert.Equal(core, AgentVersion.Current);
        Assert.False(string.IsNullOrWhiteSpace(AgentVersion.Current));
    }
}
