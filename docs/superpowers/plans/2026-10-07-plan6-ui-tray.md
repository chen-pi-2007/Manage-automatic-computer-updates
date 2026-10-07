# 计划 6：主程序界面 + 托盘 + 通知 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 做出用户真正能用的桌面程序：Win11 风格的主窗口（首页、更新、我的软件、后台项目、历史、设置 6 个页面）、右下角托盘图标和系统通知。底层全部复用计划 1～5，用户可以看软件、看更新、勾选后一键更新（每次安装仍由系统弹管理员确认框，去掉确认框是计划 7）。

**Architecture:** 三层：
- `UpdateHelper.Presentation`（新类库，不依赖 WPF）：设置的读写、界面状态和各页面的 ViewModel（CommunityToolkit.Mvvm）。只通过 `IAppBackend` 接口使用底层，所以可以用假实现做单元测试
- `UpdateHelper.App`（新 WPF 程序）：窗口和页面的 XAML、托盘、通知、单实例，以及 `IAppBackend` 的真实实现（组合计划 1～5 的扫描、规则、winget、规则库安装器）
- 底层（Core、Winget）不改

**Tech Stack:** C# / .NET 10、WPF、WPF-UI 4.3.0、CommunityToolkit.Mvvm 8.4.2、System.Windows.Forms.NotifyIcon（托盘与气泡通知）、xUnit。

**Spec:** `docs/superpowers/specs/2026-09-30-update-helper-design.md`（第 10 节界面；第 6 节更新模式；第 18 节开发顺序调整）；界面草图 https://claude.ai/artifact/THWXYi68SFSUYydjbaJWzT（Win11 设置外壳 + Geek 式紧凑表格，用户已确认）

**前置：** 计划 1～5 已合并（`main` 上的 `37aa807`）。

## 技术验证结论（2026-10-07 在开发者本机实验，代码已丢弃）

- WPF-UI 4.3.0 提供 `net10.0-windows7.0` 版本。`ui:FluentWindow`（`ExtendsContentIntoTitleBar`、`WindowBackdropType="Mica"`）+ `ui:TitleBar` + `ui:NavigationView`（`PaneDisplayMode="Left"`，`NavigationViewItem` 的 `TargetPageType` 指向页面类型）可以直接导航，**不需要**注册页面提供器；`ui:InfoBar`、`ui:CardControl`、`ui:DataGrid` 都能正常显示。本机截图确认了外观就是 Win11 设置的样子
- 托盘用 `System.Windows.Forms.NotifyIcon`（项目里设 `<UseWindowsForms>true</UseWindowsForms>`）。**必须**在项目文件里去掉两个隐式 using：`<Using Remove="System.Windows.Forms" />`、`<Using Remove="System.Drawing" />`，否则 `Application` 等类型在 WPF 和 WinForms 之间冲突，编译报 CS0104
- `NotifyIcon.ShowBalloonTip` 在 Win10/11 上显示为系统通知，点击通知触发 `BalloonTipClicked`
- App 项目引用了 Winget 项目，所以框架、架构与它一致（`net10.0-windows10.0.26100.0` + `win-x64`），并且要直接引用 ComInterop 包（计划 3 的结论）

## 范围

| 包含 | 不包含（留给后续计划） |
|---|---|
| 6 个页面：首页、更新、我的软件、后台项目、历史、设置 | 安全页、规则订阅页（计划 9） |
| 启动时自动扫描和检查更新；"立即检查"按钮 | 定时后台检查、开机自启（计划 7 的计划任务） |
| 勾选更新后一键依次安装，每个显示结果 | 不弹确认框的安装、自动安装（计划 7） |
| 设置页可以选择更新模式、观察期、检查间隔、托盘开关，保存到文件 | 模式真正生效（计划 7）；设置页会注明"自动更新将在后续版本启用" |
| 托盘图标（打开 / 检查更新 / 退出）、关窗口时缩到托盘、发现更新时弹通知、点通知打开更新页 | 卸载按钮（计划 8，先显示为不可用） |
| 单实例：重复打开时只把已有窗口调到前面 | 深色主题的手动切换（先跟随系统） |

## Global Constraints

- 沿用：警告即错误；提交信息不带任何 AI 署名；Core 不改
- `UpdateHelper.Presentation`：`net10.0-windows`，**不引用 WPF、WinForms**；所有对底层的调用都经过 `IAppBackend`
- `UpdateHelper.App`：`net10.0-windows10.0.26100.0`、`SupportedOSPlatformVersion 10.0.17763.0`、`win-x64`、`UseWPF`、`UseWindowsForms`，去掉 `System.Windows.Forms` 和 `System.Drawing` 两个隐式 using
- **界面线程不做耗时操作**：扫描、查询 winget、安装都在后台线程，界面显示"正在……"并禁用相关按钮
- 安装依次进行，一次只装一个（winget 和安装程序都不适合并发）
- 判断层归为"不自动 / 不管"的更新在界面上**不能被勾选**，并显示理由
- 设置文件损坏或缺失时用默认值，不崩溃；默认值：只提醒、观察期 3 天、检查间隔 24 小时、托盘开启（spec 第 6、18 节）
- 所有给用户看的文字都是中文
- 测试：ViewModel 和设置用单元测试（假的 `IAppBackend`）；窗口和页面用本机运行 + 截图核对

## Review Focus

1. **用户在检查进行中又点了"立即检查"或"更新所选"** → 第二次点击无效（按钮已禁用），不会并发扫描或并发安装
2. **winget 不可用或某个来源出错** → 首页显示中文警告，软件列表照常显示
3. **更新过程中某一个安装失败** → 继续装下一个，每一行显示各自的结果；全部结束后自动重新检查
4. **设置文件被改坏**（不是合法 JSON、数值越界，如观察期 -5 天）→ 用默认值或把数值限制在合理范围内，不崩溃
5. **关闭窗口** → 托盘开启时缩到托盘而不是退出；托盘关闭时真正退出；从托盘菜单"退出"一定能退出

以上每条都在对应任务里有专门的测试或本机核对步骤。

---

## 文件结构

```
src/UpdateHelper.Presentation/
  UpdateHelper.Presentation.csproj
  Settings/AppSettings.cs          更新模式、观察期、检查间隔、托盘开关；默认值和范围限制
  Settings/SettingsStore.cs        读写 %LOCALAPPDATA%\UpdateHelper\settings.json，坏文件用默认值
  IAppBackend.cs                   界面使用底层的唯一入口 + ScanSnapshot
  AppState.cs                      共享状态：最近一次扫描、更新列表、是否忙、状态文字；RefreshAsync
  Display.cs                       档位、分类、结果的中文显示文字
  ViewModels/HomeViewModel.cs
  ViewModels/UpdatesViewModel.cs   含 UpdateRow
  ViewModels/SoftwareViewModel.cs  含 SoftwareRow
  ViewModels/BackgroundViewModel.cs 含 BackgroundRow
  ViewModels/HistoryViewModel.cs
  ViewModels/SettingsViewModel.cs
src/UpdateHelper.App/
  UpdateHelper.App.csproj
  App.xaml / App.xaml.cs           资源、单实例、启动时检查
  AppHost.cs                       组合根：创建 backend、AppState、各 ViewModel
  RealBackend.cs                   IAppBackend 的真实实现
  MainWindow.xaml / .cs            外壳、导航、关窗口缩到托盘
  TrayIcon.cs                      托盘图标、菜单、气泡通知
src/UpdateHelper.Presentation/TrayPolicy.cs    何时弹通知、关窗口缩到托盘还是退出
src/UpdateHelper.Presentation/Settings/SettingsService.cs  首页和设置页共用的当前设置
tools/Screenshot-Window.ps1      开发用：启动程序并截下主窗口
  Pages/HomePage.xaml / .cs
  Pages/UpdatesPage.xaml / .cs
  Pages/SoftwarePage.xaml / .cs
  Pages/BackgroundPage.xaml / .cs
  Pages/HistoryPage.xaml / .cs
  Pages/SettingsPage.xaml / .cs
tests/UpdateHelper.Presentation.Tests/
  UpdateHelper.Presentation.Tests.csproj
  FakeBackend.cs
  SettingsStoreTests.cs
  AppStateTests.cs
  UpdatesViewModelTests.cs
  PageViewModelTests.cs            首页、软件、后台项目、历史、设置
  TrayPolicyTests.cs
```

## 任务清单

- Task 1：Presentation 项目 + 设置（AppSettings、SettingsStore）
- Task 2：IAppBackend、AppState、Display
- Task 3：UpdatesViewModel（勾选、依次安装、结果）
- Task 4：其他页面的 ViewModel（首页、我的软件、后台项目、历史、设置）
- Task 5：App 项目：RealBackend、AppHost、主窗口和 6 个页面
- Task 6：托盘、通知、关窗口缩到托盘、单实例；本机运行并截图核对

---

### Task 1：Presentation 项目 + 设置（AppSettings、SettingsStore）

**Files:**
- Create: `src/UpdateHelper.Presentation/UpdateHelper.Presentation.csproj`
- Create: `src/UpdateHelper.Presentation/Settings/AppSettings.cs`
- Create: `src/UpdateHelper.Presentation/Settings/SettingsStore.cs`
- Create: `tests/UpdateHelper.Presentation.Tests/UpdateHelper.Presentation.Tests.csproj`
- Test: `tests/UpdateHelper.Presentation.Tests/SettingsStoreTests.cs`

**Interfaces:**
- Produces（命名空间 `UpdateHelper.Presentation.Settings`）：
  - `enum UpdateMode { NotifyOnly, Tiered, FullAuto, OnOpenOnly }`（只提醒 / 分级自动 / 全部自动 / 打开才检查）
  - `sealed record AppSettings`：`UpdateMode Mode = NotifyOnly`、`int ObservationDays = 3`、`int CheckIntervalHours = 24`、`bool TrayEnabled = true`；`AppSettings Normalized()` 把数值限制在范围内（观察期 0～30 天，检查间隔 1～168 小时），未定义的模式值换成 `NotifyOnly`
  - `sealed class SettingsStore(string filePath)`：`AppSettings Load()`（文件不存在或坏了都返回默认值，**永不抛异常**）、`void Save(AppSettings settings)`（先写临时文件再替换，避免写到一半断电留下坏文件）、`static string DefaultPath`（`%LOCALAPPDATA%\UpdateHelper\settings.json`）

- [ ] **Step 1: 建项目**

```bash
dotnet new classlib -o src/UpdateHelper.Presentation -n UpdateHelper.Presentation
rm src/UpdateHelper.Presentation/Class1.cs
dotnet new xunit -o tests/UpdateHelper.Presentation.Tests -n UpdateHelper.Presentation.Tests
rm tests/UpdateHelper.Presentation.Tests/UnitTest1.cs
dotnet sln add src/UpdateHelper.Presentation tests/UpdateHelper.Presentation.Tests
dotnet add src/UpdateHelper.Presentation reference src/UpdateHelper.Core
dotnet add src/UpdateHelper.Presentation package CommunityToolkit.Mvvm --version 8.4.2
dotnet add tests/UpdateHelper.Presentation.Tests reference src/UpdateHelper.Presentation src/UpdateHelper.Core
```

然后和计划 1 一样，删掉两个新 csproj 里模板自带的 `<TargetFramework>`、`<Nullable>`、`<ImplicitUsings>` 三行（由 `Directory.Build.props` 统一提供 `net10.0-windows`）。

Run: `dotnet build`
Expected: 0 警告 0 错误

- [ ] **Step 2: 写失败的测试** `tests/UpdateHelper.Presentation.Tests/SettingsStoreTests.cs`

```csharp
using System.Text;
using UpdateHelper.Presentation.Settings;

namespace UpdateHelper.Presentation.Tests;

public sealed class SettingsStoreTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("uh-settings-").FullName;
    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    private string PathOf(string name = "settings.json") => Path.Combine(_dir, "sub", name);

    [Fact]
    public void Missing_file_gives_defaults()
    {
        var s = new SettingsStore(PathOf()).Load();
        Assert.Equal(new AppSettings(), s);
        Assert.Equal(UpdateMode.NotifyOnly, s.Mode);   // spec 第 6 节：默认只提醒
        Assert.Equal(3, s.ObservationDays);
        Assert.Equal(24, s.CheckIntervalHours);
        Assert.True(s.TrayEnabled);
    }

    [Fact]
    public void Saved_settings_are_loaded_back()
    {
        var store = new SettingsStore(PathOf());   // 目录不存在也能保存
        var wanted = new AppSettings { Mode = UpdateMode.Tiered, ObservationDays = 7, CheckIntervalHours = 6, TrayEnabled = false };
        store.Save(wanted);
        Assert.Equal(wanted, store.Load());
        Assert.Contains("\"Tiered\"", File.ReadAllText(PathOf()));   // 模式按名字保存，文件可读
    }

    [Theory]   // Review Focus 4
    [InlineData("这不是 JSON")]
    [InlineData("{\"Mode\": \"NoSuchMode\"}")]
    [InlineData("[1, 2, 3]")]
    [InlineData("")]
    public void Corrupt_file_gives_defaults(string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(PathOf())!);
        File.WriteAllText(PathOf(), content, Encoding.UTF8);
        Assert.Equal(new AppSettings(), new SettingsStore(PathOf()).Load());
    }

    [Theory]   // Review Focus 4：数值越界时限制到范围内
    [InlineData(-5, 0, 0, 1)]
    [InlineData(999, 30, 1000, 168)]
    [InlineData(10, 10, 12, 12)]
    public void Out_of_range_numbers_are_clamped(int days, int expectedDays, int hours, int expectedHours)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(PathOf())!);
        File.WriteAllText(PathOf(), $"{{\"ObservationDays\": {days}, \"CheckIntervalHours\": {hours}}}");
        var s = new SettingsStore(PathOf()).Load();
        Assert.Equal(expectedDays, s.ObservationDays);
        Assert.Equal(expectedHours, s.CheckIntervalHours);
    }

    [Fact]
    public void Unknown_fields_are_ignored_and_missing_fields_use_defaults()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(PathOf())!);
        File.WriteAllText(PathOf(), "{\"Mode\": \"FullAuto\", \"SomethingNew\": 1}");
        var s = new SettingsStore(PathOf()).Load();
        Assert.Equal(UpdateMode.FullAuto, s.Mode);
        Assert.Equal(3, s.ObservationDays);
    }

    [Fact]
    public void Save_leaves_no_temporary_file()
    {
        new SettingsStore(PathOf()).Save(new AppSettings());
        Assert.Equal(new[] { "settings.json" }, Directory.GetFiles(Path.GetDirectoryName(PathOf())!).Select(Path.GetFileName));
    }
}
```

