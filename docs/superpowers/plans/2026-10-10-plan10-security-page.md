# 计划 10：安全页（显示杀毒软件状态）Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 在程序里加一个"安全"页，诚实显示这台电脑当前由哪个杀毒软件保护、是否真的在运行；装了第三方杀软就显示"由 XX 保护"，是 Windows Defender 就读它真实状态（实时保护是否开启），什么都没有或实时保护关着就给出提示。提供一个"打开 Windows 安全中心"按钮。

**Architecture:** Core 只读查两个 WMI 命名空间（SecurityCenter2 列出注册的杀软、Defender 状态类读 Defender 真实运行情况），都藏在 `ISecurityProbe` 接口后面便于测试。`SecurityJudge` 纯逻辑把探针结果判成一个给用户看的状态。Presentation 层把它变成界面文字，App 加"安全"导航页。全程只读、不需要管理员权限。本期不在程序内触发扫描（涉及提权细节），只提供"打开 Windows 安全中心"的入口。

**Tech Stack:** .NET 10、System.Management（CIM 查询）、WPF/WPF-UI、xUnit。

**Spec:** `docs/superpowers/specs/2026-09-30-update-helper-design.md`（第 9 节安全模块）

## 技术验证（2026-10-10 本机，普通权限）

- `root\SecurityCenter2` 的 `AntiVirusProduct` 可只读查询，返回 `displayName`、`productState`、`pathToSignedProductExe`。
- `root\Microsoft\Windows\Defender` 的 `MSFT_MpComputerStatus` 可只读查询，返回 `AMRunningMode`、`RealTimeProtectionEnabled`、`AntivirusEnabled`、`AntivirusSignatureVersion`、`AntivirusSignatureLastUpdated`。
- 实测本机：SecurityCenter2 注册的是 Windows Defender，但 Defender 状态类显示 `AMRunningMode=Not running`、`RealTimeProtectionEnabled=False`——**所以不能只念 SecurityCenter2 的注册名说"受保护"，必须看 Defender 真实运行状态**，这正是本页要诚实处理的核心场景。
- C# 用 `System.Management`（ManagementObjectSearcher）即可查询，不用起 PowerShell。

## 范围

- **做**：检测注册的杀软；是 Defender 时读真实运行状态；判成"受保护 / 实时保护未开启 / 未检测到 / 未知"四态并给中文说明；安全页；"打开 Windows 安全中心"按钮。
- **不做**：程序内一键触发 Defender 扫描（涉及提权和 Defender 未运行时的边界，留作后续）；隔离/删除威胁（交给 Defender 自己）；第三方杀软的开关状态深解码（productState 各厂商编码不一，不可靠，只显示"检测到 XX"，不妄称开或关）。

## Global Constraints

- 目标框架：Core 为 `net10.0-windows`；Presentation 为 `net10.0-windows`；App 为 `net10.0-windows10.0.26100.0`、win-x64
- `TreatWarningsAsErrors=true`、`Nullable=enable`、`ImplicitUsings=enable`（App 不隐式导入 System.IO，需要时全限定）
- 只读：只查 WMI、不改任何东西、不需要管理员权限；任何查询失败都不抛异常，当作"读不到"，判成"未知"
- 诚实：是 Windows Defender 时，以 `MSFT_MpComputerStatus` 的 `RealTimeProtectionEnabled` 为准，不以 SecurityCenter2 的注册为准；第三方杀软只显示"检测到 XX"，不妄称开/关
- 所有给用户看的文字是中文；提交信息不写任何 Claude 署名行
- `System.Management` 选当前 `dotnet list package --vulnerable` 不报漏洞的版本（建议 9.0.0，实现时核对）

## Review Focus

