# 计划 9：卸载残留扫描（只读预览）Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 在"我的软件"里对任意一个软件点"查看残留"，扫出它的安装目录、数据文件夹、服务/计划任务/自启项、注册表残留和规则声明的残留，按"确定属于 / 可能属于 / 多软件共用 / 个人数据"四档分类并给出默认勾选，用只读表格展示。这一期不删除、不备份（删除+备份等后台助手，按钮先置灰）。

**Architecture:** 判断逻辑放 Core 的纯类 `LeftoverJudge`（给定证据 → 分类/默认勾选/提示），`LeftoverScanner` 从软件分组 + 命中规则 + 全量扫描结果里收集候选项，经白名单过滤、算大小、交给 `LeftoverJudge`。文件系统和注册表的读取藏在 `IFileProbe`/`IRegistryProbe` 接口后面，便于测试；真实实现只读磁盘和注册表，不需要管理员权限。Presentation 层把 `LeftoverItem` 列表变成表格行。App 层在"我的软件"加"查看残留"对话框。

**Tech Stack:** .NET 10、System.IO、Microsoft.Win32（只读注册表）、WPF/WPF-UI、xUnit。

**Spec:** `docs/superpowers/specs/2026-09-30-update-helper-design.md`（第 7 节卸载器）

## 精确卸载三层（2026-10-09 与用户确认，本计划做第 1 层）

- 第 1 层（本计划）：**卸载时扫残留**——只读，不常驻，不吃性能；按名称/发布者/规则扫出残留并分类。
- 第 2 层：**知名流氓软件规则**——把 360 这类"到处开枝散叶"的软件的已知残留位置写成 YAML 规则，扫残留时精准命中。规则是文本、零性能。本计划把 `leftovers` 规则格式打通并写一个示例。
- 第 3 层：**安装时记足迹**（第二期，等应用商店）——只在"通过本软件安装某个软件"时，装前装后各拍一次快照，一对比就知道这次装了什么，实现精确卸载。只在那一次安装时跑几秒，不常驻。
- 明确**不做**：24 小时监控全系统文件/注册表操作——那需要内核驱动或系统级 ETW，要管理员权限、有性能和硬盘开销，与"稳 + 不吃性能"冲突，且追不回已装软件。

## 范围

- **做**：残留扫描器（只读）、四档判断、系统白名单保护、"多软件共用"检测、`leftovers` 规则接入 + 一个示例规则 + rules README 文档、残留表格的界面逻辑和"查看残留"对话框（删除按钮置灰）。
- **不做**：真正的删除、备份、恢复（等后台助手，第 7 期之后）；运行软件自带卸载程序（同样等后台助手）；安装足迹（第二期）。

## Global Constraints

- 目标框架：Core 为 `net10.0-windows`；Presentation 为 `net10.0-windows`；App 为 `net10.0-windows10.0.26100.0`、win-x64
- `TreatWarningsAsErrors=true`、`Nullable=enable`、`ImplicitUsings=enable`（App 项目不隐式导入 System.IO，需要时全限定）
- 只读：扫描不修改磁盘、不写注册表、不需要管理员权限；任何读取失败都不抛异常，当作"读不到"继续
- 系统目录和系统注册表键在白名单保护下，永远不列为残留（spec 第 7 节）
- 四档判断与默认勾选严格按 spec 第 7 节表格：确定属于=勾选；可能属于=不勾选；多软件共用=不勾选+黄色；个人数据=不勾选+红色
- 如实告知："扫描只能尽量找全，做不到 100%"——对话框里要有这句话（spec 第 7 节）
- 所有给用户看的文字是中文；提交信息不写任何 Claude 署名行
- 残留路径里的 `%APPDATA%` 等环境变量要展开（spec 第 7 节、LeftoverRule 注释）

## Review Focus

1. **候选路径落在系统目录/系统注册表键**（如安装位置被错填成 `C:\Windows`）：白名单挡下，永不列出 → Task 2 `System_paths_are_never_listed`
2. **同一路径被多个已装软件引用**（如 `AppData\Tencent` 被 QQ 和微信共用）：判为"多软件共用"、不勾选、黄色 → Task 2 `Shared_path_detected_from_other_software`
3. **规则把路径标为个人数据**（聊天记录/存档）：判为"个人数据"、不勾选、红色，且优先级高于"确定属于" → Task 1 `Personal_beats_owned`
4. **残留路径含环境变量或根本不存在**：环境变量展开后再判断是否存在；不存在的候选不列出 → Task 2 `Missing_paths_are_skipped`、Task 3 `Env_vars_expanded`
5. **算文件夹大小时目录超大或无权限**：大小显示为"未知"，不卡界面、不抛异常 → Task 2 `Size_failure_is_unknown`

---

## 文件结构

| 文件 | 职责 |
|---|---|
| `src/UpdateHelper.Core/Uninstall/LeftoverModels.cs` | `LeftoverType`、`LeftoverCategory` 枚举、`LeftoverItem` 记录 |
| `src/UpdateHelper.Core/Uninstall/LeftoverJudge.cs` | 纯判断：证据 → 分类 + 默认勾选 + 中文提示 |
| `src/UpdateHelper.Core/Uninstall/ILeftoverProbes.cs` | `IFileProbe`、`IRegistryProbe` 只读探针接口 |
| `src/UpdateHelper.Core/Uninstall/LeftoverScanner.cs` | 从分组+规则+全量结果收集候选、白名单、算大小、调 Judge |
| `src/UpdateHelper.Core/Uninstall/SystemPaths.cs` | 系统目录/系统注册表键白名单 |
| `src/UpdateHelper.Core/Uninstall/RealLeftoverProbes.cs` | 真实只读探针（磁盘 + 注册表） |
| `src/UpdateHelper.Presentation/ViewModels/LeftoverViewModel.cs` | 残留表格行、汇总、默认勾选 |
| `src/UpdateHelper.Presentation/IAppBackend.cs` | 加 `ScanLeftovers(SoftwareGroup)` |
| `src/UpdateHelper.Presentation/ViewModels/SoftwareViewModel.cs` | 行里带上 SoftwareGroup，加"查看残留"命令 |
| `src/UpdateHelper.App/RealBackend.cs` | 实现 ScanLeftovers（后台线程、真实探针） |
| `src/UpdateHelper.App/Pages/SoftwarePage.xaml`(.cs) | "查看残留"按钮 + 对话框 |
| `rules/tencent.qq.yaml` 等 | 加一个 `leftovers:` 示例 |
| `rules/README.md` | 文档化 leftovers 格式 |

