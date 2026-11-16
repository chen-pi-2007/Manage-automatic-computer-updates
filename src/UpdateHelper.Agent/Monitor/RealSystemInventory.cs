using Microsoft.Win32;
using UpdateHelper.Core.Monitor;

namespace UpdateHelper.Agent.Monitor;

/// <summary>
/// 系统名册的真实现：服务与驱动取自 HKLM\SYSTEM\CurrentControlSet\Services 的子键（Type 1/2/8 是驱动），
/// 计划任务取自 %SystemRoot%\System32\Tasks 下的任务文件（相对路径即任务名，如 \Vendor\Updater）。
/// 两处都要管理员才能完整读取，提权助手正好满足。读不到整块（被拒绝）就抛出，由处理器标"不完整"，不悄悄当成空。
/// </summary>
public sealed class RealSystemInventory : ISystemInventory
{
    public static string TasksRoot { get; } = Path.Combine(Environment.SystemDirectory, "Tasks");

    // SERVICE_KERNEL_DRIVER=1、SERVICE_FILE_SYSTEM_DRIVER=2、SERVICE_RECOGNIZER_DRIVER=8
    private static bool IsDriverType(int type) => (type & (1 | 2 | 8)) != 0 && (type & 0x30) == 0;

    public IReadOnlyCollection<InventoryItem> Snapshot()
    {
        var items = new List<InventoryItem>();

        using (var services = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services")
                              ?? throw new IOException("读不到服务列表"))
        {
            foreach (var name in services.GetSubKeyNames())
            {
                int type;
                try
                {
                    using var k = services.OpenSubKey(name);
                    if (k?.GetValue("Type") is not int t) continue;   // 没有 Type 的子键不是服务（如某些配置键）
                    type = t;
                }
                catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
                {
                    type = 0x10;   // 个别受保护的键读不了 Type：当服务记，名字仍参与比对
                }
                items.Add(new InventoryItem(IsDriverType(type) ? FootprintEntryType.Driver : FootprintEntryType.Service, name));
            }
        }

        foreach (var file in Directory.EnumerateFiles(TasksRoot, "*", new EnumerationOptions
                 {
                     RecurseSubdirectories = true,
                     IgnoreInaccessible = false,
                     AttributesToSkip = 0,
                 }))
        {
            items.Add(new InventoryItem(FootprintEntryType.ScheduledTask, "\\" + Path.GetRelativePath(TasksRoot, file)));
        }

        return items;
    }
}