1. **注册的是 Defender 但实际没运行**（本机真实状态）：判成"实时保护未开启"、给开启提示，不显示"受保护" → Task 1 `Defender_registered_but_off_is_warned`
2. **装了第三方杀软**（如火绒）：显示"由 XX 保护"，不去读 Defender 状态 → Task 1 `Third_party_av_shown_as_protected`
3. **什么杀软都没注册**：显示"没有检测到杀毒软件"的提示，不崩溃 → Task 1 `No_av_detected`
4. **WMI 查询抛异常或命名空间不存在**（精简版系统、被策略禁用）：探针返回空/ null，页面显示"无法读取安全状态"，不崩溃 → Task 2 `Probe_never_throws`、Task 1 `Null_defender_is_unknown`
5. **同时注册了多个杀软**：第三方优先于 Defender（第三方在管时 Defender 让位） → Task 1 `Third_party_wins_over_defender`

---

## 文件结构

| 文件 | 职责 |
|---|---|
| `src/UpdateHelper.Core/Security/SecurityModels.cs` | `RegisteredAntivirus`、`DefenderStatus`、`AntivirusState`、`SecurityStatus` |
| `src/UpdateHelper.Core/Security/ISecurityProbe.cs` | 只读探针接口 |
| `src/UpdateHelper.Core/Security/SecurityJudge.cs` | 纯逻辑：探针结果 → SecurityStatus |
| `src/UpdateHelper.Core/Security/RealSecurityProbe.cs` | System.Management CIM 查询实现 |
| `src/UpdateHelper.Presentation/ViewModels/SecurityViewModel.cs` | 安全页界面文字 |
| `src/UpdateHelper.Presentation/IAppBackend.cs` | 加 `GetSecurityStatus()` |
| `src/UpdateHelper.App/RealBackend.cs` | 实现 GetSecurityStatus（后台线程、真实探针） |
| `src/UpdateHelper.App/Pages/SecurityPage.xaml`(.cs) | 安全页 + "打开 Windows 安全中心"按钮 |
| `src/UpdateHelper.App/MainWindow.xaml`、`App.xaml.cs`、`AppHost.cs` | 导航项、--page security、ViewModel 接线 |

---

### Task 1: 安全状态判断（纯逻辑）

**Files:**
- Create: `src/UpdateHelper.Core/Security/SecurityModels.cs`
- Create: `src/UpdateHelper.Core/Security/ISecurityProbe.cs`
- Create: `src/UpdateHelper.Core/Security/SecurityJudge.cs`
- Test: `tests/UpdateHelper.Core.Tests/SecurityJudgeTests.cs`

**Interfaces:**
- Produces:
  - `sealed record RegisteredAntivirus(string DisplayName, uint ProductState, string? ExePath)`
  - `sealed record DefenderStatus(string AmRunningMode, bool RealTimeProtectionEnabled, bool AntivirusEnabled, string? SignatureVersion, DateTimeOffset? SignatureLastUpdated)`
  - `enum AntivirusState { Protected, RealTimeOff, NotDetected, Unknown }`
  - `sealed record SecurityStatus(AntivirusState State, string ProviderName, string Headline, string Detail)`
  - `interface ISecurityProbe { IReadOnlyList<RegisteredAntivirus> QueryRegistered(); DefenderStatus? QueryDefender(); }`
  - `static class SecurityJudge { SecurityStatus Evaluate(IReadOnlyList<RegisteredAntivirus> registered, DefenderStatus? defender) }`

判断规则：
1. 注册列表里有"非 Windows Defender"的杀软（按 DisplayName 不等于 "Windows Defender"，忽略大小写）→ `Protected`，ProviderName=该名，Headline=`由 {名} 保护`，Detail=`检测到第三方杀毒软件正在保护这台电脑`。多个取第一个。
2. 否则若注册了 Windows Defender（或 Defender 状态非空）：看 `defender`
   - `defender` 为 null → `Unknown`，Headline=`无法读取 Windows Defender 状态`，Detail=`可以在 Windows 安全中心查看`
   - `defender.RealTimeProtectionEnabled` 为 true → `Protected`，ProviderName=`Windows Defender`，Headline=`Windows Defender 正在保护`，Detail 含特征码日期（有则 `病毒库更新于 {date:yyyy-MM-dd}`，无则 `已开启实时保护`）
   - 为 false → `RealTimeOff`，Headline=`实时保护未开启`，Detail=`Windows Defender 已注册，但实时保护是关闭的，建议在 Windows 安全中心开启`
