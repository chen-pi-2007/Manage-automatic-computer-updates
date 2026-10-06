# 计划 4：手动更新一个软件（winget 安装） Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 用户指定一个有更新的软件，程序做完装前检查后通过 winget 静默升级它，装完核对版本，并把结果写进更新历史。ScanCli 增加 `--install <包 id>`，在本机对一个用户选定的软件实际更新一次。

**Architecture:** 编排逻辑放在 Core 的 `Install` 命名空间：`UpdateExecutor` 只依赖四个接口——安装器 `IPackageInstaller`、版本探测 `IVersionProbe`、运行中进程探测 `IRunningProcessProbe`、更新历史 `IUpdateHistory`，因此所有判断分支都能用假实现做单元测试。真实实现：winget 安装器放在 `UpdateHelper.Winget`；版本探测、进程探测、历史记录放在 Core。

**Tech Stack:** C# / .NET 10、Microsoft.WindowsPackageManager.ComInterop 1.29.380、System.Text.Json、xUnit。

**Spec:** `docs/superpowers/specs/2026-09-30-update-helper-design.md`（第 6 节更新器；构建顺序第 14 节第 4 步的前半部分）

**前置：** 计划 1～3 已合并（`main` 上的 `a055767`）。

## 范围说明（本计划只做构建顺序第 4 步的前半部分）

| 计划 | 内容 |
|---|---|
| **计划 4（本计划）** | 手动更新一个软件：装前检查 → winget 静默升级 → 装后核对 → 更新历史 |
| 计划 5 | 补充库软件的安装器 + Authenticode 签名校验；在 Windows 沙盒里用自制假安装包测试失败、签名不对等情况 |
| 计划 6 | 后台助手 + 计划任务（管理员权限）+ 四种更新模式 + 不打扰规则 + 批量更新前的系统还原点 |

本计划**不做**、留给后续计划的 spec 第 6 节内容：旧版安装包缓存（随版本回退一起做）、系统还原点（计划 6 批量更新时做）、BITS 下载与限速（计划 5、6）、以管理员身份在后台安装（计划 6）。

## winget 安装接口（2026-10-06 在开发者本机用反射确认）

- `PackageManager.UpgradePackageAsync(CatalogPackage, InstallOptions)` → `IAsyncOperationWithProgress<InstallResult, InstallProgress>`
- `InstallOptions`：`PackageInstallMode`（用 `Silent`）、`PackageInstallScope`（`Any / User / System / UserOrUnknown / SystemOrUnknown`）、`AcceptPackageAgreements`、`AllowHashMismatch`（**必须保持 false**：这是 winget 的文件指纹校验）
- `InstallResult`：`Status`（`Ok, BlockedByPolicy, CatalogError, InternalError, InvalidOptions, DownloadError, InstallError, ManifestError, NoApplicableInstallers, NoApplicableUpgrade, PackageAgreementsNotAccepted`）、`RebootRequired`、`InstallerErrorCode`、`ExtendedErrorCode`
- `InstallProgress`：`State`、`DownloadProgress`（0～1）、`InstallationProgress`（0～1）
- 按 id 精确查找：`FindPackagesOptions.Filters` 加 `PackageMatchFilter { Field = Id, Option = Equals, Value = 包 id }`
- 计划 3 的结论仍然适用：COM 列表只能按下标遍历

## Global Constraints

- 沿用前几个计划：Core 和测试 `net10.0-windows`；Winget 和 ScanCli `net10.0-windows10.0.26100.0` + `win-x64`；警告即错误；提交信息不带任何 AI 署名
- **绝不关闭用户正在运行的程序**：软件在运行就拒绝安装并说明（spec 第 6 节）
- **绝不自动重启电脑**：需要重启的只提示（spec 第 6 节）
- **`AllowHashMismatch` 永远为 false**：文件指纹对不上就不装（spec 第 6 节"winget 的包核对指纹"）
- **保持原安装范围**：原来装在当前用户下的继续装在当前用户下，原来给所有用户装的保持不变（spec 第 6 节）
- 判断层归为"不自动"或"不管"的更新，本计划一律拒绝执行，并显示判断层给出的理由
- 装完必须重新读取版本：登记还在但版本没变 = 失败（spec 第 6 节"装后核对"）
- 同一版本自动重试最多 2 次（spec 第 6 节）；本计划只有手动安装，手动安装不受此限，但历史记录要能统计失败次数，供计划 6 使用
- 真实安装只在 Task 6 进行，**执行前必须得到用户对具体软件的明确同意**；单元测试一律用假实现

## Review Focus

1. **安装过程中抛异常**（winget 服务中途断开、COM 调用失败）→ 记为失败并写入历史，不让异常冒出去导致程序崩溃
2. **新版本换了注册表键名**（MSI 升级后产品代码变化，原来的卸载键消失）→ 不能误判为失败，记为"已安装，但登记信息变了，下次扫描再确认"
3. **软件的某个后台进程在运行**（如 QQ 只剩托盘进程）→ 同样拒绝安装，并说出进程名
4. **历史文件损坏或被手工改坏**（某一行不是合法 JSON）→ 跳过坏行，其余记录照常读取，追加新记录不受影响
5. **用户按 Ctrl+C 取消**（取消令牌触发）→ 记为"已取消"，不当作失败计入重试次数

以上每条都在对应任务里有专门的测试。

---

## 文件结构

```
src/UpdateHelper.Core/Install/
  InstallModels.cs           安装相关的数据：InstallScopeHint、InstallerReport、ExecuteOutcome、ExecuteResult、HistoryRecord
  Abstractions.cs            四个接口：IPackageInstaller、IVersionProbe、IRunningProcessProbe、IUpdateHistory
  UpdateExecutor.cs          编排：装前检查 → 安装 → 装后核对 → 写历史
  JsonLinesUpdateHistory.cs  历史记录：每行一个 JSON，存在 %LOCALAPPDATA%\UpdateHelper\history.jsonl
  RegistryVersionProbe.cs    按卸载键名重新读取版本
  RunningProcessProbe.cs     找出安装目录下正在运行的进程
src/UpdateHelper.Winget/
  WingetSession.cs           （新）查询和安装共用的连接代码
  WingetUpdateSource.cs      （修改）改用 WingetSession
  WingetInstaller.cs         通过 UpgradePackageAsync 静默升级
src/UpdateHelper.ScanCli/Program.cs   （修改）加 --install <包 id> 和 --yes
tests/UpdateHelper.Core.Tests/
  UpdateExecutorTests.cs
  JsonLinesUpdateHistoryTests.cs
  RunningProcessProbeTests.cs
```

