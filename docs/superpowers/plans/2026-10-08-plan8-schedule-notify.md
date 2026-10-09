# 计划 8：标准通知、定时检查、开机启动、观察期记录 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 更新提醒改用标准 Windows 通知（开着"请勿打扰"也能在通知中心看到）；程序在托盘里时按设置的间隔自动检查；可选开机自动启动到托盘；记录每个新版本第一次被发现的时间。

**Architecture:** 判断逻辑都放在 Presentation 层的纯类里（带测试）：`CheckSchedule` 决定现在该不该检查，`FirstSeenStore` 记录版本首次发现时间，`AutoStartPolicy` 决定开机启动登记该是什么样。App 层只负责接线：一个每分钟触发一次的计时器调用 `CheckSchedule`；用 `Microsoft.Toolkit.Uwp.Notifications` 发标准通知；用 `HKCU\...\Run` 登记开机启动（只在用户打开开关时写入）。不涉及管理员权限。

**Tech Stack:** .NET 10 WPF、Microsoft.Toolkit.Uwp.Notifications 7.1.3、xUnit。

**Spec:** `docs/superpowers/specs/2026-09-30-update-helper-design.md`（第 5 节观察期、第 6 节模式与提示、第 18 节检查频率）

## 技术验证（2026-10-08 本机）

- `Microsoft.Toolkit.Uwp.Notifications` 7.1.3 在 `net10.0-windows10.0.26100.0` 下能编译，`ToastContentBuilder().Show()` 发出的通知进了通知中心。
- 它间接引用 `System.Drawing.Common` 4.7.0（有已知漏洞，NU1904 警告）。项目开着 TreatWarningsAsErrors，所以 App 要直接引用新版 `System.Drawing.Common` 覆盖它。

## 范围

- **做**：标准通知（替换托盘气泡通知，点通知打开更新页）；定时检查；"开机自动启动"开关（默认关）；版本首次发现时间记录。
- **不做**：分级自动 / 全部自动的静默安装（需要"免确认更新"，暂缓）；不打扰规则（只对自动安装有意义）。设置页提示文字相应调整。

## Global Constraints

- 目标框架：Presentation 为 `net10.0-windows`；App 为 `net10.0-windows10.0.26100.0`、win-x64
- `TreatWarningsAsErrors=true`、`Nullable=enable`
- 检查频率："登录后 5 分钟检查一次；电脑一直开着时，距上次检查满 24 小时再查一次。用户可在设置中修改"（spec 第 18 节）
- "打开才检查"模式下不注册定时检查，托盘也不做后台检查（spec 第 6 节）
- 观察期："新版本第一次被发现（本地记录时间）后，先等 N 天"（spec 第 5 节）——本计划只做记录
- 开机启动默认关闭，只在用户打开开关时写入；关闭开关时删除登记
- 所有给用户看的文字是中文；提交信息不写任何 Claude 署名行
- 设置文件、首次发现记录都放在 `%LOCALAPPDATA%\UpdateHelper\`，读取永不抛异常，坏文件用默认值

## Review Focus

1. **检查正在进行时计时器又到点**：不重复开始检查 → Task 1 `Does_not_check_while_busy`
2. **电脑睡眠很久后唤醒**：唤醒后下一次计时器触发就检查一次，不会连续补检查多次 → Task 1 `Long_gap_triggers_one_check`
3. **首次发现记录文件损坏或被删**：当作空记录继续，不崩溃 → Task 2 `Corrupt_file_reads_as_empty`
4. **程序被移动到别的文件夹后开机启动登记还指向旧路径**：程序启动时发现开关是开的就按当前路径重写登记 → Task 3 `Enabled_registration_points_to_current_exe`
5. **通知被点击时程序窗口没显示**：打开窗口并跳到更新页 → Task 4 手动检查

---

## 文件结构

| 文件 | 职责 |
|---|---|
| `src/UpdateHelper.Presentation/CheckSchedule.cs` | 纯逻辑：现在该不该自动检查 |
| `src/UpdateHelper.Presentation/FirstSeenStore.cs` | 版本首次发现时间的读写（JSON 文件） |
| `src/UpdateHelper.Presentation/AutoStartPolicy.cs` | 纯逻辑：开机启动登记应有的值 |
| `src/UpdateHelper.Presentation/Settings/AppSettings.cs` | 加 `StartWithWindows`（默认 false） |
| `src/UpdateHelper.Presentation/ViewModels/SettingsViewModel.cs` | 开机启动开关；提示文字 |
| `src/UpdateHelper.Presentation/AppState.cs` | 每次检查完记录首次发现时间 |
| `src/UpdateHelper.App/Notifier.cs` | 发标准通知、处理点击 |
| `src/UpdateHelper.App/AutoStart.cs` | 读写 HKCU Run 登记 |
| `src/UpdateHelper.App/App.xaml.cs` | 接线：计时器、通知、开机启动 |
| `src/UpdateHelper.App/Pages/SettingsPage.xaml` | 开机启动开关 |

---

### Task 1: 定时检查的判断逻辑

**Files:**
- Create: `src/UpdateHelper.Presentation/CheckSchedule.cs`
- Test: `tests/UpdateHelper.Presentation.Tests/CheckScheduleTests.cs`

**Interfaces:**
- Produces: `public static class CheckSchedule { public static readonly TimeSpan LoginDelay = TimeSpan.FromMinutes(5); public static bool ShouldCheck(DateTimeOffset now, DateTimeOffset startedAt, bool startedAtLogin, DateTimeOffset? lastChecked, AppSettings settings, bool busy) }`

规则（按顺序）：
1. `busy` → false
2. `settings.Mode == UpdateMode.OnOpenOnly` → false（启动时那一次由 App 直接做，不经过这里）
3. 从没检查过（`lastChecked` 为 null）：开机启动的（`startedAtLogin`）要等 `now - startedAt >= LoginDelay`；手动打开的立即 true
4. 检查过：`now - lastChecked >= TimeSpan.FromHours(settings.CheckIntervalHours)` → true，否则 false

- [ ] **Step 1: 写失败的测试**

```csharp
using UpdateHelper.Presentation.Settings;