3. 注册列表为空且 defender 为 null → `NotDetected`，Headline=`没有检测到杀毒软件`，Detail=`建议开启 Windows Defender 或安装一款杀毒软件`

- [ ] **Step 1: 写失败的测试**

```csharp
using UpdateHelper.Core.Security;

namespace UpdateHelper.Core.Tests;

public sealed class SecurityJudgeTests
{
    private static RegisteredAntivirus Av(string name) => new(name, 0, null);
    private static DefenderStatus Def(bool rtp) => new("Normal", rtp, rtp, "1.400", new DateTimeOffset(2026, 10, 9, 0, 0, 0, TimeSpan.Zero));

    [Fact]
    public void Third_party_av_shown_as_protected()
    {
        var s = SecurityJudge.Evaluate([Av("火绒安全软件")], null);
        Assert.Equal(AntivirusState.Protected, s.State);
        Assert.Equal("火绒安全软件", s.ProviderName);
        Assert.Contains("由 火绒安全软件 保护", s.Headline);
    }

    [Fact]
    public void Third_party_wins_over_defender()
    {
        // 同时注册了第三方和 Defender，且 Defender 没在跑：以第三方为准
        var s = SecurityJudge.Evaluate([Av("Windows Defender"), Av("卡巴斯基")], Def(false));
        Assert.Equal(AntivirusState.Protected, s.State);
        Assert.Equal("卡巴斯基", s.ProviderName);
    }

    [Fact]
    public void Defender_running_is_protected()
    {
        var s = SecurityJudge.Evaluate([Av("Windows Defender")], Def(true));
        Assert.Equal(AntivirusState.Protected, s.State);
        Assert.Equal("Windows Defender", s.ProviderName);
        Assert.Contains("2026-10-09", s.Detail);
    }

    [Fact]
    public void Defender_registered_but_off_is_warned()
    {
        var s = SecurityJudge.Evaluate([Av("Windows Defender")], Def(false));
        Assert.Equal(AntivirusState.RealTimeOff, s.State);
        Assert.Contains("实时保护未开启", s.Headline);
        Assert.Contains("安全中心", s.Detail);
    }

    [Fact]
    public void Null_defender_is_unknown()
    {
        var s = SecurityJudge.Evaluate([Av("Windows Defender")], null);
        Assert.Equal(AntivirusState.Unknown, s.State);
        Assert.Contains("无法读取", s.Headline);
    }

    [Fact]
    public void No_av_detected()
    {
        var s = SecurityJudge.Evaluate([], null);
        Assert.Equal(AntivirusState.NotDetected, s.State);
        Assert.Contains("没有检测到", s.Headline);
    }

    [Fact]
    public void Defender_name_match_is_case_insensitive()
    {
        var s = SecurityJudge.Evaluate([Av("windows defender")], Def(true));
        Assert.Equal(AntivirusState.Protected, s.State);
        Assert.Equal("Windows Defender", s.ProviderName);
    }
}
```

- [ ] **Step 2: 运行测试，确认失败**

Run: `dotnet test tests/UpdateHelper.Core.Tests --filter SecurityJudgeTests`
Expected: 编译失败（类型不存在）

- [ ] **Step 3: 实现**

`src/UpdateHelper.Core/Security/SecurityModels.cs`：

```csharp
namespace UpdateHelper.Core.Security;

/// <summary>SecurityCenter2 里注册的一个杀毒软件。</summary>
public sealed record RegisteredAntivirus(string DisplayName, uint ProductState, string? ExePath);

/// <summary>Windows Defender 的真实运行状态（来自 MSFT_MpComputerStatus）。</summary>
public sealed record DefenderStatus(
    string AmRunningMode, bool RealTimeProtectionEnabled, bool AntivirusEnabled,
    string? SignatureVersion, DateTimeOffset? SignatureLastUpdated);

/// <summary>安全状态四态。</summary>
public enum AntivirusState { Protected, RealTimeOff, NotDetected, Unknown }

/// <summary>给用户看的安全状态。</summary>
public sealed record SecurityStatus(AntivirusState State, string ProviderName, string Headline, string Detail);
```