---

### Task 1: 四档判断（纯逻辑）

**Files:**
- Create: `src/UpdateHelper.Core/Uninstall/LeftoverModels.cs`
- Create: `src/UpdateHelper.Core/Uninstall/LeftoverJudge.cs`
- Test: `tests/UpdateHelper.Core.Tests/LeftoverJudgeTests.cs`

**Interfaces:**
- Produces:
  - `enum LeftoverType { InstallDir, DataDir, File, RegistryKey, Service, ScheduledTask, StartupEntry }`
  - `enum LeftoverCategory { Owned, Possible, Shared, Personal }`
  - `sealed record LeftoverItem(LeftoverType Type, string Path, LeftoverCategory Category, long? SizeBytes, bool DefaultChecked, string? Note)`
  - `static class LeftoverJudge { LeftoverItem Classify(LeftoverType type, string path, long? sizeBytes, bool ruleOwned, bool rulePersonal, bool ruleShared, bool isInstallLocation, bool sharedWithOthers, bool nameMatchOnly) }`

判断优先级（安全优先，个人数据和共用先于确定属于）：
1. `rulePersonal` → Personal，不勾选，Note="个人数据（聊天记录/存档/文档等），删除可能造成不可恢复的损失"
2. `ruleShared || sharedWithOthers` → Shared，不勾选，Note="其他已安装的软件也在用这个位置，删除可能影响它们"
3. `ruleOwned || isInstallLocation` → Owned，勾选，Note=null
4. 其余（`nameMatchOnly`）→ Possible，不勾选，Note="按名称/发布者推测，可能属于这个软件"
5. 服务/计划任务/自启项（type 为 Service/ScheduledTask/StartupEntry）：已由归组确认属于本软件 → Owned，勾选（除非上面的 personal/shared 命中，但这三类一般不会）

- [ ] **Step 1: 写失败的测试**

```csharp
using UpdateHelper.Core.Uninstall;

namespace UpdateHelper.Core.Tests;

public sealed class LeftoverJudgeTests
{
    private static LeftoverItem Classify(bool owned = false, bool personal = false, bool shared = false,
        bool isInstallLoc = false, bool sharedOthers = false, bool nameOnly = false,
        LeftoverType type = LeftoverType.DataDir) =>
        LeftoverJudge.Classify(type, @"C:\x", 100, owned, personal, shared, isInstallLoc, sharedOthers, nameOnly);

    [Fact]
    public void Install_location_is_owned_and_checked()
    {
        var item = Classify(isInstallLoc: true);
        Assert.Equal(LeftoverCategory.Owned, item.Category);
        Assert.True(item.DefaultChecked);
        Assert.Null(item.Note);
    }

    [Fact]
    public void Rule_owned_is_checked()
    {
        Assert.True(Classify(owned: true).DefaultChecked);
    }

    [Fact]
    public void Possible_is_unchecked_with_note()
    {
        var item = Classify(nameOnly: true);
        Assert.Equal(LeftoverCategory.Possible, item.Category);
        Assert.False(item.DefaultChecked);
        Assert.Contains("推测", item.Note);
    }

    [Fact]
    public void Shared_is_unchecked_with_warning()
    {
        var item = Classify(sharedOthers: true, isInstallLoc: true);   // 即使看着像自己的，共用也不默认删
        Assert.Equal(LeftoverCategory.Shared, item.Category);
        Assert.False(item.DefaultChecked);
        Assert.Contains("其他", item.Note);
    }

    [Fact]
    public void Personal_beats_owned()
    {
        // 规则同时标 owned 和 personal，或个人数据恰好在安装目录内：个人数据优先，保护它
        var item = Classify(owned: true, personal: true, isInstallLoc: true);
        Assert.Equal(LeftoverCategory.Personal, item.Category);
        Assert.False(item.DefaultChecked);
        Assert.Contains("个人数据", item.Note);
    }

    [Fact]
    public void Personal_beats_shared()
    {
        var item = Classify(personal: true, sharedOthers: true);
        Assert.Equal(LeftoverCategory.Personal, item.Category);
    }

    [Theory]
    [InlineData(LeftoverType.Service)]
    [InlineData(LeftoverType.ScheduledTask)]
    [InlineData(LeftoverType.StartupEntry)]
    public void Background_items_are_owned(LeftoverType type)
    {
        var item = Classify(type: type);
        Assert.Equal(LeftoverCategory.Owned, item.Category);
        Assert.True(item.DefaultChecked);
    }

    [Fact]
    public void Carries_through_path_type_size()
    {
        var item = LeftoverJudge.Classify(LeftoverType.File, @"C:\a\b.log", 2048,
            false, false, false, false, false, true);
        Assert.Equal(LeftoverType.File, item.Type);
        Assert.Equal(@"C:\a\b.log", item.Path);
        Assert.Equal(2048, item.SizeBytes);
    }
}
```

- [ ] **Step 2: 运行测试，确认失败**

Run: `dotnet test tests/UpdateHelper.Core.Tests --filter LeftoverJudgeTests`
Expected: 编译失败（类型不存在）

- [ ] **Step 3: 实现**

`src/UpdateHelper.Core/Uninstall/LeftoverModels.cs`：

```csharp
namespace UpdateHelper.Core.Uninstall;

/// <summary>残留的类型。</summary>
public enum LeftoverType { InstallDir, DataDir, File, RegistryKey, Service, ScheduledTask, StartupEntry }

/// <summary>残留归属判断（spec 第 7 节）。</summary>
public enum LeftoverCategory { Owned, Possible, Shared, Personal }

/// <summary>一条残留。SizeBytes 为 null 表示大小未知；Note 是给用户看的中文提示/警告。</summary>
public sealed record LeftoverItem(
    LeftoverType Type,
    string Path,
    LeftoverCategory Category,
    long? SizeBytes,
    bool DefaultChecked,
    string? Note);
```

`src/UpdateHelper.Core/Uninstall/LeftoverJudge.cs`：

