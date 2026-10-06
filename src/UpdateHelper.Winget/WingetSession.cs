using System.Runtime.InteropServices;
using Microsoft.Management.Deployment;

namespace UpdateHelper.Winget;

/// <summary>创建 winget 的 PackageManager 并连接"本机已装 + winget 源"的组合目录。查询和安装共用。</summary>
internal static class WingetSession
{
    public static PackageManager CreateManager()
    {
        try
        {
            return new PackageManager();
        }
        catch (Exception ex) when (ex is COMException or TypeInitializationException or DllNotFoundException
                                       or FileNotFoundException or InvalidCastException)
        {
            throw new WingetUnavailableException(
                "没有找到可用的 winget（Windows 程序包管理器）。可以在微软商店安装或更新“应用安装程序”后再试。", ex);
        }
    }

    public static PackageCatalog ConnectInstalledAndRemote(PackageManager manager)
    {
        var options = new CreateCompositePackageCatalogOptions();
        options.Catalogs.Add(manager.GetPredefinedPackageCatalog(PredefinedPackageCatalog.OpenWindowsCatalog));
        options.CompositeSearchBehavior = CompositeSearchBehavior.LocalCatalogs;

        var connect = manager.CreateCompositePackageCatalog(options).Connect();
        if (connect.Status != ConnectResultStatus.Ok)
            throw new WingetUnavailableException($"无法连接 winget 软件源（{connect.Status}），请检查网络后重试。");
        return connect.PackageCatalog;
    }
}
