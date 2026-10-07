using UpdateHelper.Core.Grouping;
using UpdateHelper.Core.Install;
using UpdateHelper.Core.Scanning;
using UpdateHelper.Core.Updates;

namespace UpdateHelper.Presentation;

/// <summary>界面上显示的中文文字。</summary>
public static class Display
{
    public static string TierName(UpdateTier tier) => tier switch
    {
        UpdateTier.Low => "低风险",
        UpdateTier.Careful => "需确认",
        UpdateTier.NeverAuto => "不自动",
        _ => "不管",
    };

    public static string CategoryName(SoftwareCategory category) => category switch
    {
        SoftwareCategory.Application => "普通软件",
        SoftwareCategory.DevTool => "开发工具",
        SoftwareCategory.Game => "游戏",
        SoftwareCategory.Runtime => "运行库",
        SoftwareCategory.Driver => "驱动",
        _ => "系统组件",
    };

    public static string OutcomeName(ExecuteOutcome outcome) => outcome switch
    {
        ExecuteOutcome.Succeeded => "已更新",
        ExecuteOutcome.NeedsReboot => "需要重启",
        ExecuteOutcome.Failed => "失败",
        ExecuteOutcome.Refused => "没有安装",
        _ => "已取消",
    };

    public static string KindName(BackgroundKind kind) => kind switch
    {
        BackgroundKind.Service => "服务",
        BackgroundKind.RunKey => "开机自启",
        BackgroundKind.StartupFolder => "启动文件夹",
        _ => "计划任务",
    };
}
