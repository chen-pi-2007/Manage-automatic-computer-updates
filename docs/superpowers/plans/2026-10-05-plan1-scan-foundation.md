# 计划 1：扫描底层 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 做出只读的扫描核心库：读出电脑上全部已装软件（含隐藏条目）、服务、开机自启项、计划任务，把它们归组、分类，并用一个命令行工具在真实电脑上验证。

**Architecture:** `UpdateHelper.Core` 类库通过"来源"接口（`IUninstallSource`、`IBackgroundSource`）读取系统信息，真实实现读注册表和任务计划程序，测试用假数据实现。纯逻辑部分（命令行路径解析、归组、分类）不碰系统，全部单元测试覆盖。`UpdateHelper.ScanCli` 把结果打印出来，用于在本机核对。

**Tech Stack:** C# / .NET 10（`net10.0-windows`）、xUnit、Microsoft.Win32.Registry、任务计划程序 COM（`Schedule.Service`）；学习版用 Python 3（`winreg`）。

**Spec:** `docs/superpowers/specs/2026-09-30-update-helper-design.md`（第 3、4 节；构建顺序见第 14 节第 1 步）

## Global Constraints

- 目标框架：`net10.0-windows`；所有项目开启 `<Nullable>enable</Nullable>`、`<ImplicitUsings>enable</ImplicitUsings>`、`<TreatWarningsAsErrors>true</TreatWarningsAsErrors>`
- 支持系统：Windows 10 1809 及以上、Windows 11
- **只读**：本计划的任何代码都不得写注册表、改服务、改计划任务或删除文件
- 不访问网络
- 界面/输出文字用中文；代码标识符用英文
- 测试中不得读取真实注册表（通过假来源注入）；真实系统只由 `ScanCli` 手动运行
- 根命名空间：`UpdateHelper.Core`

## Review Focus

1. **注册表值类型不对**（比如 `DisplayVersion` 存成 DWORD、`EstimatedSize` 存成字符串）→ 该字段当作缺失，扫描不能崩
2. **某个注册表键没有读取权限** → 跳过这个键，其他照常
3. **命令行里的路径格式怪**（不带引号但含空格、含 `%ProgramFiles%`、`rundll32 x.dll,入口`、`svchost -k`）→ 尽量解析出 exe 路径，解析不出就返回 null，不抛异常
4. **同一软件在 64 位和 32 位注册表各登记一次** → 只显示一条
5. **计划任务读取失败**（COM 不可用、任务没有"启动程序"类动作、XML 损坏）→ 跳过该任务，其余照常

以上每一条都在对应任务里有专门的测试。

---

## 文件结构

```
UpdateHelper.slnx
Directory.Build.props                      统一编译选项
src/UpdateHelper.Core/
  UpdateHelper.Core.csproj
  CoreInfo.cs                              产品名等基本信息
  Scanning/UninstallEntry.cs               注册表卸载条目（数据）
  Scanning/IUninstallSource.cs             卸载条目来源接口
  Scanning/RegistryUninstallSource.cs      真实实现：读 3 处注册表
  Scanning/RegistryValues.cs               安全读取注册表值的小工具
  Scanning/BackgroundItem.cs               后台项目（数据）
  Scanning/IBackgroundSource.cs            后台项目来源接口
  Scanning/ServiceSource.cs                服务（读注册表 Services 键）
  Scanning/RunKeySource.cs                 Run 键开机自启
  Scanning/StartupFolderSource.cs          启动文件夹
  Scanning/ScheduledTaskSource.cs          计划任务（COM）
  Scanning/CommandLineParser.cs            从命令行提取 exe 路径（纯逻辑）
  Grouping/SoftwareCategory.cs             分类枚举
  Grouping/SoftwareGroup.cs                归组结果（含 ScanResult）
  Grouping/PathUtil.cs                     路径规范化、"是否在某目录内"
  Grouping/SoftwareClassifier.cs           分类规则（纯逻辑）
  Grouping/SoftwareGrouper.cs              归组算法（纯逻辑）
  Scanning/SystemScanner.cs                把所有来源串起来
src/UpdateHelper.ScanCli/
  UpdateHelper.ScanCli.csproj
  Program.cs                               打印扫描结果 / 导出 JSON
tests/UpdateHelper.Core.Tests/
  UpdateHelper.Core.Tests.csproj
  Fakes.cs                                 测试数据构造器
  SmokeTests.cs
  BackgroundParsingTests.cs
  CommandLineParserTests.cs
  RegistryValuesTests.cs
  SoftwareClassifierTests.cs
  SoftwareGrouperTests.cs
  SystemScannerTests.cs
learning/python/01_扫描已装软件.py
learning/python/README.md
```

## 任务清单

- Task 1：安装 .NET 10 SDK，搭项目骨架
- Task 2：卸载条目数据模型 + 安全读值 + 注册表来源
- Task 3：命令行路径解析
- Task 4：后台项目来源（服务、Run 键、启动文件夹、计划任务）
- Task 5：分类器
- Task 6：归组器
- Task 7：SystemScanner + ScanCli，本机核对
- Task 8：Python 学习版 01

---

### Task 1：安装 .NET 10 SDK，搭项目骨架

**Files:**
- Create: `Directory.Build.props`、`UpdateHelper.slnx`、`.gitignore`
- Create: `src/UpdateHelper.Core/UpdateHelper.Core.csproj`
- Create: `src/UpdateHelper.ScanCli/UpdateHelper.ScanCli.csproj`、`src/UpdateHelper.ScanCli/Program.cs`
- Create: `tests/UpdateHelper.Core.Tests/UpdateHelper.Core.Tests.csproj`、`tests/UpdateHelper.Core.Tests/SmokeTests.cs`

**Interfaces:**
- Produces: 可编译的解决方案；命令 `dotnet test` 能跑通

- [ ] **Step 1: 安装 SDK**（本机只有运行时，没有 SDK）

Run: `winget install --id Microsoft.DotNet.SDK.10 -e --accept-package-agreements --accept-source-agreements`
会弹一次管理员确认框，需要用户点"是"。
Then: `dotnet --list-sdks`
Expected: 出现一行 `10.0.xxx`

- [ ] **Step 2: 写 `Directory.Build.props`**（放在仓库根目录，所有项目自动继承）

```xml
<Project>
  <PropertyGroup>
    <TargetFramework>net10.0-windows</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
    <LangVersion>latest</LangVersion>
  </PropertyGroup>
</Project>
```

- [ ] **Step 3: 用模板创建三个项目和解决方案**

```bash
dotnet new classlib -o src/UpdateHelper.Core -n UpdateHelper.Core
dotnet new console  -o src/UpdateHelper.ScanCli -n UpdateHelper.ScanCli
dotnet new xunit    -o tests/UpdateHelper.Core.Tests -n UpdateHelper.Core.Tests
dotnet new sln -n UpdateHelper
dotnet sln add src/UpdateHelper.Core src/UpdateHelper.ScanCli tests/UpdateHelper.Core.Tests
dotnet add src/UpdateHelper.ScanCli reference src/UpdateHelper.Core
dotnet add tests/UpdateHelper.Core.Tests reference src/UpdateHelper.Core
dotnet new gitignore
```

然后删掉模板生成的 `src/UpdateHelper.Core/Class1.cs` 和 `tests/UpdateHelper.Core.Tests/UnitTest1.cs`；并把三个 csproj 里模板自带的 `<TargetFramework>`、`<Nullable>`、`<ImplicitUsings>` 三行删掉（由 `Directory.Build.props` 统一提供，避免重复）。

- [ ] **Step 4: 写一个冒烟测试** `tests/UpdateHelper.Core.Tests/SmokeTests.cs`

```csharp
namespace UpdateHelper.Core.Tests;

public class SmokeTests
{
    [Fact]
    public void Core_assembly_loads()
    {
        Assert.Equal("UpdateHelper.Core", typeof(UpdateHelper.Core.CoreInfo).Assembly.GetName().Name);
    }
}
```

- [ ] **Step 5: 运行，确认失败**

Run: `dotnet test`
Expected: 编译失败，提示找不到 `UpdateHelper.Core.CoreInfo`

- [ ] **Step 6: 写 `src/UpdateHelper.Core/CoreInfo.cs`**

```csharp
namespace UpdateHelper.Core;

/// <summary>核心库的基本信息。</summary>
public static class CoreInfo
{
    public const string ProductName = "更新管理小助手";
}
```

- [ ] **Step 7: 运行，确认通过**

Run: `dotnet test`
Expected: `Passed!  - Failed: 0, Passed: 1`

- [ ] **Step 8: Commit**

```bash
git add -A
git commit -m "chore: 搭建 .NET 10 解决方案骨架（Core / ScanCli / Tests）"
```

### Task 2：卸载条目数据模型 + 安全读值 + 注册表来源

**Files:**
- Create: `src/UpdateHelper.Core/Scanning/UninstallEntry.cs`
- Create: `src/UpdateHelper.Core/Scanning/RegistryValues.cs`
- Create: `src/UpdateHelper.Core/Scanning/IUninstallSource.cs`
- Create: `src/UpdateHelper.Core/Scanning/RegistryUninstallSource.cs`
- Test: `tests/UpdateHelper.Core.Tests/RegistryValuesTests.cs`

**Interfaces:**
- Produces:
  - `enum UninstallHive { LocalMachine64, LocalMachine32, CurrentUser }`
  - `sealed record UninstallEntry(string KeyName, UninstallHive Hive, string DisplayName, string? DisplayVersion, string? Publisher, string? InstallLocation, string? UninstallString, string? QuietUninstallString, bool IsSystemComponent, string? ParentKeyName, string? ReleaseType, long? EstimatedSizeKb)`，以及计算属性 `bool IsUpdateOrPatch`（`ParentKeyName` 或 `ReleaseType` 非空）
  - `static class RegistryValues`：`string? AsString(object?)`、`long? AsLong(object?)`、`bool AsFlag(object?)`、`UninstallEntry? ToEntry(string keyName, UninstallHive hive, Func<string, object?> getValue)`
  - `interface IUninstallSource { IReadOnlyList<UninstallEntry> Read(); }`
  - `sealed class RegistryUninstallSource : IUninstallSource`

- [ ] **Step 1: 写失败的测试** `tests/UpdateHelper.Core.Tests/RegistryValuesTests.cs`