## 任务清单

- Task 1：安装相关的数据和接口
- Task 2：UpdateExecutor（编排和所有判断分支）
- Task 3：JsonLinesUpdateHistory（更新历史）
- Task 4：RegistryVersionProbe 和 RunningProcessProbe
- Task 5：WingetInstaller
- Task 6：ScanCli 接入 `--install`，在本机更新一个用户选定的软件

---

### Task 1：安装相关的数据和接口

只有类型定义，没有逻辑，所以这一步没有单独的测试，下一个任务的测试会用到全部类型。

**Files:**
- Create: `src/UpdateHelper.Core/Install/InstallModels.cs`
- Create: `src/UpdateHelper.Core/Install/Abstractions.cs`

**Interfaces:**
- Consumes: `JudgedUpdate`、`UpdateTier`（计划 3）
- Produces（命名空间 `UpdateHelper.Core.Install`）：
  - `enum InstallScopeHint { User, Machine }`
  - `sealed record InstallerReport(bool Success, bool RebootRequired, string? ErrorMessage, uint? InstallerErrorCode)`
  - `enum ExecuteOutcome { Succeeded, NeedsReboot, Failed, Refused, Cancelled }`
  - `sealed record ExecuteResult(ExecuteOutcome Outcome, string Message, string? VersionAfter)`
  - `sealed record HistoryRecord(DateTimeOffset Time, string PackageId, string Name, string FromVersion, string ToVersion, ExecuteOutcome Outcome, string Message, bool Automatic)`
  - `interface IPackageInstaller { string Name { get; } Task<InstallerReport> UpgradeAsync(string packageId, InstallScopeHint scope, IProgress<double>? progress, CancellationToken cancellationToken); }`
  - `interface IVersionProbe { string? GetInstalledVersion(string uninstallKeyName); }`——键不存在返回 null
  - `interface IRunningProcessProbe { IReadOnlyList<string> FindRunningUnder(IReadOnlyList<string> directories); }`——返回进程名（如 `QQ.exe`），去重
  - `interface IUpdateHistory { void Append(HistoryRecord record); IReadOnlyList<HistoryRecord> ReadAll(); }`

- [ ] **Step 1: 数据** `src/UpdateHelper.Core/Install/InstallModels.cs`

```csharp
namespace UpdateHelper.Core.Install;

/// <summary>安装范围：保持和原来一致（spec 第 6 节）。</summary>
public enum InstallScopeHint
{
    /// <summary>原来只装给当前用户（卸载信息在 HKCU）</summary>
    User,
    /// <summary>原来装给所有用户（卸载信息在 HKLM）</summary>
    Machine,
}

/// <summary>安装器（如 winget）报告的原始结果。</summary>
public sealed record InstallerReport(bool Success, bool RebootRequired, string? ErrorMessage, uint? InstallerErrorCode);

public enum ExecuteOutcome
{
    /// <summary>已更新</summary>
    Succeeded,
    /// <summary>已安装，但需要重启电脑才能完成（不会自动重启）</summary>
    NeedsReboot,
    /// <summary>安装失败</summary>
    Failed,
    /// <summary>装前检查没通过，没有开始安装</summary>
    Refused,
    /// <summary>用户取消</summary>
    Cancelled,
}

/// <summary>一次更新的最终结果。Message 是给用户看的中文说明。</summary>
public sealed record ExecuteResult(ExecuteOutcome Outcome, string Message, string? VersionAfter);

/// <summary>更新历史中的一条记录（spec 第 6 节"所有操作都写进更新历史"）。</summary>
public sealed record HistoryRecord(
    DateTimeOffset Time,
    string PackageId,
    string Name,
    string FromVersion,
    string ToVersion,
    ExecuteOutcome Outcome,
    string Message,
    bool Automatic);
```

- [ ] **Step 2: 接口** `src/UpdateHelper.Core/Install/Abstractions.cs`

```csharp
namespace UpdateHelper.Core.Install;

/// <summary>能把一个包升级到最新版的安装器（如 winget）。</summary>
public interface IPackageInstaller
{
    string Name { get; }

    /// <summary>静默升级。出错可以抛异常，也可以返回 Success=false；取消时抛 OperationCanceledException。</summary>
    Task<InstallerReport> UpgradeAsync(string packageId, InstallScopeHint scope, IProgress<double>? progress,
        CancellationToken cancellationToken);
}

/// <summary>重新读取某个卸载登记的当前版本，用于装后核对。</summary>
public interface IVersionProbe
{
    /// <summary>键不存在时返回 null。</summary>
    string? GetInstalledVersion(string uninstallKeyName);
}

/// <summary>找出程序文件位于给定目录内、正在运行的进程。</summary>
public interface IRunningProcessProbe
{
    /// <summary>返回去重后的进程名（如 "QQ.exe"）。</summary>
    IReadOnlyList<string> FindRunningUnder(IReadOnlyList<string> directories);
}

/// <summary>更新历史。</summary>
public interface IUpdateHistory
{
    void Append(HistoryRecord record);
    IReadOnlyList<HistoryRecord> ReadAll();
}
```

- [ ] **Step 3: 编译**

Run: `dotnet build`
Expected: 0 警告 0 错误

- [ ] **Step 4: Commit**

```bash
git add -A
git commit -m "feat(install): 安装相关的数据类型和四个接口"
```

### Task 2：UpdateExecutor（编排和所有判断分支）

**Files:**
- Create: `src/UpdateHelper.Core/Install/UpdateExecutor.cs`
- Test: `tests/UpdateHelper.Core.Tests/UpdateExecutorTests.cs`

