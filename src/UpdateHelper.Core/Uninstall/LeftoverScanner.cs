using UpdateHelper.Core.Grouping;
using UpdateHelper.Core.Rules;
using UpdateHelper.Core.Scanning;

namespace UpdateHelper.Core.Uninstall;

/// <summary>
/// 扫描一个软件的残留（只读）：安装目录、规则残留、数据目录启发式、后台项目、注册表。
/// 系统目录/系统注册表键被白名单挡下；同一位置被别的已装软件引用时判为"多软件共用"。
/// 读取失败一律当"读不到"，不抛异常。
/// </summary>
public sealed class LeftoverScanner(IFileProbe files, IRegistryProbe registry)
{
    public IReadOnlyList<LeftoverItem> Scan(SoftwareGroup group, Rule? rule, ScanResult allSoftware)
    {
        var items = new List<LeftoverItem>();
        var seenDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 其他软件的安装位置（用于共用检测）
        var otherInstallDirs = allSoftware.Groups
            .Where(g => !ReferenceEquals(g, group))
            .SelectMany(g => g.InstallLocations)
            .Select(PathUtil.NormalizeDir)
            .OfType<string>()
            .ToList();

        // 1. 安装目录
        foreach (var loc in group.InstallLocations)
            AddDir(items, seenDirs, loc, LeftoverType.InstallDir, isInstallLocation: true,
                ruleOwned: false, rulePersonal: false, ruleShared: false, nameMatchOnly: false, otherInstallDirs);

        // 2. 规则残留
        foreach (var lr in rule?.Leftovers ?? [])
        {
            var expanded = Environment.ExpandEnvironmentVariables(lr.Path);
            var owned = lr.Kind == LeftoverKind.Owned;
            var personal = lr.Kind == LeftoverKind.Personal;
            var shared = lr.Kind == LeftoverKind.Shared;
            if (IsRegistryPath(expanded))
            {
                if (!SystemPaths.IsProtectedRegistryKey(expanded) && registry.KeyExists(expanded))
                    items.Add(LeftoverJudge.Classify(LeftoverType.RegistryKey, expanded, null,
                        owned, personal, shared, false, false, false));
            }
            else
            {
                AddDir(items, seenDirs, expanded, LeftoverType.DataDir, isInstallLocation: false,
                    owned, personal, shared, nameMatchOnly: false, otherInstallDirs);
            }
        }

        // 3. 数据目录启发式
        var needles = new[] { group.Name, group.Publisher }
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s!.Replace(" ", "").ToLowerInvariant())
            .Where(s => s.Length >= 3)   // 太短的名字别乱匹配
            .ToList();
        foreach (var special in new[]
                 {
                     Environment.SpecialFolder.ApplicationData,
                     Environment.SpecialFolder.LocalApplicationData,
                     Environment.SpecialFolder.CommonApplicationData,
                 })
        {
            var baseDir = Environment.GetFolderPath(special);
            foreach (var child in files.GetChildDirectories(baseDir))
            {
                var name = Path.GetFileName(child.TrimEnd('\\')).Replace(" ", "").ToLowerInvariant();
                if (needles.Any(n => name.Contains(n)))
                    AddDir(items, seenDirs, child, LeftoverType.DataDir, isInstallLocation: false,
                        ruleOwned: false, rulePersonal: false, ruleShared: false, nameMatchOnly: true, otherInstallDirs);
            }
        }

        // 4. 后台项目
        foreach (var b in group.Background)
        {
            var type = b.Kind switch
            {
                BackgroundKind.Service => LeftoverType.Service,
                BackgroundKind.ScheduledTask => LeftoverType.ScheduledTask,
                _ => LeftoverType.StartupEntry,
            };
            var label = b.ExecutablePath is null ? b.Name : $"{b.Name}（{b.ExecutablePath}）";
            items.Add(LeftoverJudge.Classify(type, label, null, false, false, false, false, false, false));
        }

        return items;
    }

    private void AddDir(List<LeftoverItem> items, HashSet<string> seen, string? path, LeftoverType type,
        bool isInstallLocation, bool ruleOwned, bool rulePersonal, bool ruleShared, bool nameMatchOnly,
        List<string> otherInstallDirs)
    {
        var norm = PathUtil.NormalizeDir(path);
        if (norm is null || !seen.Add(norm)) return;
        if (SystemPaths.IsProtectedDirectory(norm)) return;
        if (!files.DirectoryExists(norm)) return;

        var sharedWithOthers = otherInstallDirs.Any(o => PathUtil.IsUnder(o, norm));
        var size = files.DirectorySize(norm);
        items.Add(LeftoverJudge.Classify(type, norm.TrimEnd('\\'), size,
            ruleOwned, rulePersonal, ruleShared, isInstallLocation, sharedWithOthers, nameMatchOnly));
    }

    private static bool IsRegistryPath(string path) =>
        path.StartsWith("HKCU\\", StringComparison.OrdinalIgnoreCase)
        || path.StartsWith("HKLM\\", StringComparison.OrdinalIgnoreCase)
        || path.StartsWith("HKCR\\", StringComparison.OrdinalIgnoreCase);
}