`src/UpdateHelper.Core/Security/ISecurityProbe.cs`：

```csharp
namespace UpdateHelper.Core.Security;

/// <summary>只读查询安全状态。两个方法失败都返回空/null，不抛异常。</summary>
public interface ISecurityProbe
{
    /// <summary>SecurityCenter2 里注册的杀毒软件；读不到返回空列表。</summary>
    IReadOnlyList<RegisteredAntivirus> QueryRegistered();

    /// <summary>Windows Defender 的真实状态；Defender 不可用或读不到返回 null。</summary>
    DefenderStatus? QueryDefender();
}
```

`src/UpdateHelper.Core/Security/SecurityJudge.cs`：

```csharp
namespace UpdateHelper.Core.Security;

/// <summary>把探针结果判成一个诚实的安全状态（spec 第 9 节）。纯逻辑。</summary>
public static class SecurityJudge
{
    private const string DefenderName = "Windows Defender";

    public static SecurityStatus Evaluate(IReadOnlyList<RegisteredAntivirus> registered, DefenderStatus? defender)
    {
        // 1. 第三方杀软优先（它在管时 Defender 会让位）
        var thirdParty = registered.FirstOrDefault(a =>
            !string.Equals(a.DisplayName, DefenderName, StringComparison.OrdinalIgnoreCase));
        if (thirdParty is not null)
            return new SecurityStatus(AntivirusState.Protected, thirdParty.DisplayName,
                $"由 {thirdParty.DisplayName} 保护", "检测到第三方杀毒软件正在保护这台电脑");

        var defenderRegistered = registered.Any(a =>
            string.Equals(a.DisplayName, DefenderName, StringComparison.OrdinalIgnoreCase));

        // 2. 只有 Defender（或没有注册但能读到 Defender 状态）
        if (defenderRegistered || defender is not null)
        {
            if (defender is null)
                return new SecurityStatus(AntivirusState.Unknown, DefenderName,
                    "无法读取 Windows Defender 状态", "可以在 Windows 安全中心查看");
            if (defender.RealTimeProtectionEnabled)
            {
                var detail = defender.SignatureLastUpdated is { } d
                    ? $"病毒库更新于 {d.LocalDateTime:yyyy-MM-dd}"
                    : "已开启实时保护";
                return new SecurityStatus(AntivirusState.Protected, DefenderName, "Windows Defender 正在保护", detail);
            }
            return new SecurityStatus(AntivirusState.RealTimeOff, DefenderName, "实时保护未开启",
                "Windows Defender 已注册，但实时保护是关闭的，建议在 Windows 安全中心开启");
        }

        // 3. 什么都没有
        return new SecurityStatus(AntivirusState.NotDetected, "",
            "没有检测到杀毒软件", "建议开启 Windows Defender 或安装一款杀毒软件");
    }
}
```

- [ ] **Step 4: 运行测试，确认通过**

Run: `dotnet test tests/UpdateHelper.Core.Tests --filter SecurityJudgeTests`
Expected: 7 个全部通过

- [ ] **Step 5: 提交**

```bash
git add src/UpdateHelper.Core/Security/SecurityModels.cs src/UpdateHelper.Core/Security/ISecurityProbe.cs src/UpdateHelper.Core/Security/SecurityJudge.cs tests/UpdateHelper.Core.Tests/SecurityJudgeTests.cs
git commit -m "feat(core): 安全状态判断——第三方杀软优先、Defender 以真实运行状态为准、诚实四态"
```

---

### Task 2: 真实只读探针（System.Management CIM）

**Files:**
- Modify: `src/UpdateHelper.Core/UpdateHelper.Core.csproj`（加 System.Management 包）
- Create: `src/UpdateHelper.Core/Security/RealSecurityProbe.cs`
- Test: `tests/UpdateHelper.Core.Tests/RealSecurityProbeTests.cs`

**Interfaces:**
- Consumes: Task 1 的 `ISecurityProbe`、`RegisteredAntivirus`、`DefenderStatus`
- Produces: `sealed class RealSecurityProbe : ISecurityProbe`（只读 CIM 查询，任何失败返回空/null，永不抛）