**Interfaces:**
- Consumes: Task 1 的全部类型；`JudgedUpdate`、`UpdateTier`、`UpdateCandidate`、`AppVersion`（计划 3）；`SoftwareGroup`、`UninstallHive`、`SoftwareGrouper`（计划 1）；`Fakes.Entry`
- Produces:
  - `sealed class UpdateExecutor(IPackageInstaller installer, IVersionProbe versions, IRunningProcessProbe processes, IUpdateHistory history, TimeProvider? clock = null)`
  - `const int MaxAutomaticRetries = 2`
  - `Task<ExecuteResult> ExecuteAsync(JudgedUpdate update, bool automatic, IProgress<double>? progress, CancellationToken cancellationToken)`——**永不抛异常**（取消也转成 `Cancelled` 结果）

**流程（先命中先返回）：**

| # | 情况 | 结果 | 写历史 |
|---|---|---|---|
| 1 | 档位是"不自动"或"不管"，或对不上软件组 | Refused："不能更新：{判断层的理由}" | 否 |
| 2 | 自动模式，且这个包的这个目标版本历史上已失败 ≥ 2 次 | Refused："这个版本已经失败 2 次，不再自动重试" | 否 |
| 3 | 软件安装目录下有进程在运行 | Refused："{软件名} 正在运行（QQ.exe、QQProtect.exe），请先关闭再更新" | 否 |
| 4 | 安装器抛 `OperationCanceledException` | Cancelled："已取消" | 是 |
| 5 | 安装器抛其他异常 | Failed："安装过程中出错：{异常信息}" | 是 |
| 6 | 安装器返回 Success=false | Failed："安装失败：{错误信息}（错误码 0x…）" | 是 |
| 7 | 安装器要求重启 | NeedsReboot："已安装，需要重启电脑才能完成（不会自动重启）" | 是 |
| 8 | 装后读不到原来的卸载键 | Succeeded："已安装，但登记信息有变化，下次扫描时再确认版本" | 是 |
| 9 | 装后版本没有变新 | Failed："安装程序报告成功，但版本没有变化（仍是 {版本}）" | 是 |
| 10 | 其他 | Succeeded："已从 {旧版本} 更新到 {新版本}" | 是 |

- 安装范围：主条目的卸载信息在 HKCU → `User`，否则 → `Machine`
- 装后核对用哪个键：候选的 `ProductCodes` 里第一个能对上该组主条目或组件 `KeyName` 的；都对不上就用主条目的 `KeyName`
- 重启判断（第 7 条）放在版本核对之前：很多软件要重启后才会改写版本号，不能因此判为失败

- [ ] **Step 1: 写失败的测试** `tests/UpdateHelper.Core.Tests/UpdateExecutorTests.cs`

