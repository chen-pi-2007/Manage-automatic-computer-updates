namespace UpdateHelper.Core.Scanning;

public enum BackgroundKind { Service, RunKey, StartupFolder, ScheduledTask }

/// <summary>一个后台项目：服务、开机自启或计划任务。</summary>
/// <param name="Detail">补充说明：服务的启动方式、自启项所在位置、任务路径等。</param>
public sealed record BackgroundItem(
    BackgroundKind Kind,
    string Name,
    string? DisplayName,
    string? Command,
    string? ExecutablePath,
    string? Detail);
