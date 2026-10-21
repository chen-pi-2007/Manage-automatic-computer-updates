using UpdateHelper.Core.Scanning;
using UpdateHelper.Core.Uninstall;

namespace UpdateHelper.Core.Agent;

/// <summary>
/// 后台助手清残留的安全核心：不信任请求里的任何路径，先自己独立重扫该软件的残留，
/// 只删除"自扫结果 ∩ 用户勾选"的项；请求里重扫没产出的路径一律忽略，杜绝任意路径删除。
/// </summary>
public sealed class ResidueRemovalService(
    Func<string, UninstallHive, IReadOnlyList<LeftoverItem>> rescan,
    LeftoverRemover remover)
{
    public RemovalResult Handle(AgentRequest req)
    {
        var scanned = rescan(req.UninstallKeyName!, req.Hive!.Value);
        var selected = new HashSet<string>(req.SelectedPaths ?? [], StringComparer.OrdinalIgnoreCase);
        var intersection = scanned.Where(i => selected.Contains(i.Path)).ToList();
        return remover.Remove(req.UninstallKeyName!, intersection);
    }
}
