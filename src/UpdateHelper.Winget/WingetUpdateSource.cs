using Microsoft.Management.Deployment;
using UpdateHelper.Core.Updates;

namespace UpdateHelper.Winget;

/// <summary>
/// 通过 winget 官方 COM 接口查询"哪些已装软件有新版本"。只查询，不安装、不下载。
/// 注意：COM 列表只能按下标遍历（foreach 会抛"不支持此接口"）；每读一个属性都是跨进程调用，
/// 所以先只读 IsUpdateAvailable，有更新的才读其他属性。
/// </summary>
public sealed class WingetUpdateSource : IUpdateSource
{
    public string Name => "winget";

    public IReadOnlyList<UpdateCandidate> GetAvailableUpdates()
    {
        var manager = WingetSession.CreateManager();
        var catalog = WingetSession.ConnectInstalledAndRemote(manager);

        var found = catalog.FindPackages(new FindPackagesOptions());
        if (found.Status != FindPackagesResultStatus.Ok)
            throw new WingetUnavailableException($"winget 查询已装软件失败（{found.Status}）。");

        var result = new List<UpdateCandidate>();
        var matches = found.Matches;
        for (var i = 0; i < matches.Count; i++)
        {
            var package = matches[i].CatalogPackage;
            if (!package.IsUpdateAvailable) continue;

            var id = package.Id;
            // 只存在于本机、winget 源里没有的条目
            if (id.StartsWith(@"ARP\", StringComparison.OrdinalIgnoreCase)
                || id.StartsWith(@"MSIX\", StringComparison.OrdinalIgnoreCase)) continue;

            var installed = package.InstalledVersion;
            var available = package.DefaultInstallVersion;
            if (installed is null || available is null) continue;

            result.Add(new UpdateCandidate(id, package.Name, installed.Publisher,
                installed.Version, available.Version, ToList(installed.ProductCodes)));
        }
        return result;
    }

    /// <summary>COM 列表按下标复制成普通列表。</summary>
    private static List<string> ToList(IReadOnlyList<string> comList)
    {
        var list = new List<string>(comList.Count);
        for (var i = 0; i < comList.Count; i++) list.Add(comList[i]);
        return list;
    }
}
