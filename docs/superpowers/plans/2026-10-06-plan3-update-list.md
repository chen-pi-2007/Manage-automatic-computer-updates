# 计划 3：判断层 + winget，做出"更新列表" Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 向 winget 查询哪些已装软件有新版本，把结果挂到计划 1 的软件组上，并按 spec 第 5 节分成"低风险 / 需确认 / 不自动 / 不管"四档，每条都附上理由；ScanCli 能在本机打印这份更新列表。**本计划只查询，不安装任何东西。**

**Architecture:** 纯逻辑放在 Core 的 `Updates` 命名空间：`AppVersion`（能比较各种怪版本号）、`UpdateMatcher`（按注册表键名把 winget 结果对到软件组）、`UpdateJudge`（分档）。和 winget 的 COM 接口打交道的代码单独放进新项目 `UpdateHelper.Winget`，通过 Core 里的 `IUpdateSource` 接口接入——这样 Core 和测试项目不受 winget 包的平台要求影响，测试也不需要真的连 winget。

**Tech Stack:** C# / .NET 10、Microsoft.WindowsPackageManager.ComInterop 1.29.380、xUnit；学习版 Python 3。

**Spec:** `docs/superpowers/specs/2026-09-30-update-helper-design.md`（第 5 节判断层；第 3 节 winget 接口；构建顺序第 14 节第 3 步）

**前置：** 计划 1、2 已合并（`main` 上的 `443efea`）。

## 技术验证结论（2026-10-06 在开发者本机实验，代码已丢弃）

- NuGet 包 `Microsoft.WindowsPackageManager.ComInterop` 1.29.380 **要求目标框架为 `net8.0-windows10.0.26100.0` 或更高**，并且**必须指定 CPU 架构**（否则编译报错"AnyCPU"）。包里自带 C# 投影 dll 和一个本地 `Microsoft.Management.Deployment.dll`，**直接 `new PackageManager()` 即可**，不需要手动拷贝文件或写工厂类
- 包的 MSBuild 文件放在 `build/` 而不是 `buildTransitive/`，**所以每个要运行 winget 代码的可执行项目都必须直接引用这个包**，否则本地 dll 不会被复制到输出目录
- 查询方式：`OpenWindowsCatalog` + 本地已装软件做"组合目录"（`CompositeSearchBehavior.LocalCatalogs`），`FindPackages` 一次拿到全部已装软件。本机：254 个，其中 57 个 `IsUpdateAvailable`
- **COM 列表只能按下标遍历**：对 `Matches`、`ProductCodes` 用 `foreach` 会抛 `InvalidCastException: 不支持此接口`
- **每读一个属性都是一次跨进程调用**：254 个软件各读 5～6 个属性共耗时约 48 秒。所以先只读 `IsUpdateAvailable`，有更新的才读其他属性
- `InstalledVersion.ProductCodes` 是**小写的注册表卸载键名**（QQ → `qq`，Git → `git_is1`，WPS → `kingsoft office`，MSI 安装的是小写 GUID）→ 可以和计划 1 的 `UninstallEntry.KeyName` 忽略大小写精确对应
- 只存在于本机、winget 源里没有的软件，`Id` 形如 `ARP\User\X64\Kingsoft Office` 或 `MSIX\...`，要排除

## Global Constraints

- Core 和测试项目保持 `net10.0-windows`；**只有** `UpdateHelper.Winget` 和 `UpdateHelper.ScanCli` 改成 `net10.0-windows10.0.26100.0` + `win-x64`，并设 `SupportedOSPlatformVersion` 为 `10.0.17763.0`（spec：支持 Windows 10 1809 及以上）
- **不安装、不下载任何东西**：本计划不得调用 `InstallPackageAsync`、`DownloadPackageAsync` 或任何会改动系统的接口
- 判断要**宁可保守**：拿不准的一律归到"不自动"并写明原因（spec 第 1 节："把稳放在第一位"）
- 每条判断都要有一句中文理由，界面以后直接展示
- 观察期（spec 第 5 节）只在"托管自动更新"开启时生效，本计划没有自动更新，**不实现观察期**，留给计划 4
- 提交信息不带任何 AI 署名（用户全局规则）

## Review Focus

1. **怪版本号**（`< 3.10.8`、`> 1.8.10`、`2021.3.45f2c1`、`2026.07-1`、`v1.2`、`26.168.0830.0006`、空字符串、纯文字）→ 能比的正确比较，比不了的返回"无法比较"而不是崩溃或给出错误结论
2. **同名软件登记了多份**（微信 3.9 和 4.1 各一组）→ 这个软件的更新归到"不自动"，并说明原因
3. **winget 不可用**（精简版系统没装、服务坏了、连接失败）→ 返回明确的中文提示，扫描和规则部分照常显示
4. **winget 说有更新、但版本比较认为新版本不比现在新**（版本号格式不一致导致）→ 不列入更新
5. **一个 winget 包对不上任何软件组**（ProductCodes 为空或没有匹配的键名）→ 归到"不自动"，理由是"没有在扫描结果里找到对应的软件"

以上每条都在对应任务里有专门的测试（第 3 条在 Task 3 用假来源测"抛异常"，并在 Task 5 本机核对提示文字）。

---

## 文件结构

```
src/UpdateHelper.Core/Updates/
  AppVersion.cs              版本号解析和比较（纯逻辑）
  UpdateCandidate.cs         更新来源给出的一条"可更新"记录 + IUpdateSource 接口
  UpdateMatcher.cs           按 ProductCodes 对到软件组
  UpdateJudge.cs             分档：UpdateTier、JudgedUpdate、Judge 方法
  UpdateService.cs           调用来源 + 分档，来源出错转成中文警告
src/UpdateHelper.Winget/
  UpdateHelper.Winget.csproj 新项目（26100 + win-x64 + ComInterop 包）
  WingetUpdateSource.cs      通过 COM 查询 winget
  WingetUnavailableException.cs
src/UpdateHelper.ScanCli/
  UpdateHelper.ScanCli.csproj（修改）改框架、引用 Winget 项目和 ComInterop 包
  Program.cs                 （修改）加 --updates
tests/UpdateHelper.Core.Tests/
  AppVersionTests.cs
  UpdateMatcherTests.cs
  UpdateJudgeTests.cs
learning/python/02_版本号比较.py
learning/python/03_调用winget.py
learning/python/README.md    （修改）表格加两行
```

## 任务清单

- Task 1：AppVersion（版本号解析和比较）
- Task 2：UpdateCandidate、IUpdateSource、UpdateMatcher
- Task 3：UpdateJudge（分档和理由）+ UpdateService
- Task 4：UpdateHelper.Winget 项目（COM 查询）
- Task 5：ScanCli 接入 `--updates`，本机核对
- Task 6：Python 学习版 02、03

---

### Task 1：AppVersion（版本号解析和比较）

**Files:**
- Create: `src/UpdateHelper.Core/Updates/AppVersion.cs`
- Test: `tests/UpdateHelper.Core.Tests/AppVersionTests.cs`