```csharp
using UpdateHelper.Core.Scanning;

namespace UpdateHelper.Core.Tests;

public class RegistryValuesTests
{
    [Theory]
    [InlineData("1.2.3", "1.2.3")]
    [InlineData("  ", null)]
    [InlineData(null, null)]
    public void AsString_trims_and_treats_blank_as_missing(string? input, string? expected)
        => Assert.Equal(expected, RegistryValues.AsString(input));

    [Fact]
    public void AsString_converts_dword_to_text()   // 版本号被存成 DWORD 的情况（Review Focus 1）
        => Assert.Equal("10", RegistryValues.AsString(10));

    [Fact]
    public void AsString_ignores_binary()
        => Assert.Null(RegistryValues.AsString(new byte[] { 1, 2 }));

    [Theory]
    [InlineData(1234, 1234L)]
    [InlineData("5678", 5678L)]   // 大小被存成字符串的情况（Review Focus 1）
    [InlineData("abc", null)]
    [InlineData(null, null)]
    public void AsLong_accepts_numbers_and_numeric_strings(object? input, long? expected)
        => Assert.Equal(expected, RegistryValues.AsLong(input));

    [Theory]
    [InlineData(1, true)]
    [InlineData(0, false)]
    [InlineData("1", true)]
    [InlineData(null, false)]
    public void AsFlag_reads_dword_booleans(object? input, bool expected)
        => Assert.Equal(expected, RegistryValues.AsFlag(input));

    private static Func<string, object?> Values(Dictionary<string, object?> d)
        => name => d.TryGetValue(name, out var v) ? v : null;

    [Fact]
    public void ToEntry_maps_all_fields()
    {
        var e = RegistryValues.ToEntry("QQ", UninstallHive.LocalMachine32, Values(new()
        {
            ["DisplayName"] = "QQ",
            ["DisplayVersion"] = "9.9.20.36330",
            ["Publisher"] = "腾讯科技(深圳)有限公司",
            ["InstallLocation"] = @"C:\Program Files\Tencent\QQNT\",
            ["UninstallString"] = "\"C:\\Program Files\\Tencent\\QQNT\\Uninstall.exe\"",
            ["SystemComponent"] = 0,
            ["EstimatedSize"] = 573440,
        }));

        Assert.NotNull(e);
        Assert.Equal("QQ", e!.DisplayName);
        Assert.Equal("9.9.20.36330", e.DisplayVersion);
        Assert.Equal(@"C:\Program Files\Tencent\QQNT\", e.InstallLocation);
        Assert.False(e.IsSystemComponent);
        Assert.False(e.IsUpdateOrPatch);
        Assert.Equal(573440L, e.EstimatedSizeKb);
    }

    [Fact]
    public void ToEntry_returns_null_without_display_name()
        => Assert.Null(RegistryValues.ToEntry("x", UninstallHive.CurrentUser, Values(new())));

    [Fact]
    public void ToEntry_marks_hidden_and_patch_entries()
    {
        var e = RegistryValues.ToEntry("KB1", UninstallHive.LocalMachine64, Values(new()
        {
            ["DisplayName"] = "Python 3.10.2 Core Interpreter (64-bit)",
            ["SystemComponent"] = 1,
            ["ParentKeyName"] = "Python310",
        }));
        Assert.True(e!.IsSystemComponent);
        Assert.True(e.IsUpdateOrPatch);
    }

    [Fact]
    public void ToEntry_returns_null_when_key_is_unreadable()   // Review Focus 2
    {
        var e = RegistryValues.ToEntry("locked", UninstallHive.LocalMachine64,
            _ => throw new UnauthorizedAccessException());
        Assert.Null(e);
    }
}
```

- [ ] **Step 2: 运行，确认失败**

Run: `dotnet test --filter RegistryValuesTests`
Expected: 编译失败，找不到 `UpdateHelper.Core.Scanning` 里的类型

- [ ] **Step 3: 写数据模型** `src/UpdateHelper.Core/Scanning/UninstallEntry.cs`

```csharp
namespace UpdateHelper.Core.Scanning;

/// <summary>卸载信息所在的注册表位置。</summary>
public enum UninstallHive
{
    /// <summary>HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall</summary>
    LocalMachine64,
    /// <summary>HKLM\SOFTWARE\WOW6432Node\...\Uninstall（32 位软件）</summary>
    LocalMachine32,
    /// <summary>HKCU\...\Uninstall（只给当前用户装的软件）</summary>
    CurrentUser,
}

/// <summary>注册表里的一条卸载登记。</summary>
public sealed record UninstallEntry(
    string KeyName,
    UninstallHive Hive,
    string DisplayName,
    string? DisplayVersion,
    string? Publisher,
    string? InstallLocation,
    string? UninstallString,
    string? QuietUninstallString,
    bool IsSystemComponent,
    string? ParentKeyName,
    string? ReleaseType,
    long? EstimatedSizeKb)
{
    /// <summary>是不是某个软件的补丁或更新包（不是独立软件）。</summary>
    public bool IsUpdateOrPatch => ParentKeyName is not null || ReleaseType is not null;
}
```

- [ ] **Step 4: 写安全读值** `src/UpdateHelper.Core/Scanning/RegistryValues.cs`

```csharp
using System.Globalization;
using System.Security;

namespace UpdateHelper.Core.Scanning;

/// <summary>把注册表里类型不可靠的值安全地转换成需要的类型；转换不了就当作缺失。</summary>
public static class RegistryValues
{
    public static string? AsString(object? value) => value switch
    {
        string s => string.IsNullOrWhiteSpace(s) ? null : s.Trim(),
        int or long or uint or ulong => Convert.ToString(value, CultureInfo.InvariantCulture),
        _ => null,
    };

    public static long? AsLong(object? value) => value switch
    {
        int i => i,
        long l => l,
        uint u => u,
        string s when long.TryParse(s.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) => n,
        _ => null,
    };

    public static bool AsFlag(object? value) => AsLong(value) is { } n && n != 0;

    /// <summary>
    /// 用 getValue（按值名取原始值）组装一条卸载登记。
    /// 没有 DisplayName 的键不算软件，返回 null；键不可读（权限不足等）也返回 null。
    /// </summary>
    public static UninstallEntry? ToEntry(string keyName, UninstallHive hive, Func<string, object?> getValue)
    {
        try
        {
            var name = AsString(getValue("DisplayName"));
            if (name is null) return null;

            return new UninstallEntry(
                KeyName: keyName,
                Hive: hive,
                DisplayName: name,
                DisplayVersion: AsString(getValue("DisplayVersion")),
                Publisher: AsString(getValue("Publisher")),
                InstallLocation: AsString(getValue("InstallLocation")),
                UninstallString: AsString(getValue("UninstallString")),
                QuietUninstallString: AsString(getValue("QuietUninstallString")),
                IsSystemComponent: AsFlag(getValue("SystemComponent")),
                ParentKeyName: AsString(getValue("ParentKeyName")),
                ReleaseType: AsString(getValue("ReleaseType")),
                EstimatedSizeKb: AsLong(getValue("EstimatedSize")));
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or SecurityException or IOException)
        {
            return null;
        }
    }
}
```

- [ ] **Step 5: 运行，确认通过**

Run: `dotnet test --filter RegistryValuesTests`
Expected: 全部 PASS

- [ ] **Step 6: 写来源接口和真实实现**

`src/UpdateHelper.Core/Scanning/IUninstallSource.cs`：

```csharp
namespace UpdateHelper.Core.Scanning;

/// <summary>提供卸载登记的来源。真实实现读注册表，测试用假实现。</summary>
public interface IUninstallSource
{
    IReadOnlyList<UninstallEntry> Read();
}
```

`src/UpdateHelper.Core/Scanning/RegistryUninstallSource.cs`：

```csharp
using System.Security;
using Microsoft.Win32;

namespace UpdateHelper.Core.Scanning;

/// <summary>从 3 处注册表读取卸载登记（包括隐藏条目）。只读。</summary>
public sealed class RegistryUninstallSource : IUninstallSource
{
    private const string UninstallPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

    public IReadOnlyList<UninstallEntry> Read()
    {
        var result = new List<UninstallEntry>();
        ReadHive(RegistryHive.LocalMachine, RegistryView.Registry64, UninstallHive.LocalMachine64, result);
        ReadHive(RegistryHive.LocalMachine, RegistryView.Registry32, UninstallHive.LocalMachine32, result);
        ReadHive(RegistryHive.CurrentUser, RegistryView.Default, UninstallHive.CurrentUser, result);
        return result;
    }

    private static void ReadHive(RegistryHive hive, RegistryView view, UninstallHive tag, List<UninstallEntry> into)
    {
        try
        {
            using var root = RegistryKey.OpenBaseKey(hive, view);
            using var uninstall = root.OpenSubKey(UninstallPath);
            if (uninstall is null) return;

            foreach (var name in uninstall.GetSubKeyNames())
            {
                try
                {
                    using var key = uninstall.OpenSubKey(name);
                    if (key is null) continue;
                    var entry = RegistryValues.ToEntry(name, tag, v => key.GetValue(v, null, RegistryValueOptions.None));
                    if (entry is not null) into.Add(entry);
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException or SecurityException or IOException)
                {
                    // 单个键读不了就跳过（Review Focus 2）
                }
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or SecurityException or IOException)
        {
            // 整个位置读不了也不影响其他位置
        }
    }
}
```

注：`RegistryValueOptions.None` 会自动展开 `REG_EXPAND_SZ` 里的 `%ProgramFiles%` 等变量。

- [ ] **Step 7: 编译并跑全部测试**

Run: `dotnet build && dotnet test`
Expected: 0 警告 0 错误；测试全部 PASS

- [ ] **Step 8: Commit**

```bash
git add -A
git commit -m "feat(core): 读取注册表卸载登记（含隐藏条目），容错处理异常值和无权限的键"
```

### Task 3：命令行路径解析

后台项目（服务、自启、计划任务）只给出一整行命令，要靠它判断"属于哪个软件"，就得先从命令里取出程序路径。这是纯逻辑，不碰系统。

**Files:**
- Create: `src/UpdateHelper.Core/Scanning/CommandLineParser.cs`
- Test: `tests/UpdateHelper.Core.Tests/CommandLineParserTests.cs`

**Interfaces:**
- Produces: `static class CommandLineParser { static string? ExtractExecutable(string? commandLine); }`
  - 返回展开环境变量后的程序（或 dll）完整路径；解析不了返回 null；**永不抛异常**

- [ ] **Step 1: 写失败的测试** `tests/UpdateHelper.Core.Tests/CommandLineParserTests.cs`

