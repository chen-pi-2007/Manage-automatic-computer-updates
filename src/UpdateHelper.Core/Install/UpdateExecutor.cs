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

        // 2. 自动模式下的重试上限
        if (automatic && CountFailures(c) >= MaxAutomaticRetries)
            return new ExecuteResult(ExecuteOutcome.Refused,
                $"这个版本已经失败 {MaxAutomaticRetries} 次，不再自动重试", null);

        // 3. 正在运行就不装，绝不替用户关闭程序
        var running = processes.FindRunningUnder(group.InstallLocations.ToList());
        if (running.Count > 0)
            return new ExecuteResult(ExecuteOutcome.Refused,
                $"{group.Name} 正在运行（{string.Join("、", running)}），请先关闭再更新", null);

        var scope = group.Primary?.Hive == UninstallHive.CurrentUser ? InstallScopeHint.User : InstallScopeHint.Machine;

        ExecuteResult result;
        try
        {
            var report = await installer.UpgradeAsync(c.PackageId, scope, progress, cancellationToken);
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
        history.Append(new HistoryRecord(_clock.GetLocalNow(), c.PackageId, group.Name, c.InstalledVersion,
            c.AvailableVersion, result.Outcome, result.Message, automatic));
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

        var after = versions.GetInstalledVersion(ProbeKey(update));
        if (after is null)
            return new ExecuteResult(ExecuteOutcome.Succeeded, "已安装，但登记信息有变化，下次扫描时再确认版本", null);

        var before = update.Candidate.InstalledVersion;
        var afterVersion = AppVersion.Parse(after);
        var beforeVersion = AppVersion.Parse(before);
        var newer = afterVersion is not null && beforeVersion is not null
            ? afterVersion.CompareTo(beforeVersion) > 0
            : !string.Equals(after, before, StringComparison.OrdinalIgnoreCase);
        if (!newer)
            return new ExecuteResult(ExecuteOutcome.Failed, $"安装程序报告成功，但版本没有变化（仍是 {after}）", after);

        return new ExecuteResult(ExecuteOutcome.Succeeded, $"已从 {before} 更新到 {after}", after);
    }

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