namespace UpdateHelper.Presentation.Tests;

public sealed class CheckScheduleTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 8, 9, 0, 0, TimeSpan.FromHours(8));
    private static readonly AppSettings Default = new();   // 只提醒、24 小时

    [Fact]
    public void Does_not_check_while_busy()
    {
        Assert.False(CheckSchedule.ShouldCheck(T0.AddDays(3), T0, false, T0, Default, busy: true));
    }

    [Fact]
    public void Open_only_mode_never_checks_in_background()
    {
        var s = Default with { Mode = UpdateMode.OnOpenOnly };
        Assert.False(CheckSchedule.ShouldCheck(T0.AddDays(3), T0, false, T0, s, busy: false));
        Assert.False(CheckSchedule.ShouldCheck(T0.AddMinutes(10), T0, true, null, s, busy: false));
    }

    [Fact]
    public void Login_start_waits_five_minutes_before_first_check()
    {
        Assert.False(CheckSchedule.ShouldCheck(T0.AddMinutes(4), T0, startedAtLogin: true, null, Default, false));
        Assert.True(CheckSchedule.ShouldCheck(T0.AddMinutes(5), T0, startedAtLogin: true, null, Default, false));
    }

    [Fact]
    public void Manual_start_checks_immediately_when_never_checked()
    {
        Assert.True(CheckSchedule.ShouldCheck(T0, T0, startedAtLogin: false, null, Default, false));
    }

    [Fact]
    public void Checks_again_after_interval()
    {
        Assert.False(CheckSchedule.ShouldCheck(T0.AddHours(23).AddMinutes(59), T0, false, T0, Default, false));
        Assert.True(CheckSchedule.ShouldCheck(T0.AddHours(24), T0, false, T0, Default, false));
    }

    [Fact]
    public void Uses_interval_from_settings()
    {
        var s = Default with { CheckIntervalHours = 6 };
        Assert.True(CheckSchedule.ShouldCheck(T0.AddHours(6), T0, false, T0, s, false));
    }

    [Fact]
    public void Long_gap_triggers_one_check()
    {
        // 睡眠三天后唤醒：到点就检查一次；检查完 lastChecked 更新，下一分钟不会再检查
        var wake = T0.AddDays(3);
        Assert.True(CheckSchedule.ShouldCheck(wake, T0, false, T0, Default, false));
        Assert.False(CheckSchedule.ShouldCheck(wake.AddMinutes(1), T0, false, wake, Default, false));
    }
}
```

- [ ] **Step 2: 运行测试，确认失败**

Run: `dotnet test tests/UpdateHelper.Presentation.Tests --filter CheckScheduleTests`
Expected: 编译失败（`CheckSchedule` 不存在）

- [ ] **Step 3: 实现**

```csharp
using UpdateHelper.Presentation.Settings;

