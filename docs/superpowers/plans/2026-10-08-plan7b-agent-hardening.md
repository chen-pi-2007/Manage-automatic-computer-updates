# 计划 7 补充：后台助手加固 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 修掉计划 7 整体审查发现的问题，让"授权一次"真正只能做"把已安装的 winget 软件升级到更高的版本"这一件事。

**Architecture:** 计划任务不再直接启动 .NET 写的后台助手，而是启动一个用 Native AOT 编译的原生小程序"干净启动器"。启动器只保留白名单里的环境变量（系统路径之类），`Path` 只取系统级（HKLM）的值，然后以 `--serve` 启动同目录下的后台助手并等待它结束。这样后台助手的 .NET 运行时在启动时读不到任何用户可控的运行时配置。另外：后台助手关掉 .NET 启动钩子作为第二层；升级前核对目标版本高于已安装版本、安装范围与已安装的一致；启用 / 关闭时等待提权进程有时间上限。

**Tech Stack:** .NET 10 Native AOT（开发机需要 VS 2022 Build Tools 的 C++ 工作负载，已安装）、xUnit。

**Spec:** `docs/superpowers/specs/2026-09-30-update-helper-design.md`（第 3 节权限模型）；前序计划 `docs/superpowers/plans/2026-10-07-plan7-agent.md`

## 背景（整体审查结论，2026-10-08）

- Critical：提权启动的后台助手会受当前用户可控的运行时启动配置影响，绕过"只运行 Program Files 里的副本"这层保护。具体利用方式审查员没有写出，本计划也不写；修法是让提权进程的环境不来自用户。
- Important：可以请求比已安装更旧的版本（降级）；安装范围由请求方决定；启用 / 关闭时等待提权进程没有上限。
- 已实测：本机 Native AOT 发布需要把 `%ProgramFiles(x86)%\Microsoft Visual Studio\Installer`（vswhere 所在目录）加到 PATH，否则 ILCompiler 的 `findvcvarsall.bat` 找不到 vswhere。

## Global Constraints

- 计划 7 的 Global Constraints 全部继续有效（框架、TreatWarningsAsErrors、只传结构化指令、登录用户身份、只接 winget、中文文字、提交不加署名、ps1 带 BOM）
- 启动器必须是 Native AOT 编译的原生程序；任何情况下都不能让 JIT 版（带 .dll / .runtimeconfig.json 的）启动器出现在 Program Files 里被计划任务运行
- 启动器不引用 UpdateHelper.Core 或任何第三方包（保持 AOT 简单、没有反射）
- 环境变量用**白名单**，不用黑名单
- 不在文档、注释、提交信息、测试名里写具体的利用方法；只写"不继承用户的环境变量"

## Review Focus

1. **用户环境里有任意自定义变量**（包括 .NET 相关的）：后台助手进程的环境里不能出现白名单以外的变量 → Task 8 `Drops_everything_not_on_the_allow_list`、Task 9 冒烟检查
2. **用户改了自己的 Path**：后台助手的 Path 只能来自系统级设置 → Task 8 `Path_comes_only_from_machine_value`
3. **启动器不是原生程序**（有人用普通方式编译了它）：`--install` 拒绝部署 → Task 9 `--install` 检查
4. **请求降级或换安装范围**：后台助手拒绝，不调用 winget 升级 → Task 10 `UpgradeGuard` 测试
5. **提权进程卡住**：启用 / 关闭最多等 10 分钟，然后给出中文说明，按钮恢复 → Task 11

---

## 文件结构

| 文件 | 职责 |
|---|---|
| `src/UpdateHelper.AgentLauncher/UpdateHelper.AgentLauncher.csproj` | 启动器项目（PublishAot） |
| `src/UpdateHelper.AgentLauncher/CleanEnvironment.cs` | 纯逻辑：由当前环境和系统 Path 算出干净的环境变量 |
| `src/UpdateHelper.AgentLauncher/Program.cs` | 启动器入口：读系统 Path → 算干净环境 → 启动后台助手 → 等待 → 返回退出码 |
| `src/UpdateHelper.App/UpdateHelper.App.csproj` | 构建 / 发布时自动以 AOT 方式发布启动器到输出目录 |
| `src/UpdateHelper.Agent/UpdateHelper.Agent.csproj` | 关掉启动钩子 |
| `src/UpdateHelper.Core/Agent/AgentPaths.cs` | 加启动器文件名 |
| `src/UpdateHelper.Core/Agent/AgentSetup.cs` | 任务改为运行启动器；状态判断要求启动器存在；判断启动器是不是原生程序 |
| `src/UpdateHelper.Agent/Program.cs` | `--install` 拒绝部署非原生启动器 |
| `src/UpdateHelper.Core/Agent/UpgradeGuard.cs` | 纯逻辑：目标版本必须更高、安装范围必须一致 |
| `src/UpdateHelper.Winget/WingetInstaller.cs` | 后台助手模式下调用 UpgradeGuard |
| `src/UpdateHelper.App/AgentController.cs` | 等待提权进程最多 10 分钟 |
| `tests/UpdateHelper.Core.Tests/CleanEnvironmentTests.cs`、`UpgradeGuardTests.cs` | 测试 |