- [ ] **Step 3: 运行，确认失败**

Run: `dotnet test tests/UpdateHelper.Presentation.Tests`
Expected: 编译失败，找不到 `UpdateHelper.Presentation.Settings`

- [ ] **Step 4: 设置数据** `src/UpdateHelper.Presentation/Settings/AppSettings.cs`

```csharp
namespace UpdateHelper.Presentation.Settings;

/// <summary>四种更新模式（spec 第 6 节，用户自选）。</summary>
public enum UpdateMode
{
    /// <summary>只提醒：发现更新时通知，点击后自动装好（默认）</summary>
    NotifyOnly,
    /// <summary>分级自动：低风险的在空闲时自动装，其余提醒</summary>
    Tiered,
    /// <summary>全部自动：除锁定的软件外全部自动装</summary>
    FullAuto,
    /// <summary>打开才检查：没有后台检查</summary>
    OnOpenOnly,
}

/// <summary>用户设置。默认值来自 spec 第 6、18 节。</summary>
public sealed record AppSettings
{
    public const int MaxObservationDays = 30;
    public const int MinCheckIntervalHours = 1;
    public const int MaxCheckIntervalHours = 168;

    public UpdateMode Mode { get; init; } = UpdateMode.NotifyOnly;
    public int ObservationDays { get; init; } = 3;
    public int CheckIntervalHours { get; init; } = 24;
    public bool TrayEnabled { get; init; } = true;

    /// <summary>把越界的数值限制到范围内，未定义的模式换成默认。</summary>
    public AppSettings Normalized() => this with
    {
        Mode = Enum.IsDefined(Mode) ? Mode : UpdateMode.NotifyOnly,
        ObservationDays = Math.Clamp(ObservationDays, 0, MaxObservationDays),
        CheckIntervalHours = Math.Clamp(CheckIntervalHours, MinCheckIntervalHours, MaxCheckIntervalHours),
    };
}
```

- [ ] **Step 5: 读写** `src/UpdateHelper.Presentation/Settings/SettingsStore.cs`

```csharp
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace UpdateHelper.Presentation.Settings;

/// <summary>设置文件读写。读取永不抛异常：文件缺失或损坏都用默认值。</summary>
public sealed class SettingsStore(string filePath)
{
    public static string DefaultPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UpdateHelper", "settings.json");

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public AppSettings Load()
    {
        try
        {
            if (!File.Exists(filePath)) return new AppSettings();
            return (JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(filePath), Options) ?? new AppSettings()).Normalized();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return new AppSettings();
        }
    }

    /// <summary>先写临时文件再替换，写到一半断电也不会留下坏文件。</summary>
    public void Save(AppSettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(filePath))!);
        var temp = filePath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(settings.Normalized(), Options), new UTF8Encoding(false));
        File.Move(temp, filePath, overwrite: true);
    }
}
```

注：空文件和 `[1, 2, 3]` 都会让 `Deserialize` 抛 `JsonException`，落到默认值。

- [ ] **Step 6: 运行，确认通过**

Run: `dotnet test tests/UpdateHelper.Presentation.Tests`
Expected: 全部 PASS

- [ ] **Step 7: Commit**

```bash
git add -A
git commit -m "feat(presentation): 界面层项目与设置读写，坏文件用默认值"
```

### Task 2：IAppBackend、AppState、Display

**Files:**
- Create: `src/UpdateHelper.Presentation/IAppBackend.cs`
- Create: `src/UpdateHelper.Presentation/AppState.cs`
- Create: `src/UpdateHelper.Presentation/Display.cs`
- Create: `tests/UpdateHelper.Presentation.Tests/FakeBackend.cs`
- Test: `tests/UpdateHelper.Presentation.Tests/AppStateTests.cs`

**Interfaces:**
- Consumes: `ScanResult`、`SoftwareGroup`、`SoftwareCategory`、`SoftwareGrouper`（计划 1）；`BackgroundItem`、`BackgroundKind`（计划 1）；`RuleSet`（计划 2）；`UpdateReport`、`JudgedUpdate`、`UpdateTier`、`UpdateCandidate`（计划 3）；`ExecuteResult`、`ExecuteOutcome`、`HistoryRecord`（计划 4）
- Produces（命名空间 `UpdateHelper.Presentation`）：
  - `sealed record ScanSnapshot(ScanResult Result, RuleSet Rules, IReadOnlyDictionary<BackgroundItem, string> Explanations, IReadOnlyList<string> Warnings)`——已经套用过规则的扫描结果
  - `interface IAppBackend`：`Task<ScanSnapshot> ScanAsync(CancellationToken)`、`Task<UpdateReport> CheckUpdatesAsync(ScanSnapshot, CancellationToken)`、`Task<ExecuteResult> InstallAsync(JudgedUpdate, IProgress<double>?, CancellationToken)`、`IReadOnlyList<HistoryRecord> ReadHistory()`
  - `sealed partial class AppState(IAppBackend backend, TimeProvider? clock = null) : ObservableObject`：可观察属性 `ScanSnapshot? Snapshot`、`IReadOnlyList<JudgedUpdate> Updates`（初始为空）、`DateTimeOffset? LastChecked`、`bool IsBusy`、`string StatusText`、`string? Warning`；方法 `Task RefreshAsync()`（忙时直接返回）、`Task<bool> RunExclusiveAsync(Func<Task> work)`（忙时返回 false，不执行）；属性 `IAppBackend Backend`
  - `static class Display`：`TierName(UpdateTier)`、`CategoryName(SoftwareCategory)`、`OutcomeName(ExecuteOutcome)`、`KindName(BackgroundKind)`——中文显示文字
  - 测试辅助 `FakeBackend : IAppBackend`（可控制返回值、可以"卡住"等待、记录调用次数）

**线程约定：** `AppState` 里的 `await` 不加 `ConfigureAwait(false)`。界面线程调用时，后续代码回到界面线程，属性和集合都在界面线程上更新；耗时工作由 `RealBackend` 放到后台线程（Task 5）。

- [ ] **Step 1: 测试辅助** `tests/UpdateHelper.Presentation.Tests/FakeBackend.cs`

```csharp
using UpdateHelper.Core.Grouping;
using UpdateHelper.Core.Install;
using UpdateHelper.Core.Rules;
using UpdateHelper.Core.Scanning;
using UpdateHelper.Core.Updates;

namespace UpdateHelper.Presentation.Tests;

/// <summary>可控制的假底层：返回固定结果，可以让扫描"卡住"直到测试放行。</summary>
public sealed class FakeBackend : IAppBackend
{
    public ScanSnapshot Snapshot { get; set; } = MakeSnapshot([]);
    public UpdateReport Report { get; set; } = new([], null);
    public Exception? ScanError { get; set; }
    public TaskCompletionSource? ScanGate { get; set; }
    public Func<JudgedUpdate, ExecuteResult> Install { get; set; } =
        u => new ExecuteResult(ExecuteOutcome.Succeeded, $"已从 {u.Candidate.InstalledVersion} 更新到 {u.Candidate.AvailableVersion}", u.Candidate.AvailableVersion);
    public List<HistoryRecord> History { get; } = [];

    public int ScanCalls { get; private set; }
    public List<string> Installed { get; } = [];

    public async Task<ScanSnapshot> ScanAsync(CancellationToken cancellationToken)
    {
        ScanCalls++;
        if (ScanGate is not null) await ScanGate.Task;
        if (ScanError is not null) throw ScanError;
        return Snapshot;
    }

    public Task<UpdateReport> CheckUpdatesAsync(ScanSnapshot snapshot, CancellationToken cancellationToken)
        => Task.FromResult(Report);

    public Task<ExecuteResult> InstallAsync(JudgedUpdate update, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        Installed.Add(update.Candidate.PackageId);
        return Task.FromResult(Install(update));
    }

    public IReadOnlyList<HistoryRecord> ReadHistory() => History;

    // —— 构造测试数据 ——

    public static UninstallEntry Entry(string name, string? version = "1.0", string? publisher = "Pub",
        string? location = null, bool hidden = false)
        => new(name, UninstallHive.LocalMachine64, name, version, publisher, location, null, null, hidden, null, null, null);

    public static ScanSnapshot MakeSnapshot(IReadOnlyList<UninstallEntry> entries, IReadOnlyList<BackgroundItem>? background = null,
        IReadOnlyDictionary<BackgroundItem, string>? explanations = null, IReadOnlyList<string>? warnings = null)
        => new(SoftwareGrouper.Group(entries, background ?? []), RuleSet.Empty,
               explanations ?? new Dictionary<BackgroundItem, string>(), warnings ?? []);

    public static JudgedUpdate Update(string name, UpdateTier tier, string reason = "小版本更新", SoftwareGroup? group = null)
        => new(new UpdateCandidate("Pkg." + name, name, null, "1.0", "1.1", [name]), group, tier, reason);
}
```

- [ ] **Step 2: 写失败的测试** `tests/UpdateHelper.Presentation.Tests/AppStateTests.cs`

```csharp
using UpdateHelper.Core.Updates;
using static UpdateHelper.Presentation.Tests.FakeBackend;

namespace UpdateHelper.Presentation.Tests;

public class AppStateTests
{
    [Fact]
    public async Task Refresh_fills_snapshot_updates_and_time()
    {
        var backend = new FakeBackend
        {
            Snapshot = MakeSnapshot([Entry("QQ")]),
            Report = new UpdateReport([Update("QQ", UpdateTier.Low)], null),
        };
        var state = new AppState(backend);

        await state.RefreshAsync();

        Assert.Same(backend.Snapshot, state.Snapshot);
        Assert.Single(state.Updates);
        Assert.NotNull(state.LastChecked);
        Assert.False(state.IsBusy);
        Assert.Equal("", state.StatusText);
        Assert.Null(state.Warning);
    }

    [Fact]
    public async Task Second_refresh_while_busy_is_ignored()   // Review Focus 1
    {
        var gate = new TaskCompletionSource();
        var backend = new FakeBackend { ScanGate = gate };
        var state = new AppState(backend);

        var first = state.RefreshAsync();
        Assert.True(state.IsBusy);
        Assert.Equal("正在扫描电脑上的软件……", state.StatusText);

        await state.RefreshAsync();          // 忙时直接返回
        Assert.Equal(1, backend.ScanCalls);

        gate.SetResult();
        await first;
        Assert.False(state.IsBusy);
    }

    [Fact]
    public async Task Exclusive_work_is_refused_while_busy()
    {
        var gate = new TaskCompletionSource();
        var state = new AppState(new FakeBackend { ScanGate = gate });
        var refresh = state.RefreshAsync();

        var ran = false;
        Assert.False(await state.RunExclusiveAsync(() => { ran = true; return Task.CompletedTask; }));
        Assert.False(ran);

        gate.SetResult();
        await refresh;
        Assert.True(await state.RunExclusiveAsync(() => { ran = true; return Task.CompletedTask; }));
        Assert.True(ran);
        Assert.False(state.IsBusy);
    }

    [Fact]
    public async Task Scan_failure_becomes_warning_and_keeps_old_snapshot()   // Review Focus 2
    {
        var backend = new FakeBackend { Snapshot = MakeSnapshot([Entry("QQ")]) };
        var state = new AppState(backend);
        await state.RefreshAsync();
        var old = state.Snapshot;

        backend.ScanError = new InvalidOperationException("注册表读取失败");
        await state.RefreshAsync();

        Assert.Same(old, state.Snapshot);
        Assert.Equal("检查失败：注册表读取失败", state.Warning);
        Assert.False(state.IsBusy);
    }

    [Fact]
    public async Task Warnings_from_scan_and_update_check_are_joined()   // Review Focus 2
    {
        var backend = new FakeBackend
        {
            Snapshot = MakeSnapshot([Entry("QQ")], warnings: ["读取计划任务失败"]),
            Report = new UpdateReport([], "无法从 winget 获取更新信息：没有找到 winget"),
        };
        var state = new AppState(backend);
        await state.RefreshAsync();

        Assert.Equal("读取计划任务失败；无法从 winget 获取更新信息：没有找到 winget", state.Warning);
        Assert.NotNull(state.Snapshot);   // 软件列表照常可用
    }

    [Theory]
    [InlineData(UpdateTier.Low, "低风险")]
    [InlineData(UpdateTier.Careful, "需确认")]
    [InlineData(UpdateTier.NeverAuto, "不自动")]
    [InlineData(UpdateTier.Ignored, "不管")]
    public void Tier_names(UpdateTier tier, string name) => Assert.Equal(name, Display.TierName(tier));
}
```

- [ ] **Step 3: 运行，确认失败**

Run: `dotnet test tests/UpdateHelper.Presentation.Tests`
Expected: 编译失败，找不到 `AppState`、`IAppBackend`

- [ ] **Step 4: 接口** `src/UpdateHelper.Presentation/IAppBackend.cs`

```csharp
using UpdateHelper.Core.Grouping;
using UpdateHelper.Core.Install;
using UpdateHelper.Core.Rules;
using UpdateHelper.Core.Scanning;
using UpdateHelper.Core.Updates;

namespace UpdateHelper.Presentation;

/// <summary>一次扫描的结果（已套用规则），以及后台项目说明和扫描时的警告。</summary>
public sealed record ScanSnapshot(
    ScanResult Result,
    RuleSet Rules,
    IReadOnlyDictionary<BackgroundItem, string> Explanations,
    IReadOnlyList<string> Warnings);

/// <summary>界面使用底层的唯一入口。真实实现在 App 项目（RealBackend），测试用 FakeBackend。</summary>
public interface IAppBackend
{
    Task<ScanSnapshot> ScanAsync(CancellationToken cancellationToken);
    Task<UpdateReport> CheckUpdatesAsync(ScanSnapshot snapshot, CancellationToken cancellationToken);
    Task<ExecuteResult> InstallAsync(JudgedUpdate update, IProgress<double>? progress, CancellationToken cancellationToken);
    IReadOnlyList<HistoryRecord> ReadHistory();
}
```

- [ ] **Step 5: 共享状态** `src/UpdateHelper.Presentation/AppState.cs`

