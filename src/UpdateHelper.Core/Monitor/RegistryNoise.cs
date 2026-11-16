namespace UpdateHelper.Core.Monitor;

/// <summary>
/// 注册表噪声：任何程序一运行，系统自己就会顺手在这些位置记东西（执行记录、界面缓存、打开对话框的历史），
/// 写入发生在该程序的进程里，按进程归属会被算到软件头上，但它们不是软件的足迹。
/// 和文件侧的 LocationClassifier 丢 %TEMP%、Prefetch 是同一个道理。只列确定是系统行为的位置，宁少勿多。
/// </summary>
public static class RegistryNoise
{
    private static readonly string[] Segments =
    [
        @"\Services\bam\",                                   // 后台活动调节器：记录每个进程的最后运行时间
        @"\Explorer\UserAssist\",                            // 开始菜单的"常用程序"统计
        @"\MuiCache",                                        // 程序显示名缓存
        @"\AppCompatFlags\Compatibility Assistant\",         // 程序兼容性助手的运行记录
        @"\Explorer\RecentDocs",                             // 最近使用的文档
        @"\Explorer\ComDlg32\",                              // "打开/保存"对话框的历史
        @"\Explorer\SessionInfo\",                           // 资源管理器会话状态
        @"\Shell\BagMRU",                                    // 文件夹视图记忆
        @"\Shell\Bags\",
    ];

    public static bool IsNoise(string registryPath)
    {
        foreach (var s in Segments)
            if (registryPath.Contains(s, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }
}