---

### Task 8: 干净环境的计算逻辑

**Files:**
- Create: `src/UpdateHelper.AgentLauncher/CleanEnvironment.cs`
- Modify: `tests/UpdateHelper.Core.Tests/UpdateHelper.Core.Tests.csproj`（把这个源文件链接进测试项目编译）
- Test: `tests/UpdateHelper.Core.Tests/CleanEnvironmentTests.cs`

**Interfaces:**
- Produces: `namespace UpdateHelper.AgentLauncher; public static class CleanEnvironment { public static IReadOnlyList<string> AllowedNames { get; } public static Dictionary<string, string> Build(IReadOnlyDictionary<string, string> current, string? machinePath, string systemRoot) }` —— 只保留白名单里的变量（名字不区分大小写，输出用白名单里的标准写法）；`Path` 一律用 `machinePath`，为空时用 `{systemRoot}\system32;{systemRoot};{systemRoot}\System32\Wbem`；`SystemRoot` 缺失时补上 `systemRoot`

- [ ] **Step 1: 写失败的测试**

`tests/UpdateHelper.Core.Tests/UpdateHelper.Core.Tests.csproj` 的 `<ItemGroup>` 里加：

```xml
    <!-- 启动器是 AOT 程序、不能被引用；只把它的纯逻辑文件拿来测试 -->
    <Compile Include="..\..\src\UpdateHelper.AgentLauncher\CleanEnvironment.cs" Link="Linked\CleanEnvironment.cs" />
```

`tests/UpdateHelper.Core.Tests/CleanEnvironmentTests.cs`：

```csharp
using UpdateHelper.AgentLauncher;

namespace UpdateHelper.Core.Tests;

public sealed class CleanEnvironmentTests
{
    private static Dictionary<string, string> Env(params (string Name, string Value)[] items) =>
        items.ToDictionary(i => i.Name, i => i.Value, StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void Keeps_allowed_variables_with_canonical_names()
    {
        var result = CleanEnvironment.Build(
            Env(("systemroot", @"C:\Windows"), ("USERPROFILE", @"C:\Users\a"), ("TEMP", @"C:\Users\a\AppData\Local\Temp")),
            machinePath: @"C:\Windows\system32;C:\Windows", systemRoot: @"C:\Windows");

        Assert.Equal(@"C:\Windows", result["SystemRoot"]);
        Assert.Equal(@"C:\Users\a", result["USERPROFILE"]);
        Assert.Equal(@"C:\Users\a\AppData\Local\Temp", result["TEMP"]);
        Assert.Contains("SystemRoot", result.Keys);   // 标准写法，不是 systemroot
    }

    [Fact]
    public void Drops_everything_not_on_the_allow_list()
    {
        var result = CleanEnvironment.Build(
            Env(("SystemRoot", @"C:\Windows"), ("DOTNET_SOMETHING", "x"), ("COMPLUS_SOMETHING", "x"),
                ("CORECLR_SOMETHING", "x"), ("MY_TOOL_HOME", "x"), ("PSModulePath", "x")),
            machinePath: @"C:\Windows\system32", systemRoot: @"C:\Windows");

        Assert.All(result.Keys, k => Assert.Contains(k, CleanEnvironment.AllowedNames));
        Assert.DoesNotContain(result.Keys, k => k.StartsWith("DOTNET_", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(result.Keys, k => k.StartsWith("COMPLUS_", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(result.Keys, k => k.StartsWith("CORECLR_", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain("MY_TOOL_HOME", result.Keys);
    }

    [Fact]
    public void Path_comes_only_from_machine_value()
    {
        var result = CleanEnvironment.Build(
            Env(("SystemRoot", @"C:\Windows"), ("Path", @"C:\Users\a\evil;C:\Windows\system32")),
            machinePath: @"C:\Windows\system32;C:\Windows", systemRoot: @"C:\Windows");

        Assert.Equal(@"C:\Windows\system32;C:\Windows", result["Path"]);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Missing_machine_path_falls_back_to_system_directories(string? machinePath)
    {
        var result = CleanEnvironment.Build(Env(("SystemRoot", @"C:\Windows")), machinePath, systemRoot: @"C:\Windows");
        Assert.Equal(@"C:\Windows\system32;C:\Windows;C:\Windows\System32\Wbem", result["Path"]);
    }

    [Fact]
    public void SystemRoot_is_always_present()
    {
        var result = CleanEnvironment.Build(Env(), machinePath: null, systemRoot: @"C:\Windows");
        Assert.Equal(@"C:\Windows", result["SystemRoot"]);
    }

    [Fact]
    public void Allow_list_has_no_runtime_or_loader_variables()
    {
        foreach (var name in CleanEnvironment.AllowedNames)
        {
            Assert.False(name.StartsWith("DOTNET", StringComparison.OrdinalIgnoreCase), name);
            Assert.False(name.StartsWith("COMPLUS", StringComparison.OrdinalIgnoreCase), name);
            Assert.False(name.StartsWith("CORECLR", StringComparison.OrdinalIgnoreCase), name);
            Assert.False(name.StartsWith("COR_", StringComparison.OrdinalIgnoreCase), name);
        }
    }
}
```

