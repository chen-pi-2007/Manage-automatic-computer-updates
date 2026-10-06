using UpdateHelper.Core.Grouping;
using UpdateHelper.Core.Scanning;
using UpdateHelper.Core.Updates;

namespace UpdateHelper.Core.Install;

/// <summary>执行一次更新：装前检查 → 安装 → 装后核对 → 写历史。永不抛异常。流程见计划 4 Task 2 的表格。</summary>
public sealed class UpdateExecutor(
    IPackageInstaller installer,
    IVersionProbe versions,
    IRunningProcessProbe processes,
    IUpdateHistory history,
    TimeProvider? clock = null)
{
    public const int MaxAutomaticRetries = 2;

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    public async Task<ExecuteResult> ExecuteAsync(JudgedUpdate update, bool automatic, IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        var c = update.Candidate;
        var group = update.Group;

        // 1. 判断层说不能装
        if (group is null || update.Tier is UpdateTier.NeverAuto or UpdateTier.Ignored)
            return new ExecuteResult(ExecuteOutcome.Refused, $"不能更新：{update.Reason}", null);

        // 2. 自动模式下的重试上限（读不了历史就不冒险）
        if (automatic)
        {
            int failures;
            try
            {
                failures = CountFailures(c);
            }
            catch (Exception ex)
            {
                return new ExecuteResult(ExecuteOutcome.Refused, $"无法读取更新历史（{ex.Message}），自动更新跳过", null);
            }
            if (failures >= MaxAutomaticRetries)
                return new ExecuteResult(ExecuteOutcome.Refused,
                    $"这个版本已经失败 {MaxAutomaticRetries} 次，不再自动重试", null);
        }

        // 3. 正在运行就不装，绝不替用户关闭程序
        var directories = RunningCheckDirectories(group);
        if (directories.Count == 0)
        {
            // 不知道程序在哪，就没法确认它是否在运行：自动模式跳过；手动模式由用户自己确认（界面会提示）
            if (automatic)
                return new ExecuteResult(ExecuteOutcome.Refused,
                    $"无法确定 {group.Name} 的安装位置，不能确认它是否在运行，自动更新跳过", null);
        }
        else
        {
            var running = processes.FindRunningUnder(directories);
            if (running.Count > 0)
                return new ExecuteResult(ExecuteOutcome.Refused,
                    $"{group.Name} 正在运行（{string.Join("、", running)}），请先关闭再更新", null);
        }

        var scope = group.Primary?.Hive == UninstallHive.CurrentUser ? InstallScopeHint.User : InstallScopeHint.Machine;

        ExecuteResult result;
        try
        {
            var report = await installer.UpgradeAsync(c.PackageId, c.AvailableVersion, scope, progress, cancellationToken);
            result = Interpret(report, update);
        }
        catch (OperationCanceledException)
        {
            result = new ExecuteResult(ExecuteOutcome.Cancelled, "已取消", null);
        }
        catch (Exception ex)
        {
            result = new ExecuteResult(ExecuteOutcome.Failed, $"安装过程中出错：{ex.Message}", null);
        }

        // ToVersion 一律记"目标版本"，这样重试次数可以按"包 id + 目标版本"统计
        try
        {
            history.Append(new HistoryRecord(_clock.GetLocalNow(), c.PackageId, group.Name, c.InstalledVersion,
                c.AvailableVersion, result.Outcome, result.Message, automatic));
        }
        catch (Exception ex)
        {
            // 软件可能已经装好了，结果必须照常交给用户
            result = result with { Message = $"{result.Message}（更新历史写入失败：{ex.Message}）" };
        }
        return result;
    }

    private ExecuteResult Interpret(InstallerReport report, JudgedUpdate update)
    {
        if (!report.Success)
        {
            var code = report.InstallerErrorCode is { } n and not 0 ? $"（错误码 0x{n:X8}）" : "";
            return new ExecuteResult(ExecuteOutcome.Failed, $"安装失败：{report.ErrorMessage ?? "未知原因"}{code}", null);
        }

        // 很多软件重启后才改写版本号，所以先判断重启
        if (report.RebootRequired)
            return new ExecuteResult(ExecuteOutcome.NeedsReboot, "已安装，需要重启电脑才能完成（不会自动重启）", null);

        var key = ProbeKey(update);
        var after = versions.GetInstalledVersion(key);
        if (after is null)
            return new ExecuteResult(ExecuteOutcome.Succeeded, "已安装，但登记信息有变化，下次扫描时再确认版本", null);

        // 和扫描时同一个注册表键的版本比较；winget 显示的版本可能是换算过的（如 3.10.2 对应注册表 3.10.2150.0）
        var before = RegistryVersionAtScan(update.Group!, key) ?? update.Candidate.InstalledVersion;
        var afterVersion = AppVersion.Parse(after);
        var beforeVersion = AppVersion.Parse(before);
        var newer = afterVersion is not null && beforeVersion is not null
            ? afterVersion.CompareTo(beforeVersion) > 0
            : !string.Equals(after, before, StringComparison.OrdinalIgnoreCase);
        if (!newer)
            return new ExecuteResult(ExecuteOutcome.Failed, $"安装程序报告成功，但版本没有变化（仍是 {after}）", after);

        return new ExecuteResult(ExecuteOutcome.Succeeded, $"已从 {before} 更新到 {after}", after);
    }

    /// <summary>
    /// "软件是否在运行"要检查的目录：登记（或推断）的安装位置，加上它的后台项目（服务、自启项等）程序所在目录。
    /// 返回空列表表示没法检查。
    /// </summary>
    public static IReadOnlyList<string> RunningCheckDirectories(SoftwareGroup group) =>
        group.InstallLocations
            .Concat(group.Background.Select(b => b.ExecutablePath is null ? null : Path.GetDirectoryName(b.ExecutablePath))
                                    .OfType<string>())
            .Where(d => d.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>界面用：手动更新前，如果没法确认软件是否在运行，要提醒用户先自己关掉。</summary>
    public static bool CanCheckRunningState(JudgedUpdate update) =>
        update.Group is not null && RunningCheckDirectories(update.Group).Count > 0;

    private static string? RegistryVersionAtScan(SoftwareGroup group, string key) =>
        (group.Primary is null ? group.Components : group.Components.Prepend(group.Primary))
            .FirstOrDefault(e => string.Equals(e.KeyName, key, StringComparison.OrdinalIgnoreCase))
            ?.DisplayVersion;

    /// <summary>装后核对用的卸载键：优先用候选 ProductCodes 中能对上的那个，否则用主条目的键。</summary>
    private static string ProbeKey(JudgedUpdate update)
    {
        var group = update.Group!;
        var keys = (group.Primary is null ? group.Components : group.Components.Prepend(group.Primary))
            .Select(e => e.KeyName)
            .ToList();
        foreach (var code in update.Candidate.ProductCodes)
        {
            var hit = keys.FirstOrDefault(k => string.Equals(k, code, StringComparison.OrdinalIgnoreCase));
            if (hit is not null) return hit;
        }
        return group.Primary?.KeyName ?? keys.First();
    }

    private int CountFailures(UpdateCandidate c) =>
        history.ReadAll().Count(r => r.Outcome == ExecuteOutcome.Failed
                                     && string.Equals(r.PackageId, c.PackageId, StringComparison.OrdinalIgnoreCase)
                                     && string.Equals(r.ToVersion, c.AvailableVersion, StringComparison.OrdinalIgnoreCase));
}