namespace UpdateHelper.Presentation;

/// <summary>托盘里的定时检查：现在该不该自动检查一次（spec 第 6、18 节）。</summary>
public static class CheckSchedule
{
    /// <summary>开机自动启动后，等这么久再做第一次检查，避免和开机时的其他程序抢资源。</summary>
    public static readonly TimeSpan LoginDelay = TimeSpan.FromMinutes(5);

    public static bool ShouldCheck(DateTimeOffset now, DateTimeOffset startedAt, bool startedAtLogin,
        DateTimeOffset? lastChecked, AppSettings settings, bool busy)
    {
        if (busy) return false;
        if (settings.Mode == UpdateMode.OnOpenOnly) return false;
        if (lastChecked is null) return !startedAtLogin || now - startedAt >= LoginDelay;
        return now - lastChecked.Value >= TimeSpan.FromHours(settings.CheckIntervalHours);
    }
}
```

- [ ] **Step 4: 运行测试，确认通过**

Run: `dotnet test tests/UpdateHelper.Presentation.Tests --filter CheckScheduleTests`
Expected: 7 个全部通过

- [ ] **Step 5: 提交**

```bash
git add src/UpdateHelper.Presentation/CheckSchedule.cs tests/UpdateHelper.Presentation.Tests/CheckScheduleTests.cs
git commit -m "feat(presentation): 定时检查的判断——开机启动后等 5 分钟，之后按设置的间隔"
```

---

### Task 2: 记录版本首次发现时间

**Files:**
- Create: `src/UpdateHelper.Presentation/FirstSeenStore.cs`
- Modify: `src/UpdateHelper.Presentation/AppState.cs`
- Test: `tests/UpdateHelper.Presentation.Tests/FirstSeenStoreTests.cs`

**Interfaces:**
- Produces:
  - `public sealed class FirstSeenStore(string filePath, TimeProvider? clock = null) { public static string DefaultPath { get; } public DateTimeOffset? Get(string packageId, string version); public void Record(IEnumerable<(string PackageId, string Version)> seen) }` —— `Record` 只给还没有记录的"包 id + 版本"写入当前时间；已有的保持原时间；读取失败当作空；写入先写临时文件再替换；写入失败不抛异常（下次再记）
  - `AppState` 构造函数增加可选参数 `FirstSeenStore? firstSeen = null`；`RefreshAsync` 成功拿到更新列表后调用 `firstSeen?.Record(...)`（"不管"档也记录；记录失败不影响检查结果）

- [ ] **Step 1: 写失败的测试**

```csharp
namespace UpdateHelper.Presentation.Tests;

