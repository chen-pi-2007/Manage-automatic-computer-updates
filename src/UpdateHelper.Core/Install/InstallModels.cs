namespace UpdateHelper.Core.Install;

/// <summary>安装范围：保持和原来一致（spec 第 6 节）。</summary>
public enum InstallScopeHint
{
    /// <summary>原来只装给当前用户（卸载信息在 HKCU）</summary>
    User,
    /// <summary>原来装给所有用户（卸载信息在 HKLM）</summary>
    Machine,
}

/// <summary>安装器（如 winget）报告的原始结果。</summary>
public sealed record InstallerReport(bool Success, bool RebootRequired, string? ErrorMessage, uint? InstallerErrorCode);

public enum ExecuteOutcome
{
    /// <summary>已更新</summary>
    Succeeded,
    /// <summary>已安装，但需要重启电脑才能完成（不会自动重启）</summary>
    NeedsReboot,
    /// <summary>安装失败</summary>
    Failed,
    /// <summary>装前检查没通过，没有开始安装</summary>
    Refused,
    /// <summary>用户取消</summary>
    Cancelled,
}

/// <summary>一次更新的最终结果。Message 是给用户看的中文说明。</summary>
public sealed record ExecuteResult(ExecuteOutcome Outcome, string Message, string? VersionAfter);

/// <summary>更新历史中的一条记录（spec 第 6 节"所有操作都写进更新历史"）。</summary>
public sealed record HistoryRecord(
    DateTimeOffset Time,
    string PackageId,
    string Name,
    string FromVersion,
    string ToVersion,
    ExecuteOutcome Outcome,
    string Message,
    bool Automatic);
