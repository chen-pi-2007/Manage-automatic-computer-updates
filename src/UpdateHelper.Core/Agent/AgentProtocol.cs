using System.Text.Json;
using System.Text.Json.Serialization;
using UpdateHelper.Core.Install;

namespace UpdateHelper.Core.Agent;

/// <summary>App 能让后台助手做的事：只有这两种，不执行任意命令（spec 第 3 节）。</summary>
public enum AgentOp { Ping, Upgrade }

/// <summary>App → Agent 的一条请求。</summary>
public sealed record AgentRequest(
    int Version,
    AgentOp Op,
    string? PackageId = null,
    string? TargetVersion = null,
    InstallScopeHint Scope = InstallScopeHint.Machine);

public enum AgentMessageType { Progress, Result, Pong }

/// <summary>Agent → App 的一条消息：进度（0～1）、最终结果，或对 Ping 的回答。</summary>
public sealed record AgentResponse(
    AgentMessageType Type,
    double Progress = 0,
    bool Success = false,
    bool RebootRequired = false,
    string? ErrorMessage = null,
    uint? InstallerErrorCode = null,
    string? AgentVersion = null,
    bool Elevated = false);

/// <summary>管道上每条消息是一行 JSON（UTF-8）。</summary>
public static class AgentProtocol
{
    /// <summary>协议版本：App 和 Agent 不一致时 Agent 拒绝请求，App 提示"更新后台助手"。</summary>
    public const int Version = 1;

    /// <summary>一行最多这么多字符，超过就当作格式不对（防止对方发来无限长的数据把内存吃光）。</summary>
    public const int MaxLineChars = 65536;

    private static readonly JsonSerializerOptions Options = new()
    {
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) },
    };

    public static string Encode<T>(T message) => JsonSerializer.Serialize(message, Options);

    /// <summary>格式不对、类型对不上、枚举值不认识时返回 null，永不抛异常。</summary>
    public static T? Decode<T>(string line) where T : class
    {
        if (string.IsNullOrWhiteSpace(line) || line.Length > MaxLineChars) return null;
        try
        {
            return JsonSerializer.Deserialize<T>(line, Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static AgentResponse ToResult(InstallerReport r) =>
        new(AgentMessageType.Result, Progress: 1, Success: r.Success, RebootRequired: r.RebootRequired,
            ErrorMessage: r.ErrorMessage, InstallerErrorCode: r.InstallerErrorCode);

    public static InstallerReport ToReport(AgentResponse r) =>
        new(r.Success, r.RebootRequired, r.ErrorMessage, r.InstallerErrorCode);
}