**Interfaces:**
- Produces（命名空间 `UpdateHelper.Core.Updates`）：
  - `readonly record struct VersionPart(long Number, string Suffix)`
  - `sealed class AppVersion : IComparable<AppVersion>`
    - `static AppVersion? Parse(string? text)`：解析不了返回 null
    - `string Original`、`IReadOnlyList<VersionPart> Parts`、`bool IsApproximate`（原文带 `<` 或 `>`，表示 winget 也不确定确切版本）
    - `long Major`（第 1 段数字）、`long Minor`（第 2 段数字，没有为 0）
    - `int CompareTo(AppVersion? other)`

**解析规则：**
1. 去掉首尾空白；开头是 `<` 或 `>` 时记为 `IsApproximate` 并去掉；再去掉开头的 `v`/`V`
2. 按 `.`、`-`、`_`、`+` 切段，空段丢弃
3. 每段 = 开头的数字（`Number`）+ 剩下的文字（`Suffix`）。整段都是文字时 `Number = 0`
4. **第一段必须以数字开头**，否则整个版本号无法解析（比如 `abc`、`beta`）；数字超过 18 位也视为无法解析
5. 比较：逐段比较，较短的一方用 `(0, "")` 补齐；先比 `Number`，再比 `Suffix`——**没有后缀比有后缀大**（`1.0.0` > `1.0.0-beta`），都有后缀时按忽略大小写的字典序

- [ ] **Step 1: 写失败的测试** `tests/UpdateHelper.Core.Tests/AppVersionTests.cs`

```csharp
using UpdateHelper.Core.Updates;

namespace UpdateHelper.Core.Tests;

public class AppVersionTests
{
    [Theory]
    [InlineData("3.1.12", "3.1.41", -1)]                       // 网易云
    [InlineData("9.9.20.36330", "9.9.33.52230", -1)]           // QQ
    [InlineData("12.1.0.23125", "12.1.0.24000", -1)]           // WPS
    [InlineData("2022.10", "2026.07-1", -1)]                   // Anaconda
    [InlineData("7.0.12", "7.2.20", -1)]                       // VirtualBox
    [InlineData("2.50.1", "2.55.0.5", -1)]                     // Git
    [InlineData("1.2", "1.2.0", 0)]                            // 末尾补 0
    [InlineData("1.10", "1.9", 1)]                             // 按数字比，不按字符串
    [InlineData("26.168.0830.0006", "26.173.0906.0008", -1)]   // 带前导 0
    [InlineData("1.0.0", "1.0.0-beta", 1)]                     // 正式版比预览版新
    [InlineData("2021.3.45f2c1", "2021.3.45f1", 1)]            // Unity 的后缀
    [InlineData("v1.2.3", "1.2.3", 0)]
    [InlineData("3.3.3-c7", "3.21.3.65535", -1)]               // Unity Hub
    public void Compares(string a, string b, int expected)
        => Assert.Equal(expected, Math.Sign(AppVersion.Parse(a)!.CompareTo(AppVersion.Parse(b))));

    [Theory]
    [InlineData("< 3.10.8")]
    [InlineData("> 1.8.10")]
    public void Winget_range_versions_are_approximate(string text)   // Review Focus 1
    {
        var v = AppVersion.Parse(text)!;
        Assert.True(v.IsApproximate);
        Assert.Equal(text, v.Original);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("abc")]
    [InlineData("beta.1")]
    [InlineData("<")]
    [InlineData("1234567890123456789012.1")]   // 数字太长
    public void Garbage_is_not_parsed(string? text)                  // Review Focus 1
        => Assert.Null(AppVersion.Parse(text));

    [Theory]
    [InlineData("8.0.3310.9", 8, 0)]
    [InlineData("2026.07-1", 2026, 7)]
    [InlineData("13", 13, 0)]
    public void Major_and_minor(string text, long major, long minor)
    {
        var v = AppVersion.Parse(text)!;
        Assert.Equal(major, v.Major);
        Assert.Equal(minor, v.Minor);
    }

    [Fact]
    public void Compare_to_null_is_greater()
        => Assert.True(AppVersion.Parse("1.0")!.CompareTo(null) > 0);
}
```

- [ ] **Step 2: 运行，确认失败**

Run: `dotnet test --filter AppVersionTests`
Expected: 编译失败，找不到 `UpdateHelper.Core.Updates`

- [ ] **Step 3: 实现** `src/UpdateHelper.Core/Updates/AppVersion.cs`

```csharp
using System.Globalization;

namespace UpdateHelper.Core.Updates;

/// <summary>版本号中的一段：开头的数字 + 后面的文字（如 "45f2c1" → 45, "f2c1"）。</summary>
public readonly record struct VersionPart(long Number, string Suffix);

/// <summary>能比较各种软件版本号的解析结果。解析规则见计划 3 Task 1。</summary>
public sealed class AppVersion : IComparable<AppVersion>
{
    private static readonly char[] Separators = ['.', '-', '_', '+'];

    private AppVersion(string original, IReadOnlyList<VersionPart> parts, bool approximate)
    {
        Original = original;
        Parts = parts;
        IsApproximate = approximate;
    }

    public string Original { get; }
    public IReadOnlyList<VersionPart> Parts { get; }

    /// <summary>原文带 &lt; 或 &gt;：winget 只知道大概范围，不知道确切版本。</summary>
    public bool IsApproximate { get; }

    public long Major => Parts[0].Number;
    public long Minor => Parts.Count > 1 ? Parts[1].Number : 0;

    public static AppVersion? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var s = text.Trim();

        var approximate = false;
        if (s[0] is '<' or '>')
        {
            approximate = true;
            s = s[1..].Trim();
        }
        if (s.Length > 0 && s[0] is 'v' or 'V') s = s[1..];

        var parts = new List<VersionPart>();
        foreach (var segment in s.Split(Separators, StringSplitOptions.RemoveEmptyEntries))
        {
            var digits = 0;
            while (digits < segment.Length && char.IsAsciiDigit(segment[digits])) digits++;

            if (digits == 0)
            {
                if (parts.Count == 0) return null;          // 第一段必须以数字开头
                parts.Add(new VersionPart(0, segment));
                continue;
            }
            if (digits > 18) return null;                   // 太长，long 放不下
            var number = long.Parse(segment.AsSpan(0, digits), CultureInfo.InvariantCulture);
            parts.Add(new VersionPart(number, segment[digits..]));
        }

        return parts.Count == 0 ? null : new AppVersion(text.Trim(), parts, approximate);
    }

    public int CompareTo(AppVersion? other)
    {
        if (other is null) return 1;
        var n = Math.Max(Parts.Count, other.Parts.Count);
        for (var i = 0; i < n; i++)
        {
            var a = i < Parts.Count ? Parts[i] : new VersionPart(0, "");
            var b = i < other.Parts.Count ? other.Parts[i] : new VersionPart(0, "");

            var byNumber = a.Number.CompareTo(b.Number);
            if (byNumber != 0) return byNumber;

            var bySuffix = CompareSuffix(a.Suffix, b.Suffix);
            if (bySuffix != 0) return bySuffix;
        }
        return 0;
    }

    /// <summary>没有后缀的比有后缀的大（正式版 &gt; 预览版）；都有后缀时按字典序。</summary>
    private static int CompareSuffix(string a, string b)
    {
        if (a.Length == 0 && b.Length == 0) return 0;
        if (a.Length == 0) return 1;
        if (b.Length == 0) return -1;
        return string.Compare(a, b, StringComparison.OrdinalIgnoreCase);
    }

    public override string ToString() => Original;
}
```