```csharp
using CommunityToolkit.Mvvm.ComponentModel;
using UpdateHelper.Core.Updates;

namespace UpdateHelper.Presentation;

/// <summary>
/// 各页面共享的状态：最近一次扫描、更新列表、是否正在忙。
/// 同一时间只做一件耗时的事（扫描/检查或安装），忙时新的请求直接忽略（Review Focus 1）。
/// </summary>
public sealed partial class AppState(IAppBackend backend, TimeProvider? clock = null) : ObservableObject
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    public IAppBackend Backend => backend;

    [ObservableProperty] private ScanSnapshot? _snapshot;
    [ObservableProperty] private IReadOnlyList<JudgedUpdate> _updates = [];
    [ObservableProperty] private DateTimeOffset? _lastChecked;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private string? _warning;

    /// <summary>重新扫描并检查更新。忙时直接返回；出错转成 Warning，不抛异常。</summary>
    public async Task RefreshAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        try
        {
            StatusText = "正在扫描电脑上的软件……";
            var snapshot = await backend.ScanAsync(CancellationToken.None);
            Snapshot = snapshot;

            StatusText = "正在检查更新……";
            var report = await backend.CheckUpdatesAsync(snapshot, CancellationToken.None);
            Updates = report.Updates;

            var warnings = snapshot.Warnings.Concat(report.Warning is null ? [] : [report.Warning]).ToList();
            Warning = warnings.Count == 0 ? null : string.Join("；", warnings);
            LastChecked = _clock.GetLocalNow();
        }
        catch (Exception ex)
        {
            Warning = $"检查失败：{ex.Message}";
        }
        finally
        {
            StatusText = "";
            IsBusy = false;
        }
    }

    /// <summary>在"不忙"时独占执行一段工作（如安装）。忙时返回 false 且不执行。</summary>
    public async Task<bool> RunExclusiveAsync(Func<Task> work)
    {
        if (IsBusy) return false;
        IsBusy = true;
        try
        {
            await work();
            return true;
        }
        finally
        {
            IsBusy = false;
        }
    }
}
```

注：`[ObservableProperty]` 由 CommunityToolkit.Mvvm 的源生成器生成同名的大写属性（`_snapshot` → `Snapshot`），类必须是 `partial`。主构造函数参数 `backend` 被生成器之外的方法捕获，这是允许的。

- [ ] **Step 6: 显示文字** `src/UpdateHelper.Presentation/Display.cs`

```csharp
using UpdateHelper.Core.Grouping;
using UpdateHelper.Core.Install;
using UpdateHelper.Core.Scanning;
using UpdateHelper.Core.Updates;

namespace UpdateHelper.Presentation;

/// <summary>界面上显示的中文文字。</summary>
public static class Display
{
    public static string TierName(UpdateTier tier) => tier switch
    {
        UpdateTier.Low => "低风险",
        UpdateTier.Careful => "需确认",
        UpdateTier.NeverAuto => "不自动",
        _ => "不管",
    };

    public static string CategoryName(SoftwareCategory category) => category switch
    {
        SoftwareCategory.Application => "普通软件",
        SoftwareCategory.DevTool => "开发工具",
        SoftwareCategory.Game => "游戏",
        SoftwareCategory.Runtime => "运行库",
        SoftwareCategory.Driver => "驱动",
        _ => "系统组件",
    };

    public static string OutcomeName(ExecuteOutcome outcome) => outcome switch
    {
        ExecuteOutcome.Succeeded => "已更新",
        ExecuteOutcome.NeedsReboot => "需要重启",
        ExecuteOutcome.Failed => "失败",
        ExecuteOutcome.Refused => "没有安装",
        _ => "已取消",
    };

    public static string KindName(BackgroundKind kind) => kind switch
    {
        BackgroundKind.Service => "服务",
        BackgroundKind.RunKey => "开机自启",
        BackgroundKind.StartupFolder => "启动文件夹",
        _ => "计划任务",
    };
}
```

- [ ] **Step 7: 运行，确认通过**

Run: `dotnet test tests/UpdateHelper.Presentation.Tests`
Expected: 全部 PASS

- [ ] **Step 8: Commit**

```bash
git add -A
git commit -m "feat(presentation): 界面共享状态，忙时忽略重复请求，出错转成警告"
```

### Task 3：UpdatesViewModel（勾选、依次安装、结果）

**Files:**
- Create: `src/UpdateHelper.Presentation/ViewModels/UpdatesViewModel.cs`
- Test: `tests/UpdateHelper.Presentation.Tests/UpdatesViewModelTests.cs`

**Interfaces:**
- Consumes: `AppState`、`Display`、`IAppBackend`（Task 2）；`JudgedUpdate`、`UpdateTier`（计划 3）；`ExecuteResult`、`ExecuteOutcome`（计划 4）；`FakeBackend`（Task 2）
- Produces（命名空间 `UpdateHelper.Presentation.ViewModels`）：
  - `sealed partial class UpdateRow : ObservableObject`：`JudgedUpdate Update`、`string Name`、`string Versions`（"1.0 → 1.1"）、`string TierName`、`string Reason`、`bool CanSelect`（只有低风险、需确认可以勾选）、可观察的 `bool IsSelected`（默认只勾低风险；不能勾选的行永远是 false）、`string Status`
  - `sealed partial class UpdatesViewModel : ObservableObject`：`ObservableCollection<UpdateRow> Rows`（不含"不管"档）、`ObservableCollection<string> LastResults`（最近一次批量更新的结果，每行"名称：结果"）、`string Summary`、`IAsyncRelayCommand InstallSelectedCommand`、`IAsyncRelayCommand RefreshCommand`
  - `AppState.Updates` 变化时自动重建 `Rows`

**批量更新流程：**
1. 没有勾选任何行，或 `AppState` 正忙 → 命令不可用（按钮变灰，Review Focus 1）
2. 通过 `AppState.RunExclusiveAsync` 独占执行，按列表顺序**一个一个**安装
3. 每一行先显示"正在更新……"，结束后显示"{结果}：{说明}"；某一个出错或抛异常，记下来，**继续下一个**（Review Focus 3）
4. 全部结束后把结果写进 `LastResults`（重新检查后行会被重建，所以结果另外保存），然后调用 `AppState.RefreshAsync()` 重新检查

- [ ] **Step 1: 写失败的测试** `tests/UpdateHelper.Presentation.Tests/UpdatesViewModelTests.cs`

```csharp
using UpdateHelper.Core.Install;
using UpdateHelper.Core.Updates;
using UpdateHelper.Presentation.ViewModels;
using static UpdateHelper.Presentation.Tests.FakeBackend;

namespace UpdateHelper.Presentation.Tests;

public class UpdatesViewModelTests
{
    private static async Task<(UpdatesViewModel Vm, FakeBackend Backend, AppState State)> Make(params JudgedUpdate[] updates)
    {
        var backend = new FakeBackend { Report = new UpdateReport(updates, null) };
        var state = new AppState(backend);
        await state.RefreshAsync();
        return (new UpdatesViewModel(state), backend, state);
    }

    [Fact]
    public async Task Rows_follow_tiers_and_default_selection()
    {
        var (vm, _, _) = await Make(
            Update("QQ", UpdateTier.Low),
            Update("VirtualBox", UpdateTier.Careful, "开发工具跨了小版本（7.0 → 7.2）"),
            Update("微信", UpdateTier.NeverAuto, "电脑上登记了多个\"微信\""),
            Update("CS2", UpdateTier.Ignored, "游戏由 Steam 等平台负责更新"));

        Assert.Equal(new[] { "QQ", "VirtualBox", "微信" }, vm.Rows.Select(r => r.Name));   // "不管"档不显示
        Assert.Equal(new[] { true, false, false }, vm.Rows.Select(r => r.IsSelected));       // 默认只勾低风险
        Assert.Equal(new[] { true, true, false }, vm.Rows.Select(r => r.CanSelect));
        Assert.Equal("1.0 → 1.1", vm.Rows[0].Versions);
        Assert.Equal("需确认", vm.Rows[1].TierName);
        Assert.Equal("3 个更新，已选 1 个", vm.Summary);
    }

    [Fact]
    public async Task Never_auto_row_cannot_be_selected()
    {
        var (vm, _, _) = await Make(Update("微信", UpdateTier.NeverAuto));
        vm.Rows[0].IsSelected = true;
        Assert.False(vm.Rows[0].IsSelected);
    }

    [Fact]
    public async Task Selection_changes_update_summary_and_command()
    {
        var (vm, _, _) = await Make(Update("QQ", UpdateTier.Low), Update("VirtualBox", UpdateTier.Careful));
        vm.Rows[1].IsSelected = true;
        Assert.Equal("2 个更新，已选 2 个", vm.Summary);

        vm.Rows[0].IsSelected = false;
        vm.Rows[1].IsSelected = false;
        Assert.False(vm.InstallSelectedCommand.CanExecute(null));
    }

    [Fact]
    public async Task Installs_selected_rows_one_by_one_then_rechecks()
    {
        var (vm, backend, _) = await Make(Update("QQ", UpdateTier.Low), Update("网易云音乐", UpdateTier.Low),
            Update("VirtualBox", UpdateTier.Careful));
        var scansBefore = backend.ScanCalls;

        await vm.InstallSelectedCommand.ExecuteAsync(null);

        Assert.Equal(new[] { "Pkg.QQ", "Pkg.网易云音乐" }, backend.Installed);   // 没勾的 VirtualBox 不装
        Assert.Equal(new[] { "QQ：已更新：已从 1.0 更新到 1.1", "网易云音乐：已更新：已从 1.0 更新到 1.1" }, vm.LastResults);
        Assert.Equal(scansBefore + 1, backend.ScanCalls);   // 结束后重新检查
    }

    [Fact]
    public async Task One_failure_does_not_stop_the_rest()   // Review Focus 3
    {
        var (vm, backend, _) = await Make(Update("A", UpdateTier.Low), Update("B", UpdateTier.Low), Update("C", UpdateTier.Low));
        backend.Install = u => u.Candidate.Name switch
        {
            "A" => new ExecuteResult(ExecuteOutcome.Failed, "安装失败：InstallError", null),
            "B" => throw new InvalidOperationException("RPC 断开"),
            _ => new ExecuteResult(ExecuteOutcome.Succeeded, "已从 1.0 更新到 1.1", "1.1"),
        };

        await vm.InstallSelectedCommand.ExecuteAsync(null);

        Assert.Equal(new[] { "Pkg.A", "Pkg.B", "Pkg.C" }, backend.Installed);
        Assert.Equal(new[] { "A：失败：安装失败：InstallError", "B：失败：RPC 断开", "C：已更新：已从 1.0 更新到 1.1" }, vm.LastResults);
    }

    [Fact]
    public async Task Command_is_disabled_while_busy()   // Review Focus 1
    {
        var (vm, backend, state) = await Make(Update("QQ", UpdateTier.Low));
        var gate = new TaskCompletionSource();
        backend.ScanGate = gate;

        var refresh = state.RefreshAsync();
        Assert.False(vm.InstallSelectedCommand.CanExecute(null));

        gate.SetResult();
        await refresh;
        Assert.True(vm.InstallSelectedCommand.CanExecute(null));
    }
}
```

- [ ] **Step 2: 运行，确认失败**

Run: `dotnet test tests/UpdateHelper.Presentation.Tests`
Expected: 编译失败，找不到 `UpdatesViewModel`

- [ ] **Step 3: 实现** `src/UpdateHelper.Presentation/ViewModels/UpdatesViewModel.cs`

```csharp
using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UpdateHelper.Core.Updates;

namespace UpdateHelper.Presentation.ViewModels;

/// <summary>更新列表里的一行。</summary>
public sealed partial class UpdateRow : ObservableObject
{
    public UpdateRow(JudgedUpdate update)
    {
        Update = update;
        CanSelect = update.Tier is UpdateTier.Low or UpdateTier.Careful;
        IsSelected = update.Tier == UpdateTier.Low;    // 默认只勾低风险（用属性赋值，不直接写字段：MVVMTK0034）
    }

    public JudgedUpdate Update { get; }
    public string Name => Update.Group?.Name ?? Update.Candidate.Name;
    public string Versions => $"{Update.Candidate.InstalledVersion} → {Update.Candidate.AvailableVersion}";
    public string TierName => Display.TierName(Update.Tier);
    public string Reason => Update.Reason;

    /// <summary>"不自动"的更新不能勾选（spec 第 5 节）。</summary>
    public bool CanSelect { get; }

    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private string _status = "";

    partial void OnIsSelectedChanged(bool value)
    {
        if (value && !CanSelect) IsSelected = false;
    }
}

/// <summary>更新页：列出可用更新，勾选后依次安装。</summary>
public sealed partial class UpdatesViewModel : ObservableObject
{
    private readonly AppState _state;

    public UpdatesViewModel(AppState state)
    {
        _state = state;
        _state.PropertyChanged += OnStateChanged;
        Rebuild();
    }

    public ObservableCollection<UpdateRow> Rows { get; } = [];
    public ObservableCollection<string> LastResults { get; } = [];

    public string Summary => $"{Rows.Count} 个更新，已选 {Rows.Count(r => r.IsSelected)} 个";

    private bool CanInstall() => !_state.IsBusy && Rows.Any(r => r.IsSelected);

    [RelayCommand(CanExecute = nameof(CanInstall))]
    private async Task InstallSelectedAsync()
    {
        var selected = Rows.Where(r => r.IsSelected).ToList();
        var results = new List<string>();

        var ran = await _state.RunExclusiveAsync(async () =>
        {
            foreach (var row in selected)
            {
                row.Status = "正在更新……";
                string text;
                try
                {
                    var result = await _state.Backend.InstallAsync(row.Update, null, CancellationToken.None);
                    text = $"{Display.OutcomeName(result.Outcome)}：{result.Message}";
                }
                catch (Exception ex)
                {
                    text = $"失败：{ex.Message}";   // 一个出错，继续下一个
                }
                row.Status = text;
                results.Add($"{row.Name}：{text}");
            }
        });
        if (!ran) return;

        LastResults.Clear();
        foreach (var line in results) LastResults.Add(line);
        await _state.RefreshAsync();
    }

    [RelayCommand]
    private Task RefreshAsync() => _state.RefreshAsync();

    private void OnStateChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AppState.Updates)) Rebuild();
        if (e.PropertyName == nameof(AppState.IsBusy)) InstallSelectedCommand.NotifyCanExecuteChanged();
    }

    private void Rebuild()
    {
        foreach (var row in Rows) row.PropertyChanged -= OnRowChanged;
        Rows.Clear();
        foreach (var update in _state.Updates.Where(u => u.Tier != UpdateTier.Ignored))
        {
            var row = new UpdateRow(update);
            row.PropertyChanged += OnRowChanged;
            Rows.Add(row);
        }
        SelectionChanged();
    }

    private void OnRowChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(UpdateRow.IsSelected)) SelectionChanged();
    }

    private void SelectionChanged()
    {
        OnPropertyChanged(nameof(Summary));
        InstallSelectedCommand.NotifyCanExecuteChanged();
    }
}
```