```csharp
using UpdateHelper.Core.Grouping;
using UpdateHelper.Core.Install;
using UpdateHelper.Core.Scanning;
using UpdateHelper.Core.Updates;
using static UpdateHelper.Core.Tests.Fakes;

namespace UpdateHelper.Core.Tests;

public class UpdateExecutorTests
{
    // —— 假实现 ——
    private sealed class FakeInstaller(Func<InstallerReport> behave) : IPackageInstaller
    {
        public List<(string Id, InstallScopeHint Scope)> Calls { get; } = [];
        public string Name => "fake";

        public Task<InstallerReport> UpgradeAsync(string packageId, InstallScopeHint scope, IProgress<double>? progress,
            CancellationToken cancellationToken)
        {
            Calls.Add((packageId, scope));
            progress?.Report(1.0);
            return Task.FromResult(behave());
        }
    }

    private sealed class FakeVersions(Dictionary<string, string?> byKey) : IVersionProbe
    {
        public List<string> Asked { get; } = [];
        public string? GetInstalledVersion(string key)
        {
            Asked.Add(key);
            return byKey.TryGetValue(key, out var v) ? v : null;
        }
    }

    private sealed class FakeProcesses(params string[] running) : IRunningProcessProbe
    {
        public IReadOnlyList<string> FindRunningUnder(IReadOnlyList<string> directories) => running;
    }

    private sealed class MemoryHistory : IUpdateHistory
    {
        public List<HistoryRecord> Records { get; } = [];
        public void Append(HistoryRecord record) => Records.Add(record);
        public IReadOnlyList<HistoryRecord> ReadAll() => Records;
    }

    // —— 测试数据：Bandizip 7.30 → 7.46，装在 HKLM ——
    private static JudgedUpdate Update(UpdateTier tier = UpdateTier.Low, UninstallHive hive = UninstallHive.LocalMachine64,
        string[]? codes = null)
    {
        var group = SoftwareGrouper.Group(
            [Entry("Bandizip", "Bandisoft.com", "7.30", location: @"C:\Program Files\Bandizip", key: "Bandizip", hive: hive)],
            []).Groups.Single();
        var candidate = new UpdateCandidate("Bandisoft.Bandizip", "Bandizip", "Bandisoft.com", "7.30", "7.46",
            codes ?? ["bandizip"]);
        return new JudgedUpdate(candidate, group, tier, "小版本更新");
    }

    private static readonly InstallerReport Ok = new(true, false, null, null);

    private static (UpdateExecutor Executor, FakeInstaller Installer, FakeVersions Versions, MemoryHistory History) Make(
        Func<InstallerReport>? behave = null, string? versionAfter = "7.46", string[]? running = null)
    {
        var installer = new FakeInstaller(behave ?? (() => Ok));
        var versions = new FakeVersions(new() { ["Bandizip"] = versionAfter });
        var history = new MemoryHistory();
        return (new UpdateExecutor(installer, versions, new FakeProcesses(running ?? []), history), installer, versions, history);
    }

    private static Task<ExecuteResult> Run(UpdateExecutor e, JudgedUpdate u, bool automatic = false)
        => e.ExecuteAsync(u, automatic, null, CancellationToken.None);

    [Fact]
    public async Task Successful_update()
    {
        var (executor, installer, _, history) = Make();
        var result = await Run(executor, Update());

        Assert.Equal(ExecuteOutcome.Succeeded, result.Outcome);
        Assert.Equal("已从 7.30 更新到 7.46", result.Message);
        Assert.Equal("7.46", result.VersionAfter);
        Assert.Equal(("Bandisoft.Bandizip", InstallScopeHint.Machine), Assert.Single(installer.Calls));
        var record = Assert.Single(history.Records);
        Assert.Equal(ExecuteOutcome.Succeeded, record.Outcome);
        Assert.False(record.Automatic);
        Assert.Equal("7.46", record.ToVersion);
    }

    [Fact]
    public async Task Current_user_install_keeps_user_scope()
    {
        var (executor, installer, _, _) = Make();
        await Run(executor, Update(hive: UninstallHive.CurrentUser));
        Assert.Equal(InstallScopeHint.User, installer.Calls.Single().Scope);
    }

    [Theory]
    [InlineData(UpdateTier.NeverAuto)]
    [InlineData(UpdateTier.Ignored)]
    public async Task Judge_refusals_are_respected(UpdateTier tier)
    {
        var (executor, installer, _, history) = Make();
        var result = await Run(executor, Update(tier));

        Assert.Equal(ExecuteOutcome.Refused, result.Outcome);
        Assert.Equal("不能更新：小版本更新", result.Message);
        Assert.Empty(installer.Calls);
        Assert.Empty(history.Records);
    }

    [Fact]
    public async Task Running_program_blocks_install()   // Review Focus 3
    {
        var (executor, installer, _, history) = Make(running: ["Bandizip.exe", "bdzsfx.exe"]);
        var result = await Run(executor, Update());

        Assert.Equal(ExecuteOutcome.Refused, result.Outcome);
        Assert.Equal("Bandizip 正在运行（Bandizip.exe、bdzsfx.exe），请先关闭再更新", result.Message);
        Assert.Empty(installer.Calls);
        Assert.Empty(history.Records);
    }

    [Fact]
    public async Task Installer_failure_is_recorded()
    {
        var (executor, _, _, history) = Make(() => new InstallerReport(false, false, "InstallError", 0x80070643));
        var result = await Run(executor, Update());

        Assert.Equal(ExecuteOutcome.Failed, result.Outcome);
        Assert.Equal("安装失败：InstallError（错误码 0x80070643）", result.Message);
        Assert.Equal(ExecuteOutcome.Failed, Assert.Single(history.Records).Outcome);
    }

    [Fact]
    public async Task Installer_exception_becomes_failure()   // Review Focus 1
    {
        var (executor, _, _, history) = Make(() => throw new InvalidOperationException("RPC 服务器不可用"));
        var result = await Run(executor, Update());

        Assert.Equal(ExecuteOutcome.Failed, result.Outcome);
        Assert.Equal("安装过程中出错：RPC 服务器不可用", result.Message);
        Assert.Single(history.Records);
    }

    [Fact]
    public async Task Cancellation_is_not_a_failure()   // Review Focus 5
    {
        var (executor, _, _, history) = Make(() => throw new OperationCanceledException());
        var result = await Run(executor, Update());

        Assert.Equal(ExecuteOutcome.Cancelled, result.Outcome);
        Assert.Equal(ExecuteOutcome.Cancelled, Assert.Single(history.Records).Outcome);
    }

    [Fact]
    public async Task Unchanged_version_is_failure()
    {
        var (executor, _, _, _) = Make(versionAfter: "7.30");
        var result = await Run(executor, Update());

        Assert.Equal(ExecuteOutcome.Failed, result.Outcome);
        Assert.Equal("安装程序报告成功，但版本没有变化（仍是 7.30）", result.Message);
    }

    [Fact]
    public async Task Missing_key_after_install_is_success_with_note()   // Review Focus 2
    {
        var (executor, _, _, _) = Make(versionAfter: null);
        var result = await Run(executor, Update());

        Assert.Equal(ExecuteOutcome.Succeeded, result.Outcome);
        Assert.Equal("已安装，但登记信息有变化，下次扫描时再确认版本", result.Message);
        Assert.Null(result.VersionAfter);
    }

    [Fact]
    public async Task Reboot_required_wins_over_version_check()
    {
        var (executor, _, _, history) = Make(() => new InstallerReport(true, true, null, null), versionAfter: "7.30");
        var result = await Run(executor, Update());

        Assert.Equal(ExecuteOutcome.NeedsReboot, result.Outcome);
        Assert.Equal("已安装，需要重启电脑才能完成（不会自动重启）", result.Message);
        Assert.Equal(ExecuteOutcome.NeedsReboot, Assert.Single(history.Records).Outcome);
    }

    [Fact]
    public async Task Probe_uses_matching_component_key()
    {
        var group = SoftwareGrouper.Group(
        [
            Entry("Python 3.10.2 (64-bit)", "Python Software Foundation", "3.10.2", key: "{PRIMARY}"),
            Entry("Python 3.10.2 Core Interpreter (64-bit)", "Python Software Foundation", hidden: true, key: "{CORE}"),
        ], []).Groups.Single();
        var update = new JudgedUpdate(
            new UpdateCandidate("Python.Python.3.10", "Python 3.10", null, "3.10.2", "3.10.11", ["{core}"]),
            group, UpdateTier.Low, "小版本更新");

        var installer = new FakeInstaller(() => Ok);
        var versions = new FakeVersions(new() { ["{CORE}"] = "3.10.11" });
        var executor = new UpdateExecutor(installer, versions, new FakeProcesses(), new MemoryHistory());

        var result = await Run(executor, update);

        Assert.Equal(ExecuteOutcome.Succeeded, result.Outcome);
        Assert.Equal("{CORE}", Assert.Single(versions.Asked));
    }

    private static HistoryRecord Past(ExecuteOutcome outcome, string toVersion = "7.46")
        => new(DateTimeOffset.Now, "Bandisoft.Bandizip", "Bandizip", "7.30", toVersion, outcome, "", true);

    [Fact]
    public async Task Automatic_mode_stops_after_two_failures_of_same_version()
    {
        var (executor, installer, _, history) = Make();
        history.Records.AddRange([Past(ExecuteOutcome.Failed), Past(ExecuteOutcome.Failed)]);

        var result = await Run(executor, Update(), automatic: true);

        Assert.Equal(ExecuteOutcome.Refused, result.Outcome);
        Assert.Equal("这个版本已经失败 2 次，不再自动重试", result.Message);
        Assert.Empty(installer.Calls);
    }

    [Fact]
    public async Task Retry_limit_ignores_other_versions_cancellations_and_manual_mode()
    {
        var (executor, installer, _, history) = Make();
        history.Records.AddRange([Past(ExecuteOutcome.Failed, "7.45"), Past(ExecuteOutcome.Failed), Past(ExecuteOutcome.Cancelled)]);

        Assert.Equal(ExecuteOutcome.Succeeded, (await Run(executor, Update(), automatic: true)).Outcome);

        history.Records.Add(Past(ExecuteOutcome.Failed));   // 现在同版本失败 2 次了
        Assert.Equal(ExecuteOutcome.Succeeded, (await Run(executor, Update(), automatic: false)).Outcome);  // 手动不受限
        Assert.Equal(2, installer.Calls.Count);
    }

    [Fact]
    public async Task Progress_is_forwarded()
    {
        var (executor, _, _, _) = Make();
        var seen = new List<double>();
        await executor.ExecuteAsync(Update(), false, new SyncProgress(seen.Add), CancellationToken.None);
        Assert.Equal(new[] { 1.0 }, seen);
    }

    /// <summary>Progress&lt;T&gt; 会切到别的线程回调，测试里用同步版本。</summary>
    private sealed class SyncProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }
}
```