- [ ] **Step 2: 运行测试，确认失败**

Run: `dotnet test tests/UpdateHelper.Core.Tests --filter CleanEnvironmentTests`
Expected: 编译失败（找不到 `CleanEnvironment.cs` 或类型）

- [ ] **Step 3: 实现**

`src/UpdateHelper.AgentLauncher/CleanEnvironment.cs`：

```csharp
namespace UpdateHelper.AgentLauncher;

/// <summary>
/// 后台助手以管理员身份运行，它的环境变量不能来自用户：用户能改自己的环境变量，
/// 而 .NET 程序启动时会读取其中一部分。这里只保留白名单里的系统类变量，Path 只用系统级的值。
/// </summary>
public static class CleanEnvironment
{
    /// <summary>允许传给后台助手的变量（标准写法）。都是 Windows 自己设置的路径和系统信息，不含任何运行时配置。</summary>
    public static IReadOnlyList<string> AllowedNames { get; } =
    [
        "SystemRoot", "windir", "SystemDrive", "ComSpec", "PATHEXT", "OS",
        "PROCESSOR_ARCHITECTURE", "PROCESSOR_IDENTIFIER", "PROCESSOR_LEVEL", "PROCESSOR_REVISION", "NUMBER_OF_PROCESSORS",
        "COMPUTERNAME", "USERNAME", "USERDOMAIN", "USERDOMAIN_ROAMINGPROFILE", "LOGONSERVER", "SESSIONNAME",
        "USERPROFILE", "HOMEDRIVE", "HOMEPATH", "APPDATA", "LOCALAPPDATA", "TEMP", "TMP",
        "ProgramData", "ALLUSERSPROFILE", "PUBLIC",
        "ProgramFiles", "ProgramFiles(x86)", "ProgramW6432",
        "CommonProgramFiles", "CommonProgramFiles(x86)", "CommonProgramW6432",
        "Path",
    ];

    public static Dictionary<string, string> Build(IReadOnlyDictionary<string, string> current, string? machinePath, string systemRoot)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in AllowedNames)
        {
            if (name == "Path") continue;
            var value = current.FirstOrDefault(kv => string.Equals(kv.Key, name, StringComparison.OrdinalIgnoreCase)).Value;
            if (value is not null) result[name] = value;
        }

        if (!result.ContainsKey("SystemRoot")) result["SystemRoot"] = systemRoot;
        result["Path"] = string.IsNullOrWhiteSpace(machinePath)
            ? $@"{systemRoot}\system32;{systemRoot};{systemRoot}\System32\Wbem"
            : machinePath;
        return result;
    }
}
```

（此时 `src/UpdateHelper.AgentLauncher/` 目录里只有这一个文件，项目文件在 Task 9 建。）

- [ ] **Step 4: 运行测试，确认通过**

Run: `dotnet test tests/UpdateHelper.Core.Tests --filter CleanEnvironmentTests`
Expected: 全部通过

- [ ] **Step 5: 提交**

```bash
git add src/UpdateHelper.AgentLauncher/CleanEnvironment.cs tests/UpdateHelper.Core.Tests
git commit -m "feat(agent): 干净环境——后台助手只拿到白名单里的系统变量，Path 只用系统级的值"
```

---

### Task 9: 干净启动器（Native AOT）并接入计划任务

**Files:**
- Create: `src/UpdateHelper.AgentLauncher/UpdateHelper.AgentLauncher.csproj`
- Create: `src/UpdateHelper.AgentLauncher/Program.cs`
- Modify: `UpdateHelper.slnx`（加入启动器项目）
- Modify: `src/UpdateHelper.App/UpdateHelper.App.csproj`（构建和发布时 AOT 发布启动器）
- Modify: `src/UpdateHelper.Agent/UpdateHelper.Agent.csproj`（`<StartupHookSupport>false</StartupHookSupport>`）
- Modify: `src/UpdateHelper.Core/Agent/AgentPaths.cs`、`AgentSetup.cs`
- Modify: `src/UpdateHelper.Agent/Program.cs`
- Test: `tests/UpdateHelper.Core.Tests/AgentSetupTests.cs`（更新）