```csharp
using UpdateHelper.Core.Scanning;

namespace UpdateHelper.Core.Tests;

public class CommandLineParserTests
{
    [Theory]
    // 带引号
    [InlineData("\"C:\\Program Files\\Tencent\\QQNT\\QQ.exe\" /background", @"C:\Program Files\Tencent\QQNT\QQ.exe")]
    // 不带引号、路径含空格（Review Focus 3）
    [InlineData(@"C:\Program Files\Docker\Docker\Docker Desktop.exe", @"C:\Program Files\Docker\Docker\Docker Desktop.exe")]
    [InlineData(@"C:\Program Files (x86)\pcsuite\pcsuite.exe --openAsHidden", @"C:\Program Files (x86)\pcsuite\pcsuite.exe")]
    // 不带引号、无空格
    [InlineData(@"C:\Windows\system32\SecurityHealthSystray.exe", @"C:\Windows\system32\SecurityHealthSystray.exe")]
    // 服务常见的 svchost
    [InlineData(@"C:\Windows\system32\svchost.exe -k netsvcs -p", @"C:\Windows\system32\svchost.exe")]
    // rundll32：真正的主人是后面的 dll
    [InlineData(@"rundll32.exe C:\Tools\helper.dll,Start", @"C:\Tools\helper.dll")]
    [InlineData("C:\\Windows\\System32\\rundll32.exe \"C:\\My Tools\\h.dll\",Run", @"C:\My Tools\h.dll")]
    public void Extracts_program_path(string command, string expected)
        => Assert.Equal(expected, CommandLineParser.ExtractExecutable(command), ignoreCase: true);

    [Fact]
    public void Expands_environment_variables()
    {
        var pf = Environment.GetEnvironmentVariable("ProgramFiles")!;
        Assert.Equal(Path.Combine(pf, @"App\a.exe"),
            CommandLineParser.ExtractExecutable(@"%ProgramFiles%\App\a.exe -x"), ignoreCase: true);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\"")]          // 只有半个引号
    [InlineData("\"\"")]        // 空引号
    public void Returns_null_for_garbage(string? command)
        => Assert.Null(CommandLineParser.ExtractExecutable(command));

    [Fact]
    public void Falls_back_to_first_token_without_exe_suffix()
        => Assert.Equal(@"C:\tools\run.cmd", CommandLineParser.ExtractExecutable(@"C:\tools\run.cmd arg1"));
}
```

- [ ] **Step 2: 运行，确认失败**

Run: `dotnet test --filter CommandLineParserTests`
Expected: 编译失败，找不到 `CommandLineParser`

- [ ] **Step 3: 实现** `src/UpdateHelper.Core/Scanning/CommandLineParser.cs`

```csharp
namespace UpdateHelper.Core.Scanning;

/// <summary>从一整行命令里取出程序（或 rundll32 加载的 dll）的路径。纯逻辑，永不抛异常。</summary>
public static class CommandLineParser
{
    public static string? ExtractExecutable(string? commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine)) return null;
        var text = Environment.ExpandEnvironmentVariables(commandLine).Trim();

        var (program, rest) = SplitFirst(text);
        if (program is null) return null;

        // rundll32 x.dll,入口 —— 返回 dll 路径
        if (Path.GetFileName(program).Equals("rundll32.exe", StringComparison.OrdinalIgnoreCase)
            || program.Equals("rundll32", StringComparison.OrdinalIgnoreCase))
        {
            var (dllPart, _) = SplitFirst(rest);
            if (dllPart is null) return null;
            var comma = dllPart.IndexOf(',');
            var dll = (comma >= 0 ? dllPart[..comma] : dllPart).Trim().Trim('"');
            return dll.Length == 0 ? null : dll;
        }

        return program;
    }

    /// <summary>取出第一个"程序"部分和剩余参数。</summary>
    private static (string? First, string Rest) SplitFirst(string text)
    {
        text = text.TrimStart();
        if (text.Length == 0) return (null, "");

        if (text[0] == '"')
        {
            var end = text.IndexOf('"', 1);
            if (end < 0) return (null, "");
            var inner = text[1..end].Trim();
            return (inner.Length == 0 ? null : inner, text[(end + 1)..]);
        }

        // rundll32 的 dll 参数可能是 "x.dll",入口 这种半带引号的形式，已在上面按引号处理
        // 不带引号：逐个累加空格分隔的片段，第一个以 .exe 结尾的就是程序（路径可以含空格）
        var parts = text.Split(' ');
        for (var i = 0; i < parts.Length; i++)
        {
            var candidate = string.Join(' ', parts[..(i + 1)]);
            if (candidate.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                return (candidate, string.Join(' ', parts[(i + 1)..]));
        }

        // 都不以 .exe 结尾（比如 .cmd、或 "x.dll,入口"）：取第一个片段
        return (parts[0], string.Join(' ', parts[1..]));
    }
}
```

- [ ] **Step 4: 运行，确认通过**

Run: `dotnet test --filter CommandLineParserTests`
Expected: 全部 PASS

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat(core): 从命令行中解析程序路径（处理引号、空格、环境变量、rundll32）"
```

### Task 4：后台项目来源（服务、Run 键、启动文件夹、计划任务）

每种来源都拆成两半：**"把原始数据变成 BackgroundItem"的纯函数**（单元测试覆盖）和**"去系统里取原始数据"的薄壳**（只在 Task 7 的本机运行中验证）。

**Files:**
- Create: `src/UpdateHelper.Core/Scanning/BackgroundItem.cs`
- Create: `src/UpdateHelper.Core/Scanning/IBackgroundSource.cs`
- Create: `src/UpdateHelper.Core/Scanning/ServiceSource.cs`
- Create: `src/UpdateHelper.Core/Scanning/RunKeySource.cs`
- Create: `src/UpdateHelper.Core/Scanning/StartupFolderSource.cs`
- Create: `src/UpdateHelper.Core/Scanning/ScheduledTaskSource.cs`
- Test: `tests/UpdateHelper.Core.Tests/BackgroundParsingTests.cs`

**Interfaces:**
- Consumes: `RegistryValues.AsString/AsLong`（Task 2）、`CommandLineParser.ExtractExecutable`（Task 3）
- Produces:
  - `enum BackgroundKind { Service, RunKey, StartupFolder, ScheduledTask }`
  - `sealed record BackgroundItem(BackgroundKind Kind, string Name, string? DisplayName, string? Command, string? ExecutablePath, string? Detail)`
  - `interface IBackgroundSource { IReadOnlyList<BackgroundItem> Read(); }`
  - `ServiceSource.ToItem(string name, Func<string, object?> getValue) : BackgroundItem?`
  - `RunKeySource.ToItem(string name, object? value, string location) : BackgroundItem?`
  - `ScheduledTaskSource.ParseTaskXml(string taskPath, string xml) : BackgroundItem?`
  - 四个类都实现 `IBackgroundSource`

- [ ] **Step 1: 写失败的测试** `tests/UpdateHelper.Core.Tests/BackgroundParsingTests.cs`

```csharp
using UpdateHelper.Core.Scanning;

namespace UpdateHelper.Core.Tests;

public class BackgroundParsingTests
{
    private static Func<string, object?> Values(Dictionary<string, object?> d)
        => name => d.TryGetValue(name, out var v) ? v : null;

    [Fact]
    public void Service_win32_service_is_parsed_with_start_mode()
    {
        var item = ServiceSource.ToItem("ToDesk_Service", Values(new()
        {
            ["Type"] = 16,
            ["Start"] = 2,
            ["DisplayName"] = "ToDesk Service",
            ["ImagePath"] = "\"C:\\Program Files\\ToDesk\\ToDesk_Service.exe\"",
        }));

        Assert.NotNull(item);
        Assert.Equal(BackgroundKind.Service, item!.Kind);
        Assert.Equal("ToDesk Service", item.DisplayName);
        Assert.Equal(@"C:\Program Files\ToDesk\ToDesk_Service.exe", item.ExecutablePath);
        Assert.Equal("自动启动", item.Detail);
    }

    [Theory]
    [InlineData(1)]   // 内核驱动
    [InlineData(2)]   // 文件系统驱动
    public void Service_drivers_are_skipped(int type)
        => Assert.Null(ServiceSource.ToItem("nvlddmkm", Values(new() { ["Type"] = type, ["ImagePath"] = "x.sys" })));

    [Fact]
    public void Service_without_image_path_is_skipped()
        => Assert.Null(ServiceSource.ToItem("ghost", Values(new() { ["Type"] = 16 })));

    [Theory]
    [InlineData(3, "手动启动")]
    [InlineData(4, "已禁用")]
    [InlineData(99, null)]
    public void Service_start_modes(int start, string? expected)
        => Assert.Equal(expected, ServiceSource.ToItem("s", Values(new()
            { ["Type"] = 32, ["Start"] = start, ["ImagePath"] = @"C:\a.exe" }))!.Detail);

    [Fact]
    public void RunKey_value_is_parsed()
    {
        var item = RunKeySource.ToItem("QQNT", "\"C:\\Program Files\\Tencent\\QQNT\\QQ.exe\" /background", @"HKCU\...\Run");
        Assert.Equal(BackgroundKind.RunKey, item!.Kind);
        Assert.Equal(@"C:\Program Files\Tencent\QQNT\QQ.exe", item.ExecutablePath);
        Assert.Equal(@"HKCU\...\Run", item.Detail);
    }

    [Fact]
    public void RunKey_non_string_value_is_skipped()   // Review Focus 1
        => Assert.Null(RunKeySource.ToItem("weird", new byte[] { 1 }, "HKLM"));

    private const string TaskXml = """
        <?xml version="1.0" encoding="UTF-16"?>
        <Task xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
          <Actions Context="Author">
            <Exec>
              <Command>"C:\Users\me\AppData\Local\Kingsoft\WPS Office\ksolaunch.exe"</Command>
              <Arguments>/update</Arguments>
            </Exec>
          </Actions>
        </Task>
        """;

    [Fact]
    public void Task_exec_action_is_parsed()
    {
        var item = ScheduledTaskSource.ParseTaskXml(@"\WpsUpdateTask_chen_pi", TaskXml);
        Assert.Equal(BackgroundKind.ScheduledTask, item!.Kind);
        Assert.Equal("WpsUpdateTask_chen_pi", item.Name);
        Assert.Equal(@"C:\Users\me\AppData\Local\Kingsoft\WPS Office\ksolaunch.exe", item.ExecutablePath);
        Assert.Contains("/update", item.Command);
    }

    [Fact]
    public void Task_without_exec_action_is_skipped()   // Review Focus 5（比如只有"发送邮件"或 COM 动作）
    {
        const string xml = """
            <Task xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <Actions><ComHandler><ClassId>{0000}</ClassId></ComHandler></Actions>
            </Task>
            """;
        Assert.Null(ScheduledTaskSource.ParseTaskXml(@"\x", xml));
    }