- [ ] **Step 2: 运行，确认失败**

Run: `dotnet test --filter UpdateExecutorTests`
Expected: 编译失败，找不到 `UpdateExecutor`

- [ ] **Step 3: 实现** `src/UpdateHelper.Core/Install/UpdateExecutor.cs`

```csharp
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
```

注：历史记录的 `ToVersion` 一律写目标版本（不是装后读到的版本），否则"装了但版本没变"的失败会被记成旧版本号，重试次数就统计不到。实际装上的版本写在 `Message` 里。

- [ ] **Step 4: 运行，确认通过**

Run: `dotnet test --filter UpdateExecutorTests`
Expected: 全部 PASS

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat(install): UpdateExecutor——装前检查、安装、装后核对、写历史"
```

### Task 3：JsonLinesUpdateHistory（更新历史）

每条记录一行 JSON，追加写入。选这个格式而不是 spec 第 3 节的 SQLite，是因为现在只有"追加"和"全部读出"两种操作，一行一条最简单，坏一行也不影响其他行；等界面需要按条件查询时再换成 SQLite，接口 `IUpdateHistory` 不变。

**Files:**
- Create: `src/UpdateHelper.Core/Install/JsonLinesUpdateHistory.cs`
- Test: `tests/UpdateHelper.Core.Tests/JsonLinesUpdateHistoryTests.cs`

**Interfaces:**
- Consumes: `IUpdateHistory`、`HistoryRecord`、`ExecuteOutcome`（Task 1）；`TempDir`（计划 2 测试辅助）
- Produces: `sealed class JsonLinesUpdateHistory(string filePath) : IUpdateHistory`，`static string DefaultPath`（`%LOCALAPPDATA%\UpdateHelper\history.jsonl`）

- [ ] **Step 1: 写失败的测试** `tests/UpdateHelper.Core.Tests/JsonLinesUpdateHistoryTests.cs`

```csharp
using UpdateHelper.Core.Install;

namespace UpdateHelper.Core.Tests;

public class JsonLinesUpdateHistoryTests
{
    private static HistoryRecord Record(string name, ExecuteOutcome outcome = ExecuteOutcome.Succeeded)
        => new(new DateTimeOffset(2026, 10, 6, 20, 0, 0, TimeSpan.FromHours(8)),
               "Pkg." + name, name, "1.0", "1.1", outcome, "已从 1.0 更新到 1.1", false);

    [Fact]
    public void Appended_records_are_read_back_in_order()
    {
        using var dir = new TempDir();
        var history = new JsonLinesUpdateHistory(Path.Combine(dir.Path, "sub", "history.jsonl"));   // 目录不存在也能写

        history.Append(Record("微信"));
        history.Append(Record("QQ", ExecuteOutcome.Failed));

        var all = history.ReadAll();
        Assert.Equal(new[] { "微信", "QQ" }, all.Select(r => r.Name));
        Assert.Equal(Record("微信"), all[0]);
        Assert.Equal(ExecuteOutcome.Failed, all[1].Outcome);
    }

    [Fact]
    public void Missing_file_reads_as_empty()
    {
        using var dir = new TempDir();
        Assert.Empty(new JsonLinesUpdateHistory(Path.Combine(dir.Path, "none.jsonl")).ReadAll());
    }

    [Fact]
    public void Corrupted_lines_are_skipped_and_appending_still_works()   // Review Focus 4
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "history.jsonl");
        var history = new JsonLinesUpdateHistory(path);
        history.Append(Record("A"));
        File.AppendAllText(path, "这一行被手工改坏了\n{\"PackageId\":\n\n");
        history.Append(Record("B"));

        Assert.Equal(new[] { "A", "B" }, history.ReadAll().Select(r => r.Name));
    }

    [Fact]
    public void File_is_readable_text_with_chinese_and_enum_names()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "history.jsonl");
        new JsonLinesUpdateHistory(path).Append(Record("微信", ExecuteOutcome.NeedsReboot));

        var text = File.ReadAllText(path);
        Assert.Contains("微信", text);
        Assert.Contains("\"NeedsReboot\"", text);
        Assert.Single(text.Split('\n', StringSplitOptions.RemoveEmptyEntries));
    }
}
```

- [ ] **Step 2: 运行，确认失败**

Run: `dotnet test --filter JsonLinesUpdateHistoryTests`
Expected: 编译失败，找不到 `JsonLinesUpdateHistory`

- [ ] **Step 3: 实现** `src/UpdateHelper.Core/Install/JsonLinesUpdateHistory.cs`

```csharp
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace UpdateHelper.Core.Install;

