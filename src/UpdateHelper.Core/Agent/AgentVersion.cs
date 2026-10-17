namespace UpdateHelper.Core.Agent;

/// <summary>
/// 后台助手的版本号：App 和 Agent 两个进程都从这里取。它们加载的是同一个 Core 程序集，
/// 所以 App 用来核对「部署的助手是不是当前这版」的版本，和 Agent 部署时写进 agent-version.txt 的版本必然相同。
/// （曾经 App 读自己的程序集版本、Agent 读自己的，被各自 csproj 的 &lt;Version&gt; 覆盖带偏，导致状态恒为 Outdated、免确认更新静默失效。）
/// </summary>
public static class AgentVersion
{
    public static string Current { get; } =
        (typeof(AgentVersion).Assembly.GetName().Version ?? new Version(0, 0, 0)).ToString(3);
}