注：`[RelayCommand]` 对 `InstallSelectedAsync` 方法生成名为 `InstallSelectedCommand` 的 `IAsyncRelayCommand`（去掉 Async 后缀）。异步命令在执行期间默认不允许再次执行，加上 `CanInstall` 里的 `IsBusy` 判断，双击也不会重复安装。

- [ ] **Step 4: 运行，确认通过**

Run: `dotnet test tests/UpdateHelper.Presentation.Tests`
Expected: 全部 PASS

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat(presentation): 更新页逻辑——勾选、依次安装、单个失败不影响其余"
```

### Task 4：其他页面的 ViewModel（首页、我的软件、后台项目、历史、设置）

**Files:**
- Create: `src/UpdateHelper.Presentation/Settings/SettingsService.cs`
- Create: `src/UpdateHelper.Presentation/ViewModels/HomeViewModel.cs`
- Create: `src/UpdateHelper.Presentation/ViewModels/SoftwareViewModel.cs`
- Create: `src/UpdateHelper.Presentation/ViewModels/BackgroundViewModel.cs`
- Create: `src/UpdateHelper.Presentation/ViewModels/HistoryViewModel.cs`
- Create: `src/UpdateHelper.Presentation/ViewModels/SettingsViewModel.cs`
- Test: `tests/UpdateHelper.Presentation.Tests/PageViewModelTests.cs`

**Interfaces:**
- Consumes: `AppSettings`、`UpdateMode`、`SettingsStore`（Task 1）；`AppState`、`Display`、`ScanSnapshot`（Task 2）；`SoftwareGroup`、`SoftwareCategory`、`BackgroundItem`（计划 1）；`HistoryRecord`（计划 4）；`FakeBackend`
- Produces：
  - `sealed class SettingsService : ObservableObject`（命名空间 `UpdateHelper.Presentation.Settings`）：`SettingsService(SettingsStore store)`、`AppSettings Current`（只读，变化时通知）、`void Update(Func<AppSettings, AppSettings> change)`（规范化后保存；保存失败把中文原因写进 `string? SaveError`，不抛异常）——首页和设置页共用同一个实例
  - `HomeViewModel(AppState, SettingsService)`：`SummaryTitle`、`SummaryDetail`、`LastCheckedText`、`SoftwareCountText`、`string? Warning`、`int ModeIndex`（读写，对应 `UpdateMode` 的顺序）、`IAsyncRelayCommand CheckNowCommand`（忙时不可用）
  - `SoftwareViewModel(AppState)`：`ObservableCollection<SoftwareRow> Rows`、`bool ShowAll`（默认 false：只显示普通软件和开发工具）、`string SearchText`、`string CountText`；`sealed record SoftwareRow(string Name, string Publisher, string Version, string Category, string Components, string Location)`
  - `BackgroundViewModel(AppState)`：`ObservableCollection<BackgroundRow> Rows`、`string CountText`；`sealed record BackgroundRow(string Kind, string Name, string Owner, string Explanation, string Path)`
  - `HistoryViewModel(AppState)`：`ObservableCollection<HistoryRow> Rows`（最新的在前）、`IRelayCommand ReloadCommand`；`sealed record HistoryRow(string Time, string Name, string Versions, string Outcome, string Message, string Trigger)`
  - `SettingsViewModel(SettingsService)`：`int ModeIndex`、`int ObservationDays`、`int CheckIntervalHours`、`bool TrayEnabled`、`string AutoUpdateNote`、`string? SaveError`

各页面在 `AppState` 对应属性变化时自动刷新（首页：`Updates`/`LastChecked`/`IsBusy`/`StatusText`/`Warning`；软件和后台项目：`Snapshot`；历史：`LastChecked`，因为每次安装结束都会重新检查）。

- [ ] **Step 1: 写失败的测试** `tests/UpdateHelper.Presentation.Tests/PageViewModelTests.cs`

```csharp
using UpdateHelper.Core.Install;
using UpdateHelper.Core.Scanning;
using UpdateHelper.Core.Updates;
using UpdateHelper.Presentation.Settings;
using UpdateHelper.Presentation.ViewModels;
using static UpdateHelper.Presentation.Tests.FakeBackend;

namespace UpdateHelper.Presentation.Tests;

public sealed class PageViewModelTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("uh-pages-").FullName;
    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    private SettingsService Settings() => new(new SettingsStore(Path.Combine(_dir, "settings.json")));

    // —— 首页 ——

    [Fact]
    public void Home_before_first_check()
    {
        var home = new HomeViewModel(new AppState(new FakeBackend()), Settings());
        Assert.Equal("还没有检查更新", home.SummaryTitle);
        Assert.Equal("还没有检查", home.LastCheckedText);
    }

    [Fact]
    public async Task Home_summarizes_updates_by_tier()
    {
        var backend = new FakeBackend
        {
            Snapshot = MakeSnapshot([Entry("QQ"), Entry("微信"), Entry("CUBLAS", hidden: true)]),
            Report = new UpdateReport([
                Update("QQ", UpdateTier.Low), Update("A", UpdateTier.Low), Update("B", UpdateTier.Careful),
                Update("微信", UpdateTier.NeverAuto), Update("CS2", UpdateTier.Ignored)], null),
        };
        var state = new AppState(backend);
        var home = new HomeViewModel(state, Settings());

        await state.RefreshAsync();

        Assert.Equal("有 4 个更新可用", home.SummaryTitle);   // "不管"档不算
        Assert.Equal("其中 2 个低风险、1 个需确认、1 个不自动", home.SummaryDetail);
        Assert.Equal("已识别 2 个软件", home.SoftwareCountText);   // 组件合集不算
        Assert.StartsWith("上次检查：", home.LastCheckedText);
    }

    [Fact]
    public async Task Home_all_up_to_date_and_warning()
    {
        var backend = new FakeBackend { Report = new UpdateReport([Update("CS2", UpdateTier.Ignored)], "无法从 winget 获取更新信息：x") };
        var state = new AppState(backend);
        var home = new HomeViewModel(state, Settings());
        await state.RefreshAsync();

        Assert.Equal("所有软件都是最新的", home.SummaryTitle);
        Assert.Equal("", home.SummaryDetail);
        Assert.Equal("无法从 winget 获取更新信息：x", home.Warning);
    }

    [Fact]
    public async Task Home_check_now_is_disabled_while_busy()   // Review Focus 1
    {
        var gate = new TaskCompletionSource();
        var state = new AppState(new FakeBackend { ScanGate = gate });
        var home = new HomeViewModel(state, Settings());

        var refresh = state.RefreshAsync();
        Assert.False(home.CheckNowCommand.CanExecute(null));
        Assert.Equal("正在扫描电脑上的软件……", home.SummaryTitle);

        gate.SetResult();
        await refresh;
        Assert.True(home.CheckNowCommand.CanExecute(null));
    }

    [Fact]
    public void Mode_is_shared_between_home_and_settings_and_saved()
    {
        var path = Path.Combine(_dir, "settings.json");
        var service = new SettingsService(new SettingsStore(path));
        var home = new HomeViewModel(new AppState(new FakeBackend()), service);
        var settings = new SettingsViewModel(service);

        home.ModeIndex = (int)UpdateMode.Tiered;

        Assert.Equal((int)UpdateMode.Tiered, settings.ModeIndex);
        Assert.Equal(UpdateMode.Tiered, new SettingsStore(path).Load().Mode);
    }

    [Fact]
    public void Settings_values_are_clamped_and_saved()   // Review Focus 4
    {
        var path = Path.Combine(_dir, "settings.json");
        var vm = new SettingsViewModel(new SettingsService(new SettingsStore(path)));

        vm.ObservationDays = 99;
        vm.CheckIntervalHours = 0;
        vm.TrayEnabled = false;

        Assert.Equal(30, vm.ObservationDays);
        Assert.Equal(1, vm.CheckIntervalHours);
        var saved = new SettingsStore(path).Load();
        Assert.Equal((30, 1, false), (saved.ObservationDays, saved.CheckIntervalHours, saved.TrayEnabled));
        Assert.Contains("后续版本", vm.AutoUpdateNote);
    }

    // —— 我的软件 ——

    private static async Task<AppState> StateWith(ScanSnapshot snapshot)
    {
        var state = new AppState(new FakeBackend { Snapshot = snapshot });
        await state.RefreshAsync();
        return state;
    }

    [Fact]
    public async Task Software_list_filters_and_searches()
    {
        var state = await StateWith(MakeSnapshot([
            Entry("QQ", "9.9.20", "腾讯", @"C:\Program Files\Tencent\QQNT"),
            Entry("Git", "2.50.1", "The Git Development Community"),
            Entry("Microsoft Visual C++ 2013 Redistributable (x64)", "12.0", "Microsoft Corporation"),
            Entry("Python 3.10.2 (64-bit)", "3.10.2", "Python Software Foundation"),
            Entry("Python 3.10.2 Core Interpreter (64-bit)", null, "Python Software Foundation", hidden: true)]));
        var vm = new SoftwareViewModel(state);

        Assert.Equal(new[] { "QQ", "Git", "Python 3.10.2 (64-bit)" }.Order(), vm.Rows.Select(r => r.Name).Order());  // 默认不含运行库
        Assert.Equal("共 3 个", vm.CountText);
        Assert.Equal("含 1 个组件", vm.Rows.Single(r => r.Name.StartsWith("Python")).Components);
        Assert.Equal("普通软件", vm.Rows.Single(r => r.Name == "QQ").Category);

        vm.ShowAll = true;
        Assert.Equal(4, vm.Rows.Count);

        vm.SearchText = "python";
        Assert.Equal("Python 3.10.2 (64-bit)", Assert.Single(vm.Rows).Name);
    }

    // —— 后台项目 ——

    [Fact]
    public async Task Background_rows_show_owner_and_explanation()
    {
        var task = new BackgroundItem(BackgroundKind.ScheduledTask, "WpsUpdateTask", null, null, @"D:\WPS\up.exe", null);
        var service = new BackgroundItem(BackgroundKind.Service, "gupdate", null, null, @"C:\Google\GoogleUpdate.exe", null);
        var state = await StateWith(MakeSnapshot(
            [Entry("WPS Office", location: @"D:\WPS")], [task, service],
            explanations: new Dictionary<BackgroundItem, string> { [task] = "WPS 的自动更新任务" }));
        var vm = new BackgroundViewModel(state);

        var wps = vm.Rows.Single(r => r.Name == "WpsUpdateTask");
        Assert.Equal(("计划任务", "WPS Office", "WPS 的自动更新任务"), (wps.Kind, wps.Owner, wps.Explanation));
        var g = vm.Rows.Single(r => r.Name == "gupdate");
        Assert.Equal(("服务", "（未归属）", "未知用途"), (g.Kind, g.Owner, g.Explanation));
        Assert.Equal("共 2 个后台项目", vm.CountText);
    }

    // —— 历史 ——

    [Fact]
    public async Task History_is_newest_first_with_readable_fields()
    {
        var backend = new FakeBackend();
        backend.History.Add(new HistoryRecord(new DateTimeOffset(2026, 10, 6, 20, 50, 0, TimeSpan.FromHours(8)),
            "Bilibili.Bilibili", "哔哩哔哩", "1.16.5", "1.19.0", ExecuteOutcome.Succeeded, "已从 1.16.5 更新到 1.19.0", false));
        backend.History.Add(new HistoryRecord(new DateTimeOffset(2026, 10, 7, 9, 0, 0, TimeSpan.FromHours(8)),
            "rule:x", "EV录屏", "5.2.3", "9.9.9", ExecuteOutcome.Failed, "签名不对", true));
        var state = new AppState(backend);
        var vm = new HistoryViewModel(state);

        Assert.Equal(new[] { "EV录屏", "哔哩哔哩" }, vm.Rows.Select(r => r.Name));
        Assert.Equal(new HistoryRow("2026-10-06 20:50", "哔哩哔哩", "1.16.5 → 1.19.0", "已更新", "已从 1.16.5 更新到 1.19.0", "手动"), vm.Rows[1]);
        Assert.Equal("自动", vm.Rows[0].Trigger);

        backend.History.Clear();
        await state.RefreshAsync();   // 每次检查结束都会重新读历史
        Assert.Empty(vm.Rows);
    }
}
```

- [ ] **Step 2: 运行，确认失败**

Run: `dotnet test tests/UpdateHelper.Presentation.Tests`
Expected: 编译失败，找不到 `HomeViewModel` 等类型

- [ ] **Step 3: 共享设置** `src/UpdateHelper.Presentation/Settings/SettingsService.cs`

```csharp
using CommunityToolkit.Mvvm.ComponentModel;

namespace UpdateHelper.Presentation.Settings;

/// <summary>当前设置，首页和设置页共用。每次修改都规范化后保存；保存失败不抛异常，原因写进 SaveError。</summary>
public sealed class SettingsService : ObservableObject
{
    private readonly SettingsStore _store;
    private AppSettings _current;
    private string? _saveError;

    public SettingsService(SettingsStore store)
    {
        _store = store;
        _current = store.Load();
    }

    public AppSettings Current => _current;

    public string? SaveError
    {
        get => _saveError;
        private set => SetProperty(ref _saveError, value);
    }

    public void Update(Func<AppSettings, AppSettings> change)
    {
        var next = change(_current).Normalized();
        if (next == _current) return;
        SetProperty(ref _current, next, nameof(Current));
        try
        {
            _store.Save(next);
            SaveError = null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SaveError = $"设置没能保存：{ex.Message}";
        }
    }
}
```

- [ ] **Step 4: 首页** `src/UpdateHelper.Presentation/ViewModels/HomeViewModel.cs`

```csharp
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UpdateHelper.Core.Updates;
using UpdateHelper.Presentation.Settings;

namespace UpdateHelper.Presentation.ViewModels;

/// <summary>首页：更新概况、上次检查时间、更新模式、立即检查。</summary>
public sealed partial class HomeViewModel : ObservableObject
{
    private readonly AppState _state;
    private readonly SettingsService _settings;

    public HomeViewModel(AppState state, SettingsService settings)
    {
        _state = state;
        _settings = settings;
        _state.PropertyChanged += OnStateChanged;
        _settings.PropertyChanged += (_, _) => OnPropertyChanged(nameof(ModeIndex));
    }

    private IEnumerable<JudgedUpdate> Visible => _state.Updates.Where(u => u.Tier != UpdateTier.Ignored);

    public string SummaryTitle
    {
        get
        {
            if (_state.IsBusy && _state.StatusText.Length > 0) return _state.StatusText;
            if (_state.LastChecked is null) return "还没有检查更新";
            var count = Visible.Count();
            return count == 0 ? "所有软件都是最新的" : $"有 {count} 个更新可用";
        }
    }