```csharp
namespace UpdateHelper.Core.Uninstall;

/// <summary>按 spec 第 7 节的表格给一条残留分类、决定默认是否勾选、给出提示。纯逻辑。</summary>
public static class LeftoverJudge
{
    public static LeftoverItem Classify(LeftoverType type, string path, long? sizeBytes,
        bool ruleOwned, bool rulePersonal, bool ruleShared,
        bool isInstallLocation, bool sharedWithOthers, bool nameMatchOnly)
    {
        // 安全优先：个人数据 > 多软件共用 > 确定属于 > 可能属于
        if (rulePersonal)
            return Make(type, path, sizeBytes, LeftoverCategory.Personal, false,
                "个人数据（聊天记录/存档/文档等），删除可能造成不可恢复的损失");
        if (ruleShared || sharedWithOthers)
            return Make(type, path, sizeBytes, LeftoverCategory.Shared, false,
                "其他已安装的软件也在用这个位置，删除可能影响它们");
        if (ruleOwned || isInstallLocation
            || type is LeftoverType.Service or LeftoverType.ScheduledTask or LeftoverType.StartupEntry)
            return Make(type, path, sizeBytes, LeftoverCategory.Owned, true, null);
        return Make(type, path, sizeBytes, LeftoverCategory.Possible, false,
            "按名称/发布者推测，可能属于这个软件");
    }

    private static LeftoverItem Make(LeftoverType type, string path, long? size,
        LeftoverCategory category, bool defaultChecked, string? note) =>
        new(type, path, category, size, defaultChecked, note);
}
```

- [ ] **Step 4: 运行测试，确认通过**

Run: `dotnet test tests/UpdateHelper.Core.Tests --filter LeftoverJudgeTests`
Expected: 全部通过

- [ ] **Step 5: 提交**

```bash
git add src/UpdateHelper.Core/Uninstall/LeftoverModels.cs src/UpdateHelper.Core/Uninstall/LeftoverJudge.cs tests/UpdateHelper.Core.Tests/LeftoverJudgeTests.cs
git commit -m "feat(core): 残留四档判断——个人数据和多软件共用优先于确定属于，默认不勾选"
```

---

### Task 2: 残留扫描器（收集候选 + 白名单 + 共用检测）

**Files:**
- Create: `src/UpdateHelper.Core/Uninstall/SystemPaths.cs`
- Create: `src/UpdateHelper.Core/Uninstall/ILeftoverProbes.cs`
- Create: `src/UpdateHelper.Core/Uninstall/LeftoverScanner.cs`
- Test: `tests/UpdateHelper.Core.Tests/LeftoverScannerTests.cs`

**Interfaces:**
- Consumes: Task 1 的 `LeftoverItem`/`LeftoverJudge`；现有 `SoftwareGroup`、`ScanResult`、`Rule`（`Rule.Leftovers` 是 `IReadOnlyList<LeftoverRule>`，`LeftoverRule(string Path, LeftoverKind Kind)`，`LeftoverKind` 为 `Owned/Personal/Shared`）；`PathUtil.NormalizeDir/IsUnder`
- Produces:
  - `interface IFileProbe { bool DirectoryExists(string path); bool FileExists(string path); IReadOnlyList<string> GetChildDirectories(string parent); long? DirectorySize(string path); }`
  - `interface IRegistryProbe { bool KeyExists(string path); }`（path 形如 `HKCU\Software\Foo`）
  - `static class SystemPaths { bool IsProtectedDirectory(string path); bool IsProtectedRegistryKey(string path); }`
  - `sealed class LeftoverScanner(IFileProbe files, IRegistryProbe registry) { IReadOnlyList<LeftoverItem> Scan(SoftwareGroup group, Rule? rule, ScanResult allSoftware) }`

扫描器收集这些候选（读失败一律跳过，不抛）：
1. **安装目录**：`group.InstallLocations` 里仍存在的目录 → `isInstallLocation=true`；受白名单保护的跳过。
2. **规则残留**：`rule.Leftovers`，路径用 `Environment.ExpandEnvironmentVariables` 展开；`HKCU\`/`HKLM\`/`HKCR\` 开头的当注册表项（存在才列，`KeyExists`），否则当文件/目录（存在才列）；`LeftoverKind` → `ruleOwned/rulePersonal/ruleShared`。
3. **数据目录启发式**：`%APPDATA%`、`%LOCALAPPDATA%`、`%PROGRAMDATA%` 下的**直接子目录**，名字（忽略大小写、去空格）包含产品名或发布者名的 → `nameMatchOnly=true`（若已被规则残留覆盖同一路径则跳过，避免重复）。
4. **后台项目**：`group.Background` 里的服务/计划任务/自启项 → 对应 `LeftoverType`，`isInstallLocation=false` 但 Judge 对这三类返回 Owned。
5. **共用检测**：对每个目录候选，`sharedWithOthers = allSoftware.Groups 里除本组外，有别的组的某个安装位置 IsUnder 这个候选目录`。
6. **大小**：目录候选用 `files.DirectorySize`（null=未知）；注册表和后台项不算大小（null）。
- 白名单：候选目录 `SystemPaths.IsProtectedDirectory` 为真就不列；注册表候选 `IsProtectedRegistryKey` 为真就不列。

- [ ] **Step 1: 写失败的测试**

```csharp
using UpdateHelper.Core.Grouping;
using UpdateHelper.Core.Rules;
using UpdateHelper.Core.Scanning;
using UpdateHelper.Core.Uninstall;

namespace UpdateHelper.Core.Tests;