- [ ] **Step 4: 运行，确认通过**

Run: `dotnet test --filter AppVersionTests`
Expected: 全部 PASS

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat(updates): 版本号解析与比较，兼容 winget 的范围写法和各种后缀"
```

### Task 2：UpdateCandidate、IUpdateSource、UpdateMatcher

**Files:**
- Create: `src/UpdateHelper.Core/Updates/UpdateCandidate.cs`
- Create: `src/UpdateHelper.Core/Updates/UpdateMatcher.cs`
- Test: `tests/UpdateHelper.Core.Tests/UpdateMatcherTests.cs`

**Interfaces:**
- Consumes: `SoftwareGroup`、`SoftwareGrouper.Group`（计划 1）；`Fakes.Entry`（计划 1 测试辅助）
- Produces（命名空间 `UpdateHelper.Core.Updates`）：
  - `sealed record UpdateCandidate(string PackageId, string Name, string? Publisher, string InstalledVersion, string AvailableVersion, IReadOnlyList<string> ProductCodes)`
  - `interface IUpdateSource { string Name { get; } IReadOnlyList<UpdateCandidate> GetAvailableUpdates(); }`——实现方遇到"来源不可用"时**抛异常**，由 Task 3 的 `UpdateService` 统一接住
  - `static class UpdateMatcher { static SoftwareGroup? FindGroup(UpdateCandidate candidate, IReadOnlyList<SoftwareGroup> groups); }`

**对应规则：** 候选的任意一个 `ProductCodes` 与某个组的主条目 `KeyName` 相同（忽略大小写）→ 这个组；没有就再看各组组件的 `KeyName`；都没有返回 null。

- [ ] **Step 1: 写失败的测试** `tests/UpdateHelper.Core.Tests/UpdateMatcherTests.cs`

```csharp
using UpdateHelper.Core.Grouping;
using UpdateHelper.Core.Updates;
using static UpdateHelper.Core.Tests.Fakes;

namespace UpdateHelper.Core.Tests;

public class UpdateMatcherTests
{
    private static UpdateCandidate Candidate(params string[] codes)
        => new("Some.Id", "Some", null, "1.0", "2.0", codes);

    private static readonly IReadOnlyList<SoftwareGroup> Groups = SoftwareGrouper.Group(
    [
        Entry("QQ", "腾讯科技(深圳)有限公司", key: "QQ"),
        Entry("Git", "The Git Development Community", key: "Git_is1"),
        Entry("Python 3.10.2 (64-bit)", "Python Software Foundation", key: "{21b42743-c8f9-49d7-b8b6-b5855317c7ed}"),
        Entry("Python 3.10.2 Core Interpreter (64-bit)", "Python Software Foundation", hidden: true,
              key: "{C60FD5AC-367D-4E3A-A975-F157502AC30A}"),
    ], []).Groups;

    [Theory]
    [InlineData("qq", "QQ")]            // winget 给的是小写
    [InlineData("git_is1", "Git")]
    public void Matches_primary_key_ignoring_case(string code, string expected)
        => Assert.Equal(expected, UpdateMatcher.FindGroup(Candidate(code), Groups)?.Name);

    [Fact]
    public void Matches_component_key()   // Python 的 winget 包对应的是隐藏的核心组件
        => Assert.Equal("Python 3.10.2 (64-bit)",
            UpdateMatcher.FindGroup(Candidate("{c60fd5ac-367d-4e3a-a975-f157502ac30a}"), Groups)?.Name);

    [Fact]
    public void Primary_match_wins_over_component_match()
        => Assert.Equal("QQ", UpdateMatcher.FindGroup(Candidate("{c60fd5ac-367d-4e3a-a975-f157502ac30a}", "qq"), Groups)?.Name);

    [Fact]
    public void No_product_codes_returns_null()
        => Assert.Null(UpdateMatcher.FindGroup(Candidate(), Groups));

    [Fact]
    public void Unknown_product_code_returns_null()   // Review Focus 5
        => Assert.Null(UpdateMatcher.FindGroup(Candidate("not-installed"), Groups));
}
```

- [ ] **Step 2: 运行，确认失败**

Run: `dotnet test --filter UpdateMatcherTests`
Expected: 编译失败，找不到 `UpdateCandidate`

- [ ] **Step 3: 数据和接口** `src/UpdateHelper.Core/Updates/UpdateCandidate.cs`

```csharp
namespace UpdateHelper.Core.Updates;

/// <summary>更新来源报告的一条"这个软件有新版本"。</summary>
/// <param name="PackageId">来源里的包 id，如 Tencent.QQ.NT。</param>
/// <param name="ProductCodes">已装版本对应的注册表卸载键名（winget 给的是小写），用来对到软件组。</param>
public sealed record UpdateCandidate(
    string PackageId,
    string Name,
    string? Publisher,
    string InstalledVersion,
    string AvailableVersion,
    IReadOnlyList<string> ProductCodes);

/// <summary>能报告"哪些已装软件有新版本"的来源。来源不可用时抛异常。</summary>
public interface IUpdateSource
{
    /// <summary>来源名称，用于界面和日志，如 "winget"。</summary>
    string Name { get; }

    IReadOnlyList<UpdateCandidate> GetAvailableUpdates();
}
```

- [ ] **Step 4: 对应逻辑** `src/UpdateHelper.Core/Updates/UpdateMatcher.cs`

```csharp
using UpdateHelper.Core.Grouping;

namespace UpdateHelper.Core.Updates;

/// <summary>按注册表卸载键名，把更新候选对到计划 1 扫描出的软件组。纯逻辑。</summary>
public static class UpdateMatcher
{
    public static SoftwareGroup? FindGroup(UpdateCandidate candidate, IReadOnlyList<SoftwareGroup> groups)
    {
        if (candidate.ProductCodes.Count == 0) return null;
        var codes = candidate.ProductCodes.ToHashSet(StringComparer.OrdinalIgnoreCase);

        return groups.FirstOrDefault(g => g.Primary is not null && codes.Contains(g.Primary.KeyName))
            ?? groups.FirstOrDefault(g => g.Components.Any(c => codes.Contains(c.KeyName)));
    }
}
```

- [ ] **Step 5: 运行，确认通过**

Run: `dotnet test --filter UpdateMatcherTests`
Expected: 全部 PASS

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "feat(updates): 更新来源接口，按注册表键名把候选对到软件组"
```

### Task 3：UpdateJudge（分档和理由）+ UpdateService

**Files:**
- Create: `src/UpdateHelper.Core/Updates/UpdateJudge.cs`
- Create: `src/UpdateHelper.Core/Updates/UpdateService.cs`
- Test: `tests/UpdateHelper.Core.Tests/UpdateJudgeTests.cs`