- [ ] **Step 1: 加包**

`src/UpdateHelper.Core/UpdateHelper.Core.csproj` 的包引用组加：

```xml
    <PackageReference Include="System.Management" Version="9.0.0" />
```

Run: `dotnet restore src/UpdateHelper.Core` 然后 `dotnet list src/UpdateHelper.Core package --vulnerable --include-transitive`
Expected: 无漏洞输出。若 9.0.0 报漏洞，换 `dotnet list` 不报的最低版本并在报告里写明。

- [ ] **Step 2: 写失败的测试**

```csharp
using UpdateHelper.Core.Security;

namespace UpdateHelper.Core.Tests;

public sealed class RealSecurityProbeTests
{
    // 这些是集成测试：读本机真实 WMI。断言的是"不抛异常、返回合理结构"，不假设具体杀软。
    [Fact]
    public void Query_registered_does_not_throw()
    {
        var probe = new RealSecurityProbe();
        var list = probe.QueryRegistered();   // 可能为空（看机器），但不能抛
        Assert.NotNull(list);
        Assert.All(list, a => Assert.False(string.IsNullOrWhiteSpace(a.DisplayName)));
    }

    [Fact]
    public void Query_defender_does_not_throw()
    {
        var probe = new RealSecurityProbe();
        var d = probe.QueryDefender();   // 可能为 null（没有 Defender 命名空间），但不能抛
        if (d is not null)
            Assert.False(string.IsNullOrWhiteSpace(d.AmRunningMode));
    }

    [Fact]
    public void Evaluate_on_real_machine_produces_a_status()
    {
        var probe = new RealSecurityProbe();
        var status = SecurityJudge.Evaluate(probe.QueryRegistered(), probe.QueryDefender());
        Assert.False(string.IsNullOrWhiteSpace(status.Headline));
    }
}
```

- [ ] **Step 3: 运行测试，确认失败**

Run: `dotnet test tests/UpdateHelper.Core.Tests --filter RealSecurityProbeTests`
Expected: 编译失败（`RealSecurityProbe` 不存在）

- [ ] **Step 4: 实现**

`src/UpdateHelper.Core/Security/RealSecurityProbe.cs`：

```csharp
using System.Globalization;
using System.Management;

namespace UpdateHelper.Core.Security;

/// <summary>
/// 真实只读安全探针：查 SecurityCenter2 的注册杀软、Defender 状态类。
/// 任何失败（命名空间不存在、被策略禁用、WMI 出错）都返回空/null，不抛异常。
/// </summary>
public sealed class RealSecurityProbe : ISecurityProbe
{
    public IReadOnlyList<RegisteredAntivirus> QueryRegistered()
    {
        var result = new List<RegisteredAntivirus>();
        try
        {
            using var searcher = new ManagementObjectSearcher(
                @"root\SecurityCenter2", "SELECT displayName, productState, pathToSignedProductExe FROM AntiVirusProduct");
            foreach (ManagementObject mo in searcher.Get())
            {
                using (mo)
                {
                    var name = mo["displayName"] as string;
                    if (string.IsNullOrWhiteSpace(name)) continue;
                    var state = mo["productState"] is { } s ? Convert.ToUInt32(s, CultureInfo.InvariantCulture) : 0u;
                    result.Add(new RegisteredAntivirus(name, state, mo["pathToSignedProductExe"] as string));
                }
            }
        }
        catch (Exception ex) when (ex is ManagementException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
        }
        return result;
    }

    public DefenderStatus? QueryDefender()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                @"root\Microsoft\Windows\Defender", "SELECT * FROM MSFT_MpComputerStatus");
            foreach (ManagementObject mo in searcher.Get())
            {
                using (mo)
                {
                    var mode = mo["AMRunningMode"] as string ?? "Unknown";
                    var rtp = mo["RealTimeProtectionEnabled"] as bool? ?? false;
                    var av = mo["AntivirusEnabled"] as bool? ?? false;
                    var sig = mo["AntivirusSignatureVersion"] as string;
                    DateTimeOffset? sigDate = null;
                    if (mo["AntivirusSignatureLastUpdated"] is DateTime dt)
                        sigDate = new DateTimeOffset(dt.ToUniversalTime(), TimeSpan.Zero);
                    return new DefenderStatus(mode, rtp, av, string.IsNullOrWhiteSpace(sig) ? null : sig, sigDate);
                }
            }
        }
        catch (Exception ex) when (ex is ManagementException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
        }
        return null;
    }
}
```

