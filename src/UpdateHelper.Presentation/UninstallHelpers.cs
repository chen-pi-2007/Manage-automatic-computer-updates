using UpdateHelper.Core.Scanning;
using UpdateHelper.Core.Uninstall;

namespace UpdateHelper.Presentation;

/// <summary>卸载结果的中文总结。按 RemovalResult 的各项数量计算，不看笼统的"成功"标志。</summary>
public static class UninstallMessages
{
    public static string Build(RemovalResult removal, bool nativeRan, bool nativeSkipped)
    {
        var parts = new List<string>();
        if (removal.Removed > 0) parts.Add($"已清理 {removal.Removed} 项");
        if (removal.RebootPending > 0) parts.Add($"{removal.RebootPending} 项将在重启后删除");
        if (removal.Failed.Count > 0) parts.Add($"{removal.Failed.Count} 项未能处理");
        if (parts.Count == 0) parts.Add("没有清理任何残留");

        var text = string.Join("；", parts) + "。";
        if (removal.BackupId is not null) text += "已做备份，可在“历史”页一键恢复。";
        if (nativeSkipped) text = "找不到软件自带的卸载程序，已改为强制删除。" + text;
        else if (nativeRan) text = "已运行软件自带的卸载程序。" + text;
        return text;
    }
}

/// <summary>残留清理走哪条路：当前用户的软件在本进程内删（无需管理员），机器级的交给后台助手。</summary>
public static class UninstallRouting
{
    public static bool UsesAgent(UninstallHive? hive) => hive != UninstallHive.CurrentUser;
}