**Interfaces:**
- Consumes: Task 8 `CleanEnvironment.Build`
- Produces:
  - `AgentPaths.LauncherExeName = "UpdateHelper.AgentLauncher.exe"`、`AgentPaths.LauncherExe`
  - `AgentSetup.BuildTaskXml(userSid, launcherExe)`：`<Command>` 是启动器，**没有** `<Arguments>`
  - `AgentSetup.IsNativeLauncher(string directory)`：目录里有启动器 exe，且**没有** `UpdateHelper.AgentLauncher.dll` 和 `UpdateHelper.AgentLauncher.runtimeconfig.json`
  - `AgentSetup.GetStatus`：后台助手和启动器都在才算有程序文件（缺一个算 Broken）
  - Agent `--install`：部署前 `IsNativeLauncher(AppContext.BaseDirectory)` 不成立 → 退出码 `6`；任务注册用启动器路径
  - App 侧 `AgentController` 把退出码 6 映射为"程序包不完整（缺少原生启动器），请重新下载完整的程序包"

- [ ] **Step 1: 更新 AgentSetup 测试（先红）**

`tests/UpdateHelper.Core.Tests/AgentSetupTests.cs`：
- `Task_xml_runs_installed_agent_as_this_user_with_highest_privileges` 改为传入 `@"C:\Program Files\UpdateHelper\UpdateHelper.AgentLauncher.exe"`，断言 `Command` 是它，并断言 `xml.Descendants(ns + "Arguments")` 为空。
- `Status_reflects_files_task_and_version` 和其他状态测试：准备"有程序文件"时同时写 `AgentPaths.AgentExeName` 和 `AgentPaths.LauncherExeName`；加一个断言：只有后台助手、没有启动器 + 任务存在 → `Broken`。
- 新增：

```csharp
    [Fact]
    public void Native_launcher_check()
    {
        Assert.False(AgentSetup.IsNativeLauncher(_dir));                                   // 没有启动器
        File.WriteAllText(Path.Combine(_dir, AgentPaths.LauncherExeName), "x");
        Assert.True(AgentSetup.IsNativeLauncher(_dir));                                    // 只有 exe：原生
        File.WriteAllText(Path.Combine(_dir, "UpdateHelper.AgentLauncher.dll"), "x");
        Assert.False(AgentSetup.IsNativeLauncher(_dir));                                   // 有 dll：是 .NET 普通编译的
        File.Delete(Path.Combine(_dir, "UpdateHelper.AgentLauncher.dll"));
        File.WriteAllText(Path.Combine(_dir, "UpdateHelper.AgentLauncher.runtimeconfig.json"), "{}");
        Assert.False(AgentSetup.IsNativeLauncher(_dir));
    }
```

Run: `dotnet test tests/UpdateHelper.Core.Tests --filter AgentSetupTests` → Expected: 编译失败或断言失败

- [ ] **Step 2: 实现 Core 部分**

`AgentPaths.cs` 加：

```csharp
    /// <summary>计划任务实际运行的原生小程序：清理环境变量后再启动后台助手。</summary>
    public const string LauncherExeName = "UpdateHelper.AgentLauncher.exe";

    public static string LauncherExe => Path.Combine(InstallDirectory, LauncherExeName);
```

`AgentSetup.cs`：
- `BuildTaskXml(string userSid, string launcherExe)`：参数改名，`<Command>{exe}</Command>` 用启动器路径，删掉 `<Arguments>--serve</Arguments>` 这一行；`<Description>` 改为"……计划任务运行的是清理过环境的原生启动器……"。
- `GetStatus`：`exeExists` 改为后台助手 **和** 启动器都存在。
- 新增：

```csharp
    /// <summary>
    /// 启动器必须是 Native AOT 编译的原生程序（只有一个 exe）。普通编译的 .NET 启动器旁边会有 dll 和 runtimeconfig，
    /// 它自己就会读取用户环境里的运行时配置，失去意义，所以拒绝部署。
    /// </summary>
    public static bool IsNativeLauncher(string directory) =>
        File.Exists(Path.Combine(directory, AgentPaths.LauncherExeName))
        && !File.Exists(Path.Combine(directory, "UpdateHelper.AgentLauncher.dll"))
        && !File.Exists(Path.Combine(directory, "UpdateHelper.AgentLauncher.runtimeconfig.json"));
```

Run: `dotnet test tests/UpdateHelper.Core.Tests --filter AgentSetupTests` → Expected: 通过

- [ ] **Step 3: 启动器项目**