> 注意：WMI 返回的 `AntivirusSignatureLastUpdated` 可能是 CIM DATETIME 字符串而不是 DateTime。实现时若 `mo["AntivirusSignatureLastUpdated"]` 是字符串，用 `ManagementDateTimeConverter.ToDateTime(s)` 转换；两种情况都要兜住（转换失败则 sigDate 保持 null）。实现者按本机实际返回类型处理，并在报告里说明本机返回的是哪种。

- [ ] **Step 5: 运行测试，确认通过**

Run: `dotnet test tests/UpdateHelper.Core.Tests --filter RealSecurityProbeTests`
Expected: 3 个全部通过（本机能读到 Defender）

- [ ] **Step 6: 跑全部核心测试并提交**

Run: `dotnet test tests/UpdateHelper.Core.Tests`
Expected: 全部通过

```bash
git add src/UpdateHelper.Core/UpdateHelper.Core.csproj src/UpdateHelper.Core/Security/RealSecurityProbe.cs tests/UpdateHelper.Core.Tests/RealSecurityProbeTests.cs
git commit -m "feat(core): 真实只读安全探针——CIM 查 SecurityCenter2 和 Defender 状态，失败不抛"
```

---

### Task 3: 安全页界面

**Files:**
- Modify: `src/UpdateHelper.Presentation/IAppBackend.cs`
- Create: `src/UpdateHelper.Presentation/ViewModels/SecurityViewModel.cs`
- Modify: `tests/UpdateHelper.Presentation.Tests/FakeBackend.cs`
- Test: `tests/UpdateHelper.Presentation.Tests/SecurityViewModelTests.cs`
- Modify: `src/UpdateHelper.App/RealBackend.cs`
- Modify: `src/UpdateHelper.App/AppHost.cs`
- Create: `src/UpdateHelper.App/Pages/SecurityPage.xaml`、`SecurityPage.xaml.cs`
- Modify: `src/UpdateHelper.App/MainWindow.xaml`（导航项）、`App.xaml.cs`（PageTypes）

**Interfaces:**
- Consumes: Task 1~2 的 `SecurityStatus`、`SecurityJudge`、`RealSecurityProbe`
- Produces:
  - `IAppBackend` 加 `SecurityStatus GetSecurityStatus();`
  - `SecurityViewModel`：`Headline`、`Detail`、`StateText`（受保护/未开启/未检测到/未知）、`StateColor`（绿/黄/红/灰的十六进制）、异步 `RefreshAsync()`（后台线程查）、`OpenSecurityCenterCommand`（App 里实现为打开 `windowsdefender://`）

- [ ] **Step 1: 写失败的测试**