**Interfaces:**
- Consumes: `AppVersion`（Task 1）；`UpdateCandidate`、`IUpdateSource`、`UpdateMatcher.FindGroup`（Task 2）；`SoftwareGroup`、`SoftwareCategory`、`ScanResult`、`SoftwareGrouper`（计划 1）；`RuleSet`、`Rule`、`UpdateRule`、`RiskLevel`、`RuleApplier`（计划 2）；`Fakes.Entry`
- Produces（命名空间 `UpdateHelper.Core.Updates`）：
  - `enum UpdateTier { Low, Careful, NeverAuto, Ignored }`
  - `sealed record JudgedUpdate(UpdateCandidate Candidate, SoftwareGroup? Group, UpdateTier Tier, string Reason)`
  - `static class UpdateJudge { static IReadOnlyList<JudgedUpdate> Judge(IReadOnlyList<UpdateCandidate> candidates, IReadOnlyList<SoftwareGroup> groups, RuleSet rules); }`——结果按档位、再按名称排序
  - `sealed record UpdateReport(IReadOnlyList<JudgedUpdate> Updates, string? Warning)`
  - `static class UpdateService { static UpdateReport Check(IUpdateSource source, ScanResult scan, RuleSet rules); }`——来源抛异常时返回空列表和中文警告，不向外抛

**判断顺序（先命中先算，对应 spec 第 5 节；"宁可保守"）：**

| # | 条件 | 档位 | 理由（示例） |
|---|---|---|---|
| 0 | 两个版本号都能解析、已装版本不是范围写法，而新版本 ≤ 已装版本 | **不列入结果** | —（Review Focus 4） |
| 1 | 对不上软件组 | 不自动 | 没有在扫描结果里找到对应的软件，无法确认装的是哪一个 |
| 2 | 分类是游戏 | 不管 | 游戏由 Steam 等平台负责更新 |
| 3 | 分类是系统/厂商组件合集 | 不自动 | 这是系统或厂商组件，由对应的软件负责更新 |
| 4 | 还有别的组和它同名 | 不自动 | 电脑上登记了多个"微信"（3.9.12.51、4.1.15.13），无法确定该更新哪一个 |
| 5 | 分类是运行库 | 不自动 | 运行库通常多个版本并存，由需要它的软件负责安装 |
| 6 | 任一版本号解析不了，或已装版本是范围写法 | 不自动 | 版本号无法准确比较（< 3.10.8 → 3.14.7） |
| 7 | 命中的规则写了 `risk: never-auto` | 不自动 | 规则标记为永不自动更新 |
| 8 | 命中的规则写了 `risk: careful` | 需确认 | 规则标记为需要确认后再更新 |
| 9 | 主版本号变了 | 需确认 | 大版本变化（2022 → 2026），可能有较大改动 |
| 10 | 分类是驱动 | 需确认 | 驱动更新可能影响硬件，建议确认后再装 |
| 11 | 开发工具，且次版本号变了 | 需确认 | 开发工具跨了小版本（7.0 → 7.2），可能影响开发环境 |
| 12 | 普通软件，且次版本号跨了 10 个以上 | 需确认 | 一次跳过了很多版本（4.46 → 4.93） |
| 13 | 其他 | 低风险 | 小版本更新 |

第 11、12 条是 spec"跨度超过阈值"的内置默认值：开发工具对环境敏感，从严；国产普通软件小版本号跳得快（百度网盘 8.2 → 8.8），从宽。以后可以由规则覆盖。

- [ ] **Step 1: 写失败的测试** `tests/UpdateHelper.Core.Tests/UpdateJudgeTests.cs`