`src/UpdateHelper.AgentLauncher/UpdateHelper.AgentLauncher.csproj`：

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <!-- 后台助手的原生启动器：必须用 Native AOT 发布（dotnet publish），不读取用户环境里的运行时配置 -->
  <PropertyGroup>
    <OutputType>WinExe</OutputType>
    <TargetFramework>net10.0-windows</TargetFramework>
    <RuntimeIdentifier>win-x64</RuntimeIdentifier>
    <PublishAot>true</PublishAot>
    <InvariantGlobalization>true</InvariantGlobalization>
    <StartupHookSupport>false</StartupHookSupport>
    <AssemblyName>UpdateHelper.AgentLauncher</AssemblyName>
    <RootNamespace>UpdateHelper.AgentLauncher</RootNamespace>
  </PropertyGroup>

</Project>
```

`src/UpdateHelper.AgentLauncher/Program.cs`：

```csharp
using System.Collections;
using System.Diagnostics;
using Microsoft.Win32;
using UpdateHelper.AgentLauncher;

// 计划任务运行的就是这个程序。它不接受任何参数：
// 读系统级 Path → 只保留白名单里的环境变量 → 以 --serve 启动同目录下的后台助手 → 等它结束，返回它的退出码。

var directory = AppContext.BaseDirectory;
var agent = Path.Combine(directory, "UpdateHelper.Agent.exe");
if (!File.Exists(agent)) return 10;

var systemRoot = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
string? machinePath = null;
try
{
    using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Session Manager\Environment");
    if (key?.GetValue("Path") is string raw) machinePath = ExpandSystem(raw, systemRoot);
}
catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { }

var current = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
foreach (DictionaryEntry e in Environment.GetEnvironmentVariables())
    if (e.Key is string k && e.Value is string v) current[k] = v;

var start = new ProcessStartInfo(agent, "--serve")
{
    UseShellExecute = false,
    CreateNoWindow = true,
    WorkingDirectory = directory,
};
start.Environment.Clear();
foreach (var (name, value) in CleanEnvironment.Build(current, machinePath, systemRoot))
    start.Environment[name] = value;

try
{
    using var process = Process.Start(start);
    if (process is null) return 11;
    process.WaitForExit();
    return process.ExitCode;
}
catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
{
    return 11;
}

// 系统级 Path 里只会用到 %SystemRoot% 这类系统变量；不用 Environment.ExpandEnvironmentVariables（那会用到用户的变量）
static string ExpandSystem(string value, string systemRoot) =>
    value.Replace("%SystemRoot%", systemRoot, StringComparison.OrdinalIgnoreCase)
         .Replace("%windir%", systemRoot, StringComparison.OrdinalIgnoreCase);
```

`UpdateHelper.slnx` 照其他项目加入 `src/UpdateHelper.AgentLauncher/UpdateHelper.AgentLauncher.csproj`。

`src/UpdateHelper.Agent/UpdateHelper.Agent.csproj` 的 PropertyGroup 加 `<StartupHookSupport>false</StartupHookSupport>`（第二层防护）。

- [ ] **Step 4: App 构建 / 发布时 AOT 发布启动器**

`src/UpdateHelper.App/UpdateHelper.App.csproj` 末尾（`</Project>` 之前）加：

```xml
  <!-- 后台助手的原生启动器：构建和发布时都用 Native AOT 发布到输出目录（不能用普通 ProjectReference，那样得到的是 JIT 版）。
       vswhere 所在目录要在 PATH 里，否则 ILCompiler 找不到 C++ 工具链（本机实测）。 -->
  <PropertyGroup>
    <AgentLauncherProject>$(MSBuildThisFileDirectory)..\UpdateHelper.AgentLauncher\UpdateHelper.AgentLauncher.csproj</AgentLauncherProject>
    <VsWhereDir>$([System.Environment]::GetFolderPath(SpecialFolder.ProgramFilesX86))\Microsoft Visual Studio\Installer</VsWhereDir>
  </PropertyGroup>

  <Target Name="PublishAgentLauncherToOutput" AfterTargets="Build" Condition="'$(DesignTimeBuild)' != 'true'"
          Inputs="$(AgentLauncherProject);$(MSBuildThisFileDirectory)..\UpdateHelper.AgentLauncher\Program.cs;$(MSBuildThisFileDirectory)..\UpdateHelper.AgentLauncher\CleanEnvironment.cs"
          Outputs="$(OutDir)UpdateHelper.AgentLauncher.exe">
    <Exec Command="dotnet publish &quot;$(AgentLauncherProject)&quot; -c $(Configuration) -o &quot;$(OutDir)launcher-tmp&quot; --nologo -v q"
          EnvironmentVariables="PATH=$(VsWhereDir);$(PATH)" />
    <Copy SourceFiles="$(OutDir)launcher-tmp\UpdateHelper.AgentLauncher.exe" DestinationFolder="$(OutDir)" />
    <RemoveDir Directories="$(OutDir)launcher-tmp" />
  </Target>

  <Target Name="PublishAgentLauncherToPublishDir" AfterTargets="Publish">
    <Exec Command="dotnet publish &quot;$(AgentLauncherProject)&quot; -c $(Configuration) -o &quot;$(PublishDir)launcher-tmp&quot; --nologo -v q"
          EnvironmentVariables="PATH=$(VsWhereDir);$(PATH)" />
    <Copy SourceFiles="$(PublishDir)launcher-tmp\UpdateHelper.AgentLauncher.exe" DestinationFolder="$(PublishDir)" />
    <RemoveDir Directories="$(PublishDir)launcher-tmp" />
  </Target>