public sealed class FirstSeenStoreTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("uh-seen-").FullName;
    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    private string FilePath => Path.Combine(_dir, "first-seen.json");

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now.ToUniversalTime();
    }

    [Fact]
    public void Records_first_time_and_keeps_it()
    {
        var clock = new FixedClock(new DateTimeOffset(2026, 10, 8, 9, 0, 0, TimeSpan.Zero));
        var store = new FirstSeenStore(FilePath, clock);

        store.Record([("Tencent.QQ", "9.9.21")]);
        clock.Now = clock.Now.AddDays(2);
        store.Record([("Tencent.QQ", "9.9.21"), ("Git.Git", "2.51.0")]);

        Assert.Equal(new DateTimeOffset(2026, 10, 8, 9, 0, 0, TimeSpan.Zero), store.Get("Tencent.QQ", "9.9.21"));
        Assert.Equal(new DateTimeOffset(2026, 10, 10, 9, 0, 0, TimeSpan.Zero), store.Get("Git.Git", "2.51.0"));
        Assert.Null(store.Get("Tencent.QQ", "9.9.22"));
    }

    [Fact]
    public void Survives_restart()
    {
        var clock = new FixedClock(new DateTimeOffset(2026, 10, 8, 9, 0, 0, TimeSpan.Zero));
        new FirstSeenStore(FilePath, clock).Record([("Tencent.QQ", "9.9.21")]);

        Assert.Equal(clock.Now, new FirstSeenStore(FilePath).Get("Tencent.QQ", "9.9.21"));
    }

    [Fact]
    public void Package_id_is_case_insensitive()
    {
        var store = new FirstSeenStore(FilePath);
        store.Record([("Tencent.QQ", "9.9.21")]);
        Assert.NotNull(store.Get("tencent.qq", "9.9.21"));
    }

    [Fact]
    public void Corrupt_file_reads_as_empty()
    {
        File.WriteAllText(FilePath, "{ not json");
        var store = new FirstSeenStore(FilePath);

        Assert.Null(store.Get("Tencent.QQ", "9.9.21"));
        store.Record([("Tencent.QQ", "9.9.21")]);          // 不抛异常，并且覆盖成好文件
        Assert.NotNull(new FirstSeenStore(FilePath).Get("Tencent.QQ", "9.9.21"));
    }

    [Fact]
    public void Missing_directory_is_created()
    {
        var store = new FirstSeenStore(Path.Combine(_dir, "sub", "first-seen.json"));
        store.Record([("Tencent.QQ", "9.9.21")]);
        Assert.NotNull(store.Get("Tencent.QQ", "9.9.21"));
    }
}
```

另在 `tests/UpdateHelper.Presentation.Tests/AppStateTests.cs` 加一个测试（用现有的 FakeBackend 造数方式）：

```csharp
    [Fact]
    public async Task Refresh_records_first_seen_versions()
    {
        var dir = Directory.CreateTempSubdirectory("uh-state-seen-").FullName;
        try
        {
            var backend = new FakeBackend { Report = new UpdateReport([Update("QQ", UpdateTier.Low)], null) };
            var store = new FirstSeenStore(Path.Combine(dir, "first-seen.json"));
            var state = new AppState(backend, firstSeen: store);

            await state.RefreshAsync();

            var u = state.Updates.Single();
            Assert.NotNull(store.Get(u.Candidate.PackageId, u.Candidate.AvailableVersion));
        }
        finally { Directory.Delete(dir, true); }
    }
```

（`Update(...)` 是 FakeBackend 里已有的造数方法；如果 AppStateTests.cs 没有 `using static ...FakeBackend;`，照 PageViewModelTests.cs 的写法加上。）

- [ ] **Step 2: 运行测试，确认失败**

Run: `dotnet test tests/UpdateHelper.Presentation.Tests --filter "FirstSeenStoreTests|AppStateTests"`
Expected: 编译失败

- [ ] **Step 3: 实现**

`src/UpdateHelper.Presentation/FirstSeenStore.cs`：

```csharp
using System.Text;
using System.Text.Json;

namespace UpdateHelper.Presentation;

/// <summary>
/// 记录每个"包 id + 版本"第一次被发现的时间，供观察期使用（spec 第 5 节）。
/// 读取永不抛异常（文件缺失或损坏当作空）；写入失败也不抛异常，下次检查时再记。
/// </summary>
public sealed class FirstSeenStore(string filePath, TimeProvider? clock = null)
{
    public static string DefaultPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UpdateHelper", "first-seen.json");

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    public DateTimeOffset? Get(string packageId, string version) =>
        Load().TryGetValue(Key(packageId, version), out var t) ? t : null;

    public void Record(IEnumerable<(string PackageId, string Version)> seen)
    {
        var data = Load();
        var now = _clock.GetUtcNow();
        var changed = false;
        foreach (var (id, version) in seen)
            changed |= data.TryAdd(Key(id, version), now);
        if (!changed) return;

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(filePath))!);
            var temp = filePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(data), new UTF8Encoding(false));
            File.Move(temp, filePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static string Key(string packageId, string version) => $"{packageId.ToLowerInvariant()}|{version}";

    private Dictionary<string, DateTimeOffset> Load()
    {
        try
        {
            if (!File.Exists(filePath)) return [];
            return JsonSerializer.Deserialize<Dictionary<string, DateTimeOffset>>(File.ReadAllText(filePath)) ?? [];
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return [];
        }
    }
}
```

`src/UpdateHelper.Presentation/AppState.cs`：
- 构造函数改为 `AppState(IAppBackend backend, TimeProvider? clock = null, FirstSeenStore? firstSeen = null)`（保留现有参数顺序，新参数放最后，现有调用不用改）。
- `RefreshAsync` 里 `Updates = report.Updates;` 之后加：

```csharp
            // 观察期从"第一次被发现"开始算；记录失败不影响这次检查
            firstSeen?.Record(report.Updates.Select(u => (u.Candidate.PackageId, u.Candidate.AvailableVersion)));