    [Fact]
    public void Task_with_broken_xml_is_skipped()   // Review Focus 5
        => Assert.Null(ScheduledTaskSource.ParseTaskXml(@"\x", "<Task><Actions>"));
}
```

- [ ] **Step 2: 运行，确认失败**

Run: `dotnet test --filter BackgroundParsingTests`
Expected: 编译失败，找不到 `ServiceSource` 等类型

- [ ] **Step 3: 数据模型和接口**

`src/UpdateHelper.Core/Scanning/BackgroundItem.cs`：

```csharp
namespace UpdateHelper.Core.Scanning;

public enum BackgroundKind { Service, RunKey, StartupFolder, ScheduledTask }

/// <summary>一个后台项目：服务、开机自启或计划任务。</summary>
/// <param name="Detail">补充说明：服务的启动方式、自启项所在位置、任务路径等。</param>
public sealed record BackgroundItem(
    BackgroundKind Kind,
    string Name,
    string? DisplayName,
    string? Command,
    string? ExecutablePath,
    string? Detail);
```

`src/UpdateHelper.Core/Scanning/IBackgroundSource.cs`：

```csharp
namespace UpdateHelper.Core.Scanning;

public interface IBackgroundSource
{
    IReadOnlyList<BackgroundItem> Read();
}
```

- [ ] **Step 4: 服务** `src/UpdateHelper.Core/Scanning/ServiceSource.cs`

```csharp
using System.Security;
using Microsoft.Win32;

namespace UpdateHelper.Core.Scanning;

/// <summary>从 HKLM\SYSTEM\CurrentControlSet\Services 读取普通服务（跳过驱动）。只读。</summary>
public sealed class ServiceSource : IBackgroundSource
{
    private const int Win32ServiceMask = 0x10 | 0x20; // 独占进程 / 共享进程

    public IReadOnlyList<BackgroundItem> Read()
    {
        var result = new List<BackgroundItem>();
        try
        {
            using var services = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services");
            if (services is null) return result;
            foreach (var name in services.GetSubKeyNames())
            {
                try
                {
                    using var key = services.OpenSubKey(name);
                    if (key is null) continue;
                    var item = ToItem(name, v => key.GetValue(v));
                    if (item is not null) result.Add(item);
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException or SecurityException or IOException) { }
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or SecurityException or IOException) { }
        return result;
    }

    public static BackgroundItem? ToItem(string name, Func<string, object?> getValue)
    {
        var type = RegistryValues.AsLong(getValue("Type")) ?? 0;
        if ((type & Win32ServiceMask) == 0) return null;   // 驱动等不是普通服务

        var imagePath = RegistryValues.AsString(getValue("ImagePath"));
        if (imagePath is null) return null;

        var detail = RegistryValues.AsLong(getValue("Start")) switch
        {
            2 => "自动启动",
            3 => "手动启动",
            4 => "已禁用",
            _ => null,
        };

        return new BackgroundItem(BackgroundKind.Service, name,
            RegistryValues.AsString(getValue("DisplayName")),
            imagePath, CommandLineParser.ExtractExecutable(imagePath), detail);
    }
}
```

注：服务的 `DisplayName` 可能是 `@%SystemRoot%\system32\xxx.dll,-100` 这种资源引用，第一期原样保留，显示时再处理。

- [ ] **Step 5: Run 键** `src/UpdateHelper.Core/Scanning/RunKeySource.cs`

```csharp
using System.Security;
using Microsoft.Win32;

namespace UpdateHelper.Core.Scanning;

/// <summary>读取 3 处 Run 键里的开机自启项。只读。</summary>
public sealed class RunKeySource : IBackgroundSource
{
    private static readonly (RegistryHive Hive, RegistryView View, string Label)[] Locations =
    [
        (RegistryHive.LocalMachine, RegistryView.Registry64, @"HKLM\...\Run"),
        (RegistryHive.LocalMachine, RegistryView.Registry32, @"HKLM\...\Run (32 位)"),
        (RegistryHive.CurrentUser, RegistryView.Default, @"HKCU\...\Run"),
    ];

    public IReadOnlyList<BackgroundItem> Read()
    {
        var result = new List<BackgroundItem>();
        foreach (var (hive, view, label) in Locations)
        {
            try
            {
                using var root = RegistryKey.OpenBaseKey(hive, view);
                using var run = root.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run");
                if (run is null) continue;
                foreach (var name in run.GetValueNames())
                {
                    var item = ToItem(name, run.GetValue(name), label);
                    if (item is not null) result.Add(item);
                }
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or SecurityException or IOException) { }
        }
        return result;
    }

    public static BackgroundItem? ToItem(string name, object? value, string location)
    {
        if (value is not string command || string.IsNullOrWhiteSpace(command)) return null;
        return new BackgroundItem(BackgroundKind.RunKey, name, null, command,
            CommandLineParser.ExtractExecutable(command), location);
    }
}
```

- [ ] **Step 6: 启动文件夹** `src/UpdateHelper.Core/Scanning/StartupFolderSource.cs`

```csharp
namespace UpdateHelper.Core.Scanning;

/// <summary>读取"启动"文件夹（当前用户 + 所有用户）里的快捷方式和程序。只读。</summary>
public sealed class StartupFolderSource : IBackgroundSource
{
    public IReadOnlyList<BackgroundItem> Read()
    {
        var result = new List<BackgroundItem>();
        foreach (var folder in new[] { Environment.SpecialFolder.Startup, Environment.SpecialFolder.CommonStartup })
        {
            var dir = Environment.GetFolderPath(folder);
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) continue;
            foreach (var file in Directory.EnumerateFiles(dir))
            {
                if (Path.GetFileName(file).Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)) continue;
                var target = file.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) ? ResolveShortcut(file) : file;
                result.Add(new BackgroundItem(BackgroundKind.StartupFolder,
                    Path.GetFileNameWithoutExtension(file), null, target ?? file, target, dir));
            }
        }
        return result;
    }

    /// <summary>用 WScript.Shell 读取快捷方式的目标；失败返回 null。</summary>
    private static string? ResolveShortcut(string lnkPath)
    {
        try
        {
            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType is null) return null;
            dynamic shell = Activator.CreateInstance(shellType)!;
            var target = (string)shell.CreateShortcut(lnkPath).TargetPath;
            return string.IsNullOrWhiteSpace(target) ? null : target;
        }
        catch (Exception)
        {
            return null;   // COM 不可用或快捷方式损坏
        }
    }
}
```

- [ ] **Step 7: 计划任务** `src/UpdateHelper.Core/Scanning/ScheduledTaskSource.cs`

```csharp
using System.Xml;
using System.Xml.Linq;

namespace UpdateHelper.Core.Scanning;

/// <summary>通过任务计划程序 COM 读取非微软的计划任务（含隐藏任务）。只读。</summary>
public sealed class ScheduledTaskSource : IBackgroundSource
{
    private const int TaskEnumHidden = 1;
    private static readonly XNamespace Ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";

    public IReadOnlyList<BackgroundItem> Read()
    {
        var result = new List<BackgroundItem>();
        try
        {
            var type = Type.GetTypeFromProgID("Schedule.Service");
            if (type is null) return result;
            dynamic service = Activator.CreateInstance(type)!;
            service.Connect();
            Walk(service.GetFolder("\\"), result);
        }
        catch (Exception)
        {
            // COM 不可用：整体跳过（Review Focus 5）
        }
        return result;
    }

    private static void Walk(dynamic folder, List<BackgroundItem> into)
    {
        string path = folder.Path;
        if (path.StartsWith(@"\Microsoft", StringComparison.OrdinalIgnoreCase)) return;

        try
        {
            foreach (dynamic task in folder.GetTasks(TaskEnumHidden))
            {
                try
                {
                    var item = ParseTaskXml((string)task.Path, (string)task.Xml);
                    if (item is not null) into.Add(item);
                }
                catch (Exception) { /* 单个任务读不了就跳过 */ }
            }
            foreach (dynamic sub in folder.GetFolders(0))
                Walk(sub, into);
        }
        catch (Exception) { /* 某个文件夹没权限就跳过 */ }
    }

    /// <summary>从任务 XML 中取第一个"启动程序"动作；没有或 XML 损坏都返回 null。</summary>
    public static BackgroundItem? ParseTaskXml(string taskPath, string xml)
    {
        try
        {
            var doc = XDocument.Parse(xml);
            var exec = doc.Descendants(Ns + "Exec").FirstOrDefault();
            var command = exec?.Element(Ns + "Command")?.Value.Trim();
            if (string.IsNullOrEmpty(command)) return null;

            var args = exec!.Element(Ns + "Arguments")?.Value.Trim();
            var full = string.IsNullOrEmpty(args) ? command : $"{command} {args}";
            var name = taskPath[(taskPath.LastIndexOf('\\') + 1)..];

            return new BackgroundItem(BackgroundKind.ScheduledTask, name, null, full,
                CommandLineParser.ExtractExecutable(command), taskPath);
        }
        catch (XmlException)
        {
            return null;
        }
    }
}
```

注：XML 里的 `<Command>` 有时自带引号（如测试用例），`CommandLineParser` 会去掉。

- [ ] **Step 8: 运行，确认通过**

Run: `dotnet test --filter BackgroundParsingTests`
Expected: 全部 PASS

- [ ] **Step 9: 全量编译和测试**

Run: `dotnet build && dotnet test`
Expected: 0 警告 0 错误；全部 PASS

- [ ] **Step 10: Commit**

```bash
git add -A
git commit -m "feat(core): 读取服务、开机自启、启动文件夹和计划任务"
```

### Task 5：分类器

给每个软件打上分类，界面默认只展示"普通软件"和"开发工具"。这里先用内置的关键词规则；计划 2 接入 YAML 规则后，规则里的分类会优先于这里的判断。

**Files:**
- Create: `src/UpdateHelper.Core/Grouping/SoftwareCategory.cs`
- Create: `src/UpdateHelper.Core/Grouping/SoftwareClassifier.cs`
- Create: `tests/UpdateHelper.Core.Tests/Fakes.cs`
- Test: `tests/UpdateHelper.Core.Tests/SoftwareClassifierTests.cs`

**Interfaces:**
- Consumes: `UninstallEntry`（Task 2）
- Produces:
  - `enum SoftwareCategory { Application, DevTool, Game, Runtime, Driver, SystemComponent }`
  - `static class SoftwareClassifier { static SoftwareCategory Classify(UninstallEntry entry); }`（不会返回 `SystemComponent`，那个由归组器给"组件合集"用）
  - 测试辅助 `static class Fakes { static UninstallEntry Entry(string name, string? publisher = null, string? version = null, string? location = null, bool hidden = false, string? key = null, UninstallHive hive = UninstallHive.LocalMachine64, string? parentKey = null); }`

- [ ] **Step 1: 写测试辅助** `tests/UpdateHelper.Core.Tests/Fakes.cs`

```csharp
using UpdateHelper.Core.Scanning;