public sealed class LeftoverScannerTests
{
    private sealed class FakeFiles : IFileProbe
    {
        public HashSet<string> Dirs { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> Files { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, List<string>> Children { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, long?> Sizes { get; } = new(StringComparer.OrdinalIgnoreCase);
        public bool DirectoryExists(string path) => Dirs.Contains(path.TrimEnd('\\'));
        public bool FileExists(string path) => Files.Contains(path);
        public IReadOnlyList<string> GetChildDirectories(string parent) =>
            Children.TryGetValue(parent.TrimEnd('\\'), out var c) ? c : [];
        public long? DirectorySize(string path) => Sizes.TryGetValue(path.TrimEnd('\\'), out var s) ? s : 0;
    }

    private sealed class FakeRegistry : IRegistryProbe
    {
        public HashSet<string> Keys { get; } = new(StringComparer.OrdinalIgnoreCase);
        public bool KeyExists(string path) => Keys.Contains(path);
    }

    private static UninstallEntry Entry(string name, string? installLoc) => new(
        name, UninstallHive.LocalMachine64, name, "1.0", "Pub", installLoc,
        null, null, false, null, null, null);

    private static SoftwareGroup Group(string name, string? installLoc, string? publisher = "Pub") =>
        new() { Name = name, Publisher = publisher, Primary = Entry(name, installLoc) };

    private static ScanResult Result(params SoftwareGroup[] groups) => new(groups, [], groups.Length);

    [Fact]
    public void Lists_existing_install_dir_as_owned()
    {
        var files = new FakeFiles();
        files.Dirs.Add(@"C:\Apps\Foo");
        files.Sizes[@"C:\Apps\Foo"] = 5000;
        var g = Group("Foo", @"C:\Apps\Foo");
        var items = new LeftoverScanner(files, new FakeRegistry()).Scan(g, null, Result(g));

        var dir = Assert.Single(items, i => i.Type == LeftoverType.InstallDir);
        Assert.Equal(LeftoverCategory.Owned, dir.Category);
        Assert.True(dir.DefaultChecked);
        Assert.Equal(5000, dir.SizeBytes);
    }

    [Fact]
    public void Missing_paths_are_skipped()
    {
        var files = new FakeFiles();   // C:\Apps\Foo 不存在
        var g = Group("Foo", @"C:\Apps\Foo");
        Assert.Empty(new LeftoverScanner(files, new FakeRegistry()).Scan(g, null, Result(g)));
    }

    [Fact]
    public void System_paths_are_never_listed()
    {
        var files = new FakeFiles();
        var win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        files.Dirs.Add(win.TrimEnd('\\'));
        var g = Group("Bad", win);
        Assert.Empty(new LeftoverScanner(files, new FakeRegistry()).Scan(g, null, Result(g)));
    }

    [Fact]
    public void Shared_path_detected_from_other_software()
    {
        var files = new FakeFiles();
        files.Dirs.Add(@"C:\Shared\Tencent");
        files.Dirs.Add(@"C:\Shared\Tencent\QQ");
        // 另一个软件（微信）装在同一个父目录下
        var qq = Group("QQ", @"C:\Shared\Tencent\QQ");
        var wechat = Group("WeChat", @"C:\Shared\Tencent\WeChat");
        var rule = new Rule("t", "T", new RuleMatch("QQ", null), null, null, [],
            [new LeftoverRule(@"C:\Shared\Tencent", LeftoverKind.Owned)]);
        var items = new LeftoverScanner(files, new FakeRegistry()).Scan(qq, rule, Result(qq, wechat));

        var shared = Assert.Single(items, i => i.Path.TrimEnd('\\').Equals(@"C:\Shared\Tencent", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(LeftoverCategory.Shared, shared.Category);
        Assert.False(shared.DefaultChecked);
    }

    [Fact]
    public void Rule_personal_path_is_red_and_unchecked()
    {
        var files = new FakeFiles();
        var appdata = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        files.Dirs.Add(Path.Combine(appdata, "FooChats").TrimEnd('\\'));
        var g = Group("Foo", null);
        var rule = new Rule("f", "Foo", new RuleMatch("Foo", null), null, null, [],
            [new LeftoverRule(@"%APPDATA%\FooChats", LeftoverKind.Personal)]);
        var items = new LeftoverScanner(files, new FakeRegistry()).Scan(g, rule, Result(g));

        var personal = Assert.Single(items, i => i.Category == LeftoverCategory.Personal);
        Assert.False(personal.DefaultChecked);
        Assert.Contains("个人数据", personal.Note);
    }

    [Fact]
    public void Registry_leftover_listed_only_when_key_exists()
    {
        var reg = new FakeRegistry();
        reg.Keys.Add(@"HKCU\Software\Foo");
        var g = Group("Foo", null);
        var rule = new Rule("f", "Foo", new RuleMatch("Foo", null), null, null, [],
            [new LeftoverRule(@"HKCU\Software\Foo", LeftoverKind.Owned),
             new LeftoverRule(@"HKCU\Software\Missing", LeftoverKind.Owned)]);
        var items = new LeftoverScanner(new FakeFiles(), reg).Scan(g, rule, Result(g));

        var key = Assert.Single(items, i => i.Type == LeftoverType.RegistryKey);
        Assert.Equal(@"HKCU\Software\Foo", key.Path);
    }

    [Fact]
    public void Background_items_become_leftovers()
    {
        var g = Group("Foo", null);
        g.Background.Add(new BackgroundItem(BackgroundKind.Service, "FooSvc", null, null, @"C:\Apps\Foo\svc.exe", null));
        g.Background.Add(new BackgroundItem(BackgroundKind.ScheduledTask, "FooTask", null, null, null, null));
        var items = new LeftoverScanner(new FakeFiles(), new FakeRegistry()).Scan(g, null, Result(g));

        Assert.Contains(items, i => i.Type == LeftoverType.Service && i.Path.Contains("FooSvc"));
        Assert.Contains(items, i => i.Type == LeftoverType.ScheduledTask && i.Path.Contains("FooTask"));
        Assert.All(items.Where(i => i.Type is LeftoverType.Service or LeftoverType.ScheduledTask),
            i => Assert.True(i.DefaultChecked));
    }

    [Fact]
    public void Data_dir_name_match_is_possible()
    {
        var files = new FakeFiles();
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData).TrimEnd('\\');
        files.Children[local] = [Path.Combine(local, "FooBar"), Path.Combine(local, "Unrelated")];
        files.Dirs.Add(Path.Combine(local, "FooBar"));
        files.Dirs.Add(Path.Combine(local, "Unrelated"));
        var g = Group("FooBar", null);
        var items = new LeftoverScanner(files, new FakeRegistry()).Scan(g, null, Result(g));

        var hit = Assert.Single(items, i => i.Path.Contains("FooBar"));
        Assert.Equal(LeftoverCategory.Possible, hit.Category);
        Assert.DoesNotContain(items, i => i.Path.Contains("Unrelated"));
    }

    [Fact]
    public void Size_failure_is_unknown()
    {
        var files = new FakeFiles();
        files.Dirs.Add(@"C:\Apps\Foo");
        files.Sizes[@"C:\Apps\Foo"] = null;   // 算不出
        var g = Group("Foo", @"C:\Apps\Foo");
        var item = Assert.Single(new LeftoverScanner(files, new FakeRegistry()).Scan(g, null, Result(g)),
            i => i.Type == LeftoverType.InstallDir);
        Assert.Null(item.SizeBytes);
    }
}
```

- [ ] **Step 2: 运行测试，确认失败**

Run: `dotnet test tests/UpdateHelper.Core.Tests --filter LeftoverScannerTests`
Expected: 编译失败

- [ ] **Step 3: 实现**

`src/UpdateHelper.Core/Uninstall/ILeftoverProbes.cs`：

```csharp
namespace UpdateHelper.Core.Uninstall;

/// <summary>只读文件系统探针。所有方法失败都返回"不存在/空/未知"，不抛异常。</summary>
public interface IFileProbe
{
    bool DirectoryExists(string path);
    bool FileExists(string path);
    IReadOnlyList<string> GetChildDirectories(string parent);
    long? DirectorySize(string path);
}

/// <summary>只读注册表探针。path 形如 HKCU\Software\Foo。</summary>
public interface IRegistryProbe
{
    bool KeyExists(string path);
}
```

`src/UpdateHelper.Core/Uninstall/SystemPaths.cs`：

```csharp
using UpdateHelper.Core.Grouping;

namespace UpdateHelper.Core.Uninstall;

/// <summary>系统目录和系统注册表键的白名单保护：这些永远不作为残留列出（spec 第 7 节）。</summary>
public static class SystemPaths
{
    private static readonly string[] ProtectedDirs =
    [
        Environment.GetFolderPath(Environment.SpecialFolder.Windows),
        Environment.GetFolderPath(Environment.SpecialFolder.System),
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        @"C:\Users", @"C:\",
    ];

    /// <summary>候选目录是否受保护：等于或位于上面任一系统目录"本身"，或是盘符根目录。
    /// 注意：系统目录"之内"的具体子目录（如某软件装在 Program Files\Foo）不算受保护。</summary>
    public static bool IsProtectedDirectory(string path)
    {
        var norm = PathUtil.NormalizeDir(path);
        if (norm is null) return true;   // 规范化不了的，保守起见不列
        foreach (var p in ProtectedDirs)
        {
            var dir = PathUtil.NormalizeDir(p);
            if (dir is not null && string.Equals(norm, dir, StringComparison.OrdinalIgnoreCase)) return true;
        }
        // 盘符根目录（C:\ D:\ …）
        var trimmed = norm.TrimEnd('\\');
        if (trimmed.Length == 2 && trimmed[1] == ':') return true;
        return false;
    }

    /// <summary>系统注册表键：HKLM\SYSTEM、HKLM\SOFTWARE\Microsoft\Windows 等不作为残留。
    /// 规则声明的 HKCU/HKLM\Software\<厂商> 不在此列。</summary>
    public static bool IsProtectedRegistryKey(string path)
    {
        var p = path.Replace('/', '\\').Trim().TrimEnd('\\');
        string[] protectedPrefixes =
        [
            @"HKLM\SYSTEM", @"HKLM\SECURITY", @"HKLM\SAM", @"HKLM\HARDWARE",
            @"HKLM\SOFTWARE\Microsoft\Windows", @"HKLM\SOFTWARE\Microsoft\Windows NT",
            @"HKCU\SOFTWARE\Microsoft\Windows",
        ];
        // 等于保护前缀，或是它的祖先（如单独一个 HKLM、HKCU）
        if (p.Equals("HKLM", StringComparison.OrdinalIgnoreCase) || p.Equals("HKCU", StringComparison.OrdinalIgnoreCase)
            || p.Equals("HKCR", StringComparison.OrdinalIgnoreCase)) return true;
        return protectedPrefixes.Any(pre =>
            p.Equals(pre, StringComparison.OrdinalIgnoreCase)
            || p.StartsWith(pre + "\\", StringComparison.OrdinalIgnoreCase));
    }
}
```

`src/UpdateHelper.Core/Uninstall/LeftoverScanner.cs`：

```csharp
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
    private static readonly char[] RegistryHivePrefixes = [];   // 见 IsRegistryPath

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
```

> 注意：`RegistryHivePrefixes` 那个空字段是占位，实际用 `IsRegistryPath` 判断；实现时删掉这个没用的字段，避免 `TreatWarningsAsErrors` 下的未使用告警。

- [ ] **Step 4: 运行测试，确认通过**

Run: `dotnet test tests/UpdateHelper.Core.Tests --filter LeftoverScannerTests`
Expected: 全部通过

- [ ] **Step 5: 跑全部核心测试并提交**

Run: `dotnet test tests/UpdateHelper.Core.Tests`
Expected: 原有 + 新增全部通过

```bash
git add src/UpdateHelper.Core/Uninstall tests/UpdateHelper.Core.Tests/LeftoverScannerTests.cs
git commit -m "feat(core): 残留扫描器——安装目录/规则残留/数据目录/后台项/注册表，白名单保护、共用检测"
```

---

### Task 3: 真实探针 + 规则示例 + 文档

**Files:**
- Create: `src/UpdateHelper.Core/Uninstall/RealLeftoverProbes.cs`
- Modify: `rules/tencent.qq.yaml`（加 `leftovers:` 示例）
- Modify: `rules/README.md`（文档化 leftovers 格式）
- Modify: `docs/superpowers/specs/2026-09-30-update-helper-design.md`（第 7 节记三层决定）
- Test: `tests/UpdateHelper.Core.Tests/RealLeftoverProbesTests.cs`、`tests/UpdateHelper.Core.Tests/RuleParserTests.cs`（给已有文件加一条 leftovers 解析断言，若已覆盖则跳过）

**Interfaces:**
- Consumes: Task 2 的 `IFileProbe`/`IRegistryProbe`
- Produces: `sealed class RealFileProbe : IFileProbe`、`sealed class RealRegistryProbe : IRegistryProbe`（都只读，失败不抛）

- [ ] **Step 1: 写失败的测试**

```csharp
using UpdateHelper.Core.Uninstall;

namespace UpdateHelper.Core.Tests;

public sealed class RealLeftoverProbesTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("uh-probe-").FullName;
    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    [Fact]
    public void Directory_exists_and_size()
    {
        File.WriteAllText(Path.Combine(_dir, "a.txt"), new string('x', 1000));
        Directory.CreateDirectory(Path.Combine(_dir, "sub"));
        File.WriteAllText(Path.Combine(_dir, "sub", "b.txt"), new string('y', 500));
        var probe = new RealFileProbe();

        Assert.True(probe.DirectoryExists(_dir));
        Assert.False(probe.DirectoryExists(Path.Combine(_dir, "nope")));
        Assert.Equal(1500, probe.DirectorySize(_dir));   // 递归求和
    }

    [Fact]
    public void Child_directories_listed()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "c1"));
        Directory.CreateDirectory(Path.Combine(_dir, "c2"));
        var kids = new RealFileProbe().GetChildDirectories(_dir);
        Assert.Equal(2, kids.Count);
    }