```csharp
using UpdateHelper.Core.Security;
using UpdateHelper.Presentation.ViewModels;

namespace UpdateHelper.Presentation.Tests;

public sealed class SecurityViewModelTests
{
    private static SecurityViewModel Vm(SecurityStatus s)
    {
        var backend = new FakeBackend { OnGetSecurityStatus = () => s };
        return new SecurityViewModel(backend);
    }

    [Fact]
    public async Task Protected_shows_green()
    {
        var vm = Vm(new SecurityStatus(AntivirusState.Protected, "Windows Defender", "Windows Defender 正在保护", "病毒库更新于 2026-10-09"));
        await vm.RefreshAsync();
        Assert.Equal("Windows Defender 正在保护", vm.Headline);
        Assert.Equal("已保护", vm.StateText);
        Assert.Equal("#3FA34D", vm.StateColor);
    }

    [Fact]
    public async Task Realtime_off_shows_red()
    {
        var vm = Vm(new SecurityStatus(AntivirusState.RealTimeOff, "Windows Defender", "实时保护未开启", "……"));
        await vm.RefreshAsync();
        Assert.Equal("未开启", vm.StateText);
        Assert.Equal("#D9534F", vm.StateColor);
    }

    [Fact]
    public async Task Not_detected_shows_red()
    {
        var vm = Vm(new SecurityStatus(AntivirusState.NotDetected, "", "没有检测到杀毒软件", "……"));
        await vm.RefreshAsync();
        Assert.Equal("未检测到", vm.StateText);
    }

    [Fact]
    public async Task Unknown_shows_gray()
    {
        var vm = Vm(new SecurityStatus(AntivirusState.Unknown, "Windows Defender", "无法读取 Windows Defender 状态", "……"));
        await vm.RefreshAsync();
        Assert.Equal("未知", vm.StateText);
        Assert.Equal("#808080", vm.StateColor);
    }

    [Fact]
    public void Before_refresh_shows_querying()
    {
        var vm = Vm(new SecurityStatus(AntivirusState.Protected, "x", "x", "x"));
        Assert.Contains("正在", vm.Headline);
    }
}
```

`tests/UpdateHelper.Presentation.Tests/FakeBackend.cs` 加（文件头 `using UpdateHelper.Core.Security;`）：

```csharp
    public Func<SecurityStatus> OnGetSecurityStatus { get; set; } =
        () => new SecurityStatus(AntivirusState.Protected, "Windows Defender", "Windows Defender 正在保护", "");
    public SecurityStatus GetSecurityStatus() => OnGetSecurityStatus();
```

- [ ] **Step 2: 运行测试，确认失败**

Run: `dotnet test tests/UpdateHelper.Presentation.Tests --filter SecurityViewModelTests`
Expected: 编译失败

- [ ] **Step 3: 实现界面逻辑**

`src/UpdateHelper.Presentation/IAppBackend.cs` 接口里加（文件头加 `using UpdateHelper.Core.Security;`）：

```csharp
    /// <summary>查询当前的杀毒软件保护状态（只读）。</summary>
    SecurityStatus GetSecurityStatus();
```

`src/UpdateHelper.Presentation/ViewModels/SecurityViewModel.cs`：

```csharp
using CommunityToolkit.Mvvm.ComponentModel;
using UpdateHelper.Core.Security;

namespace UpdateHelper.Presentation.ViewModels;

/// <summary>安全页：显示杀毒软件保护状态。查询在后台线程，不卡界面。</summary>
public sealed partial class SecurityViewModel(IAppBackend backend) : ObservableObject
{
    private SecurityStatus? _status;

    public string Headline => _status?.Headline ?? "正在检查安全状态……";
    public string Detail => _status?.Detail ?? "";
    public string ProviderName => _status?.ProviderName ?? "";

    public string StateText => _status?.State switch
    {
        AntivirusState.Protected => "已保护",
        AntivirusState.RealTimeOff => "未开启",
        AntivirusState.NotDetected => "未检测到",
        AntivirusState.Unknown => "未知",
        _ => "",
    };

    public string StateColor => _status?.State switch
    {
        AntivirusState.Protected => "#3FA34D",
        AntivirusState.RealTimeOff => "#D9534F",
        AntivirusState.NotDetected => "#D9534F",
        _ => "#808080",
    };

    public async Task RefreshAsync()
    {
        var s = await Task.Run(backend.GetSecurityStatus);
        _status = s;
        OnPropertyChanged(nameof(Headline));
        OnPropertyChanged(nameof(Detail));
        OnPropertyChanged(nameof(ProviderName));
        OnPropertyChanged(nameof(StateText));
        OnPropertyChanged(nameof(StateColor));
    }
}
```

> `GetSecurityStatus` 的实现（RealBackend）要保证不抛异常；RefreshAsync 不加 try/catch 也应安全，但为稳妥，RealBackend.GetSecurityStatus 内部兜底返回 Unknown。

- [ ] **Step 4: 运行测试，确认通过**

Run: `dotnet test tests/UpdateHelper.Presentation.Tests`
Expected: 全部通过

- [ ] **Step 5: 接入 App**