    public string SummaryDetail
    {
        get
        {
            var list = Visible.ToList();
            if (list.Count == 0) return "";
            int Count(UpdateTier t) => list.Count(u => u.Tier == t);
            return $"其中 {Count(UpdateTier.Low)} 个低风险、{Count(UpdateTier.Careful)} 个需确认、{Count(UpdateTier.NeverAuto)} 个不自动";
        }
    }

    public string LastCheckedText =>
        _state.LastChecked is { } t ? $"上次检查：{t:yyyy-MM-dd HH:mm}" : "还没有检查";

    public string SoftwareCountText =>
        $"已识别 {_state.Snapshot?.Result.Groups.Count(g => g.Primary is not null) ?? 0} 个软件";

    public string? Warning => _state.Warning;

    public int ModeIndex
    {
        get => (int)_settings.Current.Mode;
        set => _settings.Update(s => s with { Mode = (UpdateMode)value });
    }

    private bool CanCheck() => !_state.IsBusy;

    [RelayCommand(CanExecute = nameof(CanCheck))]
    private Task CheckNowAsync() => _state.RefreshAsync();

    private void OnStateChanged(object? sender, PropertyChangedEventArgs e)
    {
        OnPropertyChanged(nameof(SummaryTitle));
        OnPropertyChanged(nameof(SummaryDetail));
        OnPropertyChanged(nameof(LastCheckedText));
        OnPropertyChanged(nameof(SoftwareCountText));
        OnPropertyChanged(nameof(Warning));
        if (e.PropertyName == nameof(AppState.IsBusy)) CheckNowCommand.NotifyCanExecuteChanged();
    }
}
```

- [ ] **Step 5: 我的软件** `src/UpdateHelper.Presentation/ViewModels/SoftwareViewModel.cs`

```csharp
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using UpdateHelper.Core.Grouping;

namespace UpdateHelper.Presentation.ViewModels;

public sealed record SoftwareRow(string Name, string Publisher, string Version, string Category, string Components, string Location);

/// <summary>我的软件：已归组的软件列表，默认只显示普通软件和开发工具，可搜索。</summary>
public sealed partial class SoftwareViewModel : ObservableObject
{
    private readonly AppState _state;

    public SoftwareViewModel(AppState state)
    {
        _state = state;
        _state.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(AppState.Snapshot)) Rebuild(); };
        Rebuild();
    }

    public ObservableCollection<SoftwareRow> Rows { get; } = [];

    [ObservableProperty] private bool _showAll;
    [ObservableProperty] private string _searchText = "";

    public string CountText => $"共 {Rows.Count} 个";

    partial void OnShowAllChanged(bool value) => Rebuild();
    partial void OnSearchTextChanged(string value) => Rebuild();

    private void Rebuild()
    {
        Rows.Clear();
        var groups = _state.Snapshot?.Result.Groups ?? [];
        foreach (var g in groups)
        {
            if (!ShowAll && g.Category is not (SoftwareCategory.Application or SoftwareCategory.DevTool)) continue;
            if (SearchText.Length > 0 && !g.Name.Contains(SearchText.Trim(), StringComparison.OrdinalIgnoreCase)) continue;
            Rows.Add(new SoftwareRow(g.Name, g.Publisher ?? "", g.Version ?? "", Display.CategoryName(g.Category),
                g.Components.Count > 0 ? $"含 {g.Components.Count} 个组件" : "",
                g.InstallLocations.FirstOrDefault() ?? ""));
        }
        OnPropertyChanged(nameof(CountText));
    }
}
```

- [ ] **Step 6: 后台项目** `src/UpdateHelper.Presentation/ViewModels/BackgroundViewModel.cs`

```csharp
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using UpdateHelper.Core.Scanning;

namespace UpdateHelper.Presentation.ViewModels;

public sealed record BackgroundRow(string Kind, string Name, string Owner, string Explanation, string Path);

/// <summary>后台项目：服务、开机自启、计划任务，附归属软件和一句话说明。第一期只看不改。</summary>
public sealed class BackgroundViewModel : ObservableObject
{
    private readonly AppState _state;

    public BackgroundViewModel(AppState state)
    {
        _state = state;
        _state.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(AppState.Snapshot)) Rebuild(); };
        Rebuild();
    }

    public ObservableCollection<BackgroundRow> Rows { get; } = [];
    public string CountText => $"共 {Rows.Count} 个后台项目";

    private void Rebuild()
    {
        Rows.Clear();
        var snapshot = _state.Snapshot;
        if (snapshot is not null)
        {
            var items = snapshot.Result.Groups.SelectMany(g => g.Background.Select(b => (Item: b, Owner: g.Name)))
                .Concat(snapshot.Result.UnassignedBackground.Select(b => (Item: b, Owner: "（未归属）")))
                .OrderBy(x => x.Owner == "（未归属）")
                .ThenBy(x => x.Owner, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(x => x.Item.Kind);
            foreach (var (item, owner) in items)
            {
                Rows.Add(new BackgroundRow(Display.KindName(item.Kind), item.Name, owner,
                    snapshot.Explanations.TryGetValue(item, out var text) ? text : "未知用途",
                    item.ExecutablePath ?? item.Command ?? ""));
            }
        }
        OnPropertyChanged(nameof(CountText));
    }
}
```

- [ ] **Step 7: 历史** `src/UpdateHelper.Presentation/ViewModels/HistoryViewModel.cs`

```csharp
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace UpdateHelper.Presentation.ViewModels;

public sealed record HistoryRow(string Time, string Name, string Versions, string Outcome, string Message, string Trigger);

/// <summary>更新历史，最新的在前。每次检查结束（包括安装后的重新检查）自动重新读取。</summary>
public sealed partial class HistoryViewModel : ObservableObject
{
    private readonly AppState _state;

    public HistoryViewModel(AppState state)
    {
        _state = state;
        _state.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(AppState.LastChecked)) Reload(); };
        Reload();
    }

    public ObservableCollection<HistoryRow> Rows { get; } = [];

    [RelayCommand]
    private void Reload()
    {
        Rows.Clear();
        foreach (var r in _state.Backend.ReadHistory().OrderByDescending(r => r.Time))
        {
            Rows.Add(new HistoryRow($"{r.Time:yyyy-MM-dd HH:mm}", r.Name, $"{r.FromVersion} → {r.ToVersion}",
                Display.OutcomeName(r.Outcome), r.Message, r.Automatic ? "自动" : "手动"));
        }
    }
}
```

注：`History_is_newest_first…` 最后一段先清空历史再 `RefreshAsync()`。`LastChecked` 每次检查都会被赋新值（时间不同），所以会触发重新读取；如果两次检查的时间完全相同，`ObservableProperty` 不会发出通知——测试里两次调用相隔至少几微秒，`TimeProvider.System` 的精度足够区分。

- [ ] **Step 8: 设置页** `src/UpdateHelper.Presentation/ViewModels/SettingsViewModel.cs`

```csharp
using CommunityToolkit.Mvvm.ComponentModel;
using UpdateHelper.Presentation.Settings;

namespace UpdateHelper.Presentation.ViewModels;

/// <summary>设置页。修改立即保存；数值越界会被限制到范围内。</summary>
public sealed class SettingsViewModel : ObservableObject
{
    private readonly SettingsService _settings;

    public SettingsViewModel(SettingsService settings)
    {
        _settings = settings;
        _settings.PropertyChanged += (_, _) => OnPropertyChanged(string.Empty);   // 刷新全部绑定
    }

    public int ModeIndex
    {
        get => (int)_settings.Current.Mode;
        set => _settings.Update(s => s with { Mode = (UpdateMode)value });
    }

    public int ObservationDays
    {
        get => _settings.Current.ObservationDays;
        set => _settings.Update(s => s with { ObservationDays = value });
    }

    public int CheckIntervalHours
    {
        get => _settings.Current.CheckIntervalHours;
        set => _settings.Update(s => s with { CheckIntervalHours = value });
    }

    public bool TrayEnabled
    {
        get => _settings.Current.TrayEnabled;
        set => _settings.Update(s => s with { TrayEnabled = value });
    }

    public string? SaveError => _settings.SaveError;

    public string AutoUpdateNote => "分级自动、全部自动和定时检查将在后续版本启用；目前程序打开时检查一次，有更新会提醒你。";
}
```

- [ ] **Step 9: 运行，确认通过**

Run: `dotnet test tests/UpdateHelper.Presentation.Tests`
Expected: 全部 PASS

- [ ] **Step 10: Commit**

```bash
git add -A
git commit -m "feat(presentation): 首页、我的软件、后台项目、历史、设置的界面逻辑"
```

### Task 5：App 项目——RealBackend、AppHost、主窗口和 6 个页面

窗口和页面没有单元测试，交付物是"编译 0 警告 + 本机运行截图核对"（Step 9）。所有逻辑都在 Task 2～4 的 ViewModel 里，已有测试。

**补充技术验证（2026-10-07，实验程序）：** WPF-UI 4.3.0 中本任务用到的图标 `Home24`、`ArrowSync24`、`Apps24`、`Pulse24`、`History24`、`Settings24`、`Search24`、`Warning24` 都存在；`ui:DataGrid`、`ui:TextBox`、`ui:CardControl`、`ui:InfoBar`、`ui:NumberBox`、`ui:ToggleSwitch`、`ui:ProgressRing` 都存在。**在 `Loaded` 事件里同步关闭 `FluentWindow` 会在 WindowChromeWorker 里抛 NullReferenceException**——需要"启动时不显示窗口"时，不要先 Show 再关，而是根本不调用 Show（Task 6）。

**Files:**
- Create: `src/UpdateHelper.App/UpdateHelper.App.csproj`
- Create: `src/UpdateHelper.App/App.xaml`、`App.xaml.cs`
- Create: `src/UpdateHelper.App/RealBackend.cs`
- Create: `src/UpdateHelper.App/AppHost.cs`
- Create: `src/UpdateHelper.App/MainWindow.xaml`、`MainWindow.xaml.cs`
- Create: `src/UpdateHelper.App/Pages/HomePage.xaml`、`UpdatesPage.xaml`、`SoftwarePage.xaml`、`BackgroundPage.xaml`、`HistoryPage.xaml`、`SettingsPage.xaml` 及各自的 `.xaml.cs`

**Interfaces:**
- Consumes: Task 1～4 的全部 ViewModel、`AppState`、`SettingsService`、`SettingsStore`、`IAppBackend`、`ScanSnapshot`；`SystemScanner`（计划 1）；`RuleSetLoader`、`RuleSource`、`RuleTrust`、`RuleApplier`、`RuleSet`（计划 2）；`UpdateService`、`RuleUpdateSource`、`JudgedUpdate`（计划 3、5）；`UpdateExecutor`、`InstallerRouter`、`RuleInstaller`、`HttpDownloader`、`ProcessRunner`、`RegistryVersionProbe`、`RunningProcessProbe`、`JsonLinesUpdateHistory`（计划 4、5）；`AuthenticodeVerifier`（计划 5）；`WingetUpdateSource`、`WingetInstaller`（计划 3、4）
- Produces（命名空间 `UpdateHelper.App`）：
  - `sealed class RealBackend(string rulesDirectory) : IAppBackend`——每个方法都用 `Task.Run` 放到后台线程
  - `static class AppHost`：`Initialize()`，以及 `State`、`Settings`、`Home`、`Updates`、`Software`、`Background`、`History`、`SettingsPage` 这些静态属性（页面构造时从这里取 DataContext）
  - 程序集名 `UpdateHelper`（生成 `UpdateHelper.exe`），官方规则随程序复制到输出目录的 `rules\`

- [ ] **Step 1: 建项目**

```bash
dotnet new wpf -o src/UpdateHelper.App -n UpdateHelper.App
rm src/UpdateHelper.App/MainWindow.xaml src/UpdateHelper.App/MainWindow.xaml.cs
dotnet sln add src/UpdateHelper.App
```

把 `src/UpdateHelper.App/UpdateHelper.App.csproj` 整个替换为：

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <!-- 引用了 winget 项目，框架和架构要与它一致；托盘用 WinForms 的 NotifyIcon -->
  <PropertyGroup>
    <OutputType>WinExe</OutputType>
    <TargetFramework>net10.0-windows10.0.26100.0</TargetFramework>
    <SupportedOSPlatformVersion>10.0.17763.0</SupportedOSPlatformVersion>
    <RuntimeIdentifier>win-x64</RuntimeIdentifier>
    <UseWPF>true</UseWPF>
    <UseWindowsForms>true</UseWindowsForms>
    <AssemblyName>UpdateHelper</AssemblyName>
    <RootNamespace>UpdateHelper.App</RootNamespace>
  </PropertyGroup>

  <!-- 不去掉的话 Application 等类型在 WPF 和 WinForms 之间冲突（CS0104） -->
  <ItemGroup>
    <Using Remove="System.Windows.Forms" />
    <Using Remove="System.Drawing" />
  </ItemGroup>

  <ItemGroup>
    <PackageReference Include="WPF-UI" Version="4.3.0" />
    <PackageReference Include="CommunityToolkit.Mvvm" Version="8.4.2" />
    <!-- 必须直接引用，本地 dll 才会复制到输出目录（计划 3 的结论） -->
    <PackageReference Include="Microsoft.WindowsPackageManager.ComInterop" Version="1.29.380" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\UpdateHelper.Core\UpdateHelper.Core.csproj" />
    <ProjectReference Include="..\UpdateHelper.Winget\UpdateHelper.Winget.csproj" />
    <ProjectReference Include="..\UpdateHelper.Presentation\UpdateHelper.Presentation.csproj" />
  </ItemGroup>

  <!-- 官方规则随程序发布 -->
  <ItemGroup>
    <Content Include="..\..\rules\*.yaml" Link="rules\%(Filename)%(Extension)" CopyToOutputDirectory="PreserveNewest" />
  </ItemGroup>

</Project>
```

- [ ] **Step 2: 真实的底层** `src/UpdateHelper.App/RealBackend.cs`