```

（只复制 exe：AOT 发布目录里的 .pdb 不需要；临时目录用完删掉，避免把别的文件带进输出目录。）

- [ ] **Step 5: Agent `--install` 改用启动器**

`src/UpdateHelper.Agent/Program.cs`：
- 顶部注释的退出码说明加上 `6 程序包里没有原生启动器`。
- 在"1. 结束安装目录里正在运行的后台助手"之前加：

```csharp
// 0. 程序包里必须有原生（AOT）启动器；普通编译的启动器会读取用户环境里的运行时配置，失去加固意义
if (!AgentSetup.IsNativeLauncher(AppContext.BaseDirectory)) return 6;
```

- 结束进程的循环同时处理 `UpdateHelper.AgentLauncher` 进程（同样只结束安装目录里的）。
- 注册任务时 `BuildTaskXml(userSid, Path.Combine(target, AgentPaths.LauncherExeName))`。

`src/UpdateHelper.App/AgentController.cs` 的退出码映射加：`6 => "程序包不完整（缺少原生启动器），请重新下载完整的程序包",`

- [ ] **Step 6: 构建并核对**

Run:
```powershell
dotnet build -c Debug -v q --nologo
$out = "src/UpdateHelper.App/bin/Debug/net10.0-windows10.0.26100.0/win-x64"
"启动器：$(Test-Path "$out/UpdateHelper.AgentLauncher.exe")；没有 JIT 残留：$(-not (Test-Path "$out/UpdateHelper.AgentLauncher.dll"))；没有临时目录：$(-not (Test-Path "$out/launcher-tmp"))"
```
Expected: `0 个错误`，三项都是 `True`

- [ ] **Step 7: 冒烟检查（普通权限，不弹 UAC）**

1. 在一个临时 PowerShell 里给当前进程设一个自定义变量 `$env:UH_PROBE='1'`，再设一个 `DOTNET_` 开头的无害变量 `$env:DOTNET_UH_PROBE='1'`，然后启动 `UpdateHelper.AgentLauncher.exe`（无参数）。用 `Get-CimInstance Win32_Process` 确认它拉起了 `UpdateHelper.Agent.exe --serve`（命令行含 `--serve`，父进程是启动器）。
2. 确认后台助手进程的环境里没有这两个变量：后台助手没有"打印环境"的功能，**不要为此加功能**；改用 Windows 自带的方式核对——在 scratchpad 写一个读取另一个进程环境块的小工具会很复杂，所以只做这一项可行的检查：用 Task 8 的单元测试覆盖逻辑，冒烟只核对"启动器能拉起后台助手、后台助手能回答 Ping（`AgentPipeClient` 期望路径用这次输出目录里的 `UpdateHelper.Agent.exe`）"，Ping 回复里 `Elevated` 为 false（普通权限）。
3. 结束所有 `UpdateHelper.Agent` / `UpdateHelper.AgentLauncher` 进程。
4. `UpdateHelper.Agent.exe --install --user-sid <当前 SID>`（普通权限）仍返回 5；在一个临时复制的目录里删掉启动器后再运行（同样普通权限，先过 SID 检查再过提权检查——注意 6 的检查在提权检查之后，所以普通权限下看到的仍是 5，这是预期；6 的分支由 Step 1 的 `IsNativeLauncher` 单元测试覆盖）。

把输出记进报告。

- [ ] **Step 8: 全部测试并提交**

Run: `dotnet test`
Expected: 全部通过

```bash
git add UpdateHelper.slnx src tests
git commit -m "feat(agent): 计划任务改为运行原生干净启动器，后台助手不再继承用户的环境变量"
```

---

### Task 10: 只升不降、安装范围跟随已安装的

**Files:**
- Create: `src/UpdateHelper.Core/Agent/UpgradeGuard.cs`
- Modify: `src/UpdateHelper.Winget/WingetInstaller.cs`
- Modify: `src/UpdateHelper.Agent/Program.cs`（`new WingetInstaller(agentMode: true)`）
- Test: `tests/UpdateHelper.Core.Tests/UpgradeGuardTests.cs`

**Interfaces:**
- Produces:
  - `public static class UpgradeGuard { public static string? Check(string? installedVersion, string targetVersion, InstallScopeHint requestedScope, InstallScopeHint? installedScope) }` —— null = 允许；否则中文原因
  - `WingetInstaller(bool agentMode = false)`：agentMode 下，找到包、确认已安装之后、设置安装选项之前调用 `UpgradeGuard.Check`；不通过直接返回失败，不调用 winget 升级。安装范围在 agentMode 下用已安装的范围（已知时）

规则：
- 已安装版本或目标版本解析不了（`AppVersion.Parse` 返回 null 或 `IsApproximate`）→ "无法确认 X 比已安装的 Y 新，后台助手不安装"
- 目标 ≤ 已安装 → "目标版本 X 不比已安装的 Y 新，后台助手不做降级或重装"
- `installedScope` 已知且与 `requestedScope` 不同 → "请求的安装范围和已安装的不一致，后台助手不安装"
- `installedScope` 为 null（winget 没给出）→ 不因范围拒绝

- [ ] **Step 1: 写失败的测试**

```csharp
using UpdateHelper.Core.Agent;
using UpdateHelper.Core.Install;

