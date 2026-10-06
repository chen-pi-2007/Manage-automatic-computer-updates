namespace UpdateHelper.Core.Updates;

/// <summary>更新来源报告的一条"这个软件有新版本"。</summary>
/// <param name="PackageId">来源里的包 id，如 Tencent.QQ.NT。</param>
/// <param name="ProductCodes">已装版本对应的注册表卸载键名（winget 给的是小写），用来对到软件组。</param>
public sealed record UpdateCandidate(
    string PackageId,
    string Name,
    string? Publisher,
    string InstalledVersion,
    string AvailableVersion,
    IReadOnlyList<string> ProductCodes);

/// <summary>能报告"哪些已装软件有新版本"的来源。来源不可用时抛异常。</summary>
public interface IUpdateSource
{
    /// <summary>来源名称，用于界面和日志，如 "winget"。</summary>
    string Name { get; }

    IReadOnlyList<UpdateCandidate> GetAvailableUpdates();
}
