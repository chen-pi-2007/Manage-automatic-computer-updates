namespace UpdateHelper.Core.Monitor;

/// <summary>
/// 监控安装前对所选安装包路径的基本校验：必须是本机上<b>存在的文件</b>，扩展名为 .exe 或 .msi。
/// 为什么：路径会被直接交给 ShellExecute 启动，并发给提权助手记录；拒绝网络共享、文档、快捷方式、脚本等，
/// 不让"监控一次安装"变成任意文件的启动器。
/// </summary>
public static class MonitorInstallerPath
{
    /// <summary>通过返回 null；不通过返回中文原因。</summary>
    public static string? Validate(string? installerPath)
    {
        if (string.IsNullOrWhiteSpace(installerPath)) return "没有选择安装程序。";
        string full;
        try
        {
            if (!Path.IsPathRooted(installerPath)) return "安装程序路径必须是完整路径。";
            full = Path.GetFullPath(installerPath);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return "安装程序路径不合法。";
        }
        // 只接受本机盘符路径：网络共享（\server\share）和设备路径（\.\、\?\）一律不要
        if (full.StartsWith(@"\\", StringComparison.Ordinal)) return "只能监控本机磁盘上的安装程序，不支持网络位置。";
        var ext = Path.GetExtension(full);
        if (!ext.Equals(".exe", StringComparison.OrdinalIgnoreCase) && !ext.Equals(".msi", StringComparison.OrdinalIgnoreCase))
            return "只能监控 .exe 或 .msi 安装程序。";
        if (!File.Exists(full)) return "找不到所选的安装程序文件。";
        return null;
    }
}