/// <summary>更新历史：每条记录一行 JSON，追加写入。坏行在读取时跳过。</summary>
public sealed class JsonLinesUpdateHistory(string filePath) : IUpdateHistory
{
    public static string DefaultPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UpdateHelper", "history.jsonl");

    private static readonly JsonSerializerOptions Options = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,   // 中文原样写入，文件可直接阅读
        Converters = { new JsonStringEnumConverter() },
    };

    public void Append(HistoryRecord record)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(filePath))!);
        File.AppendAllText(filePath, JsonSerializer.Serialize(record, Options) + "\n", new UTF8Encoding(false));
    }

    public IReadOnlyList<HistoryRecord> ReadAll()
    {
        if (!File.Exists(filePath)) return [];

        var result = new List<HistoryRecord>();
        foreach (var line in File.ReadLines(filePath))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                if (JsonSerializer.Deserialize<HistoryRecord>(line, Options) is { } record) result.Add(record);
            }
            catch (JsonException)
            {
                // 坏行跳过（Review Focus 4）
            }
        }
        return result;
    }
}
```

- [ ] **Step 4: 运行，确认通过**

Run: `dotnet test --filter JsonLinesUpdateHistoryTests`
Expected: 全部 PASS

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat(install): 更新历史，每行一条 JSON，坏行跳过"
```

### Task 4：RegistryVersionProbe 和 RunningProcessProbe

**Files:**
- Create: `src/UpdateHelper.Core/Install/RegistryVersionProbe.cs`
- Create: `src/UpdateHelper.Core/Install/RunningProcessProbe.cs`
- Test: `tests/UpdateHelper.Core.Tests/RunningProcessProbeTests.cs`

**Interfaces:**
- Consumes: `IVersionProbe`、`IRunningProcessProbe`（Task 1）；`RegistryValues.AsString`（计划 1）；`PathUtil.NormalizeDir`、`PathUtil.IsUnder`（计划 1）
- Produces: `sealed class RegistryVersionProbe : IVersionProbe`、`sealed class RunningProcessProbe : IRunningProcessProbe`

`RegistryVersionProbe` 只读注册表，测试里不能碰真实注册表（计划 1 的约束），所以它在 Task 6 的本机运行中验证；`RunningProcessProbe` 可以用测试进程自己来测。

- [ ] **Step 1: 写失败的测试** `tests/UpdateHelper.Core.Tests/RunningProcessProbeTests.cs`

```csharp
using UpdateHelper.Core.Install;

namespace UpdateHelper.Core.Tests;

public class RunningProcessProbeTests
{
    [Fact]
    public void Finds_the_current_test_process()
    {
        var exe = Environment.ProcessPath!;
        var found = new RunningProcessProbe().FindRunningUnder([Path.GetDirectoryName(exe)!]);
        Assert.Contains(Path.GetFileName(exe), found, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void No_directories_means_nothing_found()
        => Assert.Empty(new RunningProcessProbe().FindRunningUnder([]));

    [Fact]
    public void Unrelated_directory_finds_nothing()
    {
        using var dir = new TempDir();
        Assert.Empty(new RunningProcessProbe().FindRunningUnder([dir.Path]));
    }

    [Fact]
    public void Results_are_distinct()
    {
        var dir = Path.GetDirectoryName(Environment.ProcessPath!)!;
        var found = new RunningProcessProbe().FindRunningUnder([dir, dir]);
        Assert.Equal(found.Count, found.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }
}
```

- [ ] **Step 2: 运行，确认失败**

Run: `dotnet test --filter RunningProcessProbeTests`
Expected: 编译失败，找不到 `RunningProcessProbe`

- [ ] **Step 3: 进程探测** `src/UpdateHelper.Core/Install/RunningProcessProbe.cs`

```csharp
using System.ComponentModel;
using System.Diagnostics;
using UpdateHelper.Core.Grouping;

namespace UpdateHelper.Core.Install;

/// <summary>找出程序文件位于给定目录内、正在运行的进程。只读，不会结束任何进程。</summary>
public sealed class RunningProcessProbe : IRunningProcessProbe
{
    public IReadOnlyList<string> FindRunningUnder(IReadOnlyList<string> directories)
    {
        var dirs = directories.Select(PathUtil.NormalizeDir).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (dirs.Count == 0) return [];

        var found = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                string? path;
                try
                {
                    path = process.MainModule?.FileName;
                }
                catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or NotSupportedException)
                {
                    continue;   // 系统进程、更高权限的进程读不到路径，跳过
                }
                if (path is not null && dirs.Any(d => PathUtil.IsUnder(path, d)))
                    found.Add(Path.GetFileName(path));
            }
        }
        return found.ToList();
    }
}
```

注：以普通权限运行时读不到"以管理员身份运行"的进程路径，这类进程会被漏掉。winget 安装时如果遇到文件被占用会返回安装失败，`UpdateExecutor` 会如实记录，不会损坏软件。

- [ ] **Step 4: 版本探测** `src/UpdateHelper.Core/Install/RegistryVersionProbe.cs`

```csharp
using System.Security;
using Microsoft.Win32;
using UpdateHelper.Core.Scanning;

namespace UpdateHelper.Core.Install;

/// <summary>按卸载键名，在 3 处注册表里重新读取 DisplayVersion。只读。</summary>
public sealed class RegistryVersionProbe : IVersionProbe
{
    private const string UninstallPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

    private static readonly (RegistryHive Hive, RegistryView View)[] Locations =
    [
        (RegistryHive.LocalMachine, RegistryView.Registry64),
        (RegistryHive.LocalMachine, RegistryView.Registry32),
        (RegistryHive.CurrentUser, RegistryView.Default),
    ];

    public string? GetInstalledVersion(string uninstallKeyName)
    {
        foreach (var (hive, view) in Locations)
        {
            try
            {
                using var root = RegistryKey.OpenBaseKey(hive, view);
                using var key = root.OpenSubKey($@"{UninstallPath}\{uninstallKeyName}");
                if (key is null) continue;
                return RegistryValues.AsString(key.GetValue("DisplayVersion"));
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or SecurityException or IOException)
            {
                // 读不了就看下一处
            }
        }
        return null;
    }
}
```

