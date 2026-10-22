namespace UpdateHelper.Core.Monitor;

/// <summary>
/// 内核跟踪不可用（典型原因：NT Kernel Logger 会话已被 Process Monitor 等工具占用）。
/// Message 直接面向用户，是中文。
/// </summary>
public sealed class MonitorUnavailableException : Exception
{
    public MonitorUnavailableException(string message, Exception? inner = null) : base(message, inner) { }
}
