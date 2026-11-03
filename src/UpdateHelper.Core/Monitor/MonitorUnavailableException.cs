namespace UpdateHelper.Core.Monitor;

/// <summary>
/// 内核跟踪不可用（采集器改用 Win8+ 私有内核会话后，典型原因是私有 system-logger 槽位已满、
/// 当前系统不支持私有内核跟踪，或没有管理员权限；与 NT Kernel Logger 是否被反作弊占用【无关】）。
/// Message 直接面向用户，是中文。
/// </summary>
public sealed class MonitorUnavailableException : Exception
{
    public MonitorUnavailableException(string message, Exception? inner = null) : base(message, inner) { }
}