namespace UpdateHelper.Core.Tests;

/// <summary>快速构造测试数据。</summary>
public static class Fakes
{
    public static UninstallEntry Entry(
        string name,
        string? publisher = null,
        string? version = null,
        string? location = null,
        bool hidden = false,
        string? key = null,
        UninstallHive hive = UninstallHive.LocalMachine64,
        string? parentKey = null)
        => new(key ?? name, hive, name, version, publisher, location,
               UninstallString: null, QuietUninstallString: null,
               IsSystemComponent: hidden, ParentKeyName: parentKey, ReleaseType: null, EstimatedSizeKb: null);
}
```

- [ ] **Step 2: 写失败的测试** `tests/UpdateHelper.Core.Tests/SoftwareClassifierTests.cs`（名字都取自开发者本机的真实扫描结果）

```csharp
using UpdateHelper.Core.Grouping;
using static UpdateHelper.Core.Tests.Fakes;

namespace UpdateHelper.Core.Tests;

public class SoftwareClassifierTests
{
    [Theory]
    [InlineData("Microsoft Visual C++ 2013 Redistributable (x64) - 12.0.40664")]
    [InlineData("Microsoft Visual C++ v14 Redistributable (x86) - 14.50.35719")]
    [InlineData("Microsoft .NET Runtime - 8.0.21 (x64)")]
    [InlineData("Microsoft Windows Desktop Runtime - 8.0.14 (x64)")]
    [InlineData("Java 8 Update 331 (64-bit)")]
    [InlineData("NVIDIA PhysX 系统软件 9.23.1019")]
    public void Runtimes(string name) => Assert.Equal(SoftwareCategory.Runtime, SoftwareClassifier.Classify(Entry(name)));

    [Theory]
    [InlineData("NVIDIA 图形驱动程序 610.62")]
    [InlineData("Realtek Audio Driver")]
    [InlineData("Intel(R) Chipset Device Software")]
    [InlineData("Intel(R) Management Engine Components")]
    public void Drivers(string name) => Assert.Equal(SoftwareCategory.Driver, SoftwareClassifier.Classify(Entry(name)));

    [Fact]
    public void Steam_games_by_key_name()
        => Assert.Equal(SoftwareCategory.Game,
            SoftwareClassifier.Classify(Entry("Counter-Strike 2", "Valve", key: "Steam App 730")));

    [Theory]
    [InlineData(@"D:\SteamLibrary\steamapps\common\Terraria")]
    [InlineData(@"D:\Epic Games\HogwartsLegacy")]
    [InlineData(@"D:\WeGameApps\三角洲行动")]
    public void Games_by_install_location(string location)
        => Assert.Equal(SoftwareCategory.Game, SoftwareClassifier.Classify(Entry("某游戏", location: location)));