```csharp
using UpdateHelper.Core.Grouping;
using UpdateHelper.Core.Rules;
using UpdateHelper.Core.Scanning;
using UpdateHelper.Core.Updates;
using static UpdateHelper.Core.Tests.Fakes;

namespace UpdateHelper.Core.Tests;

public class UpdateJudgeTests
{
    private static UpdateCandidate C(string key, string installed, string available, string? name = null)
        => new($"Pkg.{key}", name ?? key, null, installed, available, [key.ToLowerInvariant()]);

    private static JudgedUpdate JudgeOne(UninstallEntry entry, UpdateCandidate candidate, RuleSet? rules = null)
    {
        var groups = SoftwareGrouper.Group([entry], []).Groups;
        if (rules is not null) RuleApplier.Apply(new ScanResult(groups, [], 1), rules);
        return Assert.Single(UpdateJudge.Judge([candidate], groups, rules ?? RuleSet.Empty));
    }

    [Fact]
    public void Patch_update_of_application_is_low_risk()
    {
        var j = JudgeOne(Entry("QQ", "腾讯科技(深圳)有限公司", "9.9.20.36330", key: "QQ"), C("QQ", "9.9.20.36330", "9.9.33.52230"));
        Assert.Equal(UpdateTier.Low, j.Tier);
        Assert.Equal("QQ", j.Group!.Name);
        Assert.Equal("小版本更新", j.Reason);
    }

    [Fact]
    public void Application_minor_jump_below_ten_is_low_risk()   // 百度网盘 8.2 → 8.8
        => Assert.Equal(UpdateTier.Low, JudgeOne(Entry("百度网盘", "北京度友科技有限公司", key: "BaiduNetdisk"),
            C("BaiduNetdisk", "8.2.7", "8.8.8")).Tier);

    [Fact]
    public void Application_minor_jump_of_ten_or_more_needs_confirmation()
    {
        var j = JudgeOne(Entry("Foo", "Foo Inc", key: "Foo"), C("Foo", "4.46.0", "4.93.0"));
        Assert.Equal(UpdateTier.Careful, j.Tier);
        Assert.Contains("4.46 → 4.93", j.Reason);
    }

    [Fact]
    public void Major_change_needs_confirmation()   // Anaconda 2022 → 2026
    {
        var j = JudgeOne(Entry("Anaconda3 2022.10 (Python 3.9.13 64-bit)", "Anaconda, Inc.", key: "Anaconda3"),
            C("Anaconda3", "2022.10", "2026.07-1"));
        Assert.Equal(UpdateTier.Careful, j.Tier);
        Assert.Contains("2022 → 2026", j.Reason);
    }

    [Fact]
    public void Devtool_minor_change_needs_confirmation()   // VirtualBox 7.0 → 7.2
    {
        var j = JudgeOne(Entry("Oracle VM VirtualBox 7.0.12", "Oracle and/or its affiliates", key: "VBox"),
            C("VBox", "7.0.12", "7.2.20"));
        Assert.Equal(UpdateTier.Careful, j.Tier);
        Assert.Contains("7.0 → 7.2", j.Reason);
    }

    [Fact]
    public void Devtool_patch_is_low_risk()   // Python 3.10.2 → 3.10.11
        => Assert.Equal(UpdateTier.Low, JudgeOne(Entry("Python 3.10.2 (64-bit)", "Python Software Foundation", key: "Py"),
            C("Py", "3.10.2", "3.10.11")).Tier);

    [Fact]
    public void Driver_needs_confirmation()
        => Assert.Equal(UpdateTier.Careful, JudgeOne(Entry("NVIDIA 图形驱动程序 610.62", "NVIDIA Corporation", key: "Drv"),
            C("Drv", "610.62", "610.80")).Tier);

    [Fact]
    public void Game_is_ignored()
        => Assert.Equal(UpdateTier.Ignored, JudgeOne(Entry("Counter-Strike 2", "Valve", key: "Steam App 730"),
            C("Steam App 730", "1.0", "1.1")).Tier);

    [Fact]
    public void Runtime_is_never_auto()
        => Assert.Equal(UpdateTier.NeverAuto, JudgeOne(
            Entry("Microsoft Visual C++ v14 Redistributable (x64) - 14.50.35719", "Microsoft Corporation", key: "VC"),
            C("VC", "14.50.35719.0", "14.51.36247.0")).Tier);

    [Fact]
    public void Approximate_installed_version_is_never_auto()   // Review Focus 1
    {
        var j = JudgeOne(Entry("Python Launcher", "Python Software Foundation", key: "PyLauncher"),
            C("PyLauncher", "< 3.10.8", "3.14.7"));
        Assert.Equal(UpdateTier.NeverAuto, j.Tier);
        Assert.Contains("< 3.10.8", j.Reason);
    }

    [Fact]
    public void Unparseable_version_is_never_auto()   // Review Focus 1
        => Assert.Equal(UpdateTier.NeverAuto, JudgeOne(Entry("Foo", "Foo Inc", key: "Foo"), C("Foo", "unknown", "2.0")).Tier);

    [Fact]
    public void Unmatched_candidate_is_never_auto()   // Review Focus 5
    {
        var j = JudgeOne(Entry("Foo", "Foo Inc", key: "Foo"), C("Other", "1.0", "1.1"));
        Assert.Equal(UpdateTier.NeverAuto, j.Tier);
        Assert.Null(j.Group);
        Assert.Contains("没有在扫描结果里找到", j.Reason);
    }

    [Fact]
    public void Not_actually_newer_is_dropped()   // Review Focus 4
    {
        var groups = SoftwareGrouper.Group([Entry("Foo", "Foo Inc", key: "Foo")], []).Groups;
        Assert.Empty(UpdateJudge.Judge([C("Foo", "2.0", "1.9")], groups, RuleSet.Empty));
        Assert.Empty(UpdateJudge.Judge([C("Foo", "2.0", "2.0.0")], groups, RuleSet.Empty));
    }

    [Fact]
    public void Same_name_registered_twice_is_never_auto()   // Review Focus 2：微信 3.9 + 4.1
    {
        var groups = SoftwareGrouper.Group([
            Entry("微信", "腾讯科技(深圳)有限公司", "3.9.12.51", key: "WeChat"),
            Entry("微信", "腾讯科技(深圳)有限公司", "4.1.15.13", key: "Weixin")], []).Groups;

        var results = UpdateJudge.Judge([C("WeChat", "3.9.12.51", "3.9.12.57", "微信"), C("Weixin", "4.1.15.13", "4.1.16.2", "微信")],
            groups, RuleSet.Empty);

        Assert.Equal(2, results.Count);
        Assert.All(results, r => Assert.Equal(UpdateTier.NeverAuto, r.Tier));
        Assert.All(results, r => Assert.Contains("3.9.12.51、4.1.15.13", r.Reason));
    }

    private static RuleSet RuleWithRisk(string displayName, RiskLevel risk) => new(
        [new Rule("r", "r", new RuleMatch(displayName, null), null, new UpdateRule(null, null, null, null, risk), [], [])],
        [], []);

    [Theory]
    [InlineData(RiskLevel.Careful, UpdateTier.Careful)]
    [InlineData(RiskLevel.NeverAuto, UpdateTier.NeverAuto)]
    public void Rule_risk_overrides_builtin_judgement(RiskLevel risk, UpdateTier expected)
    {
        var j = JudgeOne(Entry("QQ", "Tencent", key: "QQ"), C("QQ", "9.9.20", "9.9.33"), RuleWithRisk("QQ", risk));
        Assert.Equal(expected, j.Tier);
        Assert.Contains("规则", j.Reason);
    }

    [Fact]
    public void Results_are_ordered_by_tier_then_name()
    {
        var groups = SoftwareGrouper.Group([
            Entry("Zeta", "Z", key: "Z"),
            Entry("Alpha", "A", key: "A"),
            Entry("Anaconda3", "Anaconda, Inc.", key: "Ana")], []).Groups;

        var results = UpdateJudge.Judge([C("Ana", "2022.10", "2026.07"), C("Z", "1.0", "1.1"), C("A", "1.0", "1.1")],
            groups, RuleSet.Empty);

        Assert.Equal(new[] { "Alpha", "Zeta", "Anaconda3" }, results.Select(r => r.Group!.Name));
    }

    private sealed class FakeSource(Func<IReadOnlyList<UpdateCandidate>> get) : IUpdateSource
    {
        public string Name => "fake";
        public IReadOnlyList<UpdateCandidate> GetAvailableUpdates() => get();
    }

    [Fact]
    public void Service_returns_judged_updates()
    {
        var scan = SoftwareGrouper.Group([Entry("QQ", "Tencent", key: "QQ")], []);
        var report = UpdateService.Check(new FakeSource(() => [C("QQ", "1.0", "1.1")]), scan, RuleSet.Empty);
        Assert.Null(report.Warning);
        Assert.Single(report.Updates);
    }

    [Fact]
    public void Service_turns_source_failure_into_warning()   // Review Focus 3
    {
        var scan = SoftwareGrouper.Group([], []);
        var report = UpdateService.Check(
            new FakeSource(() => throw new InvalidOperationException("没有找到 winget")), scan, RuleSet.Empty);

        Assert.Empty(report.Updates);
        Assert.Contains("没有找到 winget", report.Warning);
        Assert.Contains("fake", report.Warning);
    }
}
```

- [ ] **Step 2: 运行，确认失败**

Run: `dotnet test --filter UpdateJudgeTests`
Expected: 编译失败，找不到 `UpdateJudge`、`UpdateTier`

- [ ] **Step 3: 判断逻辑** `src/UpdateHelper.Core/Updates/UpdateJudge.cs`