```

- [ ] **Step 4: 运行测试，确认通过**

Run: `dotnet test tests/UpdateHelper.Presentation.Tests`
Expected: 全部通过

- [ ] **Step 5: 提交**

```bash
git add src/UpdateHelper.Presentation tests/UpdateHelper.Presentation.Tests
git commit -m "feat(presentation): 记录每个新版本第一次被发现的时间，给观察期用"
```

---

### Task 3: 开机自动启动（设置开关 + 登记逻辑）

**Files:**
- Modify: `src/UpdateHelper.Presentation/Settings/AppSettings.cs`
- Create: `src/UpdateHelper.Presentation/AutoStartPolicy.cs`
- Modify: `src/UpdateHelper.Presentation/ViewModels/SettingsViewModel.cs`
- Create: `src/UpdateHelper.App/AutoStart.cs`
- Test: `tests/UpdateHelper.Presentation.Tests/AutoStartTests.cs`

**Interfaces:**
- Produces:
  - `AppSettings.StartWithWindows`（bool，默认 false）
  - `public static class AutoStartPolicy { public const string ValueName = "UpdateHelper"; public static string? DesiredCommand(bool enabled, string exePath) }` —— 开：`"\"<exePath>\" --minimized --login"`；关：null（表示删除登记）
  - `SettingsViewModel.StartWithWindows`（读写设置）
  - App：`static class AutoStart { static void Apply(bool enabled) }` —— 按 `AutoStartPolicy.DesiredCommand(enabled, Environment.ProcessPath!)` 写入或删除 `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` 下的 `UpdateHelper` 值；只在值不同的时候写；失败不抛异常
  - 命令行参数 `--login`：表示由开机启动拉起（Task 4 用它决定第一次检查等 5 分钟）


- [ ] **Step 1: 写失败的测试**

```csharp
namespace UpdateHelper.Presentation.Tests;

public sealed class AutoStartTests
{
    [Fact]
    public void Enabled_registration_quotes_path_and_adds_flags()
    {
        var cmd = AutoStartPolicy.DesiredCommand(true, @"C:\Program Files\UpdateHelper\UpdateHelper.exe");
        Assert.Equal("\"C:\\Program Files\\UpdateHelper\\UpdateHelper.exe\" --minimized --login", cmd);
    }

    [Fact]
    public void Disabled_means_remove()
    {
        Assert.Null(AutoStartPolicy.DesiredCommand(false, @"C:\x\UpdateHelper.exe"));
    }

    [Fact]
    public void Enabled_registration_points_to_current_exe()
    {
        // 程序被挪到别的文件夹后，开关仍是开的：登记用的是传入的当前路径，不是旧路径
        var moved = AutoStartPolicy.DesiredCommand(true, @"D:\NewFolder\UpdateHelper.exe");
        Assert.Contains(@"D:\NewFolder\UpdateHelper.exe", moved);
    }
}
```

另在 `tests/UpdateHelper.Presentation.Tests/AppearanceTests.cs`（已有的设置测试文件）或 `AgentSettingsTests.cs` 旁边新增对 `StartWithWindows` 的覆盖，放进现有的设置往返测试风格里；最简单的是在 `AppearanceTests` 加：

```csharp
    [Fact]
    public void Start_with_windows_defaults_off_and_survives_save()
    {
        Assert.False(new AppSettings().StartWithWindows);
        var store = new SettingsStore(SettingsPath);
        store.Save(new AppSettings { StartWithWindows = true });
        Assert.True(new SettingsStore(SettingsPath).Load().StartWithWindows);
    }
```

（`SettingsPath` 是 AppearanceTests 里已有的临时路径属性；若该测试类结构不同，照它现有的保存/加载写法写一条等价的。）

- [ ] **Step 2: 运行测试，确认失败**

Run: `dotnet test tests/UpdateHelper.Presentation.Tests --filter "AutoStartTests|AppearanceTests"`
Expected: 编译失败（`AutoStartPolicy`、`StartWithWindows` 不存在）

- [ ] **Step 3: 实现**

`src/UpdateHelper.Presentation/Settings/AppSettings.cs`：在 record 里加（放在 `Compact` 附近）：

```csharp
    /// <summary>开机时自动启动到托盘（默认关）。</summary>
    public bool StartWithWindows { get; init; }
```

`src/UpdateHelper.Presentation/AutoStartPolicy.cs`：

```csharp
namespace UpdateHelper.Presentation;