    [Fact]
    public void Epic_launcher_itself_is_not_a_game()
        => Assert.Equal(SoftwareCategory.Application,
            SoftwareClassifier.Classify(Entry("Epic Games Launcher", location: @"D:\Epic Games\Launcher\")));

    [Theory]
    [InlineData("Git", "The Git Development Community")]
    [InlineData("CMake", "Kitware")]
    [InlineData("Node.js", "Node.js Foundation")]
    [InlineData("Python 3.10.2 (64-bit)", "Python Software Foundation")]
    [InlineData("IntelliJ IDEA 2025.1.3", "JetBrains s.r.o.")]
    [InlineData("Docker Desktop", "Docker Inc.")]
    [InlineData("Microsoft Visual Studio Code (User)", "Microsoft Corporation")]
    [InlineData("NVIDIA CUDA Toolkit 13.0", "NVIDIA Corporation")]
    [InlineData("Oracle VM VirtualBox 7.0.12", "Oracle and/or its affiliates")]
    public void DevTools(string name, string publisher)
        => Assert.Equal(SoftwareCategory.DevTool, SoftwareClassifier.Classify(Entry(name, publisher)));

    [Theory]
    [InlineData("微信", "腾讯科技(深圳)有限公司")]
    [InlineData("WPS Office (12.1.0.23125)", "Kingsoft Corp.")]
    [InlineData("网易云音乐", "网易公司")]
    [InlineData("Digital Photo Viewer", "Some Co")]   // 名字里含 "git" 但不是 Git
    public void Everything_else_is_an_application(string name, string publisher)
        => Assert.Equal(SoftwareCategory.Application, SoftwareClassifier.Classify(Entry(name, publisher)));
}
```

- [ ] **Step 3: 运行，确认失败**

Run: `dotnet test --filter SoftwareClassifierTests`
Expected: 编译失败，找不到 `UpdateHelper.Core.Grouping`

- [ ] **Step 4: 分类枚举** `src/UpdateHelper.Core/Grouping/SoftwareCategory.cs`

```csharp
namespace UpdateHelper.Core.Grouping;

public enum SoftwareCategory
{
    /// <summary>普通软件（默认展示）</summary>
    Application,
    /// <summary>开发工具（默认展示）</summary>
    DevTool,
    /// <summary>游戏：由 Steam 等平台负责更新</summary>
    Game,
    /// <summary>运行库：多个版本共存是正常的</summary>
    Runtime,
    /// <summary>硬件驱动</summary>
    Driver,
    /// <summary>无法归属到具体软件的系统/厂商组件合集</summary>
    SystemComponent,
}
```

- [ ] **Step 5: 实现** `src/UpdateHelper.Core/Grouping/SoftwareClassifier.cs`

```csharp
using System.Text.RegularExpressions;
using UpdateHelper.Core.Scanning;

namespace UpdateHelper.Core.Grouping;

/// <summary>按名称、发布者、安装位置给软件分类。内置规则，计划 2 起可被 YAML 规则覆盖。</summary>
public static partial class SoftwareClassifier
{
    public static SoftwareCategory Classify(UninstallEntry e)
    {
        if (IsGame(e)) return SoftwareCategory.Game;
        if (RuntimePattern().IsMatch(e.DisplayName)) return SoftwareCategory.Runtime;
        if (DriverPattern().IsMatch(e.DisplayName)) return SoftwareCategory.Driver;
        if (DevToolPattern().IsMatch(e.DisplayName) || DevPublisherPattern().IsMatch(e.Publisher ?? ""))
            return SoftwareCategory.DevTool;
        return SoftwareCategory.Application;
    }

    private static bool IsGame(UninstallEntry e)
    {
        if (e.KeyName.StartsWith("Steam App ", StringComparison.OrdinalIgnoreCase)) return true;
        var loc = e.InstallLocation ?? "";
        if (loc.Contains(@"\Launcher", StringComparison.OrdinalIgnoreCase)) return false;   // 平台本身不是游戏
        return GameLocationPattern().IsMatch(loc);
    }

    [GeneratedRegex(@"\\steamapps\\common\\|\\Epic Games\\|\\WeGameApps\\|\\Ubisoft Game Launcher\\games\\", RegexOptions.IgnoreCase)]
    private static partial Regex GameLocationPattern();

    [GeneratedRegex(@"Visual C\+\+.*Redistributable|\.NET.*Runtime|Desktop Runtime|^Java \d+ Update|WebView2 Runtime|WindowsAppRuntime|PhysX|DirectX", RegexOptions.IgnoreCase)]
    private static partial Regex RuntimePattern();

    [GeneratedRegex(@"驱动|\bDriver\b|Chipset|Management Engine", RegexOptions.IgnoreCase)]
    private static partial Regex DriverPattern();

    [GeneratedRegex(@"^Git$|\bGit\b|\bCMake\b|Node\.js|\bPython\b|Visual Studio|\bDocker\b|Anaconda|MSYS2|GitHub CLI|\bArduino\b|\bUnity\b|\bCUDA\b|Nsight|Navicat|Apifox|VirtualBox|VMware|\bCursor\b", RegexOptions.IgnoreCase)]
    private static partial Regex DevToolPattern();

    [GeneratedRegex(@"JetBrains", RegexOptions.IgnoreCase)]
    private static partial Regex DevPublisherPattern();
}
```

- [ ] **Step 6: 运行，确认通过**

Run: `dotnet test --filter SoftwareClassifierTests`
Expected: 全部 PASS

- [ ] **Step 7: Commit**

```bash
git add -A
git commit -m "feat(core): 软件分类（普通/开发工具/游戏/运行库/驱动）"
```

### Task 6：归组器

把 297 条登记整理成"一个软件一行"：隐藏组件和补丁挂到所属软件下面，找不到主人的隐藏组件按发布者合成"组件合集"，后台项目按安装位置挂到软件下面。纯逻辑，全部单元测试。

**Files:**
- Create: `src/UpdateHelper.Core/Grouping/SoftwareGroup.cs`
- Create: `src/UpdateHelper.Core/Grouping/PathUtil.cs`
- Create: `src/UpdateHelper.Core/Grouping/SoftwareGrouper.cs`
- Test: `tests/UpdateHelper.Core.Tests/SoftwareGrouperTests.cs`

**Interfaces:**
- Consumes: `UninstallEntry`、`BackgroundItem`（Task 2、4）；`SoftwareClassifier.Classify`、`SoftwareCategory`（Task 5）；`Fakes.Entry`（Task 5）
- Produces:
  - `sealed class SoftwareGroup`：`string Name`、`string? Publisher`、`string? Version`、`SoftwareCategory Category`、`UninstallEntry? Primary`（组件合集为 null）、`List<UninstallEntry> Components`、`List<BackgroundItem> Background`、`bool IsIncomplete`、`IEnumerable<string> InstallLocations`
  - `sealed record ScanResult(IReadOnlyList<SoftwareGroup> Groups, IReadOnlyList<BackgroundItem> UnassignedBackground, int TotalEntries)`
  - `static class PathUtil`：`string? NormalizeDir(string? path)`、`bool IsUnder(string? path, string? dir)`
  - `static class SoftwareGrouper { static ScanResult Group(IReadOnlyList<UninstallEntry> entries, IReadOnlyList<BackgroundItem> background); static bool IsPlaceholderName(string name); }`

**归组规则（按顺序）：**
1. 去重：显示名 + 版本 + 发布者都相同（忽略大小写）的条目只保留第一条
2. 主条目：不隐藏、也不是补丁的条目，每条成为一个组
3. 其余条目（隐藏组件、补丁）找主人，先命中先算：
   a. `ParentKeyName` 等于某主条目的 `KeyName`
   b. 安装位置在某主条目的安装位置之内（取最深的那个）
   c. 发布者相同，且名称开头有共同的词（发布者名字里出现过的词不算，比如 "NVIDIA"、"Python"）；共同词最多的胜出
   d. 都不中：放进"<发布者> 组件"合集（分类为 `SystemComponent`）
4. 名称里有 `{{` 或 `${` 的条目标记为"信息不完整"
5. 后台项目：程序路径在哪个组的安装位置之内就挂到哪个组（最深的胜出）；找不到或没有路径的放进"未归属"

- [ ] **Step 1: 写失败的测试** `tests/UpdateHelper.Core.Tests/SoftwareGrouperTests.cs`

```csharp
using UpdateHelper.Core.Grouping;
using UpdateHelper.Core.Scanning;
using static UpdateHelper.Core.Tests.Fakes;

namespace UpdateHelper.Core.Tests;

public class SoftwareGrouperTests
{
    private static ScanResult Group(params UninstallEntry[] entries) => SoftwareGrouper.Group(entries, []);

    private static SoftwareGroup Find(ScanResult r, string name) => r.Groups.Single(g => g.Name == name);

    [Fact]
    public void Duplicates_across_hives_become_one_group()   // Review Focus 4
    {
        var r = Group(
            Entry("7-Zip", "Igor Pavlov", "24.08", hive: UninstallHive.LocalMachine64),
            Entry("7-Zip", "Igor Pavlov", "24.08", hive: UninstallHive.LocalMachine32, key: "7zip-32"));
        Assert.Single(r.Groups);
        Assert.Equal(2, r.TotalEntries);
    }

    [Fact]
    public void Same_name_different_versions_stay_separate()   // 运行库多版本共存
    {
        var r = Group(
            Entry("Microsoft Windows Desktop Runtime - 8.0.14 (x64)", "Microsoft Corporation", "8.0.14"),
            Entry("Microsoft Windows Desktop Runtime - 8.0.17 (x64)", "Microsoft Corporation", "8.0.17"));
        Assert.Equal(2, r.Groups.Count);
    }

    [Fact]
    public void Hidden_entry_attaches_by_parent_key()
    {
        var r = Group(
            Entry("Office", "Microsoft Corporation", key: "Office16"),
            Entry("Office 校对工具", "Microsoft Corporation", hidden: true, parentKey: "Office16"));
        var office = Find(r, "Office");
        Assert.Single(office.Components);
        Assert.Single(r.Groups);
    }

    [Fact]
    public void Hidden_entry_attaches_by_install_location()
    {
        var r = Group(
            Entry("Foo", "Foo Inc", location: @"C:\Apps\Foo\"),
            Entry("Bar Helper", "Other", hidden: true, location: @"C:\Apps\Foo\helper"));
        Assert.Single(Find(r, "Foo").Components);
    }

    [Fact]
    public void Install_location_prefix_respects_folder_boundary()
    {
        var r = Group(
            Entry("A", "X", location: @"C:\Apps\A"),
            Entry("AB part", "Y", hidden: true, location: @"C:\Apps\AB"));
        Assert.Empty(Find(r, "A").Components);   // C:\Apps\AB 不在 C:\Apps\A 里面
    }

    [Fact]
    public void Python_components_go_to_matching_python_not_launcher()
    {
        const string psf = "Python Software Foundation";
        var r = Group(
            Entry("Python 3.10.2 (64-bit)", psf, "3.10.2150.0"),
            Entry("Python Launcher", psf, "3.10.7686.0"),
            Entry("Python 3.10.2 Core Interpreter (64-bit)", psf, hidden: true),
            Entry("Python 3.10.2 pip Bootstrap (64-bit)", psf, hidden: true));
        Assert.Equal(2, Find(r, "Python 3.10.2 (64-bit)").Components.Count);
        Assert.Empty(Find(r, "Python Launcher").Components);
    }

    [Fact]
    public void Cuda_components_go_to_cuda_toolkit_not_nvidia_app()
    {
        const string nv = "NVIDIA Corporation";
        var r = Group(
            Entry("NVIDIA CUDA Toolkit 13.0", nv, "13.0"),
            Entry("NVIDIA App 11.0.7.247", nv, "11.0.7.247"),
            Entry("NVIDIA CUDA Runtime 13.0", nv, hidden: true),
            Entry("CUBLAS Runtime", nv, hidden: true));

        Assert.Single(Find(r, "NVIDIA CUDA Toolkit 13.0").Components);
        Assert.Empty(Find(r, "NVIDIA App 11.0.7.247").Components);

        var leftovers = Find(r, "NVIDIA Corporation 组件");     // CUBLAS 找不到主人
        Assert.Equal(SoftwareCategory.SystemComponent, leftovers.Category);
        Assert.Null(leftovers.Primary);
        Assert.Single(leftovers.Components);
    }

    [Fact]
    public void Orphan_without_publisher_goes_to_unknown_collection()
    {
        var r = Group(Entry("mystery", publisher: null, hidden: true));
        Assert.Equal("未知发布者的组件", Assert.Single(r.Groups).Name);
    }

    [Fact]
    public void Patch_is_not_a_primary()
    {
        var r = Group(
            Entry("Foo", "Foo Inc", key: "Foo"),
            Entry("Foo 安全更新 KB123", "Foo Inc", parentKey: "Foo"));   // 不隐藏但带 ParentKeyName
        Assert.Single(r.Groups);
        Assert.Single(Find(r, "Foo").Components);
    }

    [Theory]
    [InlineData("${{arpDisplayName}}", true)]
    [InlineData("{{name}}", true)]
    [InlineData("QQ", false)]
    public void Placeholder_names_are_detected(string name, bool expected)
        => Assert.Equal(expected, SoftwareGrouper.IsPlaceholderName(name));

    [Fact]
    public void Visible_placeholder_group_is_marked_incomplete()
    {
        var r = Group(Entry("${{arpDisplayName}}", "NVIDIA Corporation"));
        Assert.True(Assert.Single(r.Groups).IsIncomplete);
    }

    [Fact]
    public void Background_attaches_to_deepest_install_location()
    {
        var entries = new[]
        {
            Entry("Tencent 公共组件", "Tencent", location: @"C:\Program Files\Tencent"),
            Entry("QQ", "Tencent", location: @"C:\Program Files\Tencent\QQNT\"),
        };
        var bg = new[]
        {
            new BackgroundItem(BackgroundKind.RunKey, "QQNT", null, "x", @"C:\Program Files\Tencent\QQNT\QQ.exe", null),
            new BackgroundItem(BackgroundKind.Service, "Unknown", null, "y", @"D:\Other\svc.exe", null),
            new BackgroundItem(BackgroundKind.StartupFolder, "NoPath", null, "z", null, null),
        };

        var r = SoftwareGrouper.Group(entries, bg);

        Assert.Single(Find(r, "QQ").Background);
        Assert.Empty(Find(r, "Tencent 公共组件").Background);
        Assert.Equal(2, r.UnassignedBackground.Count);
    }

    [Theory]
    [InlineData(@"C:\Apps\Foo\bar.exe", @"C:\Apps\Foo", true)]
    [InlineData(@"c:\apps\foo\bar.exe", @"C:\Apps\Foo\", true)]
    [InlineData(@"C:\Apps\FooBar\x.exe", @"C:\Apps\Foo", false)]
    [InlineData(@"C:\Apps\Foo\x.exe", null, false)]
    [InlineData(null, @"C:\Apps", false)]
    [InlineData("C:\\bad|path", @"C:\", false)]   // 非法字符不抛异常
    public void IsUnder(string? path, string? dir, bool expected)
        => Assert.Equal(expected, PathUtil.IsUnder(path, dir));
}
```

- [ ] **Step 2: 运行，确认失败**

Run: `dotnet test --filter SoftwareGrouperTests`
Expected: 编译失败，找不到 `SoftwareGroup`、`SoftwareGrouper`、`PathUtil`

- [ ] **Step 3: 数据模型** `src/UpdateHelper.Core/Grouping/SoftwareGroup.cs`

```csharp
using UpdateHelper.Core.Scanning;

namespace UpdateHelper.Core.Grouping;

/// <summary>归组后的"一个软件"：主条目 + 它的组件 + 它的后台项目。</summary>
public sealed class SoftwareGroup
{
    public required string Name { get; init; }
    public string? Publisher { get; init; }
    public string? Version { get; init; }
    public SoftwareCategory Category { get; init; }

    /// <summary>主条目；"组件合集"没有主条目。</summary>
    public UninstallEntry? Primary { get; init; }

    public List<UninstallEntry> Components { get; } = [];
    public List<BackgroundItem> Background { get; } = [];

    /// <summary>登记信息不完整（比如名称是没替换的模板占位符）。</summary>
    public bool IsIncomplete { get; init; }

    /// <summary>主条目和组件的所有安装位置。</summary>
    public IEnumerable<string> InstallLocations =>
        (Primary is null ? Components : Components.Prepend(Primary))
            .Select(e => e.InstallLocation)
            .OfType<string>();
}

/// <summary>一次扫描的整理结果。</summary>
/// <param name="TotalEntries">去重前的登记总数（用于和系统实际数量核对）。</param>
public sealed record ScanResult(
    IReadOnlyList<SoftwareGroup> Groups,
    IReadOnlyList<BackgroundItem> UnassignedBackground,
    int TotalEntries);
```

- [ ] **Step 4: 路径工具** `src/UpdateHelper.Core/Grouping/PathUtil.cs`

```csharp
namespace UpdateHelper.Core.Grouping;

public static class PathUtil
{
    /// <summary>规范化为以 \ 结尾的完整目录路径；无效路径返回 null。</summary>
    public static string? NormalizeDir(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        // GetFullPath 遇到 | 等字符不一定抛异常，先自己挡掉
        if (path.IndexOfAny(Path.GetInvalidPathChars()) >= 0) return null;
        try
        {
            var full = Path.GetFullPath(path.Trim().Trim('"'));
            return full.TrimEnd('\\') + "\\";
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    /// <summary>path 是否位于 dir 目录之内（忽略大小写，按文件夹边界判断）。</summary>
    public static bool IsUnder(string? path, string? dir)
    {
        var d = NormalizeDir(dir);
        var p = NormalizeDir(path);
        return d is not null && p is not null && p.StartsWith(d, StringComparison.OrdinalIgnoreCase);
    }
}
```

- [ ] **Step 5: 归组器** `src/UpdateHelper.Core/Grouping/SoftwareGrouper.cs`

```csharp
using UpdateHelper.Core.Scanning;

namespace UpdateHelper.Core.Grouping;

/// <summary>把卸载登记和后台项目整理成"一个软件一组"。纯逻辑。</summary>
public static class SoftwareGrouper
{
    public static bool IsPlaceholderName(string name) =>
        name.Contains("{{", StringComparison.Ordinal) || name.Contains("${", StringComparison.Ordinal);

    public static ScanResult Group(IReadOnlyList<UninstallEntry> entries, IReadOnlyList<BackgroundItem> background)
    {
        // 1. 去重
        var unique = entries
            .DistinctBy(e => (e.DisplayName.ToUpperInvariant(), e.DisplayVersion?.ToUpperInvariant(), e.Publisher?.ToUpperInvariant()))
            .ToList();

        // 2. 主条目
        var groups = new List<SoftwareGroup>();
        var byPrimary = new Dictionary<UninstallEntry, SoftwareGroup>();
        foreach (var e in unique.Where(e => !e.IsSystemComponent && !e.IsUpdateOrPatch))
        {
            var g = new SoftwareGroup
            {
                Name = e.DisplayName,
                Publisher = e.Publisher,
                Version = e.DisplayVersion,
                Category = SoftwareClassifier.Classify(e),
                Primary = e,
                IsIncomplete = IsPlaceholderName(e.DisplayName),
            };
            groups.Add(g);
            byPrimary[e] = g;
        }

        // 3. 其余条目找主人
        var collections = new Dictionary<string, SoftwareGroup>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in unique.Where(e => e.IsSystemComponent || e.IsUpdateOrPatch))
        {
            var owner = FindOwner(e, byPrimary.Keys);
            if (owner is not null)
            {
                byPrimary[owner].Components.Add(e);
                continue;
            }

            var label = e.Publisher is null ? "未知发布者的组件" : $"{e.Publisher} 组件";
            if (!collections.TryGetValue(label, out var col))
            {
                col = new SoftwareGroup { Name = label, Publisher = e.Publisher, Category = SoftwareCategory.SystemComponent };
                collections[label] = col;
                groups.Add(col);
            }
            col.Components.Add(e);
        }

        // 5. 后台项目
        var unassigned = new List<BackgroundItem>();
        foreach (var item in background)
        {
            var best = groups
                .SelectMany(g => g.InstallLocations.Select(loc => (Group: g, Dir: PathUtil.NormalizeDir(loc))))
                .Where(x => x.Dir is not null && PathUtil.IsUnder(item.ExecutablePath, x.Dir))
                .OrderByDescending(x => x.Dir!.Length)
                .FirstOrDefault();
            if (best.Group is not null) best.Group.Background.Add(item);
            else unassigned.Add(item);
        }

        var ordered = groups
            .OrderBy(g => g.Category)
            .ThenBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        return new ScanResult(ordered, unassigned, entries.Count);
    }

    private static UninstallEntry? FindOwner(UninstallEntry e, IEnumerable<UninstallEntry> primaries)
    {
        var list = primaries.ToList();

        // a. ParentKeyName
        if (e.ParentKeyName is not null)
        {
            var byKey = list.FirstOrDefault(p => p.KeyName.Equals(e.ParentKeyName, StringComparison.OrdinalIgnoreCase));
            if (byKey is not null) return byKey;
        }

        // b. 安装位置（最深的胜出）
        if (e.InstallLocation is not null)
        {
            var byLocation = list
                .Where(p => p.InstallLocation is not null && PathUtil.IsUnder(e.InstallLocation, p.InstallLocation))
                .OrderByDescending(p => PathUtil.NormalizeDir(p.InstallLocation)!.Length)
                .FirstOrDefault();
            if (byLocation is not null) return byLocation;
        }

        // c. 同发布者 + 名称开头共同词
        if (e.Publisher is null) return null;
        var publisherWords = Words(e.Publisher).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return list
            .Where(p => string.Equals(p.Publisher, e.Publisher, StringComparison.OrdinalIgnoreCase))
            .Select(p => (Primary: p, Score: SharedLeadingWords(e.DisplayName, p.DisplayName, publisherWords)))
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score)
            .Select(x => x.Primary)
            .FirstOrDefault();
    }

    /// <summary>两个名称从开头起连续相同的词数，发布者名里出现的词不计分（但仍需相同才能继续往后比）。</summary>
    private static int SharedLeadingWords(string a, string b, HashSet<string> ignored)
    {
        var wa = Words(a).ToList();
        var wb = Words(b).ToList();
        var score = 0;
        for (var i = 0; i < Math.Min(wa.Count, wb.Count); i++)
        {
            if (!wa[i].Equals(wb[i], StringComparison.OrdinalIgnoreCase)) break;
            if (!ignored.Contains(wa[i])) score++;
        }
        return score;
    }

    private static IEnumerable<string> Words(string s) =>
        s.Split([' ', '(', ')', '-', ','], StringSplitOptions.RemoveEmptyEntries);
}
```

- [ ] **Step 6: 运行，确认通过**

Run: `dotnet test --filter SoftwareGrouperTests`
Expected: 全部 PASS。如果 `Python_components...` 失败，检查 `Words` 是否把 "(64-bit)" 拆开了——"Python 3.10.2 Core..." 与 "Python 3.10.2 (64-bit)" 的共同开头应为 Python、3.10.2，其中 Python 出现在发布者名里不计分，得 1 分；与 "Python Launcher" 得 0 分。

- [ ] **Step 7: 全量测试**

Run: `dotnet test`
Expected: 全部 PASS，0 警告

- [ ] **Step 8: Commit**

```bash
git add -A
git commit -m "feat(core): 归组（隐藏组件归属、组件合集、后台项目挂靠、去重）"
```

### Task 7：SystemScanner + ScanCli，本机核对

**Files:**
- Create: `src/UpdateHelper.Core/Scanning/SystemScanner.cs`
- Modify: `src/UpdateHelper.ScanCli/Program.cs`（替换模板内容）
- Test: `tests/UpdateHelper.Core.Tests/SystemScannerTests.cs`

**Interfaces:**
- Consumes: `IUninstallSource`、`RegistryUninstallSource`（Task 2）；`IBackgroundSource` 及四个实现（Task 4）；`SoftwareGrouper.Group`、`ScanResult`、`PathUtil.IsUnder`（Task 6）
- Produces:
  - `sealed record ScanReport(ScanResult Result, IReadOnlyList<string> Warnings)`
  - `sealed class SystemScanner`：构造函数 `SystemScanner(IUninstallSource uninstall, IReadOnlyList<IBackgroundSource> background, string windowsDirectory)`；`ScanReport Scan()`；`static SystemScanner CreateDefault()`
  - 行为：位于 Windows 目录下的后台项目（系统自带的服务和任务）不进入结果；某个来源抛异常时，记一条中文警告并继续扫描其他来源

- [ ] **Step 1: 写失败的测试** `tests/UpdateHelper.Core.Tests/SystemScannerTests.cs`

```csharp
using UpdateHelper.Core.Scanning;
using static UpdateHelper.Core.Tests.Fakes;

namespace UpdateHelper.Core.Tests;

public class SystemScannerTests
{
    private sealed class FakeUninstall(params UninstallEntry[] entries) : IUninstallSource
    {
        public IReadOnlyList<UninstallEntry> Read() => entries;
    }

    private sealed class FakeBackground(params BackgroundItem[] items) : IBackgroundSource
    {
        public IReadOnlyList<BackgroundItem> Read() => items;
    }

    private sealed class BrokenBackground : IBackgroundSource
    {
        public IReadOnlyList<BackgroundItem> Read() => throw new InvalidOperationException("COM 坏了");
    }

    private static BackgroundItem Svc(string name, string? exe)
        => new(BackgroundKind.Service, name, null, exe, exe, null);

    [Fact]
    public void Items_inside_windows_directory_are_dropped()
    {
        var scanner = new SystemScanner(
            new FakeUninstall(),
            [new FakeBackground(
                Svc("Winmgmt", @"C:\Windows\system32\svchost.exe"),
                Svc("ToDesk_Service", @"C:\Program Files\ToDesk\ToDesk_Service.exe"),
                Svc("NoPath", null))],
            windowsDirectory: @"C:\Windows");

        var report = scanner.Scan();

        Assert.Equal(new[] { "ToDesk_Service", "NoPath" }, report.Result.UnassignedBackground.Select(b => b.Name));
        Assert.Empty(report.Warnings);
    }

    [Fact]
    public void Broken_source_becomes_warning_and_others_still_scan()   // Review Focus 5
    {
        var scanner = new SystemScanner(
            new FakeUninstall(Entry("QQ", "Tencent", location: @"C:\Program Files\Tencent\QQNT")),
            [new BrokenBackground(), new FakeBackground(Svc("QQSvc", @"C:\Program Files\Tencent\QQNT\svc.exe"))],
            windowsDirectory: @"C:\Windows");

        var report = scanner.Scan();

        var warning = Assert.Single(report.Warnings);
        Assert.Contains("BrokenBackground", warning);
        Assert.Single(report.Result.Groups.Single(g => g.Name == "QQ").Background);
    }
}
```

- [ ] **Step 2: 运行，确认失败**

Run: `dotnet test --filter SystemScannerTests`
Expected: 编译失败，找不到 `SystemScanner`

- [ ] **Step 3: 实现** `src/UpdateHelper.Core/Scanning/SystemScanner.cs`

```csharp
using UpdateHelper.Core.Grouping;

namespace UpdateHelper.Core.Scanning;

/// <summary>一次扫描的结果 + 扫描过程中的警告（某个来源读取失败等）。</summary>
public sealed record ScanReport(ScanResult Result, IReadOnlyList<string> Warnings);

/// <summary>把所有来源串起来做一次完整扫描。只读。</summary>
public sealed class SystemScanner(
    IUninstallSource uninstall,
    IReadOnlyList<IBackgroundSource> background,
    string windowsDirectory)
{
    public static SystemScanner CreateDefault() => new(
        new RegistryUninstallSource(),
        [new ServiceSource(), new RunKeySource(), new StartupFolderSource(), new ScheduledTaskSource()],
        Environment.GetFolderPath(Environment.SpecialFolder.Windows));

    public ScanReport Scan()
    {
        var warnings = new List<string>();

        IReadOnlyList<UninstallEntry> entries = [];
        try { entries = uninstall.Read(); }
        catch (Exception ex) { warnings.Add($"读取已装软件失败（{uninstall.GetType().Name}）：{ex.Message}"); }

        var items = new List<BackgroundItem>();
        foreach (var source in background)
        {
            try
            {
                items.AddRange(source.Read().Where(i => !PathUtil.IsUnder(i.ExecutablePath, windowsDirectory)));
            }
            catch (Exception ex)
            {
                warnings.Add($"读取后台项目失败（{source.GetType().Name}）：{ex.Message}");
            }
        }

        return new ScanReport(SoftwareGrouper.Group(entries, items), warnings);
    }
}
```

- [ ] **Step 4: 运行，确认通过**

Run: `dotnet test`
Expected: 全部 PASS，0 警告

- [ ] **Step 5: 写命令行工具** `src/UpdateHelper.ScanCli/Program.cs`（整个文件替换）

```csharp
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using UpdateHelper.Core.Grouping;
using UpdateHelper.Core.Scanning;

// 用法：
//   dotnet run --project src/UpdateHelper.ScanCli            只列普通软件和开发工具
//   dotnet run --project src/UpdateHelper.ScanCli -- --all   列出全部分类
//   dotnet run --project src/UpdateHelper.ScanCli -- --json scan.json   另存完整结果
Console.OutputEncoding = Encoding.UTF8;

var showAll = args.Contains("--all");
var jsonIndex = Array.IndexOf(args, "--json");
var jsonPath = jsonIndex >= 0 && jsonIndex + 1 < args.Length ? args[jsonIndex + 1] : null;

var report = SystemScanner.CreateDefault().Scan();
var r = report.Result;
var groups = r.Groups;

Console.WriteLine($"注册表登记总数：{r.TotalEntries}");
Console.WriteLine($"整理后的软件组：{groups.Count}");
foreach (var byCat in groups.GroupBy(g => g.Category))
    Console.WriteLine($"  {byCat.Key,-16}{byCat.Count()}");
Console.WriteLine($"归入软件的组件：{groups.Sum(g => g.Components.Count)}");
Console.WriteLine($"后台项目：已归属 {groups.Sum(g => g.Background.Count)}，未归属 {r.UnassignedBackground.Count}");
foreach (var w in report.Warnings) Console.WriteLine($"警告：{w}");
Console.WriteLine();

var visible = showAll
    ? groups
    : groups.Where(g => g.Category is SoftwareCategory.Application or SoftwareCategory.DevTool).ToList();

foreach (var g in visible)
{
    var flag = g.IsIncomplete ? " [信息不完整]" : "";
    Console.WriteLine($"{g.Name}{flag}  {g.Version}  · {g.Publisher}  · {g.Category}");
    if (g.Components.Count > 0) Console.WriteLine($"    组件 {g.Components.Count} 个");
    foreach (var b in g.Background) Console.WriteLine($"    后台 [{b.Kind}] {b.Name}");
}

Console.WriteLine();
Console.WriteLine("未归属的后台项目：");
foreach (var b in r.UnassignedBackground) Console.WriteLine($"  [{b.Kind}] {b.Name}  {b.ExecutablePath}");

if (jsonPath is not null)
{
    var options = new JsonSerializerOptions
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,   // 中文不转义
        Converters = { new JsonStringEnumConverter() },
    };
    File.WriteAllText(jsonPath, JsonSerializer.Serialize(report, options), Encoding.UTF8);
    Console.WriteLine($"完整结果已保存到 {Path.GetFullPath(jsonPath)}");
}
```

- [ ] **Step 6: 在本机运行并核对**

Run: `dotnet run --project src/UpdateHelper.ScanCli -- --json scan.json`

对照设计文档里 2026-09-29 的实测数字逐项核对（软件装卸会让数字略有出入，差几个是正常的）：

| 检查项 | 预期 |
|---|---|
| 注册表登记总数 | 约 297 |
| 归入软件的组件 + 各组件合集里的条目 | 约 146（隐藏条目） |
| "NVIDIA CUDA Toolkit 13.0" | 带若干组件 |
| "Python 3.10.2 (64-bit)" | 带 Core Interpreter、pip Bootstrap 等组件 |
| "QQ" | 后台挂着 `[RunKey] QQNT` |
| 名称为 `${{arpDisplayName}}` 的条目 | 进了 "NVIDIA Corporation 组件"，或标记 [信息不完整] |
| 未归属的后台项目 | 不包含 `C:\Windows` 下的系统服务 |
| 警告 | 无（若有，记下内容） |

任何一项明显不符：**不要改测试去迁就**，先用 superpowers:systematic-debugging 找原因，补一个能重现问题的单元测试再修。

- [ ] **Step 7: 不提交扫描结果**（含本机隐私信息）

```bash
echo "scan*.json" >> .gitignore
```

- [ ] **Step 8: Commit**

```bash
git add -A
git commit -m "feat: SystemScanner 与 ScanCli，可在本机打印扫描结果"
```

### Task 8：Python 学习版 01

用 Python 复刻 Task 2 和 Task 6 的核心思路，方便对照着理解。不追求和 C# 版功能一致，只保留主干：读 3 处注册表 → 区分可见/隐藏 → 按发布者和名称开头把隐藏组件归到主软件下面。

**Files:**
- Create: `learning/python/01_扫描已装软件.py`
- Create: `learning/python/README.md`

**Interfaces:**
- 独立脚本，不被其他代码引用；只依赖 Python 3.9+ 标准库（本机是 3.9.13）

- [ ] **Step 1: 写脚本** `learning/python/01_扫描已装软件.py`

```python
"""
学习版 01：扫描已装软件
对应 C# 代码：
  - 读注册表        → src/UpdateHelper.Core/Scanning/RegistryUninstallSource.cs
  - 安全读值        → src/UpdateHelper.Core/Scanning/RegistryValues.cs
  - 隐藏组件归组    → src/UpdateHelper.Core/Grouping/SoftwareGrouper.cs（规则 c 的简化版）

运行：python learning/python/01_扫描已装软件.py
本脚本只读注册表，不会修改任何东西。
"""
import winreg

UNINSTALL = r"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"

# 三处登记位置：(根键, 访问标志, 说明)
# KEY_WOW64_64KEY / 32KEY 决定看 64 位还是 32 位那一份注册表
LOCATIONS = [
    (winreg.HKEY_LOCAL_MACHINE, winreg.KEY_WOW64_64KEY, "HKLM 64 位"),
    (winreg.HKEY_LOCAL_MACHINE, winreg.KEY_WOW64_32KEY, "HKLM 32 位"),
    (winreg.HKEY_CURRENT_USER, 0, "HKCU 当前用户"),
]


def read_value(key, name):
    """安全读值：值不存在或类型奇怪时返回 None（对应 C# 的 RegistryValues）。"""
    try:
        value, _type = winreg.QueryValueEx(key, name)
    except OSError:
        return None
    if isinstance(value, str):
        value = value.strip()
        return value or None
    if isinstance(value, int):
        return value
    return None  # 二进制等其他类型当作缺失


def read_entries():
    """读出三处注册表里所有带 DisplayName 的登记。"""
    entries = []
    for root, flag, label in LOCATIONS:
        try:
            uninstall = winreg.OpenKey(root, UNINSTALL, 0, winreg.KEY_READ | flag)
        except OSError:
            continue  # 整个位置打不开就跳过
        index = 0
        while True:
            try:
                sub_name = winreg.EnumKey(uninstall, index)
            except OSError:
                break  # 枚举完了
            index += 1
            try:
                with winreg.OpenKey(uninstall, sub_name) as key:
                    name = read_value(key, "DisplayName")
                    if not isinstance(name, str):
                        continue  # 没有显示名的键不算软件
                    entries.append({
                        "name": name,
                        "version": read_value(key, "DisplayVersion"),
                        "publisher": read_value(key, "Publisher"),
                        "hidden": read_value(key, "SystemComponent") == 1,
                        "is_patch": read_value(key, "ParentKeyName") is not None,
                        "where": label,
                    })
            except OSError:
                continue  # 单个键没权限就跳过
        winreg.CloseKey(uninstall)
    return entries


def words(text):
    """把名称拆成词，和 C# 版的 Words() 一样按空格、括号、横线、逗号拆。"""
    for ch in "()-,":
        text = text.replace(ch, " ")
    return text.split()


def shared_leading_words(a, b, ignored):
    """两个名称从开头起连续相同的词数；发布者名里出现的词不计分。"""
    score = 0
    for x, y in zip(words(a), words(b)):
        if x.lower() != y.lower():
            break
        if x.lower() not in ignored:
            score += 1
    return score


def group(entries):
    """可见的登记各成一组；隐藏组件按"同发布者 + 名称开头共同词"找主人。"""
    primaries = [e for e in entries if not e["hidden"] and not e["is_patch"]]
    groups = {id(p): {"main": p, "parts": []} for p in primaries}
    orphans = []

    for e in entries:
        if not (e["hidden"] or e["is_patch"]):
            continue
        ignored = {w.lower() for w in words(e["publisher"] or "")}
        best, best_score = None, 0
        for p in primaries:
            if p["publisher"] != e["publisher"]:
                continue
            score = shared_leading_words(e["name"], p["name"], ignored)
            if score > best_score:
                best, best_score = p, score
        if best is None:
            orphans.append(e)
        else:
            groups[id(best)]["parts"].append(e)
    return list(groups.values()), orphans


def main():
    entries = read_entries()
    hidden = [e for e in entries if e["hidden"]]
    print(f"登记总数：{len(entries)}，其中隐藏的：{len(hidden)}")

    groups, orphans = group(entries)
    print(f"整理后：{len(groups)} 个软件，{len(orphans)} 个找不到主人的组件\n")

    for g in sorted(groups, key=lambda g: g["main"]["name"].lower()):
        main_entry = g["main"]
        line = f"{main_entry['name']}  {main_entry['version'] or ''}"
        if g["parts"]:
            line += f"   （含 {len(g['parts'])} 个组件）"
        print(line)


if __name__ == "__main__":
    main()
```

- [ ] **Step 2: 写说明** `learning/python/README.md`

```markdown
# Python 学习版

每个脚本对照 C# 核心库的一个模块，用最少的代码讲清楚原理。脚本开头写明了对应的 C# 文件。
都只读取信息，不会修改电脑上的任何东西。

| 脚本 | 内容 | 对应 C# |
|---|---|---|
| 01_扫描已装软件.py | 读注册表、区分隐藏条目、把组件归到主软件下 | Scanning/RegistryUninstallSource.cs、Grouping/SoftwareGrouper.cs |

运行：`python learning/python/01_扫描已装软件.py`（需要 Python 3.9+，Windows）
```

- [ ] **Step 3: 运行并和 C# 版对比**

Run: `python learning/python/01_扫描已装软件.py`
Expected: "登记总数"和 Task 7 中 ScanCli 打印的"注册表登记总数"相同；"Python 3.10.2 (64-bit)" 后面显示"含 N 个组件"。
（Python 版没有做去重和按安装位置归组，所以软件数量会比 C# 版略多，这是有意的简化，README 不用解释。）

- [ ] **Step 4: Commit**

```bash
git add learning
git commit -m "docs(learning): Python 学习版 01 扫描已装软件"
```

---

## 完成标准

- `dotnet build` 0 警告 0 错误；`dotnet test` 全部通过
- ScanCli 在本机运行无警告，Task 7 Step 6 的核对表全部符合
- Python 学习版 01 能运行，登记总数与 ScanCli 一致
- 整个计划没有任何写注册表、改服务、删文件的代码