```csharp
using UpdateHelper.Core.Grouping;
using UpdateHelper.Core.Rules;

namespace UpdateHelper.Core.Updates;

/// <summary>更新的档位（spec 第 5 节）。</summary>
public enum UpdateTier
{
    /// <summary>低风险：分级自动模式下空闲时自动安装</summary>
    Low,
    /// <summary>需确认：只提醒</summary>
    Careful,
    /// <summary>不自动：任何模式都不自动安装，并显示原因</summary>
    NeverAuto,
    /// <summary>不管：不出现在更新列表里（游戏等）</summary>
    Ignored,
}

/// <summary>一条判断好的更新：候选、对应的软件组（可能对不上）、档位、给人看的理由。</summary>
public sealed record JudgedUpdate(UpdateCandidate Candidate, SoftwareGroup? Group, UpdateTier Tier, string Reason);

/// <summary>给更新候选分档。纯逻辑，规则见计划 3 Task 3 的判断顺序表。</summary>
public static class UpdateJudge
{
    private const int ApplicationMinorJumpThreshold = 10;

    public static IReadOnlyList<JudgedUpdate> Judge(
        IReadOnlyList<UpdateCandidate> candidates, IReadOnlyList<SoftwareGroup> groups, RuleSet rules)
    {
        var ruleById = rules.Rules.ToDictionary(r => r.Id, StringComparer.Ordinal);
        var results = new List<JudgedUpdate>();

        foreach (var c in candidates)
        {
            var installed = AppVersion.Parse(c.InstalledVersion);
            var available = AppVersion.Parse(c.AvailableVersion);

            // 0. winget 说有更新，但新版本其实不比现在新
            if (installed is { IsApproximate: false } && available is not null && available.CompareTo(installed) <= 0)
                continue;

            var group = UpdateMatcher.FindGroup(c, groups);
            var (tier, reason) = Decide(c, group, installed, available, groups, ruleById);
            results.Add(new JudgedUpdate(c, group, tier, reason));
        }

        return results
            .OrderBy(r => r.Tier)
            .ThenBy(r => r.Group?.Name ?? r.Candidate.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    private static (UpdateTier, string) Decide(
        UpdateCandidate c, SoftwareGroup? group, AppVersion? installed, AppVersion? available,
        IReadOnlyList<SoftwareGroup> groups, IReadOnlyDictionary<string, Rule> ruleById)
    {
        if (group is null)
            return (UpdateTier.NeverAuto, "没有在扫描结果里找到对应的软件，无法确认装的是哪一个");

        switch (group.Category)
        {
            case SoftwareCategory.Game:
                return (UpdateTier.Ignored, "游戏由 Steam 等平台负责更新");
            case SoftwareCategory.SystemComponent:
                return (UpdateTier.NeverAuto, "这是系统或厂商组件，由对应的软件负责更新");
        }

        var sameName = groups.Where(g => g.Primary is not null
                                         && string.Equals(g.Name, group.Name, StringComparison.OrdinalIgnoreCase))
                             .ToList();
        if (sameName.Count > 1)
        {
            var versions = string.Join("、", sameName.Select(g => g.Version ?? "未知版本").Order(StringComparer.Ordinal));
            return (UpdateTier.NeverAuto, $"电脑上登记了多个\"{group.Name}\"（{versions}），无法确定该更新哪一个");
        }

        if (group.Category == SoftwareCategory.Runtime)
            return (UpdateTier.NeverAuto, "运行库通常多个版本并存，由需要它的软件负责安装");

        if (installed is null || available is null || installed.IsApproximate)
            return (UpdateTier.NeverAuto, $"版本号无法准确比较（{c.InstalledVersion} → {c.AvailableVersion}）");

        var risk = group.RuleId is not null && ruleById.TryGetValue(group.RuleId, out var rule) ? rule.Update?.Risk : null;
        if (risk == RiskLevel.NeverAuto) return (UpdateTier.NeverAuto, "规则标记为永不自动更新");
        if (risk == RiskLevel.Careful) return (UpdateTier.Careful, "规则标记为需要确认后再更新");

        if (available.Major != installed.Major)
            return (UpdateTier.Careful, $"大版本变化（{installed.Major} → {available.Major}），可能有较大改动");

        if (group.Category == SoftwareCategory.Driver)
            return (UpdateTier.Careful, "驱动更新可能影响硬件，建议确认后再装");

        var minorText = $"{installed.Major}.{installed.Minor} → {available.Major}.{available.Minor}";
        if (group.Category == SoftwareCategory.DevTool && available.Minor != installed.Minor)
            return (UpdateTier.Careful, $"开发工具跨了小版本（{minorText}），可能影响开发环境");

        if (available.Minor - installed.Minor >= ApplicationMinorJumpThreshold)
            return (UpdateTier.Careful, $"一次跳过了很多版本（{minorText}）");

        return (UpdateTier.Low, "小版本更新");
    }
}
```

- [ ] **Step 4: 来源调用** `src/UpdateHelper.Core/Updates/UpdateService.cs`

```csharp
using UpdateHelper.Core.Grouping;
using UpdateHelper.Core.Rules;

namespace UpdateHelper.Core.Updates;

/// <summary>判断好的更新列表，以及来源不可用时的提示。</summary>
public sealed record UpdateReport(IReadOnlyList<JudgedUpdate> Updates, string? Warning);

/// <summary>向来源要候选并分档。来源出任何问题都转成中文提示，不向外抛异常。</summary>
public static class UpdateService
{
    public static UpdateReport Check(IUpdateSource source, ScanResult scan, RuleSet rules)
    {
        IReadOnlyList<UpdateCandidate> candidates;
        try
        {
            candidates = source.GetAvailableUpdates();
        }
        catch (Exception ex)
        {
            return new UpdateReport([], $"无法从 {source.Name} 获取更新信息：{ex.Message}");
        }

        return new UpdateReport(UpdateJudge.Judge(candidates, scan.Groups, rules), null);
    }
}
```

- [ ] **Step 5: 运行，确认通过**

Run: `dotnet test --filter UpdateJudgeTests`
Expected: 全部 PASS

- [ ] **Step 6: 全量测试**

Run: `dotnet test`
Expected: 全部 PASS，0 警告

- [ ] **Step 7: Commit**

```bash
git add -A
git commit -m "feat(updates): 判断层——按 spec 第 5 节分四档并给出理由"
```

### Task 4：UpdateHelper.Winget 项目（COM 查询）

COM 调用依赖本机的 winget 服务，没法写成单元测试；这个任务的交付物是"能编译、输出目录里有本地 dll"，真实查询在 Task 5 本机核对。代码写法全部来自本计划开头的技术验证。

**Files:**
- Create: `src/UpdateHelper.Winget/UpdateHelper.Winget.csproj`
- Create: `src/UpdateHelper.Winget/WingetUnavailableException.cs`
- Create: `src/UpdateHelper.Winget/WingetUpdateSource.cs`
- Modify: `UpdateHelper.slnx`（加入新项目）

**Interfaces:**
- Consumes: `IUpdateSource`、`UpdateCandidate`（Task 2）
- Produces（命名空间 `UpdateHelper.Winget`）：
  - `sealed class WingetUpdateSource : IUpdateSource`（`Name` 为 `"winget"`）
  - `sealed class WingetUnavailableException : Exception`——winget 不可用、连接失败、查询失败时抛出，`Message` 是给用户看的中文

- [ ] **Step 1: 建项目**

Run:
```bash
dotnet new classlib -o src/UpdateHelper.Winget -n UpdateHelper.Winget
rm src/UpdateHelper.Winget/Class1.cs
dotnet sln add src/UpdateHelper.Winget
```

然后把 `src/UpdateHelper.Winget/UpdateHelper.Winget.csproj` 整个替换为：

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <!-- winget 的 COM 包要求带 Windows SDK 版本号的目标框架，并且必须指定 CPU 架构（见计划开头的技术验证） -->
  <PropertyGroup>
    <TargetFramework>net10.0-windows10.0.26100.0</TargetFramework>
    <SupportedOSPlatformVersion>10.0.17763.0</SupportedOSPlatformVersion>
    <RuntimeIdentifier>win-x64</RuntimeIdentifier>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Microsoft.WindowsPackageManager.ComInterop" Version="1.29.380" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\UpdateHelper.Core\UpdateHelper.Core.csproj" />
  </ItemGroup>

</Project>
```

（`Directory.Build.props` 里的 `TargetFramework` 会被这里的值覆盖，因为项目文件里的设置在它之后生效。）

- [ ] **Step 2: 异常类型** `src/UpdateHelper.Winget/WingetUnavailableException.cs`

```csharp
namespace UpdateHelper.Winget;