/// <summary>开机自动启动在注册表 Run 键里应有的登记值。只算字符串，真正读写在 App 层。</summary>
public static class AutoStartPolicy
{
    /// <summary>Run 键下的值名。</summary>
    public const string ValueName = "UpdateHelper";

    /// <summary>开启时返回命令行（exe 路径加引号，带 --minimized --login）；关闭时返回 null，表示删除登记。</summary>
    public static string? DesiredCommand(bool enabled, string exePath) =>
        enabled ? $"\"{exePath}\" --minimized --login" : null;
}
```

`src/UpdateHelper.Presentation/ViewModels/SettingsViewModel.cs`：加属性（放在 `Compact` 附近）：

```csharp
    public bool StartWithWindows
    {
        get => _settings.Current.StartWithWindows;
        set => _settings.Update(s => s with { StartWithWindows = value });
    }
```

`src/UpdateHelper.App/AutoStart.cs`：

```csharp
using Microsoft.Win32;
using UpdateHelper.Presentation;

namespace UpdateHelper.App;

/// <summary>把"开机自动启动"开关落到注册表 HKCU Run 键。只在值需要变化时写；失败不抛异常。</summary>
public static class AutoStart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public static void Apply(bool enabled)
    {
        var desired = AutoStartPolicy.DesiredCommand(enabled, Environment.ProcessPath!);
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true)
                            ?? Registry.CurrentUser.CreateSubKey(RunKey);
            var current = key.GetValue(AutoStartPolicy.ValueName) as string;
            if (desired is null)
            {
                if (current is not null) key.DeleteValue(AutoStartPolicy.ValueName, throwOnMissingValue: false);
            }
            else if (!string.Equals(current, desired, StringComparison.Ordinal))
            {
                key.SetValue(AutoStartPolicy.ValueName, desired, RegistryValueKind.String);
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { }
    }
}
```

- [ ] **Step 4: 运行测试，确认通过**

Run: `dotnet test tests/UpdateHelper.Presentation.Tests`
Expected: 全部通过

- [ ] **Step 5: 提交**

```bash
git add src/UpdateHelper.Presentation tests/UpdateHelper.Presentation.Tests src/UpdateHelper.App/AutoStart.cs
git commit -m "feat(app): 开机自动启动开关（默认关），按当前程序路径写注册表 Run 键"
```

---

### Task 4: 标准通知、定时计时器、设置页开关（接线）

**Files:**
- Modify: `src/UpdateHelper.App/UpdateHelper.App.csproj`（加 Microsoft.Toolkit.Uwp.Notifications 和新版 System.Drawing.Common）
- Create: `src/UpdateHelper.App/Notifier.cs`
- Modify: `src/UpdateHelper.App/App.xaml.cs`（计时器、通知、开机启动接线；记录启动时间和是否开机启动；`--login`）
- Modify: `src/UpdateHelper.App/AppHost.cs`（AppState 带上 FirstSeenStore）
- Modify: `src/UpdateHelper.App/Pages/SettingsPage.xaml`（开机启动开关）
- Modify: `src/UpdateHelper.Presentation/ViewModels/SettingsViewModel.cs`（`AutoUpdateNote` 文案）

本任务没有新单元测试（都是接线和真实 Windows API），靠构建 + 截图 + 手动检查验证。

- [ ] **Step 1: 加包，覆盖有漏洞的间接依赖**

`src/UpdateHelper.App/UpdateHelper.App.csproj` 的包引用组里加：

```xml
    <PackageReference Include="Microsoft.Toolkit.Uwp.Notifications" Version="7.1.3" />
    <!-- 上面那个包间接带了有漏洞的 System.Drawing.Common 4.7.0；直接引用新版把它顶掉（TreatWarningsAsErrors 下 NU1904 会让构建失败） -->
    <PackageReference Include="System.Drawing.Common" Version="8.0.10" />
```

构建一次确认没有 NU1904：`dotnet build src/UpdateHelper.App -c Debug -v q --nologo`，Expected `0 个错误` 且无 NU1904。若新版 System.Drawing.Common 仍报漏洞，换成当前 `dotnet list package --vulnerable` 不报的最低版本，并在报告里写明版本。

- [ ] **Step 2: Notifier**

`src/UpdateHelper.App/Notifier.cs`：

```csharp
using Microsoft.Toolkit.Uwp.Notifications;

namespace UpdateHelper.App;