```csharp
using UpdateHelper.Core.Install;
using UpdateHelper.Core.Rules;
using UpdateHelper.Core.Scanning;
using UpdateHelper.Core.Security;
using UpdateHelper.Core.Updates;
using UpdateHelper.Presentation;
using UpdateHelper.Winget;

namespace UpdateHelper.App;

/// <summary>IAppBackend 的真实实现：组合计划 1～5 的底层。所有耗时工作都放到后台线程。</summary>
public sealed class RealBackend(string rulesDirectory) : IAppBackend
{
    public Task<ScanSnapshot> ScanAsync(CancellationToken cancellationToken) => Task.Run(() =>
    {
        var scan = SystemScanner.CreateDefault().Scan();
        var rules = RuleSetLoader.Load([new RuleSource(rulesDirectory, RuleTrust.Official)]);
        var applied = RuleApplier.Apply(scan.Result, rules);
        var warnings = scan.Warnings.Concat(rules.Errors.Select(e => $"规则错误：{e}")).ToList();
        return new ScanSnapshot(applied.Result, rules, applied.Explanations, warnings);
    }, cancellationToken);

    public Task<UpdateReport> CheckUpdatesAsync(ScanSnapshot snapshot, CancellationToken cancellationToken) => Task.Run(() =>
        UpdateService.Check(
            [new WingetUpdateSource(), new RuleUpdateSource(snapshot.Result, snapshot.Rules)],   // winget 优先
            snapshot.Result, snapshot.Rules), cancellationToken);

    public Task<ExecuteResult> InstallAsync(JudgedUpdate update, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var rules = RuleSetLoader.Load([new RuleSource(rulesDirectory, RuleTrust.Official)]);
        var installer = new InstallerRouter(new WingetInstaller(),
            new RuleInstaller(rules, new HttpDownloader(), new AuthenticodeVerifier(), new ProcessRunner(),
                RuleInstaller.DefaultDownloadDirectory));
        var executor = new UpdateExecutor(installer, new RegistryVersionProbe(), new RunningProcessProbe(),
            new JsonLinesUpdateHistory(JsonLinesUpdateHistory.DefaultPath));
        return Task.Run(() => executor.ExecuteAsync(update, automatic: false, progress, cancellationToken), cancellationToken);
    }

    public IReadOnlyList<HistoryRecord> ReadHistory()
    {
        try
        {
            return new JsonLinesUpdateHistory(JsonLinesUpdateHistory.DefaultPath).ReadAll();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }
}
```

- [ ] **Step 3: 组合根** `src/UpdateHelper.App/AppHost.cs`

```csharp
using UpdateHelper.Presentation;
using UpdateHelper.Presentation.Settings;
using UpdateHelper.Presentation.ViewModels;

namespace UpdateHelper.App;

/// <summary>组合根：创建底层、共享状态和各页面的 ViewModel。程序启动时调用一次 Initialize。</summary>
public static class AppHost
{
    public static AppState State { get; private set; } = null!;
    public static SettingsService Settings { get; private set; } = null!;
    public static HomeViewModel Home { get; private set; } = null!;
    public static UpdatesViewModel Updates { get; private set; } = null!;
    public static SoftwareViewModel Software { get; private set; } = null!;
    public static BackgroundViewModel Background { get; private set; } = null!;
    public static HistoryViewModel History { get; private set; } = null!;
    public static SettingsViewModel SettingsPage { get; private set; } = null!;

    public static void Initialize()
    {
        var backend = new RealBackend(Path.Combine(AppContext.BaseDirectory, "rules"));
        State = new AppState(backend);
        Settings = new SettingsService(new SettingsStore(SettingsStore.DefaultPath));
        Home = new HomeViewModel(State, Settings);
        Updates = new UpdatesViewModel(State);
        Software = new SoftwareViewModel(State);
        Background = new BackgroundViewModel(State);
        History = new HistoryViewModel(State);
        SettingsPage = new SettingsViewModel(Settings);
    }
}
```

- [ ] **Step 4: 应用资源** —— `src/UpdateHelper.App/App.xaml` 整个替换为：

```xml
<Application x:Class="UpdateHelper.App.App"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             xmlns:ui="http://schemas.lepo.co/wpfui/2022/xaml"
             ShutdownMode="OnExplicitShutdown">
    <Application.Resources>
        <ResourceDictionary>
            <ResourceDictionary.MergedDictionaries>
                <ui:ThemesDictionary Theme="Light" />
                <ui:ControlsDictionary />
            </ResourceDictionary.MergedDictionaries>
        </ResourceDictionary>
    </Application.Resources>
</Application>
```

`src/UpdateHelper.App/App.xaml.cs` 整个替换为（托盘和单实例在 Task 6 加入）：

```csharp
using System.Windows;
using Wpf.Ui.Appearance;

namespace UpdateHelper.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ApplicationThemeManager.ApplySystemTheme();   // 跟随系统的深色/浅色

        AppHost.Initialize();
        var window = new MainWindow();
        window.Closed += (_, _) => Shutdown();
        window.Show();
        _ = AppHost.State.RefreshAsync();             // 启动时扫描并检查一次
    }
}
```

注：`ShutdownMode="OnExplicitShutdown"` 是为 Task 6 准备的——缩到托盘后窗口关闭不能让程序退出。本任务里先在窗口关闭时显式 `Shutdown()`。

- [ ] **Step 5: 主窗口** `src/UpdateHelper.App/MainWindow.xaml`

```xml
<ui:FluentWindow x:Class="UpdateHelper.App.MainWindow"
        xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        xmlns:ui="http://schemas.lepo.co/wpfui/2022/xaml"
        xmlns:pages="clr-namespace:UpdateHelper.App.Pages"
        Title="更新管理小助手" Width="1180" Height="760" MinWidth="900" MinHeight="560"
        WindowStartupLocation="CenterScreen"
        ExtendsContentIntoTitleBar="True" WindowBackdropType="Mica" WindowCornerPreference="Round">
    <Grid>
        <Grid.RowDefinitions>
            <RowDefinition Height="Auto" />
            <RowDefinition Height="*" />
        </Grid.RowDefinitions>
        <ui:TitleBar Grid.Row="0" Title="更新管理小助手" />
        <ui:NavigationView x:Name="Nav" Grid.Row="1" PaneDisplayMode="Left" IsBackButtonVisible="Collapsed" OpenPaneLength="220">
            <ui:NavigationView.MenuItems>
                <ui:NavigationViewItem Content="首页" Icon="{ui:SymbolIcon Home24}" TargetPageType="{x:Type pages:HomePage}" />
                <ui:NavigationViewItem Content="更新" Icon="{ui:SymbolIcon ArrowSync24}" TargetPageType="{x:Type pages:UpdatesPage}" />
                <ui:NavigationViewItem Content="我的软件" Icon="{ui:SymbolIcon Apps24}" TargetPageType="{x:Type pages:SoftwarePage}" />
                <ui:NavigationViewItem Content="后台项目" Icon="{ui:SymbolIcon Pulse24}" TargetPageType="{x:Type pages:BackgroundPage}" />
                <ui:NavigationViewItem Content="历史" Icon="{ui:SymbolIcon History24}" TargetPageType="{x:Type pages:HistoryPage}" />
            </ui:NavigationView.MenuItems>
            <ui:NavigationView.FooterMenuItems>
                <ui:NavigationViewItem Content="设置" Icon="{ui:SymbolIcon Settings24}" TargetPageType="{x:Type pages:SettingsPage}" />
            </ui:NavigationView.FooterMenuItems>
        </ui:NavigationView>
    </Grid>
</ui:FluentWindow>
```

`src/UpdateHelper.App/MainWindow.xaml.cs`：

```csharp
using UpdateHelper.App.Pages;
using Wpf.Ui.Controls;

namespace UpdateHelper.App;

public partial class MainWindow : FluentWindow
{
    public MainWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => Nav.Navigate(typeof(HomePage));
    }

    /// <summary>打开某个页面（托盘通知点击后打开更新页）。</summary>
    public void ShowPage(Type pageType) => Nav.Navigate(pageType);
}
```

- [ ] **Step 6: 六个页面的 XAML**

每个页面的 `.xaml.cs` 都是同一个模式：先设 DataContext，再 `InitializeComponent()`。例如 `src/UpdateHelper.App/Pages/HomePage.xaml.cs`：

```csharp
namespace UpdateHelper.App.Pages;

public partial class HomePage : System.Windows.Controls.Page
{
    public HomePage()
    {
        DataContext = AppHost.Home;
        InitializeComponent();
    }
}
```

其余五个页面的 `.xaml.cs` 完全相同，只把类名和 DataContext 换成下表对应的：

| 文件 | 类名 | DataContext |
|---|---|---|
| `Pages/UpdatesPage.xaml.cs` | `UpdatesPage` | `AppHost.Updates` |
| `Pages/SoftwarePage.xaml.cs` | `SoftwarePage` | `AppHost.Software` |
| `Pages/BackgroundPage.xaml.cs` | `BackgroundPage` | `AppHost.Background` |
| `Pages/HistoryPage.xaml.cs` | `HistoryPage` | `AppHost.History` |
| `Pages/SettingsPage.xaml.cs` | `SettingsPage` | `AppHost.SettingsPage` |

警告横幅用样式触发器控制显示（`Warning` 为 null 时隐藏），不需要转换器。

`src/UpdateHelper.App/Pages/HomePage.xaml`：

```xml
<Page x:Class="UpdateHelper.App.Pages.HomePage"
      xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
      xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
      xmlns:ui="http://schemas.lepo.co/wpfui/2022/xaml" Title="首页">
    <ScrollViewer VerticalScrollBarVisibility="Auto">
        <StackPanel Margin="24,8,40,24">
            <TextBlock Text="首页" FontSize="28" FontWeight="SemiBold" Margin="0,0,0,16" />
            <ui:InfoBar Title="{Binding SummaryTitle}" Message="{Binding SummaryDetail}" IsOpen="True" IsClosable="False" Severity="Informational" />
            <ui:InfoBar Title="注意" Message="{Binding Warning}" IsOpen="True" IsClosable="False" Severity="Warning" Margin="0,8,0,0">
                <ui:InfoBar.Style>
                    <Style TargetType="ui:InfoBar" BasedOn="{StaticResource {x:Type ui:InfoBar}}">
                        <Style.Triggers>
                            <DataTrigger Binding="{Binding Warning}" Value="{x:Null}">
                                <Setter Property="Visibility" Value="Collapsed" />
                            </DataTrigger>
                        </Style.Triggers>
                    </Style>
                </ui:InfoBar.Style>
            </ui:InfoBar>

            <TextBlock Text="更新" FontSize="14" FontWeight="SemiBold" Margin="0,24,0,8" />
            <ui:CardControl>
                <ui:CardControl.Header>
                    <StackPanel>
                        <TextBlock Text="更新模式" />
                        <TextBlock Text="只提醒：发现更新时通知你，你确认后才安装" FontSize="12" Opacity="0.7" />
                    </StackPanel>
                </ui:CardControl.Header>
                <ComboBox SelectedIndex="{Binding ModeIndex}" MinWidth="160">
                    <ComboBoxItem Content="只提醒" />
                    <ComboBoxItem Content="分级自动" />
                    <ComboBoxItem Content="全部自动" />
                    <ComboBoxItem Content="打开才检查" />
                </ComboBox>
            </ui:CardControl>
            <ui:CardControl Margin="0,4,0,0">
                <ui:CardControl.Header>
                    <StackPanel>
                        <TextBlock Text="{Binding LastCheckedText}" />
                        <TextBlock Text="{Binding SoftwareCountText}" FontSize="12" Opacity="0.7" />
                    </StackPanel>
                </ui:CardControl.Header>
                <ui:Button Content="立即检查" Appearance="Primary" Command="{Binding CheckNowCommand}" />
            </ui:CardControl>
        </StackPanel>
    </ScrollViewer>
</Page>
```

`src/UpdateHelper.App/Pages/UpdatesPage.xaml`：

```xml
<Page x:Class="UpdateHelper.App.Pages.UpdatesPage"
      xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
      xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
      xmlns:ui="http://schemas.lepo.co/wpfui/2022/xaml" Title="更新">
    <Grid Margin="24,8,40,24">
        <Grid.RowDefinitions>
            <RowDefinition Height="Auto" />
            <RowDefinition Height="Auto" />
            <RowDefinition Height="*" />
            <RowDefinition Height="Auto" />
        </Grid.RowDefinitions>
        <TextBlock Grid.Row="0" Text="更新" FontSize="28" FontWeight="SemiBold" Margin="0,0,0,12" />
        <StackPanel Grid.Row="1" Orientation="Horizontal" Margin="0,0,0,12">
            <ui:Button Content="更新所选" Appearance="Primary" Command="{Binding InstallSelectedCommand}" />
            <ui:Button Content="重新检查" Command="{Binding RefreshCommand}" Margin="8,0,0,0" />
            <TextBlock Text="{Binding Summary}" VerticalAlignment="Center" Margin="16,0,0,0" Opacity="0.7" />
        </StackPanel>
        <ui:DataGrid Grid.Row="2" ItemsSource="{Binding Rows}" AutoGenerateColumns="False" CanUserAddRows="False"
                     HeadersVisibility="Column" SelectionMode="Single" IsReadOnly="True">
            <ui:DataGrid.Columns>
                <DataGridTemplateColumn Width="44">
                    <DataGridTemplateColumn.CellTemplate>
                        <DataTemplate>
                            <CheckBox IsChecked="{Binding IsSelected, UpdateSourceTrigger=PropertyChanged}" IsEnabled="{Binding CanSelect}"
                                      HorizontalAlignment="Center" />
                        </DataTemplate>
                    </DataGridTemplateColumn.CellTemplate>
                </DataGridTemplateColumn>
                <DataGridTextColumn Header="名称" Binding="{Binding Name}" Width="1.4*" />
                <DataGridTextColumn Header="版本" Binding="{Binding Versions}" Width="1.2*" />
                <DataGridTextColumn Header="风险" Binding="{Binding TierName}" Width="70" />
                <DataGridTextColumn Header="说明" Binding="{Binding Reason}" Width="2*" />
                <DataGridTextColumn Header="状态" Binding="{Binding Status}" Width="1.6*" />
            </ui:DataGrid.Columns>
        </ui:DataGrid>
        <ItemsControl Grid.Row="3" ItemsSource="{Binding LastResults}" Margin="0,12,0,0">
            <ItemsControl.ItemTemplate>
                <DataTemplate>
                    <TextBlock Text="{Binding}" FontSize="12" Opacity="0.8" />
                </DataTemplate>
            </ItemsControl.ItemTemplate>
        </ItemsControl>
    </Grid>
</Page>
```

`src/UpdateHelper.App/Pages/SoftwarePage.xaml`：