- [ ] **Step 5: 运行，确认通过**

Run: `dotnet test --filter RunningProcessProbeTests`
Expected: 全部 PASS

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "feat(install): 装后读取版本、检测安装目录下正在运行的进程"
```

### Task 5：WingetInstaller

COM 调用没法写单元测试，交付物是"能编译 + 和查询共用同一套连接代码"，真实安装在 Task 6 验证。

**Files:**
- Create: `src/UpdateHelper.Winget/WingetSession.cs`（把计划 3 里"创建 PackageManager、连接组合目录"的代码提出来，查询和安装共用）
- Modify: `src/UpdateHelper.Winget/WingetUpdateSource.cs`（改用 `WingetSession`）
- Create: `src/UpdateHelper.Winget/WingetInstaller.cs`

**Interfaces:**
- Consumes: `IPackageInstaller`、`InstallerReport`、`InstallScopeHint`（Task 1）；`WingetUnavailableException`（计划 3）
- Produces:
  - `internal static class WingetSession { PackageManager CreateManager(); PackageCatalog ConnectInstalledAndRemote(PackageManager manager); }`——两者失败都抛 `WingetUnavailableException`
  - `sealed class WingetInstaller : IPackageInstaller`（`Name` 为 `"winget"`）

- [ ] **Step 1: 共用连接代码** `src/UpdateHelper.Winget/WingetSession.cs`

```csharp
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
```

- [ ] **Step 2: 查询改用共用代码**：在 `src/UpdateHelper.Winget/WingetUpdateSource.cs` 中，把 `GetAvailableUpdates()` 开头从 `PackageManager manager;` 到 `throw new WingetUnavailableException($"无法连接 winget 软件源……");` 为止的整段（创建 manager、组合目录、连接）替换为：

```csharp
        var manager = WingetSession.CreateManager();
        var catalog = WingetSession.ConnectInstalledAndRemote(manager);
```

并把后面的 `connect.PackageCatalog.FindPackages(...)` 改成 `catalog.FindPackages(...)`；文件开头不再需要的 `using System.Runtime.InteropServices;` 删掉。

Run: `dotnet build`
Expected: 0 警告 0 错误

- [ ] **Step 3: 安装器** `src/UpdateHelper.Winget/WingetInstaller.cs`

```csharp
using Microsoft.Management.Deployment;
using UpdateHelper.Core.Install;

namespace UpdateHelper.Winget;

/// <summary>
/// 通过 winget 官方接口静默升级一个包。AllowHashMismatch 永远为 false（文件指纹对不上就不装）。
/// 需要管理员权限的安装包会由 winget 弹出系统的"用户账户控制"确认框。
/// </summary>
public sealed class WingetInstaller : IPackageInstaller
{
    public string Name => "winget";

    public async Task<InstallerReport> UpgradeAsync(string packageId, InstallScopeHint scope,
        IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var manager = WingetSession.CreateManager();
        var catalog = WingetSession.ConnectInstalledAndRemote(manager);

        var find = new FindPackagesOptions();
        find.Filters.Add(new PackageMatchFilter
        {
            Field = PackageMatchField.Id,
            Option = PackageFieldMatchOption.Equals,
            Value = packageId,
        });
        var found = catalog.FindPackages(find);
        if (found.Status != FindPackagesResultStatus.Ok || found.Matches.Count == 0)
            return new InstallerReport(false, false, $"在 winget 里找不到 {packageId}", null);
        var package = found.Matches[0].CatalogPackage;   // COM 列表按下标取

        var options = new InstallOptions
        {
            PackageInstallMode = PackageInstallMode.Silent,
            // 保持原安装范围；"OrUnknown" 让没写范围的安装包也能装
            PackageInstallScope = scope == InstallScopeHint.User
                ? PackageInstallScope.UserOrUnknown
                : PackageInstallScope.SystemOrUnknown,
            AcceptPackageAgreements = true,
            AllowHashMismatch = false,
        };

        var winProgress = new Progress<InstallProgress>(p =>
            progress?.Report(p.DownloadProgress * 0.5 + p.InstallationProgress * 0.5));

        // 取消时抛 TaskCanceledException（属于 OperationCanceledException），由 UpdateExecutor 记为"已取消"
        var result = await manager.UpgradePackageAsync(package, options).AsTask(cancellationToken, winProgress);

        if (result.Status == InstallResultStatus.Ok)
            return new InstallerReport(true, result.RebootRequired, null, null);

        var detail = result.ExtendedErrorCode?.Message;
        var message = Describe(result.Status) + (string.IsNullOrWhiteSpace(detail) ? "" : $"：{detail}");
        return new InstallerReport(false, false, message, result.InstallerErrorCode);
    }

    private static string Describe(InstallResultStatus status) => status switch
    {
        InstallResultStatus.BlockedByPolicy => "被系统策略禁止",
        InstallResultStatus.CatalogError => "无法访问 winget 软件源",
        InstallResultStatus.DownloadError => "下载安装包失败（请检查网络）",
        InstallResultStatus.InstallError => "安装程序运行出错",
        InstallResultStatus.ManifestError => "winget 里这个软件的信息有误",
        InstallResultStatus.NoApplicableInstallers => "没有适合这台电脑（系统版本、架构或安装范围）的安装包",
        InstallResultStatus.NoApplicableUpgrade => "没有可用的升级（可能已经是最新版）",
        InstallResultStatus.PackageAgreementsNotAccepted => "需要先同意软件的许可协议",
        _ => $"winget 内部错误（{status}）",
    };
}
```

- [ ] **Step 4: 编译和测试**

Run: `dotnet build`
Expected: 0 警告 0 错误

Run: `dotnet test`
Expected: 全部 PASS

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat(winget): 通过 UpgradePackageAsync 静默升级，查询与安装共用连接代码"
```