/// <summary>winget 不可用（没装、服务坏了、连不上源、查询失败）。Message 是给用户看的中文说明。</summary>
public sealed class WingetUnavailableException(string message, Exception? inner = null) : Exception(message, inner);
```

- [ ] **Step 3: 查询实现** `src/UpdateHelper.Winget/WingetUpdateSource.cs`

```csharp
using System.Runtime.InteropServices;
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
        PackageManager manager;
        try
        {
            manager = new PackageManager();
        }
        catch (Exception ex) when (ex is COMException or TypeInitializationException or DllNotFoundException
                                       or FileNotFoundException or InvalidCastException)
        {
            throw new WingetUnavailableException(
                "没有找到可用的 winget（Windows 程序包管理器）。可以在微软商店安装或更新“应用安装程序”后再试。", ex);
        }

        var options = new CreateCompositePackageCatalogOptions();
        options.Catalogs.Add(manager.GetPredefinedPackageCatalog(PredefinedPackageCatalog.OpenWindowsCatalog));
        options.CompositeSearchBehavior = CompositeSearchBehavior.LocalCatalogs;

        var connect = manager.CreateCompositePackageCatalog(options).Connect();
        if (connect.Status != ConnectResultStatus.Ok)
            throw new WingetUnavailableException($"无法连接 winget 软件源（{connect.Status}），请检查网络后重试。");

        var found = connect.PackageCatalog.FindPackages(new FindPackagesOptions());
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
```

注：以管理员身份运行时需要 winget 的"管理员模式"入口（spec 第 3 节），安装功能（计划 4）才会在管理员进程里调用它；本计划的查询只在普通权限下运行。

- [ ] **Step 4: 编译**

Run: `dotnet build`
Expected: 0 警告 0 错误

Run: `ls src/UpdateHelper.Winget/bin/Debug/net10.0-windows10.0.26100.0/win-x64/ | grep -i deployment`
Expected: 列出 `Microsoft.Management.Deployment.dll`、`Microsoft.Management.Deployment.winmd`、`Microsoft.Management.Deployment.CsWinRTProjection.dll`

- [ ] **Step 5: 全量测试仍通过**

Run: `dotnet test`
Expected: 全部 PASS（测试项目没有引用 Winget 项目，不受影响）

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "feat(winget): 通过官方 COM 接口查询可用更新（独立项目，只查询不安装）"
```

### Task 5：ScanCli 接入 `--updates`，本机核对

**Files:**
- Modify: `src/UpdateHelper.ScanCli/UpdateHelper.ScanCli.csproj`
- Modify: `src/UpdateHelper.ScanCli/Program.cs`

**Interfaces:**
- Consumes: `WingetUpdateSource`（Task 4）；`UpdateService.Check`、`UpdateReport`、`JudgedUpdate`、`UpdateTier`（Task 3）；计划 2 的 `applied`、`rules` 变量（`Program.cs` 中已有）
- Produces: 命令行参数 `--updates`

- [ ] **Step 1: 改项目文件** `src/UpdateHelper.ScanCli/UpdateHelper.ScanCli.csproj` 整个替换为：

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <!-- 引用了 winget 项目，框架和架构要与它一致；ComInterop 包必须直接引用，本地 dll 才会复制到输出目录 -->
    <TargetFramework>net10.0-windows10.0.26100.0</TargetFramework>
    <SupportedOSPlatformVersion>10.0.17763.0</SupportedOSPlatformVersion>
    <RuntimeIdentifier>win-x64</RuntimeIdentifier>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Microsoft.WindowsPackageManager.ComInterop" Version="1.29.380" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\UpdateHelper.Core\UpdateHelper.Core.csproj" />
    <ProjectReference Include="..\UpdateHelper.Winget\UpdateHelper.Winget.csproj" />
  </ItemGroup>

</Project>
```

- [ ] **Step 2: 改 `Program.cs`**

在文件开头的 using 区加两行：

```csharp
using UpdateHelper.Core.Updates;
using UpdateHelper.Winget;
```

在用法注释里加一行：

```csharp
//   dotnet run --project src/UpdateHelper.ScanCli -- --updates        查询 winget 并列出更新（只查询，不安装）
```

在 `var showAll = args.Contains("--all");` 下面加：

```csharp
var showUpdates = args.Contains("--updates");
```

在打印"未归属的后台项目"的 `foreach` 循环**之后**、`if (jsonPath is not null)` **之前**插入：

```csharp
if (showUpdates)
{
    Console.WriteLine();
    Console.WriteLine("正在向 winget 查询可用更新（只查询，不安装）……");
    var updates = UpdateService.Check(new WingetUpdateSource(), r, rules);
    if (updates.Warning is not null) Console.WriteLine($"警告：{updates.Warning}");

    foreach (var tier in updates.Updates.GroupBy(u => u.Tier))
    {
        Console.WriteLine();
        Console.WriteLine($"【{TierName(tier.Key)}】{tier.Count()} 个");
        foreach (var u in tier)
        {
            var name = u.Group?.Name ?? u.Candidate.Name;
            Console.WriteLine($"  {name}  {u.Candidate.InstalledVersion} → {u.Candidate.AvailableVersion}  ({u.Candidate.PackageId})");
            Console.WriteLine($"      {u.Reason}");
        }
    }
}
```

在文件末尾（`FindRepoRules` 之后）加：

```csharp
static string TierName(UpdateTier tier) => tier switch
{
    UpdateTier.Low => "低风险",
    UpdateTier.Careful => "需确认",
    UpdateTier.NeverAuto => "不自动",
    _ => "不管",
};
```

- [ ] **Step 3: 编译**

Run: `dotnet build`
Expected: 0 警告 0 错误

- [ ] **Step 4: 在本机运行并核对**

Run: `dotnet run --project src/UpdateHelper.ScanCli -- --updates`

（首次连接 winget 软件源可能要几十秒到几分钟，国内网络较慢。）对照 2026-09-29 的 `winget list` 结果核对：

| 检查项 | 预期 |
|---|---|
| 警告 | 无 |
| 四档合计 | 50 个左右（winget 当天报告 57 个有更新，减去 `ARP\`、`MSIX\` 条目和"其实不更新"的） |
| 微信 | 两条（3.9 和 4.1）都在【不自动】，理由提到"登记了多个" |
| Anaconda3 | 【需确认】，理由提到"2022 → 2026" |
| VirtualBox | 【需确认】，理由提到"7.0 → 7.2" |
| Python Launcher、Unity Hub | 【不自动】，理由提到"版本号无法准确比较" |
| Microsoft Visual C++ / .NET 运行库 | 【不自动】，理由提到"运行库" |
| QQ、网易云音乐 | 【低风险】 |

（可选，需要用户手动断网，执行者不要自己去禁用网卡）断网后再运行一次：应只多出一行中文"警告：无法从 winget 获取更新信息……"，扫描和规则部分照常输出（Review Focus 3；自动化覆盖见 Task 3 的 `Service_turns_source_failure_into_warning`）。

明显不符时，按 superpowers:systematic-debugging 找原因，先补能重现问题的单元测试再修。

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat(cli): ScanCli 增加 --updates，列出分档后的可用更新"
```

### Task 6：Python 学习版 02、03

**Files:**
- Create: `learning/python/02_版本号比较.py`
- Create: `learning/python/03_调用winget.py`
- Modify: `learning/python/README.md`（表格里在 01 和 04 之间加两行）

**Interfaces:**
- 独立脚本，只用 Python 3.9+ 标准库

- [ ] **Step 1: 写** `learning/python/02_版本号比较.py`