    [Fact]
    public void Nonexistent_dir_size_is_null_not_throw()
    {
        Assert.Null(new RealFileProbe().DirectorySize(Path.Combine(_dir, "ghost")));
        Assert.Empty(new RealFileProbe().GetChildDirectories(Path.Combine(_dir, "ghost")));
    }

    [Fact]
    public void Registry_key_exists()
    {
        var probe = new RealRegistryProbe();
        Assert.True(probe.KeyExists(@"HKLM\SOFTWARE\Microsoft\Windows"));
        Assert.False(probe.KeyExists(@"HKCU\Software\UpdateHelperDefinitelyMissing12345"));
        Assert.False(probe.KeyExists("not a key"));   // 不抛
    }
}
```

- [ ] **Step 2: 运行测试，确认失败**

Run: `dotnet test tests/UpdateHelper.Core.Tests --filter RealLeftoverProbesTests`
Expected: 编译失败

- [ ] **Step 3: 实现探针**

`src/UpdateHelper.Core/Uninstall/RealLeftoverProbes.cs`：

```csharp
using Microsoft.Win32;

namespace UpdateHelper.Core.Uninstall;

/// <summary>真实只读文件系统探针。所有操作失败都返回"不存在/空/未知"。</summary>
public sealed class RealFileProbe : IFileProbe
{
    public bool DirectoryExists(string path) { try { return Directory.Exists(path); } catch { return false; } }
    public bool FileExists(string path) { try { return File.Exists(path); } catch { return false; } }