```xml
<Page x:Class="UpdateHelper.App.Pages.SoftwarePage"
      xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
      xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
      xmlns:ui="http://schemas.lepo.co/wpfui/2022/xaml" Title="我的软件">
    <Grid Margin="24,8,40,24">
        <Grid.RowDefinitions>
            <RowDefinition Height="Auto" />
            <RowDefinition Height="Auto" />
            <RowDefinition Height="*" />
        </Grid.RowDefinitions>
        <TextBlock Grid.Row="0" Text="我的软件" FontSize="28" FontWeight="SemiBold" Margin="0,0,0,12" />
        <StackPanel Grid.Row="1" Orientation="Horizontal" Margin="0,0,0,12">
            <ui:TextBox PlaceholderText="搜索软件" Width="260" Icon="{ui:SymbolIcon Search24}"
                        Text="{Binding SearchText, UpdateSourceTrigger=PropertyChanged}" />
            <CheckBox Content="显示全部分类（游戏、运行库、驱动等）" IsChecked="{Binding ShowAll}" Margin="16,0,0,0" VerticalAlignment="Center" />
            <TextBlock Text="{Binding CountText}" VerticalAlignment="Center" Margin="16,0,0,0" Opacity="0.7" />
            <ui:Button Content="卸载" IsEnabled="False" Margin="16,0,0,0" ToolTipService.ShowOnDisabled="True"
                       ToolTip="卸载功能将在后续版本提供" />
        </StackPanel>
        <ui:DataGrid Grid.Row="2" ItemsSource="{Binding Rows}" AutoGenerateColumns="False" CanUserAddRows="False"
                     HeadersVisibility="Column" SelectionMode="Single" IsReadOnly="True">
            <ui:DataGrid.Columns>
                <DataGridTextColumn Header="名称" Binding="{Binding Name}" Width="2*" />
                <DataGridTextColumn Header="发布者" Binding="{Binding Publisher}" Width="1.5*" />
                <DataGridTextColumn Header="版本" Binding="{Binding Version}" Width="*" />
                <DataGridTextColumn Header="分类" Binding="{Binding Category}" Width="80" />
                <DataGridTextColumn Header="组件" Binding="{Binding Components}" Width="90" />
                <DataGridTextColumn Header="安装位置" Binding="{Binding Location}" Width="2*" />
            </ui:DataGrid.Columns>
        </ui:DataGrid>
    </Grid>
</Page>
```

`src/UpdateHelper.App/Pages/BackgroundPage.xaml`：

```xml
<Page x:Class="UpdateHelper.App.Pages.BackgroundPage"
      xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
      xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
      xmlns:ui="http://schemas.lepo.co/wpfui/2022/xaml" Title="后台项目">
    <Grid Margin="24,8,40,24">
        <Grid.RowDefinitions>
            <RowDefinition Height="Auto" />
            <RowDefinition Height="Auto" />
            <RowDefinition Height="*" />
        </Grid.RowDefinitions>
        <TextBlock Grid.Row="0" Text="后台项目" FontSize="28" FontWeight="SemiBold" Margin="0,0,0,12" />
        <TextBlock Grid.Row="1" Margin="0,0,0,12" Opacity="0.7">
            <Run Text="{Binding CountText, Mode=OneWay}" />
            <Run Text="  ·  服务、开机自启和计划任务，目前只能查看" />
        </TextBlock>
        <ui:DataGrid Grid.Row="2" ItemsSource="{Binding Rows}" AutoGenerateColumns="False" CanUserAddRows="False"
                     HeadersVisibility="Column" SelectionMode="Single" IsReadOnly="True">
            <ui:DataGrid.Columns>
                <DataGridTextColumn Header="类型" Binding="{Binding Kind}" Width="90" />
                <DataGridTextColumn Header="名称" Binding="{Binding Name}" Width="1.5*" />
                <DataGridTextColumn Header="归属软件" Binding="{Binding Owner}" Width="1.2*" />
                <DataGridTextColumn Header="说明" Binding="{Binding Explanation}" Width="2*" />
                <DataGridTextColumn Header="程序" Binding="{Binding Path}" Width="2*" />
            </ui:DataGrid.Columns>
        </ui:DataGrid>
    </Grid>
</Page>
```

`src/UpdateHelper.App/Pages/HistoryPage.xaml`：

```xml
<Page x:Class="UpdateHelper.App.Pages.HistoryPage"
      xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
      xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
      xmlns:ui="http://schemas.lepo.co/wpfui/2022/xaml" Title="历史">
    <Grid Margin="24,8,40,24">
        <Grid.RowDefinitions>
            <RowDefinition Height="Auto" />
            <RowDefinition Height="Auto" />
            <RowDefinition Height="*" />
        </Grid.RowDefinitions>
        <TextBlock Grid.Row="0" Text="历史" FontSize="28" FontWeight="SemiBold" Margin="0,0,0,12" />
        <ui:Button Grid.Row="1" Content="刷新" Command="{Binding ReloadCommand}" Margin="0,0,0,12" />
        <ui:DataGrid Grid.Row="2" ItemsSource="{Binding Rows}" AutoGenerateColumns="False" CanUserAddRows="False"
                     HeadersVisibility="Column" SelectionMode="Single" IsReadOnly="True">
            <ui:DataGrid.Columns>
                <DataGridTextColumn Header="时间" Binding="{Binding Time}" Width="140" />
                <DataGridTextColumn Header="软件" Binding="{Binding Name}" Width="1.2*" />
                <DataGridTextColumn Header="版本" Binding="{Binding Versions}" Width="1.2*" />
                <DataGridTextColumn Header="结果" Binding="{Binding Outcome}" Width="80" />
                <DataGridTextColumn Header="说明" Binding="{Binding Message}" Width="3*" />
                <DataGridTextColumn Header="方式" Binding="{Binding Trigger}" Width="60" />
            </ui:DataGrid.Columns>
        </ui:DataGrid>
    </Grid>
</Page>
```

`src/UpdateHelper.App/Pages/SettingsPage.xaml`（数字用标准 `Slider`，绑定到 int 属性时 WPF 会自动转换）：

```xml
<Page x:Class="UpdateHelper.App.Pages.SettingsPage"
      xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
      xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
      xmlns:ui="http://schemas.lepo.co/wpfui/2022/xaml" Title="设置">
    <ScrollViewer VerticalScrollBarVisibility="Auto">
        <StackPanel Margin="24,8,40,24">
            <TextBlock Text="设置" FontSize="28" FontWeight="SemiBold" Margin="0,0,0,16" />
            <ui:InfoBar Title="提示" Message="{Binding AutoUpdateNote}" IsOpen="True" IsClosable="False" Severity="Informational" />

            <ui:CardControl Header="更新模式" Margin="0,16,0,0">
                <ComboBox SelectedIndex="{Binding ModeIndex}" MinWidth="160">
                    <ComboBoxItem Content="只提醒" />
                    <ComboBoxItem Content="分级自动" />
                    <ComboBoxItem Content="全部自动" />
                    <ComboBoxItem Content="打开才检查" />
                </ComboBox>
            </ui:CardControl>

            <ui:CardControl Margin="0,4,0,0">
                <ui:CardControl.Header>
                    <StackPanel>
                        <TextBlock Text="观察期" />
                        <TextBlock Text="新版本发布后先等几天，再自动安装（只在自动模式下生效）" FontSize="12" Opacity="0.7" />
                    </StackPanel>
                </ui:CardControl.Header>
                <StackPanel Orientation="Horizontal">
                    <Slider Minimum="0" Maximum="30" Width="200" IsSnapToTickEnabled="True" TickFrequency="1"
                            Value="{Binding ObservationDays}" VerticalAlignment="Center" />
                    <TextBlock Width="56" Margin="12,0,0,0" VerticalAlignment="Center">
                        <Run Text="{Binding ObservationDays, Mode=OneWay}" /><Run Text=" 天" />
                    </TextBlock>
                </StackPanel>
            </ui:CardControl>

            <ui:CardControl Margin="0,4,0,0">
                <ui:CardControl.Header>
                    <StackPanel>
                        <TextBlock Text="检查间隔" />
                        <TextBlock Text="电脑一直开着时，多久再检查一次" FontSize="12" Opacity="0.7" />
                    </StackPanel>
                </ui:CardControl.Header>
                <StackPanel Orientation="Horizontal">
                    <Slider Minimum="1" Maximum="168" Width="200" IsSnapToTickEnabled="True" TickFrequency="1"
                            Value="{Binding CheckIntervalHours}" VerticalAlignment="Center" />
                    <TextBlock Width="56" Margin="12,0,0,0" VerticalAlignment="Center">
                        <Run Text="{Binding CheckIntervalHours, Mode=OneWay}" /><Run Text=" 小时" />
                    </TextBlock>
                </StackPanel>
            </ui:CardControl>

            <ui:CardControl Margin="0,4,0,0">
                <ui:CardControl.Header>
                    <StackPanel>
                        <TextBlock Text="托盘图标" />
                        <TextBlock Text="关闭窗口时缩到右下角，而不是退出" FontSize="12" Opacity="0.7" />
                    </StackPanel>
                </ui:CardControl.Header>
                <ui:ToggleSwitch IsChecked="{Binding TrayEnabled}" />
            </ui:CardControl>

            <TextBlock Text="{Binding SaveError}" Foreground="#C42B1C" Margin="0,12,0,0" TextWrapping="Wrap" />
        </StackPanel>
    </ScrollViewer>
</Page>
```

- [ ] **Step 7: 支持启动参数 `--page`**（托盘"打开更新页"和截图核对都要用）——`App.xaml.cs` 的 `OnStartup` 里，在 `window.Show();` 之后加：

```csharp
        var pageIndex = Array.IndexOf(e.Args, "--page");
        if (pageIndex >= 0 && pageIndex + 1 < e.Args.Length && PageTypes.TryGetValue(e.Args[pageIndex + 1], out var page))
            window.Loaded += (_, _) => window.ShowPage(page);
```

并在 `App` 类里加：

```csharp
    /// <summary>--page 参数可用的页面名。</summary>
    internal static readonly Dictionary<string, Type> PageTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["home"] = typeof(Pages.HomePage),
        ["updates"] = typeof(Pages.UpdatesPage),
        ["software"] = typeof(Pages.SoftwarePage),
        ["background"] = typeof(Pages.BackgroundPage),
        ["history"] = typeof(Pages.HistoryPage),
        ["settings"] = typeof(Pages.SettingsPage),
    };
```

注：`MainWindow` 自己在 `Loaded` 里先导航到首页；`--page` 的处理器在它之后注册，所以后执行，最终停在指定页面。

- [ ] **Step 8: 编译**

Run: `dotnet build`
Expected: 0 警告 0 错误；`src/UpdateHelper.App/bin/Debug/net10.0-windows10.0.26100.0/win-x64/` 下有 `UpdateHelper.exe` 和 `rules\` 目录（12 个 yaml）

如果某个 WPF-UI 属性名（如 `PlaceholderText`、`Appearance`、`Severity`）编译报错，按编译器提示改成 4.3.0 中的实际名称，并作为 Ruling 记录。

- [ ] **Step 9: 本机运行并截图核对**

把截图脚本加入仓库 `tools/Screenshot-Window.ps1`（开发用，不随程序发布）：

```powershell
# 启动程序、等待指定秒数、截下主窗口、结束程序。开发时核对界面用。
param([string]$Exe, [string]$Out, [string]$Arguments = "", [int]$WaitSeconds = 5)
Add-Type -AssemblyName System.Drawing
Add-Type @"
using System; using System.Runtime.InteropServices;
public static class W {
  [StructLayout(LayoutKind.Sequential)] public struct R { public int L, T, Rt, B; }
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out R r);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
}
"@
[W]::SetProcessDPIAware() | Out-Null
$p = if ($Arguments) { Start-Process -FilePath $Exe -ArgumentList $Arguments -PassThru } else { Start-Process -FilePath $Exe -PassThru }
$h = [IntPtr]::Zero
for ($i = 0; $i -lt 60 -and $h -eq [IntPtr]::Zero; $i++) { Start-Sleep -Milliseconds 250; $p.Refresh(); $h = $p.MainWindowHandle }
if ($h -eq [IntPtr]::Zero) { "没有找到窗口"; $p | Stop-Process -Force; exit 1 }
Start-Sleep -Seconds $WaitSeconds
[W]::SetForegroundWindow($h) | Out-Null
Start-Sleep -Milliseconds 500
$r = New-Object W+R
[W]::GetWindowRect($h, [ref]$r) | Out-Null
$bmp = New-Object System.Drawing.Bitmap ($r.Rt - $r.L), ($r.B - $r.T)
[System.Drawing.Graphics]::FromImage($bmp).CopyFromScreen($r.L, $r.T, 0, 0, $bmp.Size)
$bmp.Save($Out, [System.Drawing.Imaging.ImageFormat]::Png)
"已截图 $($bmp.Width)x$($bmp.Height)"
$p | Stop-Process -Force
```

依次截 6 个页面（启动后会自动扫描并查询 winget，等 20 秒让检查完成）：

```powershell
$exe = "src\UpdateHelper.App\bin\Debug\net10.0-windows10.0.26100.0\win-x64\UpdateHelper.exe"
foreach ($p in "home","updates","software","background","history","settings") {
  powershell -NoProfile -File tools\Screenshot-Window.ps1 -Exe $exe -Arguments "--page $p" -WaitSeconds 20 -Out "$env:TEMP\uh-$p.png"
}
```

逐张查看截图，核对：

| 页面 | 预期 |
|---|---|
| 首页 | 横幅写"有 N 个更新可用"和三档数量；"已识别 1xx 个软件"；"上次检查：今天的时间"；模式下拉框是"只提醒" |
| 更新 | 表格有勾选框，低风险默认已勾；"不自动"行的勾选框是灰的；说明列显示理由 |
| 我的软件 | 默认约 110 行（普通软件 + 开发工具）；QQ、微信等有安装位置；"卸载"按钮是灰的 |
| 后台项目 | WPS 的计划任务归属 WPS、带说明；Google 更新服务归属 Chrome；未归属的显示"（未归属）""未知用途" |
| 历史 | 有之前的记录：哔哩哔哩"已更新"、EV录屏"失败" |
| 设置 | 模式"只提醒"、观察期 3 天、检查间隔 24 小时、托盘开关打开；顶部有"后续版本启用"的提示 |

本步骤**不点击"更新所选"**（只截图，不安装任何东西）。

- [ ] **Step 10: Commit**

```bash
git add -A
git commit -m "feat(app): 主窗口和六个页面，启动时自动检查更新"
```

### Task 6：托盘、通知、关窗口缩到托盘、单实例；本机核对

"什么时候弹通知""关窗口时是缩到托盘还是退出"这两个判断写成纯函数放在 Presentation 里（有单元测试）；托盘图标、单实例这些和系统打交道的部分放在 App 里，用本机运行核对。

**Files:**
- Create: `src/UpdateHelper.Presentation/TrayPolicy.cs`
- Test: `tests/UpdateHelper.Presentation.Tests/TrayPolicyTests.cs`
- Create: `src/UpdateHelper.App/TrayIcon.cs`
- Modify: `src/UpdateHelper.App/App.xaml.cs`（整个替换）

**Interfaces:**
- Consumes: `JudgedUpdate`、`UpdateTier`（计划 3）；`AppHost`、`MainWindow.ShowPage`、`App.PageTypes`（Task 5）；`SettingsService`（Task 4）
- Produces：
  - `static class TrayPolicy`（命名空间 `UpdateHelper.Presentation`）：`string? NotificationText(IReadOnlyList<JudgedUpdate> updates, bool windowVisible)`——窗口可见时不弹（用户已经看到了）；没有需要关注的更新（全是"不管"档）时不弹；否则返回"发现 N 个更新，其中 M 个低风险。点击查看。"；`bool HideInsteadOfClose(bool trayEnabled, bool exitRequested)`
  - `sealed class TrayIcon : IDisposable`（命名空间 `UpdateHelper.App`）：事件 `OpenRequested`、`OpenUpdatesRequested`、`CheckRequested`、`ExitRequested`；`bool Visible`；`void Notify(string title, string text)`
  - 启动参数 `--minimized`：启动时不显示窗口，只在托盘里

- [ ] **Step 1: 写失败的测试** `tests/UpdateHelper.Presentation.Tests/TrayPolicyTests.cs`

```csharp
using UpdateHelper.Core.Updates;
using static UpdateHelper.Presentation.Tests.FakeBackend;