```python
"""
学习版 02：版本号比较
对应 C# 代码：src/UpdateHelper.Core/Updates/AppVersion.cs

为什么不能直接比较字符串："1.10" 和 "1.9" 按字符串比，"1.10" 更小（因为 '1' < '9'），
但按版本号它更大。所以要拆成一段一段的数字来比。
运行：python learning/python/02_版本号比较.py
"""
import re

SEPARATORS = re.compile(r"[.\-_+]")


def parse(text):
    """把版本号拆成 [(数字, 后缀), ...]；拆不了返回 None。返回 (段列表, 是否是范围写法)。"""
    if not text or not text.strip():
        return None
    s = text.strip()
    approximate = s[0] in "<>"          # winget 的 "< 3.10.8" 表示只知道大概范围
    if approximate:
        s = s[1:].strip()
    if s[:1] in ("v", "V"):
        s = s[1:]

    parts = []
    for segment in filter(None, SEPARATORS.split(s)):
        m = re.match(r"(\d*)(.*)", segment)
        digits, suffix = m.group(1), m.group(2)
        if not digits:
            if not parts:
                return None             # 第一段必须以数字开头
            parts.append((0, segment))
        else:
            parts.append((int(digits), suffix))
    return (parts, approximate) if parts else None


def compare(a, b):
    """a < b 返回 -1，相等 0，a > b 返回 1（只比较段，不管是否范围写法）。"""
    pa, pb = a[0], b[0]
    for i in range(max(len(pa), len(pb))):
        x = pa[i] if i < len(pa) else (0, "")
        y = pb[i] if i < len(pb) else (0, "")
        if x[0] != y[0]:
            return -1 if x[0] < y[0] else 1
        if x[1] != y[1]:
            if x[1] == "":              # 没有后缀的是正式版，更新
                return 1
            if y[1] == "":
                return -1
            return -1 if x[1].lower() < y[1].lower() else 1
    return 0


def main():
    cases = [
        ("1.10", "1.9"),                       # 按数字比
        ("3.1.12", "3.1.41"),                  # 网易云
        ("2022.10", "2026.07-1"),              # Anaconda：大版本变了
        ("1.0.0", "1.0.0-beta"),               # 正式版比预览版新
        ("2021.3.45f2c1", "2021.3.45f1"),      # Unity 的后缀
        ("< 3.10.8", "3.14.7"),                # 范围写法
        ("unknown", "2.0"),                    # 拆不了
    ]
    symbol = {-1: "<", 0: "=", 1: ">"}
    for a, b in cases:
        pa, pb = parse(a), parse(b)
        if pa is None or pb is None:
            print(f"{a!r:>18} ? {b!r:<14} 无法解析")
            continue
        note = "（已装版本是范围写法，判断层会归到“不自动”）" if pa[1] else ""
        print(f"{a!r:>18} {symbol[compare(pa, pb)]} {b!r:<14}{note}")


if __name__ == "__main__":
    main()
```

- [ ] **Step 2: 写** `learning/python/03_调用winget.py`

```python
"""
学习版 03：调用 winget
对应 C# 代码：src/UpdateHelper.Winget/WingetUpdateSource.cs

C# 版用的是 winget 的官方编程接口（COM），拿到的是结构化数据。
Python 这里用最直观的办法：运行命令行 `winget upgrade`，再从打印出来的表格里拆字段。
跑一跑就能看出这种办法的问题——表格是给人看的：名字太长会被截断成"…"，
版本号里可能有空格（"< 3.10.8"），格式一变就拆错。这正是 C# 版不用它的原因。

运行：python learning/python/03_调用winget.py
只查询，不会安装任何东西。第一次运行可能要等一会儿（winget 要下载软件源索引）。
"""
import subprocess


def run_winget_upgrade():
    result = subprocess.run(
        ["winget", "upgrade", "--source", "winget", "--disable-interactivity"],
        capture_output=True,
    )
    # winget 输出 UTF-8；用 replace 防止个别字符解码失败
    return result.stdout.decode("utf-8", errors="replace")


def parse_table(text):
    """从表格里拆出 (名称, Id, 已装版本, 可用版本)。从右往左拆，因为名称里可能有空格。"""
    rows = []
    started = False
    for line in text.splitlines():
        if set(line.strip()) == {"-"}:      # 表头下面那条横线之后才是数据
            started = True
            continue
        if not started or not line.strip():
            continue
        tokens = line.split()
        if len(tokens) < 5 or tokens[-1] != "winget":
            continue                         # 末尾的统计行等
        available, installed, package_id = tokens[-2], tokens[-3], tokens[-4]
        name_end = -4
        if package_id in ("<", ">"):         # 已装版本是 "< 3.10.8" 这种带空格的写法
            installed = f"{package_id} {installed}"
            package_id = tokens[-5]
            name_end = -5
        rows.append((" ".join(tokens[:name_end]), package_id, installed, available))
    return rows


def main():
    try:
        text = run_winget_upgrade()
    except FileNotFoundError:
        print("没有找到 winget。可以在微软商店安装或更新“应用安装程序”后再试。")
        return

    rows = parse_table(text)
    print(f"winget 报告 {len(rows)} 个软件有更新：\n")
    for name, package_id, installed, available in rows:
        print(f"  {name[:24]:<24} {installed:>16} → {available:<16} {package_id}")


if __name__ == "__main__":
    main()
```

- [ ] **Step 3: README 表格**：把 `learning/python/README.md` 的表格改成（01 和 04 之间插入两行）：

```markdown
| 脚本 | 内容 | 对应 C# |
|---|---|---|
| 01_扫描已装软件.py | 读注册表、区分隐藏条目、把组件归到主软件下 | Scanning/RegistryUninstallSource.cs、Grouping/SoftwareGrouper.cs |
| 02_版本号比较.py | 把版本号拆成段来比较，处理后缀和范围写法 | Updates/AppVersion.cs |
| 03_调用winget.py | 运行 winget 命令行并拆表格（演示为什么 C# 版改用编程接口） | UpdateHelper.Winget/WingetUpdateSource.cs |
| 04_读取YAML规则.py | 读规则文件、校验、通配符匹配 | Rules/GlobPattern.cs、Rules/RuleParser.cs、Rules/RuleSetLoader.cs |
```

- [ ] **Step 4: 运行**

Run: `python learning/python/02_版本号比较.py`
Expected: 依次输出 `'1.10' > '1.9'`、`'3.1.12' < '3.1.41'`、`'2022.10' < '2026.07-1'`、`'1.0.0' > '1.0.0-beta'`、`'2021.3.45f2c1' > '2021.3.45f1'`、`'< 3.10.8' < '3.14.7'`（附"范围写法"说明）、`'unknown' ? '2.0' 无法解析`

Run: `python learning/python/03_调用winget.py`
Expected: "winget 报告 N 个软件有更新"，N 与 Task 5 中 C# 版查到的候选数量相近（表格拆分是近似的，差几个属正常，这正是脚本要演示的问题）

- [ ] **Step 5: Commit**

```bash
git add learning
git commit -m "docs(learning): Python 学习版 02 版本号比较、03 调用 winget"
```

---

## 完成标准

- `dotnet build` 0 警告 0 错误；`dotnet test` 全部通过
- ScanCli `--updates` 在本机运行无警告，Task 5 Step 4 的核对表全部符合
- Python 学习版 02、03 能运行
- 源代码中没有任何安装、下载或改动系统的调用（`grep -rn "InstallPackage\|DownloadPackage" src --include=*.cs` 无结果）