`src/UpdateHelper.App/RealBackend.cs` 加（文件头 `using UpdateHelper.Core.Security;`）：

```csharp
    public SecurityStatus GetSecurityStatus()
    {
        try
        {
            var probe = new RealSecurityProbe();
            return SecurityJudge.Evaluate(probe.QueryRegistered(), probe.QueryDefender());
        }
        catch (Exception)
        {
            return new SecurityStatus(AntivirusState.Unknown, "", "无法读取安全状态", "可以在 Windows 安全中心查看");
        }
    }
```

`src/UpdateHelper.App/AppHost.cs`：加 `public static SecurityViewModel Security { get; private set; } = null!;`，`Initialize()` 末尾加 `Security = new SecurityViewModel(backend);`。

`src/UpdateHelper.App/Pages/SecurityPage.xaml`：一个页面，顶部大标题"安全"，一张卡片显示 `StateText`（按 `StateColor` 上色的小圆点/标签）+ `Headline` + `Detail` + `ProviderName`，一个 `ui:Button` "打开 Windows 安全中心"（Click 在 code-behind 里 `Process.Start(new ProcessStartInfo("windowsdefender://") { UseShellExecute = true })`，用 try/catch 兜住 Win32Exception），和一个"重新检查"按钮调用 `AppHost.Security.RefreshAsync()`。DataContext 绑 `AppHost.Security`。`Loaded` 时 `_ = AppHost.Security.RefreshAsync();`。参照现有 HomePage.xaml 的卡片写法和 `Foreground="{DynamicResource TextFillColorPrimaryBrush}"`。

`src/UpdateHelper.App/MainWindow.xaml`：在"后台项目"和"历史"之间加一个导航项：

```xml
                <ui:NavigationViewItem Content="安全" Icon="{ui:SymbolIcon ShieldCheckmark24}" TargetPageType="{x:Type pages:SecurityPage}" />
```

`src/UpdateHelper.App/App.xaml.cs` 的 `PageTypes` 字典加 `["security"] = typeof(Pages.SecurityPage),`。

- [ ] **Step 6: 构建 + 截图核对（控制者）**

Run:
```powershell
dotnet build src/UpdateHelper.App -c Debug -v q --nologo
```
Expected: 0 个错误。控制者启动程序到安全页，截图确认：状态标签有颜色、Headline/Detail 显示当前杀软状态（本机预期"实时保护未开启"红色）、有"打开 Windows 安全中心"按钮。读图确认。

- [ ] **Step 7: 全部测试 + 提交**

Run: `dotnet test`
Expected: 全部通过

```bash
git add src tests
git commit -m "feat(app): 安全页——显示杀毒软件保护状态，一键打开 Windows 安全中心"
```

---

## 自查（对照 spec 第 9 节）

- 通过 SecurityCenter2 查当前杀软 → Task 2 QueryRegistered。✅
- 是 Defender：显示状态（本期不在程序内触发扫描，改为"打开 Windows 安全中心"入口，范围已声明）→ Task 1/3。✅（扫描触发本期不做，已注明）
- 是其他杀软：显示"由 XX 保护"，不重复扫描 → Task 1 规则 1。✅
- 诚实处理"注册了但没运行" → Task 1 RealTimeOff（本机真实场景）。✅
- 只读、不需要管理员、失败不崩溃 → Task 2 探针兜底 + Task 3 RealBackend 兜底。✅
- 占位符扫描：无 TODO；每个代码步骤有完整代码。
- 类型一致：`SecurityStatus`、`SecurityJudge.Evaluate`、`ISecurityProbe`、`RealSecurityProbe`、`IAppBackend.GetSecurityStatus`、`SecurityViewModel` 在定义与使用处签名一致。
- Review Focus 五项都指到对应 Task 的测试。

## 计划结束后

- 整个分支交给全新审查员整体审查（重点：探针是否真的不抛、Defender 以真实状态为准、第三方优先、界面查询在后台线程）。
- 审查通过、测试全绿后合并 main、推送。
- 可并入下一个发版（0.4.0）或和后续功能攒一起，由控制者/用户定。