    public IReadOnlyList<string> GetChildDirectories(string parent)
    {
        try { return Directory.Exists(parent) ? Directory.GetDirectories(parent) : []; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
    }

    public long? DirectorySize(string path)
    {
        if (!Directory.Exists(path)) return null;
        try
        {
            long total = 0;
            foreach (var file in Directory.EnumerateFiles(path, "*", new EnumerationOptions
                     {
                         RecurseSubdirectories = true,
                         AttributesToSkip = FileAttributes.ReparsePoint,   // 不跟随符号链接，防成环
                         IgnoreInaccessible = true,
                     }))
            {
                try { total += new FileInfo(file).Length; }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
            return total;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return null;
        }
    }
}

/// <summary>真实只读注册表探针。path 形如 HKCU\Software\Foo。</summary>
public sealed class RealRegistryProbe : IRegistryProbe
{
    public bool KeyExists(string path)
    {
        var parts = path.Replace('/', '\\').Split('\\', 2);
        if (parts.Length < 2) return false;
        var hive = parts[0].ToUpperInvariant() switch
        {
            "HKCU" => Registry.CurrentUser,
            "HKLM" => Registry.LocalMachine,
            "HKCR" => Registry.ClassesRoot,
            _ => null,
        };
        if (hive is null) return false;
        try
        {
            using var key = hive.OpenSubKey(parts[1]);
            return key is not null;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }
}
```

- [ ] **Step 4: 运行测试，确认通过**

Run: `dotnet test tests/UpdateHelper.Core.Tests --filter RealLeftoverProbesTests`
Expected: 全部通过

- [ ] **Step 5: 规则示例 + 文档**

在 `rules/tencent.qq.yaml` 末尾加（路径仅为格式示例，具体位置实现时按真实情况填，拿不准就只写确定的）：

```yaml
leftovers:
  - path: "%APPDATA%\\Tencent\\QQ"
    kind: shared          # Tencent 目录可能被多个腾讯软件共用
  - path: "HKCU\\Software\\Tencent\\QQ"
    kind: owned
```

`rules/README.md` 里加一节，说明 `leftovers` 格式：
- 每条 `path` + `kind`；`kind` 为 `owned`（确定属于，默认勾选）/ `personal`（个人数据，默认不勾、红色警告）/ `shared`（多软件共用，默认不勾、黄色警告）。
- `path` 支持 `%APPDATA%`、`%LOCALAPPDATA%`、`%PROGRAMDATA%` 等环境变量。
- `HKCU\`、`HKLM\`、`HKCR\` 开头的是注册表项，其余是文件或文件夹。
- 聊天记录、存档、文档一律用 `personal`；被同厂商多个软件共用的目录用 `shared`。

`docs/.../2026-09-30-update-helper-design.md` 第 7 节末尾加一小节"### 精确卸载三层（2026-10-09）"，把本计划开头的三层决定和"不做 24 小时内核监控"的理由记下来。

- [ ] **Step 6: 规则解析回归 + 提交**

Run: `dotnet test tests/UpdateHelper.Core.Tests`（确认加了 leftovers 的 qq 规则仍能正常解析、全绿；RuleSetLoader 对 rules 目录全解析的测试若存在会覆盖到）
Expected: 全部通过

```bash
git add src/UpdateHelper.Core/Uninstall/RealLeftoverProbes.cs tests/UpdateHelper.Core.Tests/RealLeftoverProbesTests.cs rules/ docs/superpowers/specs/2026-09-30-update-helper-design.md
git commit -m "feat(core): 真实只读残留探针；QQ 规则加 leftovers 示例；文档化格式和精确卸载三层"
```

---

### Task 4: 界面——我的软件"查看残留"对话框（只读预览）

**Files:**
- Modify: `src/UpdateHelper.Presentation/IAppBackend.cs`
- Create: `src/UpdateHelper.Presentation/ViewModels/LeftoverViewModel.cs`
- Modify: `src/UpdateHelper.Presentation/ViewModels/SoftwareViewModel.cs`
- Modify: `tests/UpdateHelper.Presentation.Tests/FakeBackend.cs`
- Test: `tests/UpdateHelper.Presentation.Tests/LeftoverViewModelTests.cs`
- Modify: `src/UpdateHelper.App/RealBackend.cs`
- Modify: `src/UpdateHelper.App/Pages/SoftwarePage.xaml`、`SoftwarePage.xaml.cs`

**Interfaces:**
- Consumes: Task 1~3 的 `LeftoverItem`、`LeftoverScanner`、`RealFileProbe`、`RealRegistryProbe`；现有 `SoftwareGroup`、`ScanSnapshot`（含 `Result` 和 `Rules`）
- Produces:
  - `IAppBackend` 加 `IReadOnlyList<LeftoverItem> ScanLeftovers(SoftwareGroup group);`
  - `SoftwareRow` 加 `SoftwareGroup Group`（Presentation 已依赖 Core，可直接携带）
  - `LeftoverViewModel`：从 `IReadOnlyList<LeftoverItem>` 生成 `Rows`（每行：类型中文、路径、分类中文、大小人类可读、是否勾选、警告色）、`Summary`（"共 N 项，默认选中 M 项"）、`DisclaimerText`（"扫描只能尽量找全，做不到 100%。删除和备份功能将在后台助手完成后提供。"）

- [ ] **Step 1: 写失败的测试**

```csharp
using UpdateHelper.Core.Uninstall;
using UpdateHelper.Presentation.ViewModels;

namespace UpdateHelper.Presentation.Tests;

public sealed class LeftoverViewModelTests
{
    private static LeftoverItem Item(LeftoverType type, LeftoverCategory cat, bool chk, long? size = 1024, string path = @"C:\x") =>
        new(type, path, cat, size, chk, cat == LeftoverCategory.Personal ? "个人数据" : null);

    [Fact]
    public void Rows_show_category_and_size_and_checkbox()
    {
        var vm = new LeftoverViewModel([
            Item(LeftoverType.InstallDir, LeftoverCategory.Owned, true, 5_000_000),
            Item(LeftoverType.DataDir, LeftoverCategory.Personal, false, 2048),
        ]);

        Assert.Equal(2, vm.Rows.Count);
        var owned = vm.Rows[0];
        Assert.Equal("确定属于", owned.CategoryName);
        Assert.True(owned.Checked);
        Assert.Contains("MB", owned.SizeText);
        var personal = vm.Rows[1];
        Assert.False(personal.Checked);
        Assert.Equal("个人数据", personal.CategoryName);
    }

    [Fact]
    public void Summary_counts_default_checked()
    {
        var vm = new LeftoverViewModel([
            Item(LeftoverType.InstallDir, LeftoverCategory.Owned, true),
            Item(LeftoverType.DataDir, LeftoverCategory.Shared, false),
            Item(LeftoverType.RegistryKey, LeftoverCategory.Owned, true, size: null),
        ]);
        Assert.Contains("共 3 项", vm.Summary);
        Assert.Contains("2 项", vm.Summary);   // 默认勾选 2
    }

    [Fact]
    public void Unknown_size_shows_dash()
    {
        var vm = new LeftoverViewModel([Item(LeftoverType.RegistryKey, LeftoverCategory.Owned, true, size: null)]);
        Assert.Equal("—", vm.Rows[0].SizeText);
    }

    [Fact]
    public void Empty_has_friendly_message()
    {
        var vm = new LeftoverViewModel([]);
        Assert.Empty(vm.Rows);
        Assert.Contains("没有找到", vm.Summary);
    }

    [Fact]
    public void Has_disclaimer()
    {
        Assert.Contains("做不到 100%", new LeftoverViewModel([]).DisclaimerText);
    }
}
```

`tests/UpdateHelper.Presentation.Tests/FakeBackend.cs` 加：

```csharp
    public Func<SoftwareGroup, IReadOnlyList<LeftoverItem>> OnScanLeftovers { get; set; } = _ => [];
    public IReadOnlyList<LeftoverItem> ScanLeftovers(SoftwareGroup group) => OnScanLeftovers(group);
```

（文件头补 `using UpdateHelper.Core.Uninstall;`、`using UpdateHelper.Core.Grouping;`。）

- [ ] **Step 2: 运行测试，确认失败**

Run: `dotnet test tests/UpdateHelper.Presentation.Tests --filter LeftoverViewModelTests`
Expected: 编译失败（`LeftoverViewModel` 不存在、`IAppBackend` 没有 ScanLeftovers）

- [ ] **Step 3: 实现 Presentation**

`src/UpdateHelper.Presentation/IAppBackend.cs` 接口里加（文件头加 `using UpdateHelper.Core.Uninstall;`）：

```csharp
    /// <summary>扫描一个软件的卸载残留（只读，不删除）。</summary>
    IReadOnlyList<LeftoverItem> ScanLeftovers(SoftwareGroup group);
```

`src/UpdateHelper.Presentation/ViewModels/LeftoverViewModel.cs`：

```csharp
using UpdateHelper.Core.Uninstall;

namespace UpdateHelper.Presentation.ViewModels;

/// <summary>残留表格的一行。Warning: null 无警告 / "shared" 黄 / "personal" 红（界面决定颜色）。</summary>
public sealed record LeftoverRow(string TypeName, string Path, string CategoryName, string SizeText, bool Checked, string? Warning, string? Note);

/// <summary>"查看残留"对话框的界面逻辑。只读预览，不涉及删除。</summary>
public sealed class LeftoverViewModel
{
    public LeftoverViewModel(IReadOnlyList<LeftoverItem> items)
    {
        Rows = items.Select(ToRow).ToList();
        var checkedCount = items.Count(i => i.DefaultChecked);
        Summary = items.Count == 0
            ? "没有找到可清理的残留"
            : $"共 {items.Count} 项，默认选中 {checkedCount} 项（共用和个人数据默认不选）";
    }

    public IReadOnlyList<LeftoverRow> Rows { get; }
    public string Summary { get; }
    public string DisclaimerText =>
        "扫描只能尽量找全，做不到 100%。删除和备份功能将在后台助手完成后提供。";

    private static LeftoverRow ToRow(LeftoverItem i) => new(
        TypeName(i.Type), i.Path, CategoryName(i.Category), SizeText(i.SizeBytes), i.DefaultChecked,
        i.Category switch { LeftoverCategory.Shared => "shared", LeftoverCategory.Personal => "personal", _ => null },
        i.Note);

    private static string TypeName(LeftoverType t) => t switch
    {
        LeftoverType.InstallDir => "安装目录",
        LeftoverType.DataDir => "数据文件夹",
        LeftoverType.File => "文件",
        LeftoverType.RegistryKey => "注册表项",
        LeftoverType.Service => "服务",
        LeftoverType.ScheduledTask => "计划任务",
        _ => "开机自启",
    };

    private static string CategoryName(LeftoverCategory c) => c switch
    {
        LeftoverCategory.Owned => "确定属于",
        LeftoverCategory.Possible => "可能属于",
        LeftoverCategory.Shared => "多软件共用",
        _ => "个人数据",
    };

    public static string SizeText(long? bytes)
    {
        if (bytes is null) return "—";
        double b = bytes.Value;
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var u = 0;
        while (b >= 1024 && u < units.Length - 1) { b /= 1024; u++; }
        return u == 0 ? $"{bytes} B" : $"{b:0.#} {units[u]}";
    }
}
```

`src/UpdateHelper.Presentation/ViewModels/SoftwareViewModel.cs`：`SoftwareRow` 当前是 `record SoftwareRow(string Name, string Publisher, string Version, string Category, string Components, string Location, IconSource? Icon = null)`。加一个 `SoftwareGroup Group` 参数，**放在 `Location` 之后、`Icon` 之前**（`Icon` 有默认值，必填参数不能排在它后面）：`(…, string Location, SoftwareGroup Group, IconSource? Icon = null)`。`Rebuild` 里构造 `SoftwareRow` 时在对应位置传入当前 `g`（`Rebuild` 里只有一处构造）。文件头确保有 `using UpdateHelper.Core.Grouping;`。

- [ ] **Step 4: 运行测试，确认通过**

Run: `dotnet test tests/UpdateHelper.Presentation.Tests`
Expected: 全部通过

- [ ] **Step 5: 实现 App（RealBackend + 对话框）**

`src/UpdateHelper.App/RealBackend.cs` 加（文件头 `using UpdateHelper.Core.Uninstall;`）：

```csharp
    public IReadOnlyList<LeftoverItem> ScanLeftovers(UpdateHelper.Core.Grouping.SoftwareGroup group)
    {
        var snapshot = _lastSnapshot;   // RealBackend 已经持有最近一次 ScanSnapshot；若没有则现扫一次
        var rule = snapshot?.Rules.Rules.FirstOrDefault(r => r.Id == group.RuleId);
        var result = snapshot?.Result ?? new Core.Scanning.ScanResult([group], [], 1);
        return new LeftoverScanner(new RealFileProbe(), new RealRegistryProbe()).Scan(group, rule, result);
    }
```

> 实现细节：RealBackend 目前每次 ScanAsync 都新建结果、未必缓存。若没有现成的 `_lastSnapshot` 字段，就在 ScanAsync 成功后把结果存进一个字段供 ScanLeftovers 用（共用检测需要全量结果）。调用方（SoftwarePage）总是在已经扫描过之后才点"查看残留"，所以缓存一定非空；为稳妥，null 时退化为只用当前 group 的结果。

`SoftwarePage.xaml`：给 DataGrid 的"名称"列那行右边，或工具栏加一个"查看残留"按钮；选中一行后点击，调用 `AppHost.Software` 暴露的命令（在 SoftwareViewModel 加 `SelectedRow` 和一个 `ViewLeftoversCommand`，或在 code-behind 里拿 DataGrid.SelectedItem 的 `SoftwareRow.Group` 调 `AppHost.Backend.ScanLeftovers`）。弹出一个 `ContentDialog`/`Window`，用 DataGrid 显示 `LeftoverViewModel.Rows`：列为 选(CheckBox，可勾但本期不生效)/类型/位置/分类/大小/说明；分类列按 Warning 上色（personal 红、shared 黄）。底部一行 `Summary` + `DisclaimerText`，一个置灰的"删除所选"按钮（`IsEnabled=False`，ToolTip="删除和备份功能将在后台助手完成后提供"）和一个"关闭"按钮。

扫描可能较慢（算目录大小），在后台线程跑：`await Task.Run(() => AppHost.Backend.ScanLeftovers(group))`，期间对话框显示"正在扫描残留……"。

- [ ] **Step 6: 构建 + 截图核对（控制者）**

Run:
```powershell
dotnet build src/UpdateHelper.App -c Debug -v q --nologo
```
Expected: 0 个错误。控制者启动程序 → 我的软件 → 选一个软件 → 查看残留，截图确认表格显示（类型/位置/分类/大小/说明）、共用和个人数据默认不勾且有颜色、删除按钮置灰、底部有"做不到 100%"的说明。读图确认。

- [ ] **Step 7: 全部测试 + 提交**

Run: `dotnet test`
Expected: 全部通过

```bash
git add src tests
git commit -m "feat(app): 我的软件加查看残留——只读表格预览，删除按钮置灰等后台助手"
```

---

## 自查（对照 spec 第 7 节）

- 残留扫描来源（安装目录/数据目录/注册表/服务/计划任务/自启/快捷方式）→ Task 2 覆盖前 6 类；**快捷方式和右键菜单本期未扫**（归组阶段没有现成数据，留待后续；在对话框说明里不夸大）。✅（部分，已注明）
- 四档判断与默认勾选（确定属于/可能属于/多软件共用/个人数据）→ Task 1 `LeftoverJudge` + Task 2 共用检测。✅
- 系统目录/系统注册表键白名单 → Task 2 `SystemPaths`。✅
- 如实告知"做不到 100%" → Task 4 DisclaimerText。✅
- 环境变量展开 → Task 2 `ExpandEnvironmentVariables`。✅
- 运行自带卸载程序 / 备份后删除 / 30 天恢复 → **本期不做**（范围已声明，等后台助手）。
- 占位符扫描：无 TODO；每个代码步骤有完整代码。
- 类型一致：`LeftoverItem`、`LeftoverJudge.Classify`、`IFileProbe`/`IRegistryProbe`、`LeftoverScanner.Scan`、`IAppBackend.ScanLeftovers`、`LeftoverViewModel` 在定义与使用处签名一致。
- Review Focus 五项都指到对应 Task 的测试。

## 计划结束后

- 整个分支交给全新审查员（最强模型）整体审查（重点：白名单是否真的挡住系统目录、共用检测的误判、大小计算不跟随符号链接、注册表探针只读）。
- 审查通过、测试全绿后合并 main、推送。
- 本期不单独发版（无用户可感知的大功能变化？——其实"查看残留"可感知，可并入下一个发版，或小版本 0.3.0）。发不发、版本号留给控制者/用户定。