namespace UpdateHelper.Core.Tests;

public sealed class UpgradeGuardTests
{
    [Theory]
    [InlineData("1.0", "1.1")]
    [InlineData("9.9.20.36330", "9.9.21")]
    [InlineData("2.10.91", "2.10.91.1")]
    public void Allows_newer_versions(string installed, string target)
    {
        Assert.Null(UpgradeGuard.Check(installed, target, InstallScopeHint.Machine, InstallScopeHint.Machine));
    }

    [Theory]
    [InlineData("1.1", "1.0")]
    [InlineData("1.0", "1.0")]
    [InlineData("9.9.21", "9.9.20.36330")]
    public void Refuses_downgrade_or_reinstall(string installed, string target)
    {
        Assert.Contains("不比已安装的", UpgradeGuard.Check(installed, target, InstallScopeHint.Machine, null));
    }

    [Theory]
    [InlineData(null, "1.0")]
    [InlineData("< 3.10.8", "3.10.9")]
    [InlineData("1.0", "not-a-version")]
    public void Refuses_when_versions_cannot_be_compared(string? installed, string target)
    {
        Assert.Contains("无法确认", UpgradeGuard.Check(installed, target, InstallScopeHint.Machine, null));
    }

    [Fact]
    public void Refuses_scope_change_when_installed_scope_is_known()
    {
        Assert.Contains("安装范围", UpgradeGuard.Check("1.0", "1.1", InstallScopeHint.Machine, InstallScopeHint.User));
        Assert.Null(UpgradeGuard.Check("1.0", "1.1", InstallScopeHint.User, InstallScopeHint.User));
        Assert.Null(UpgradeGuard.Check("1.0", "1.1", InstallScopeHint.Machine, null));   // winget 没给出范围：不因此拒绝
    }
}
```

> 注意：`"not-a-version"` 是否能被 `AppVersion.Parse` 解析，以实际实现为准；如果它能解析成有效版本，把这一行换成一个确实解析失败的值（例如空字符串），并在报告里说明。

Run: `dotnet test tests/UpdateHelper.Core.Tests --filter UpgradeGuardTests` → Expected: 编译失败

- [ ] **Step 2: 实现 UpgradeGuard**

```csharp
using UpdateHelper.Core.Install;
using UpdateHelper.Core.Updates;

namespace UpdateHelper.Core.Agent;

/// <summary>后台助手以管理员身份安装前的最后一道检查：只升不降，安装范围跟已安装的一致。</summary>
public static class UpgradeGuard
{
    public static string? Check(string? installedVersion, string targetVersion,
        InstallScopeHint requestedScope, InstallScopeHint? installedScope)
    {
        var installed = AppVersion.Parse(installedVersion);
        var target = AppVersion.Parse(targetVersion);
        if (installed is null || target is null || installed.IsApproximate || target.IsApproximate)
            return $"无法确认 {targetVersion} 比已安装的 {installedVersion ?? "（未知）"} 新，后台助手不安装";
        if (target.CompareTo(installed) <= 0)
            return $"目标版本 {targetVersion} 不比已安装的 {installedVersion} 新，后台助手不做降级或重装";
        if (installedScope is { } scope && scope != requestedScope)
            return "请求的安装范围和已安装的不一致，后台助手不安装";
        return null;
    }
}
```

- [ ] **Step 3: 接入 WingetInstaller**

`src/UpdateHelper.Winget/WingetInstaller.cs`：
- 类改为 `public sealed class WingetInstaller(bool agentMode = false) : IPackageInstaller`。
- 在"包必须已安装"检查之后加：

```csharp
        if (agentMode)
        {
            var installedScope = InstalledScope(package);
            if (UpgradeGuard.Check(package.InstalledVersion.Version, targetVersion, scope, installedScope) is { } refused)
                return new InstallerReport(false, false, refused, null);
            if (installedScope is { } known) scope = known;
        }