/// <summary>标准 Windows 通知：开着"请勿打扰"也会留在通知中心。点击通知带回 "page=updates"。</summary>
public static class Notifier
{
    /// <summary>点击通知时触发，参数是通知里带的 page 值（这里只有 "updates"）。</summary>
    public static event Action<string>? Activated;

    private static bool _hooked;

    public static void Show(string title, string message)
    {
        if (!_hooked)
        {
            ToastNotificationManagerCompat.OnActivated += e =>
            {
                var args = ToastArguments.Parse(e.Argument);
                if (args.TryGetValue("page", out var page)) Activated?.Invoke(page);
            };
            _hooked = true;
        }

        new ToastContentBuilder()
            .AddArgument("page", "updates")
            .AddText(title)
            .AddText(message)
            .Show();
    }

    /// <summary>退出时清理，避免通知残留回调。</summary>
    public static void Uninstall()
    {
        try { ToastNotificationManagerCompat.Uninstall(); } catch (Exception) { }
    }
}
```

> 注意：`ToastNotificationManagerCompat.OnActivated` 的回调在后台线程触发，App 里用它跳页面时要 `Dispatcher.Invoke`（见 Step 3）。

- [ ] **Step 3: App.xaml.cs 接线**

改动点（保持现有单实例、托盘、外观逻辑不动）：

1. 字段加启动时间和是否开机启动：

```csharp
    private readonly DateTimeOffset _startedAt = DateTimeOffset.Now;
    private bool _startedAtLogin;
    private System.Windows.Threading.DispatcherTimer? _checkTimer;
```

2. `OnStartup` 里解析参数时加：`_startedAtLogin = e.Args.Contains("--login");`

3. 开机启动：在读到设置后、按当前路径对齐一次登记（Review Focus 4——程序被挪动后重写）：

```csharp
        AutoStart.Apply(AppHost.Settings.Current.StartWithWindows);
        AppHost.Settings.PropertyChanged += (_, _) => AutoStart.Apply(AppHost.Settings.Current.StartWithWindows);
```

4. 通知改用 Notifier（替换原来的托盘气泡）：
   - `OnStateChanged` 里把 `_tray.Notify(...)` 换成 `Notifier.Show("更新管理小助手", text);`。
   - `OnStartup` 里订阅一次：

```csharp
        Notifier.Activated += page => Dispatcher.Invoke(() => ShowWindow(page == "updates" ? typeof(Pages.UpdatesPage) : null));
```

   - `ExitApp` 和 `OnWindowClosing` 真正退出前调用 `Notifier.Uninstall();`。
   - `TrayIcon` 的 `Notify` 方法保留但不再调用（或删掉；若删，连带去掉 ToolTipIcon 相关 using）。本步选择保留不调用，减少改动面，并在报告里说明。

5. 定时检查计时器（每分钟问一次 CheckSchedule）：

```csharp
        _checkTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
        _checkTimer.Tick += (_, _) =>
        {
            if (CheckSchedule.ShouldCheck(DateTimeOffset.Now, _startedAt, _startedAtLogin,
                    AppHost.State.LastChecked, AppHost.Settings.Current, AppHost.State.IsBusy))
                _ = AppHost.State.RefreshAsync();
        };
        _checkTimer.Start();
```

   放在启动时那次 `RefreshAsync()` 之后。注意："打开才检查"模式下 CheckSchedule 直接返回 false，计时器空转无害；启动时的首次检查仍照常（符合 spec：打开就检查一次）。

6. `OnExit` 里 `_checkTimer?.Stop();`

- [ ] **Step 4: AppHost 带上 FirstSeenStore**

`src/UpdateHelper.App/AppHost.cs` 里创建 AppState 的那行改为：

```csharp
        State = new AppState(backend, firstSeen: new FirstSeenStore(FirstSeenStore.DefaultPath));
```

- [ ] **Step 5: 设置页开机启动开关 + 文案**

`src/UpdateHelper.App/Pages/SettingsPage.xaml`：在"托盘图标"卡片后面加一张卡片：

```xml
            <ui:CardControl Margin="0,4,0,0">
                <ui:CardControl.Header>
                    <StackPanel>
                        <TextBlock Text="开机自动启动" />
                        <TextBlock Text="开机后自动到托盘运行，按设置的间隔检查更新" FontSize="12" Opacity="0.7" />
                    </StackPanel>
                </ui:CardControl.Header>
                <ui:ToggleSwitch IsChecked="{Binding StartWithWindows}" />
            </ui:CardControl>