### Task 6：ScanCli 接入 `--install`，在本机更新一个用户选定的软件

**Files:**
- Modify: `src/UpdateHelper.ScanCli/Program.cs`

**Interfaces:**
- Consumes: `UpdateExecutor`、`JsonLinesUpdateHistory`、`RegistryVersionProbe`、`RunningProcessProbe`、`ExecuteOutcome`（Task 2～4）；`WingetInstaller`、`WingetUpdateSource`（Task 5、计划 3）；`UpdateService`、`UpdateTier`（计划 3）；`Program.cs` 中已有的 `r`、`rules`、`TierName`
- Produces: 命令行参数 `--install <包 id>`、`--yes`（跳过确认）

- [ ] **Step 1: 改 `Program.cs`**

using 区加：

```csharp
using UpdateHelper.Core.Install;
```

用法注释加：

```csharp
//   dotnet run --project src/UpdateHelper.ScanCli -- --install <包 id>  更新一个软件（会先询问；加 --yes 跳过询问）
```

在 `var showUpdates = args.Contains("--updates");` 下面加：

```csharp
var installId = ArgValue("--install");
var assumeYes = args.Contains("--yes");
```

在 `if (showUpdates) { ... }` 整块**之后**、`if (jsonPath is not null)` **之前**插入：

```csharp
if (installId is not null)
{
    Console.WriteLine();
    Console.WriteLine($"准备更新 {installId}，正在查询……");
    var check = UpdateService.Check(new WingetUpdateSource(), r, rules);
    if (check.Warning is not null) Console.WriteLine($"警告：{check.Warning}");

    var target = check.Updates.FirstOrDefault(u =>
        string.Equals(u.Candidate.PackageId, installId, StringComparison.OrdinalIgnoreCase));
    if (target is null)
    {
        Console.WriteLine($"没有找到 {installId} 的可用更新。可以先用 --updates 查看可更新的包 id。");
        return;
    }

    var name = target.Group?.Name ?? target.Candidate.Name;
    Console.WriteLine($"{name}：{target.Candidate.InstalledVersion} → {target.Candidate.AvailableVersion}");
    Console.WriteLine($"判断：【{TierName(target.Tier)}】{target.Reason}");

    if (!assumeYes)
    {
        Console.Write("确定要更新吗？输入 y 并回车继续，其他任意键取消：");
        if (!string.Equals(Console.ReadLine()?.Trim(), "y", StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine("已取消，没有做任何改动。");
            return;
        }
    }

    using var cts = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };   // Ctrl+C 取消

    var executor = new UpdateExecutor(new WingetInstaller(), new RegistryVersionProbe(), new RunningProcessProbe(),
        new JsonLinesUpdateHistory(JsonLinesUpdateHistory.DefaultPath));
    var lastPercent = -1;
    var progress = new Progress<double>(p =>
    {
        var percent = (int)(p * 100);
        if (percent / 10 != lastPercent / 10) Console.WriteLine($"  进度 {percent}%");
        lastPercent = percent;
    });

    var result = await executor.ExecuteAsync(target, automatic: false, progress, cts.Token);
    Console.WriteLine($"结果：{result.Outcome}——{result.Message}");
    Console.WriteLine($"更新历史：{JsonLinesUpdateHistory.DefaultPath}");
}
```

注：top-level 程序里出现 `await` 后，`Main` 自动变成异步的，不需要改别的地方。`return;` 在 top-level 程序里表示提前结束。

- [ ] **Step 2: 编译**

Run: `dotnet build`
Expected: 0 警告 0 错误

- [ ] **Step 3: 先验证"拒绝"路径（不会安装任何东西）**

Run: `dotnet run --project src/UpdateHelper.ScanCli -- --install Python.Launcher --yes`
Expected: 判断为【不自动】，结果为 `Refused——不能更新：版本号无法准确比较（…）`；更新历史文件里**没有**新记录

Run: `dotnet run --project src/UpdateHelper.ScanCli -- --install No.Such.Package --yes`
Expected: "没有找到 No.Such.Package 的可用更新……"

- [ ] **Step 4: 本机真实更新一个软件——必须先征得用户同意**

**执行者在这一步之前停下来**，向用户确认要更新哪个软件。建议从【低风险】里选一个小巧、当前没在运行的软件，例如 `Bandisoft.Bandizip`（7.30 → 7.46）。用户同意后运行：

Run: `dotnet run --project src/UpdateHelper.ScanCli -- --install <用户同意的包 id> --yes`

如果是给所有用户安装的软件，屏幕上会弹出"用户账户控制"确认框，需要用户点"是"。

Expected:
- 依次打印进度，最后是 `结果：Succeeded——已从 7.30 更新到 7.46`（或 `NeedsReboot`）
- `%LOCALAPPDATA%\UpdateHelper\history.jsonl` 里多出一行，`Outcome` 为 `Succeeded`
- 再运行 `--updates`，这个软件不再出现在列表里

- [ ] **Step 5: 验证"正在运行时拒绝"（Review Focus 3）**：打开另一个【低风险】软件（例如网易云音乐）后运行 `--install NetEase.CloudMusic --yes`

Expected: `Refused——网易云音乐 正在运行（cloudmusic.exe…），请先关闭再更新`，没有安装任何东西

（这一步也要先告诉用户：需要用户自己打开那个软件；执行者不要替用户启动程序。）

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "feat(cli): ScanCli 增加 --install，手动更新一个软件"
```

---

## 完成标准

- `dotnet build` 0 警告 0 错误；`dotnet test` 全部通过
- 本机：拒绝路径（Step 3、5）都没有安装任何东西；用户同意的那个软件更新成功并写入历史
- 源代码中 `AllowHashMismatch` 只出现一次且为 `false`（`grep -rn "AllowHashMismatch" src --include=*.cs`）
- 源代码中没有结束进程、重启电脑的调用（`grep -rnE "\.Kill\(|shutdown|InitiateSystemShutdown" src --include=*.cs` 无结果）