```

- 新增私有方法 `InstalledScope(CatalogPackage package)`：用 `package.InstalledVersion.GetMetadata(PackageVersionMetadataField.InstalledScope)` 读出字符串，`"User"` → `InstallScopeHint.User`，`"Machine"` → `InstallScopeHint.Machine`，其他（空、`"Unknown"`）→ null。先在 winmd 里确认 `PackageVersionMetadataField.InstalledScope` 这个成员确实存在、返回值的写法（大小写）；不存在就让方法返回 null 并在报告里说明。
- `scope` 是方法参数，需要能重新赋值（C# 参数本来就可以赋值）。
- 文件头加 `using UpdateHelper.Core.Agent;`。

`src/UpdateHelper.Agent/Program.cs`：`--serve` 里改为 `new WingetInstaller(agentMode: true)`。App 侧（普通权限、用户每次确认）保持 `new WingetInstaller()`。

- [ ] **Step 4: 测试并提交**

Run: `dotnet test` → Expected: 全部通过；`dotnet build -c Debug` 0 警告

```bash
git add src tests
git commit -m "feat(agent): 后台助手只升不降，安装范围跟随已安装的软件"
```

---

### Task 11: 等待提权进程有上限，发布实测，文档

**Files:**
- Modify: `src/UpdateHelper.App/AgentController.cs`
- Modify: `CHANGELOG.md`、`docs/superpowers/specs/2026-09-30-update-helper-design.md`

- [ ] **Step 1: 等待上限**

`AgentController.RunElevatedAsync` 里 `p.WaitForExit();` 改为：

```csharp
            // 复制约 200 MB 到 Program Files 一般几十秒；给足 10 分钟，超时就告诉用户，不让按钮一直转
            if (!p.WaitForExit(TimeSpan.FromMinutes(10)))
                return "后台助手 10 分钟内没有完成，请稍后在设置里查看状态，必要时重新启用";
```

- [ ] **Step 2: 发布实测（自包含 + 依赖框架）**

在仓库外的临时目录（scratchpad）分别执行：

```powershell
dotnet publish src/UpdateHelper.App -c Release -r win-x64 --self-contained true -o <tmp>\sc --nologo -v q
dotnet publish src/UpdateHelper.App -c Release -r win-x64 --self-contained false -o <tmp>\fd --nologo -v q
```

两个目录都要满足：有 `UpdateHelper.exe`、`UpdateHelper.Agent.exe`、`UpdateHelper.AgentLauncher.exe`；没有 `UpdateHelper.AgentLauncher.dll`、`launcher-tmp`；运行启动器后能拉起 `UpdateHelper.Agent.exe --serve`（普通权限，Ping 回复 `Elevated=false`），结束进程；`UpdateHelper.Agent.exe --install --user-sid S-1-5-18` 退出码 2。用完删掉临时目录。

- [ ] **Step 3: 文档**

`CHANGELOG.md` 的 0.2.0 一节"新增"末尾加一条：

```markdown
- 后台助手加固：计划任务运行的是原生编译的启动器，它只把系统级的环境变量传给后台助手，不继承用户的环境变量；后台助手只升级到比已安装更新的版本，安装范围跟随已安装的软件
```

spec 第 17 节末尾加：

```markdown
### 计划 7 补充（2026-10-08）已解决
- 提权进程不再继承用户的环境变量：计划任务运行 Native AOT 编译的启动器，只传白名单里的系统变量，Path 只取系统级的值；后台助手同时关掉 .NET 启动钩子
- 后台助手只升不降、安装范围跟随已安装的软件
- 规则库安装包仍不经过后台助手（"校验与运行之间的文件替换""exe 安装包 silentArgs"两条在规则库接入后台助手时再处理）
```

- [ ] **Step 4: 全部测试并提交**

Run: `dotnet test` → Expected: 全部通过

```bash
git add src CHANGELOG.md docs/superpowers/specs/2026-09-30-update-helper-design.md
git commit -m "fix(app): 启用免确认更新最多等 10 分钟；文档记录后台助手加固"
```

---

## 计划结束后

- 整个分支（计划 7 + 本补充）交给全新的审查员（最强模型）再做一次整体审查，重点核对 Critical 是否已关闭
- 审查通过后请用户做实机测试（启用 → 更新一个软件不弹窗 → 重开仍已启用 → 关闭），再合并、推送、发 0.2.0
