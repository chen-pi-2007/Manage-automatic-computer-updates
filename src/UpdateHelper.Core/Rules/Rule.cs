using UpdateHelper.Core.Grouping;
using UpdateHelper.Core.Scanning;

namespace UpdateHelper.Core.Rules;

/// <summary>更新风险：normal 普通 / careful 按大版本处理 / never-auto 永不自动安装。</summary>
public enum RiskLevel { Normal, Careful, NeverAuto }

/// <summary>残留归属：owned 确定属于（默认勾选）/ personal 个人数据 / shared 多软件共用（默认不勾选）。</summary>
public enum LeftoverKind { Owned, Personal, Shared }

/// <summary>规则来源的可信度。同一 id 以官方为准。</summary>
public enum RuleTrust { Official, ThirdParty }

/// <summary>怎么认出这个软件：显示名和发布者，支持 * ? 通配符。</summary>
public sealed record RuleMatch(string DisplayName, string? Publisher);

/// <summary>去哪查最新版本（只由云端 CI 执行，客户端只保存不执行）。</summary>
public sealed record UpdateCheck(string Url, string Extract);

/// <summary>CI 查到并写进规则的最新版本。</summary>
public sealed record UpdateLatest(string Version, string Url);

public sealed record UpdateRule(
    UpdateCheck? Check,
    UpdateLatest? Latest,
    string? Signer,
    string? SilentArgs,
    RiskLevel Risk);

/// <summary>一类后台项目的说明。NamePattern 匹配服务名 / 自启项名 / 任务名 / 启动文件夹里的文件名。</summary>
public sealed record BackgroundRule(BackgroundKind Kind, string NamePattern, string Explain);

/// <summary>卸载后可能残留的位置（路径可含 %APPDATA% 等环境变量）。</summary>
public sealed record LeftoverRule(string Path, LeftoverKind Kind);

/// <summary>一条规则（对应一个 YAML 文件）。</summary>
public sealed record Rule(
    string Id,
    string Name,
    RuleMatch Match,
    SoftwareCategory? Category,
    UpdateRule? Update,
    IReadOnlyList<BackgroundRule> Background,
    IReadOnlyList<LeftoverRule> Leftovers)
{
    /// <summary>规则来自哪个文件（用于报错和排查）。</summary>
    public string SourceFile { get; init; } = "";

    public RuleTrust Trust { get; init; } = RuleTrust.Official;
}