namespace UpdateHelper.Presentation.Tests;

public class TrayPolicyTests
{
    [Fact]
    public void Notifies_when_window_is_hidden()
        => Assert.Equal("发现 3 个更新，其中 2 个低风险。点击查看。",
            TrayPolicy.NotificationText([Update("A", UpdateTier.Low), Update("B", UpdateTier.Low),
                Update("C", UpdateTier.Careful), Update("D", UpdateTier.Ignored)], windowVisible: false));

    [Fact]
    public void Silent_when_window_is_visible()
        => Assert.Null(TrayPolicy.NotificationText([Update("A", UpdateTier.Low)], windowVisible: true));

    [Fact]
    public void Silent_when_nothing_needs_attention()
        => Assert.Null(TrayPolicy.NotificationText([Update("CS2", UpdateTier.Ignored)], windowVisible: false));

    [Theory]   // Review Focus 5
    [InlineData(true, false, true)]    // 托盘开着、不是"退出"：缩到托盘
    [InlineData(false, false, false)]  // 托盘关着：真正关闭
    [InlineData(true, true, false)]    // 托盘菜单点了"退出"：一定退出
    public void Close_behaviour(bool trayEnabled, bool exitRequested, bool hide)
        => Assert.Equal(hide, TrayPolicy.HideInsteadOfClose(trayEnabled, exitRequested));
}
```

Run: `dotnet test tests/UpdateHelper.Presentation.Tests`
Expected: 编译失败，找不到 `TrayPolicy`

- [ ] **Step 2: 实现** `src/UpdateHelper.Presentation/TrayPolicy.cs`

```csharp
using UpdateHelper.Core.Updates;

namespace UpdateHelper.Presentation;

/// <summary>托盘相关的两个判断：要不要弹通知、关窗口时是缩到托盘还是退出。</summary>
public static class TrayPolicy
{
    /// <summary>需要弹通知时返回文字，否则返回 null。窗口可见时不弹，用户已经看到了。</summary>
    public static string? NotificationText(IReadOnlyList<JudgedUpdate> updates, bool windowVisible)
    {
        if (windowVisible) return null;
        var relevant = updates.Where(u => u.Tier != UpdateTier.Ignored).ToList();
        if (relevant.Count == 0) return null;
        var low = relevant.Count(u => u.Tier == UpdateTier.Low);
        return $"发现 {relevant.Count} 个更新，其中 {low} 个低风险。点击查看。";
    }

    /// <summary>托盘开着且不是用户主动"退出"时，关窗口只是缩到托盘。</summary>
    public static bool HideInsteadOfClose(bool trayEnabled, bool exitRequested) => trayEnabled && !exitRequested;
}
```

Run: `dotnet test tests/UpdateHelper.Presentation.Tests`
Expected: 全部 PASS

- [ ] **Step 3: 托盘图标** `src/UpdateHelper.App/TrayIcon.cs`

```csharp
using System.Drawing;
using System.Windows.Forms;

namespace UpdateHelper.App;

/// <summary>右下角托盘图标：右键菜单（打开 / 检查更新 / 退出），双击打开，点击通知打开更新页。</summary>
public sealed class TrayIcon : IDisposable
{
    private readonly NotifyIcon _icon;

    public TrayIcon()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("打开更新管理小助手", null, (_, _) => OpenRequested?.Invoke());
        menu.Items.Add("立即检查更新", null, (_, _) => CheckRequested?.Invoke());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) => ExitRequested?.Invoke());

        _icon = new NotifyIcon
        {
            Icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!) ?? SystemIcons.Application,
            Text = "更新管理小助手",
            ContextMenuStrip = menu,
        };
        _icon.DoubleClick += (_, _) => OpenRequested?.Invoke();
        _icon.BalloonTipClicked += (_, _) => OpenUpdatesRequested?.Invoke();
    }

    public event Action? OpenRequested;
    public event Action? OpenUpdatesRequested;
    public event Action? CheckRequested;
    public event Action? ExitRequested;

    public bool Visible
    {
        get => _icon.Visible;
        set => _icon.Visible = value;
    }

    /// <summary>弹一条系统通知（Win10/11 上显示在右下角的通知区）。</summary>
    public void Notify(string title, string text) => _icon.ShowBalloonTip(5000, title, text, ToolTipIcon.Info);

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}
```

- [ ] **Step 4: 启动流程** —— `src/UpdateHelper.App/App.xaml.cs` 整个替换为：

```csharp
using System.ComponentModel;
using System.Windows;
using UpdateHelper.Presentation;
using Wpf.Ui.Appearance;

namespace UpdateHelper.App;

public partial class App : Application
{
    private const string MutexName = @"Local\UpdateHelper.SingleInstance";
    private const string ActivateEventName = @"Local\UpdateHelper.Activate";

    /// <summary>--page 参数可用的页面名。</summary>
    internal static readonly Dictionary<string, Type> PageTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["home"] = typeof(Pages.HomePage),
        ["updates"] = typeof(Pages.UpdatesPage),
        ["software"] = typeof(Pages.SoftwarePage),
        ["background"] = typeof(Pages.BackgroundPage),
        ["history"] = typeof(Pages.HistoryPage),
        ["settings"] = typeof(Pages.SettingsPage),
    };

    private Mutex? _singleInstance;
    private EventWaitHandle? _activate;
    private MainWindow? _window;
    private TrayIcon? _tray;
    private bool _exitRequested;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 单实例：已经在运行就通知它把窗口调到前面，自己退出
        _singleInstance = new Mutex(true, MutexName, out var isFirst);
        if (!isFirst)
        {
            try { EventWaitHandle.OpenExisting(ActivateEventName).Set(); } catch (WaitHandleCannotBeOpenedException) { }
            Shutdown();
            return;
        }
        _activate = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateEventName);
        new Thread(() =>
        {
            while (_activate.WaitOne())
                Dispatcher.Invoke(() => ShowWindow(null));
        }) { IsBackground = true }.Start();

        ApplicationThemeManager.ApplySystemTheme();
        AppHost.Initialize();

        _tray = new TrayIcon { Visible = AppHost.Settings.Current.TrayEnabled };
        _tray.OpenRequested += () => ShowWindow(null);
        _tray.OpenUpdatesRequested += () => ShowWindow(typeof(Pages.UpdatesPage));
        _tray.CheckRequested += () => _ = AppHost.State.RefreshAsync();
        _tray.ExitRequested += ExitApp;
        AppHost.Settings.PropertyChanged += (_, _) => _tray.Visible = AppHost.Settings.Current.TrayEnabled;

        // 每次检查结束：窗口没显示时弹通知
        AppHost.State.PropertyChanged += OnStateChanged;

        _window = new MainWindow();
        _window.Closing += OnWindowClosing;

        var pageIndex = Array.IndexOf(e.Args, "--page");
        Type? startPage = pageIndex >= 0 && pageIndex + 1 < e.Args.Length && PageTypes.TryGetValue(e.Args[pageIndex + 1], out var p)
            ? p : null;

        // --minimized：只在托盘里，不显示窗口（不要先 Show 再关，FluentWindow 在 Loaded 里关闭会崩溃）
        if (!e.Args.Contains("--minimized")) ShowWindow(startPage);

        _ = AppHost.State.RefreshAsync();   // 启动时扫描并检查一次
    }

    private void ShowWindow(Type? page)
    {
        if (_window is null) return;
        if (!_window.IsVisible) _window.Show();
        if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
        _window.Activate();
        if (page is not null)
        {
            if (_window.IsLoaded) _window.ShowPage(page);
            else _window.Loaded += (_, _) => _window.ShowPage(page);
        }
    }

    private void OnWindowClosing(object? sender, CancelEventArgs e)
    {
        if (TrayPolicy.HideInsteadOfClose(AppHost.Settings.Current.TrayEnabled, _exitRequested))
        {
            e.Cancel = true;
            _window!.Hide();
            return;
        }
        ExitApp();
    }

    private void OnStateChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(AppState.LastChecked) || _tray is not { Visible: true }) return;
        var text = TrayPolicy.NotificationText(AppHost.State.Updates, _window is { IsVisible: true, WindowState: not WindowState.Minimized });
        if (text is not null) _tray.Notify("更新管理小助手", text);
    }

    private void ExitApp()
    {
        if (_exitRequested) return;
        _exitRequested = true;
        _tray?.Dispose();
        _window?.Close();
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();
        _activate?.Dispose();
        _singleInstance?.Dispose();
        base.OnExit(e);
    }
}
```

注：这一版替换了 Task 5 里的 `OnStartup`，`--page` 和 `PageTypes` 也合并到了这里。`ExitApp` 里的 `_window?.Close()` 会再次进入 `OnWindowClosing`，此时 `_exitRequested` 已为 true，`HideInsteadOfClose` 返回 false，然后 `ExitApp` 因为 `_exitRequested` 直接返回，不会重复执行。

- [ ] **Step 5: 编译**

Run: `dotnet build`
Expected: 0 警告 0 错误

- [ ] **Step 6: 本机核对（Review Focus 5 和单实例）**

用 PowerShell 操作窗口（不点任何安装按钮）：

```powershell
$exe = (Resolve-Path "src\UpdateHelper.App\bin\Debug\net10.0-windows10.0.26100.0\win-x64\UpdateHelper.exe").Path
Add-Type @"
using System; using System.Runtime.InteropServices;
public static class U { [DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr h, uint m, IntPtr w, IntPtr l); }
"@
# 1. 关窗口 → 缩到托盘，进程仍在
$p = Start-Process $exe -PassThru; Start-Sleep 5; $p.Refresh()
[U]::SendMessage($p.MainWindowHandle, 0x0010, [IntPtr]::Zero, [IntPtr]::Zero) | Out-Null   # WM_CLOSE
Start-Sleep 2; "关窗口后进程仍在：$(-not $p.HasExited)"
# 2. 再启动一次 → 第二个进程马上退出，第一个窗口重新出现
$p2 = Start-Process $exe -PassThru; Start-Sleep 3
"第二个实例已退出：$($p2.HasExited)；运行中的 UpdateHelper 进程数：$((Get-Process UpdateHelper).Count)"
$p | Stop-Process -Force
```

Expected：`关窗口后进程仍在：True`；`第二个实例已退出：True；运行中的 UpdateHelper 进程数：1`

```powershell
# 3. 托盘关闭时，关窗口就真正退出
$settings = "$env:LOCALAPPDATA\UpdateHelper\settings.json"
$backup = if (Test-Path $settings) { Get-Content $settings -Raw } else { $null }
Set-Content $settings '{"TrayEnabled": false}' -Encoding UTF8
$p = Start-Process $exe -PassThru; Start-Sleep 5; $p.Refresh()
[U]::SendMessage($p.MainWindowHandle, 0x0010, [IntPtr]::Zero, [IntPtr]::Zero) | Out-Null
Start-Sleep 2; "托盘关闭时关窗口后进程已退出：$($p.HasExited)"
if ($backup) { Set-Content $settings $backup -Encoding UTF8 } else { Remove-Item $settings }
```

Expected：`托盘关闭时关窗口后进程已退出：True`；之后设置文件恢复原样

```powershell
# 4. --minimized：不显示窗口，检查完成后右下角弹通知
$p = Start-Process $exe -ArgumentList "--minimized" -PassThru; Start-Sleep 25; $p.Refresh()
"窗口句柄（0 表示没有显示窗口）：$($p.MainWindowHandle)"
```

Expected：窗口句柄为 0；右下角弹出"发现 N 个更新，其中 M 个低风险。点击查看。"——**请用户确认看到了通知**，并请用户点一下通知，确认打开的是"更新"页；再在托盘图标上右键选"退出"，确认程序退出（`Get-Process UpdateHelper` 找不到进程）。这三项需要用户亲手操作，执行者不要用模拟点击代替。

- [ ] **Step 7: Commit**

```bash
git add -A
git commit -m "feat(app): 托盘图标、更新通知、关窗口缩到托盘、单实例"
```

---

## 完成标准

- `dotnet build` 0 警告 0 错误；`dotnet test`（两个测试项目）全部通过
- 6 个页面的截图与 Task 5 Step 9 的核对表一致
- Task 6 Step 6 的四项核对全部符合（第 4 项由用户亲自确认）
- 本计划的所有核对都**没有安装任何软件**

---

## 追加任务（2026-10-07，用户试用后提出）

用户试用托盘和通知后提出：首页没有图表看、程序没有图标、设置界面多加几种样式、我的软件要显示软件自己的图标。按 TDD 补做以下四项（逻辑在 Presentation 层带测试，绘制在 App 层截图核对）：

- **Task 7 外观设置**：主题（跟随系统/浅色/深色）、强调色（跟随系统 + 8 种预设）、窗口背景（云母/亚克力/纯色）、文字大小（小/标准/大）、紧凑模式。设置文件缺这些字段时用默认值，格式不对的颜色丢弃
- **Task 8 首页图表**：待更新的风险分布环形图（"不管"档不画）、软件分类条形图（从多到少，组件合集不算）
- **Task 9 程序图标**：`tools/Make-Icon.ps1` 生成多尺寸 ico（小尺寸 BMP、256 PNG），用于 exe、标题栏、托盘
- **Task 10 软件图标**：扫描时保留注册表 DisplayIcon，解析成"文件 + 序号"，界面取图标并缓存；取不到用通用图标