```

（这张卡片的 DataContext 跟"托盘图标"那张一致，绑的是 `AppHost.SettingsPage` 这个 SettingsViewModel。）

`src/UpdateHelper.Presentation/ViewModels/SettingsViewModel.cs` 的 `AutoUpdateNote` 改为：

```csharp
    public string AutoUpdateNote => "现在会按设置的间隔自动检查更新，发现更新时通知你；自动安装（分级自动、全部自动）要等后续版本的免确认更新做好后才启用。";
```

- [ ] **Step 6: 构建 + 截图核对**

Run:
```powershell
dotnet build src/UpdateHelper.App -c Debug -v q --nologo
$exe = (Get-ChildItem src/UpdateHelper.App/bin/Debug -Recurse -Filter UpdateHelper.exe | Sort-Object LastWriteTime -Descending | Select-Object -First 1).FullName
& .\tools\Screenshot-Window.ps1 -Exe $exe -Arguments "--page settings" -WaitSeconds 6 -Out "<scratchpad>\plan8-settings.png"
```
Expected: `0 个错误`；截图里设置页有"开机自动启动"开关（默认关），提示文案已更新。读这张图确认。

- [ ] **Step 7: 手动检查（控制者做，不需要用户）**

1. 删掉 `%LOCALAPPDATA%\UpdateHelper\settings.json`（用默认设置）。启动 `UpdateHelper.exe`（不带 --minimized），等启动检查完成，确认右下角弹出标准通知"发现 N 个更新"。查通知中心（用计划 6 时的 wpndatabase 查询法）确认这条通知的来源是本程序、且留在了通知中心。
2. 点那条通知，确认程序窗口打开并跳到"更新"页（Review Focus 5）。
3. 设置页打开"开机自动启动"，用 `reg query "HKCU\Software\Microsoft\Windows\CurrentVersion\Run" /v UpdateHelper` 确认写入了带当前 exe 路径和 `--minimized --login` 的值；关闭开关，确认该值被删除。
4. 把 `settings.json` 里 `CheckIntervalHours` 临时改成最小值、`LastChecked` 造一个很久以前的时间，启动程序（托盘模式），确认一分钟内自动检查了一次（看首页"上次检查"时间更新）。做完恢复。

把每步结果记进报告。

- [ ] **Step 8: 全部测试 + 提交**

Run: `dotnet test`
Expected: 全部通过（Core 397 + Presentation 原有 + 本计划新增）

```bash
git add src tests
git commit -m "feat(app): 更新提醒改用标准 Windows 通知；托盘里按间隔定时检查；设置页开机启动开关"
```

---

## 自查（写完计划后对照 spec）

- **spec 第 18 节检查频率**（登录后 5 分钟、之后 24 小时、可改）→ Task 1 `CheckSchedule` + Task 4 计时器；`--login` 区分开机启动。✅
- **spec 第 6 节"打开才检查"不后台检查**→ Task 1 规则 2。✅
- **spec 第 6 节通知点击即打开**→ Task 4 Notifier.Activated。✅（"点击通知即同意并自动装完"要等免确认更新，本期只做"打开更新页"，设置文案已说明）
- **spec 第 5 节观察期从首次发现算**→ Task 2 FirstSeenStore（本期只记录，判断层用它留到自动模式）。✅
- **"请勿打扰"也能看到**→ Task 4 标准通知（已实测进通知中心）。✅
- 占位符扫描：无 TODO / 待填；每个代码步骤都有完整代码。
- 类型一致：`CheckSchedule.ShouldCheck`、`FirstSeenStore.Record/Get`、`AutoStartPolicy.DesiredCommand`、`AppSettings.StartWithWindows` 在定义和使用处签名一致。
- Review Focus 五项都已指到对应 Task 的测试或手动检查。

## 计划结束后

- 整个分支交给一个全新的审查员（最强模型）做整体审查（重点：通知点击线程、计时器与忙碌判断、注册表写入失败处理、首次发现文件并发）。
- 审查通过、全部测试绿后合并 main、推送。
- 发 **0.2.0**：这一期不含"免确认更新"，所以 0.2.0 的 CHANGELOG 要重写成"标准通知 + 定时检查 + 开机启动 + 观察期记录"，不要写免确认更新（那部分仍在 feat/plan7-agent 分支、未合并）。更新 README 路线图。按既有发版流程打 tag、传两个 zip。发版前用 Release 版冒烟启动一次。
