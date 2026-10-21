using UpdateHelper.Core.Scanning;
using UpdateHelper.Core.Uninstall;

namespace UpdateHelper.Core.Agent;

/// <summary>助手自扫的结果：匹配到的卸载登记项（没找到为 null）和卸前的残留清单（带规则）。</summary>
public sealed record ResiduePreScan(UninstallEntry? Entry, IReadOnlyList<LeftoverItem> Residue);

/// <summary>
/// 后台助手清残留的安全核心：不信任请求里的任何路径，先自己独立重扫该软件（带匹配到的规则，
/// 这样注册表键和规则声明的残留才会出现），只删除"自扫结果 ∩ 用户勾选"的项；请求里重扫没产出的路径一律忽略。
/// 机器级要求连软件自带的卸载程序一起跑时，固定顺序：先自扫（此时卸载登记键还在）→ 跑卸载程序 → 删残留。
/// 安全要求：卸载命令只能取自助手自扫到的注册表项（UninstallString/QuietUninstallString），绝不用请求里的任何字段，
/// 以免 App 借提权的助手运行任意程序。
/// </summary>
public sealed class ResidueRemovalService(
    Func<string, UninstallHive, ResiduePreScan> prescan,
    LeftoverRemover remover,
    IFileProbe files,
    IRegistryProbe registry,
    Func<UninstallCommand, bool> runUninstaller)
{
    public RemovalResult Handle(AgentRequest req)
    {
        // 1. 先自扫：卸载程序还没跑，登记键还在，能拿到登记项和卸前残留（带规则）
        var scan = prescan(req.UninstallKeyName!, req.Hive!.Value);

        // 2. 机器级要求跑原生卸载程序：命令只来自自扫到的注册表项，绝不来自请求
        if (req.RunNativeUninstaller && scan.Entry is { } entry)
        {
            var command = UninstallCommandParser.Parse(entry, files, preferSilent: false);
            if (command is not null) _ = runUninstaller(command);
        }

        // 3. 只删"卸前残留 ∩ 勾选"的项；跑过卸载程序时再滤掉它已删除、此刻已不存在的项（免得算成失败）
        var selected = new HashSet<string>(req.SelectedPaths ?? [], StringComparer.OrdinalIgnoreCase);
        var intersection = scan.Residue
            .Where(i => selected.Contains(i.Path) && (!req.RunNativeUninstaller || StillExists(i)))
            .ToList();
        return remover.Remove(req.UninstallKeyName!, intersection);
    }

    private bool StillExists(LeftoverItem item) => item.Type switch
    {
        LeftoverType.RegistryKey => registry.KeyExists(item.Path),
        LeftoverType.InstallDir or LeftoverType.DataDir => files.DirectoryExists(item.Path),
        LeftoverType.File => files.FileExists(item.Path),
        _ => true,
    };
}
