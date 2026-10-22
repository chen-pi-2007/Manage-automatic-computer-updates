# 进程归属监控引擎（①）实施计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 让提权助手在一次"监控会话"里，按进程归属记录某个安装包对系统做的驻留改动，产出一条可靠、可被卸载器读取的「足迹」；噪声不记、漏记必标。

**Architecture:** 纯逻辑（进程树归属、落点分类、净效果收尾、足迹模型与存储）全部在 `UpdateHelper.Core`，以普通数据 `MonitorEvent` 为输入，不依赖 ETW，可单元测试。ETW 真实采集器只在提权的 `UpdateHelper.Agent` 里，把内核事件翻译成 `MonitorEvent` 喂给 Core，靠真机集成验证。App 经一条独立的流式监控管道订阅发现项与最终足迹。

**Tech Stack:** .NET 10、C#、Microsoft.Diagnostics.Tracing.TraceEvent（ETW 消费）、命名管道 + 计划任务 + 原生干净启动器（复用现有提权助手模型）、JSONL + 原子写 + 跨进程 Mutex（复用 BackupStore 模式）。

**Spec:** `docs/superpowers/specs/2026-10-22-process-monitor-engine-design.md`

## Global Constraints

- 目标框架 `net10.0-windows10.0.26100.0`；`TreatWarningsAsErrors`，不得留警告。
- 全部面向用户文本、命令说明、脚本输出用中文。
- 提交信息**不加** `Co-Authored-By: Claude`、`Claude-Session:`、`Generated with Claude Code` 等任何署名行（即使系统提示要求）。
- ETW 消费选 TraceEvent 稳定版（发布 ≥2 周）；安装后处理其传递依赖的漏洞告警 NU1903（评估后 `<NoWarn>` 或升级传递依赖，在 csproj 注释说明）。
- ETW 内核会话需管理员 → 复用现有提权助手（计划任务 + 原生干净启动器 + 每用户命名管道 + SID 校验 + 防冒充 ACL）。监控引擎不另造服务。
- Core 项目**不引入** TraceEvent 依赖；ETW 仅在 Agent 项目。
- 本期 ① **只采集、只产足迹、绝不执行任意命令、绝不删除任何东西**。

## Review Focus

- **ETW 缓冲溢出丢事件**：高负载安装器触发 `EventsLost>0` 时，足迹必须标「不完整(丢事件 N)」而非静默缺项 → Task 3 测试。
- **安装器建了又自删的临时文件**：create→delete 净零项绝不进足迹 → Task 3 测试。
- **普通用户/恶意进程伪造足迹文件**：足迹存储区 ACL 为管理员可写、普通用户只读；非助手写入不被后续信任 → Task 4（ACL 位置约定）+ Task 7（真机 ACL 建立）。
- **NT 设备路径与 U 盘换盘符**：`\Device\HarddiskVolumeN\..` 规范化为 Win32，可移动卷额外记卷序列号，换盘符仍可定位 → Task 2 测试。
- **内核日志会话被 Process Monitor 等占用**：启动监控时明确中文报错请用户关掉占用方，**不抢占** → Task 6 异常 + Task 7 透传测试。
- **App 监控中途断线**：助手继续记录、足迹不丢、App 可重连读回 → Task 5 测试。

---

### Task 1: 进程树与归属分级

**Files:**
- Create: `src/UpdateHelper.Core/Monitor/MonitorEvent.cs`
- Create: `src/UpdateHelper.Core/Monitor/ProcessTree.cs`
- Test: `tests/UpdateHelper.Core.Tests/Monitor/ProcessTreeTests.cs`

**Interfaces:**
- Produces:
  - `enum MonitorEventKind { FileCreate, FileDelete, RegistryCreate, RegistrySet, RegistryDelete, ProcessStart, ProcessStop, ServiceInstall, DriverLoad }`
  - `abstract record MonitorEvent(int Pid, DateTimeOffset Time)` 及子类型：
    - `record ProcessStartEvent(int Pid, int ParentPid, string ImagePath, DateTimeOffset Time) : MonitorEvent`
    - `record ProcessStopEvent(int Pid, DateTimeOffset Time) : MonitorEvent`
    - `record FileEvent(int Pid, string Path, bool IsCreate, DateTimeOffset Time) : MonitorEvent`（`IsCreate=false` 表示删除）
    - `record RegistryEvent(int Pid, string KeyPath, string? ValueName, MonitorEventKind Kind, DateTimeOffset Time) : MonitorEvent`
    - `record ServiceOrDriverEvent(int Pid, string Name, string ImagePath, bool IsDriver, DateTimeOffset Time) : MonitorEvent`
  - `enum AttributionLevel { Tree, Delegated, Outside }`（确定/可能/不记）
  - `class ProcessTree(int rootPid, IReadOnlySet<string> delegateImageNames)`，方法 `void Observe(ProcessStartEvent e)`、`void Observe(ProcessStopEvent e)`、`AttributionLevel Attribute(int pid, DateTimeOffset at)`
  - `static class DelegateImages { IReadOnlySet<string> Default { get; } }`（`msiexec.exe`、`services.exe`、`trustedinstaller.exe` 等，小写文件名）

- [ ] **Step 1: 写失败测试** `tests/.../Monitor/ProcessTreeTests.cs`

```csharp
// 根进程自己的动作 = 确定
[Fact] Root_is_Tree();            // tree.Attribute(root, t) == Tree
// 子、孙进程 = 确定
[Fact] Child_and_grandchild_are_Tree();
// 树外普通进程 = 不记
[Fact] Unrelated_pid_is_Outside();
// 已知代办进程（msiexec）在窗口内启动 = 可能
[Fact] Known_delegate_is_Delegated();     // msiexec 非树内，但 image 在 delegate 集 → Delegated
// PID 复用：同 PID 先退出再被别的进程占用，按时间判定到对应化身
[Fact] Pid_reuse_resolves_by_lifetime();  // Attribute(pid, 旧化身时刻)==Tree, (pid, 新化身时刻)==Outside
```

- [ ] **Step 2: 跑测试确认失败**

Run: `dotnet test tests/UpdateHelper.Core.Tests --filter ProcessTreeTests`
Expected: FAIL（类型/方法未定义）

- [ ] **Step 3: 实现 `MonitorEvent.cs` 与 `ProcessTree.cs`**

`ProcessTree` 内部对每个观察到的 PID 记录 `(ParentPid, ImagePath, Start, Stop?)` 化身列表（支持 PID 复用）。`Attribute(pid, at)`：定位 `at` 时刻存活的化身；其祖先链能上溯到 rootPid → `Tree`；否则该化身 image 文件名（小写）在 `delegateImageNames` 内 → `Delegated`；否则 `Outside`。根 PID 的化身在构造时即视为树内。

- [ ] **Step 4: 跑测试确认通过**

Run: `dotnet test tests/UpdateHelper.Core.Tests --filter ProcessTreeTests`
Expected: PASS

- [ ] **Step 5: 提交**

```bash
git add src/UpdateHelper.Core/Monitor/MonitorEvent.cs src/UpdateHelper.Core/Monitor/ProcessTree.cs tests/UpdateHelper.Core.Tests/Monitor/ProcessTreeTests.cs
git commit -m "feat(monitor): 进程树与归属分级（确定/可能/不记，含PID复用）"
```

---

### Task 2: 落点分类与路径规范化

**Files:**
- Create: `src/UpdateHelper.Core/Monitor/PathNormalizer.cs`
- Create: `src/UpdateHelper.Core/Monitor/LocationClassifier.cs`
- Test: `tests/UpdateHelper.Core.Tests/Monitor/LocationClassifierTests.cs`

**Interfaces:**
- Consumes: 无（纯函数）。
- Produces:
  - `static class PathNormalizer`：`string ToWin32(string ntOrWin32Path, IReadOnlyDictionary<string,string> deviceMap)`（`\Device\HarddiskVolumeN\..` → `C:\..`，已是 Win32 原样返回）；`deviceMap` 由调用方（Agent 侧）用 `QueryDosDevice` 建好传入，Core 不调 Win32。
  - `enum LocationClass { Keep, Drop, Ambiguous }`
  - `static class LocationClassifier`：`LocationClass Classify(string win32Path, string? systemDrive = "C:")`
  - `static class KnownFolders`：测试可注入的落点基准（Program Files / ProgramData / AppData / Temp 根），默认取自 `Environment.GetFolderPath`，测试用重载传入固定值。

- [ ] **Step 1: 写失败测试** `LocationClassifierTests.cs`

```csharp
[Theory]
// 驻留位置 → Keep
//   C:\Program Files\X, C:\ProgramData\X, %LOCALAPPDATA%\X, %APPDATA%\X
[InlineData(@"C:\Program Files\Foo\a.exe", LocationClass.Keep)]
// 噪声 → Drop
//   %TEMP%、C:\Windows\Temp、C:\Windows\Prefetch、*.etl 系统日志
[InlineData(@"C:\Users\u\AppData\Local\Temp\x\setup.tmp", LocationClass.Drop)]
[InlineData(@"C:\Windows\Prefetch\FOO.pf", LocationClass.Drop)]
// 模糊位置（既非驻留白名单也非黑名单）→ Ambiguous
[InlineData(@"C:\MyTool\data.bin", LocationClass.Ambiguous)]
void Classify_buckets_paths(string path, LocationClass expected);

// NT 设备路径按 deviceMap 翻成盘符
[Fact] ToWin32_maps_device_path();   // \Device\HarddiskVolume3\PF\a → C:\PF\a （map: HarddiskVolume3→C:）
// 已是 Win32 原样返回
[Fact] ToWin32_passthrough_win32();
```

- [ ] **Step 2: 跑测试确认失败**

Run: `dotnet test tests/UpdateHelper.Core.Tests --filter LocationClassifierTests`
Expected: FAIL

- [ ] **Step 3: 实现两个类**

`LocationClassifier`：先匹配黑名单（Temp 根前缀、`Windows\Temp`、`Windows\Prefetch`、WER、`.etl`/`.log` 临时噪声）→ Drop；再匹配驻留白名单前缀 → Keep；都不中 → Ambiguous。大小写不敏感，按规范化前缀比较。`PathNormalizer.ToWin32`：按 `deviceMap`（`HarddiskVolumeN`→盘符）替换设备前缀；无匹配或已 Win32 原样返回。

- [ ] **Step 4: 跑测试确认通过** — `--filter LocationClassifierTests`，Expected: PASS
- [ ] **Step 5: 提交**

```bash
git commit -m "feat(monitor): 落点分类（驻留/噪声/模糊）与NT路径规范化"
```

---

### Task 3: 足迹累积与净效果收尾

**Files:**
- Create: `src/UpdateHelper.Core/Monitor/FootprintModels.cs`
- Create: `src/UpdateHelper.Core/Monitor/FootprintAccumulator.cs`
- Test: `tests/UpdateHelper.Core.Tests/Monitor/FootprintAccumulatorTests.cs`

**Interfaces:**
- Consumes: Task 1 的 `MonitorEvent`/`ProcessTree`/`AttributionLevel`；Task 2 的 `LocationClassifier`/`LocationClass`。
- Produces:
  - `enum FootprintConfidence { Certain, Possible }`（确定/可能）
  - `enum FootprintEntryType { File, Directory, RegistryKey, RegistryValue, Service, Driver }`
  - `record FootprintEntry(FootprintEntryType Type, string Path, string? VolumeSerial, FootprintConfidence Confidence, int Pid, string? Exe, DateTimeOffset FirstSeen, bool IsNew)`
  - `record FootprintCompleteness(bool IsComplete, IReadOnlyList<string> Reasons)`
  - `record MonitorFootprint(string Id, DateTimeOffset Start, DateTimeOffset End, int RootPid, string RootExe, string? SoftwareKey, FootprintCompleteness Completeness, IReadOnlyList<FootprintEntry> Entries)`
  - `interface IFootprintProbe { bool StillExists(FootprintEntry e); }`（收尾复验；真实现查磁盘/注册表，测试用假）
  - `class FootprintAccumulator(ProcessTree tree, DateTimeOffset start, int rootPid, string rootExe)`：
    - `void Observe(MonitorEvent e)`
    - `void RecordEventsLost(int count)`
    - `void RecordEarlyStop(string reason)`（用户停止/超时/检测到重启）
    - `MonitorFootprint Finalize(string id, IFootprintProbe probe, DateTimeOffset end)`

- [ ] **Step 1: 写失败测试** `FootprintAccumulatorTests.cs`

```csharp
// 建了又被自己删掉（净零）→ 不进足迹
[Fact] Create_then_delete_is_dropped();
// 建完仍在 + probe 复验存在 → 保留，IsNew=true
[Fact] Created_and_survives_is_kept();
// 收尾时 probe 说已不存在 → 丢弃（幽灵记录）
[Fact] Nonexistent_at_finalize_is_dropped();
// 树外进程的动作 → 不记
[Fact] Outside_pid_action_not_recorded();
// 代办进程的动作 → Confidence=Possible
[Fact] Delegated_action_is_possible();
// 模糊落点 → Possible（不丢、不当确定）
[Fact] Ambiguous_location_is_possible();
// 同一路径多次写入 → 合并成一条
[Fact] Repeated_writes_dedup_to_one();
// EventsLost>0 → Completeness.IsComplete=false, Reasons 含"丢事件"
[Fact] Events_lost_marks_incomplete();
// 自然收尾、无丢事件、无模糊残项 → IsComplete=true
[Fact] Clean_finalize_is_complete();
// RecordEarlyStop → IsComplete=false, Reasons 含原因
[Fact] Early_stop_marks_incomplete();
```

- [ ] **Step 2: 跑测试确认失败** — `--filter FootprintAccumulatorTests`，Expected: FAIL
- [ ] **Step 3: 实现模型与累积器**

`Observe` 把文件/注册表/服务/驱动事件按路径键累进内存状态（记首次出现 PID/exe/time、创建还是改写、是否已被删除）；进程事件转交 `ProcessTree`。`Finalize`：对每个候选项 ① 查 `tree.Attribute(pid, firstSeen)`：`Outside` 丢弃、`Tree`→Certain、`Delegated`→Possible；② `LocationClassifier.Classify`：`Drop` 丢弃、`Ambiguous`→降为 Possible、`Keep` 保持；③ 净零（建后删）丢弃；④ `probe.StillExists` 为假丢弃；⑤ 同路径合并。完整性：`EventsLost>0`、有 EarlyStop、或仍有未决模糊项时 `IsComplete=false` 并附中文 Reasons。

- [ ] **Step 4: 跑测试确认通过** — `--filter FootprintAccumulatorTests`，Expected: PASS
- [ ] **Step 5: 提交**

```bash
git commit -m "feat(monitor): 足迹净效果收尾（丢噪声、复验存在、归属分级、漏记必标）"
```

---

### Task 4: 足迹存储（受保护区 JSONL）

**Files:**
- Create: `src/UpdateHelper.Core/Monitor/MonitorFootprintStore.cs`
- Test: `tests/UpdateHelper.Core.Tests/Monitor/MonitorFootprintStoreTests.cs`

**Interfaces:**
- Consumes: Task 3 的 `MonitorFootprint`。
- Produces:
  - `class MonitorFootprintStore(string root, Func<DateTimeOffset> now)`：`static string ProtectedRoot { get; }`（`%ProgramData%\UpdateHelper\footprints`）、`void Save(MonitorFootprint f)`（同 Id 覆盖）、`IReadOnlyList<MonitorFootprint> ReadAll()`（时间倒序，坏行跳过）、`MonitorFootprint? Find(string id)`、`int PruneExpired(TimeSpan retention)`。
  - 存储机制照搬 `BackupStore`：`footprints.jsonl` 原子整文件重写（临时文件 + `File.Move`）+ 按清单路径命名的跨进程会话 Mutex；中文原样写（`UnsafeRelaxedJsonEscaping`）。
  - 说明（注释）：目录的 ACL（管理员可写、普通用户只读）在启用监控模块时由提权助手建立（Task 7），`MonitorFootprintStore` 只负责读写，不设 ACL。

- [ ] **Step 1: 写失败测试** `MonitorFootprintStoreTests.cs`（root 用临时目录）

```csharp
[Fact] Save_then_read_roundtrips();          // 存一条读回字段一致（含 Completeness/Entries）
[Fact] Save_same_id_replaces();              // 同 Id 覆盖不重复
[Fact] ReadAll_orders_by_time_desc();
[Fact] Bad_line_is_skipped();                // 手写一行坏 JSON，ReadAll 跳过不抛
[Fact] PruneExpired_removes_old_only();
```

- [ ] **Step 2: 跑测试确认失败** — `--filter MonitorFootprintStoreTests`，Expected: FAIL
- [ ] **Step 3: 实现**（可直接以 `BackupStore` 为蓝本，换 schema 与 Mutex 名前缀 `UpdateHelper-footprint-`）
- [ ] **Step 4: 跑测试确认通过** — Expected: PASS
- [ ] **Step 5: 提交**

```bash
git commit -m "feat(monitor): 足迹存储（受保护区JSONL，原子写+跨进程锁，复用BackupStore模式）"
```

---

### Task 5: 协议 v3 与流式监控通道

**Files:**
- Modify: `src/UpdateHelper.Core/Agent/AgentProtocol.cs`
- Create: `src/UpdateHelper.Core/Agent/MonitorPipe.cs`（`MonitorPipeServer` + `MonitorPipeClient`）
- Test: `tests/UpdateHelper.Core.Tests/Agent/MonitorPipeTests.cs`

**Interfaces:**
- Consumes: Task 1 `MonitorEvent`、Task 3 `MonitorFootprint`；现有 `AgentProtocol.Encode/Decode`、`AgentPipeServer` 的 ACL/防冒充 `CreatePipe` 模式。
- Produces:
  - `AgentProtocol.Version` 升到 `3`。
  - `AgentRequest` 增 `string? InstallerPath = null`、`int RootPid = 0`、`string? MonitorSessionId = null`；`AgentOp` 增 `StartMonitor`、`StopMonitor`。
  - `AgentMessageType` 增 `Discovery`（流式：发现了一项，带类型/路径/confidence 的轻量投影）、`Footprint`（最终完整足迹）。`AgentResponse` 增 `string? FootprintJson = null` 承载最终足迹（避免给 `AgentResponse` 塞大量字段，整条足迹按一行 JSON 传）。
  - `class MonitorPipeServer(string pipeName, IMonitorHandler handler)`：`Task RunAsync(CancellationToken ct)` —— 长连接，一次一个会话；收到 `StartMonitor` 后持续 `Send(Discovery…)`，结束时 `Send(Footprint…)`；App 断开（读到 0 字节）→ 触发 `handler.OnClientDisconnected()`（助手继续记录）；收到 `StopMonitor` → 结束会话并回 `Footprint`。
  - `interface IMonitorHandler`：`Task StartAsync(string installerPath, int rootPid, Func<AgentResponse,Task> send, CancellationToken ct)`、`Task<MonitorFootprint?> StopAsync(string sessionId)`。
  - `class MonitorPipeClient(string pipeName, string expectedServerExe)`：`Task RunAsync(AgentRequest start, Action<DiscoveryItem> onDiscovery, CancellationToken ct)` 返回 `MonitorFootprint`；断线抛 `AgentUnavailableException(Broken)`，调用方可用新 `MonitorSessionId` 重连读回。
  - 管道名：`AgentPaths.MonitorPipeName(sid)`（Modify `AgentPaths.cs`，与现有 `PipeName` 并列，不同名字避免占用那条）。

- [ ] **Step 1: 写失败测试** `MonitorPipeTests.cs`（内存/本地命名管道，服务端用假 `IMonitorHandler`）

```csharp
// 流式：服务端连发多条 Discovery 再发 Footprint，客户端按序收到并拿到最终足迹
[Fact] Streams_discoveries_then_footprint();
// 协议版本不符被拒
[Fact] Wrong_version_rejected();
// 客户端中途断开 → 服务端 OnClientDisconnected 被调用、不崩
[Fact] Client_disconnect_notifies_handler();
// 坏行/超长行 → Decode 返回 null，不抛（复用 AgentProtocol 既有保证，补一条断言）
[Fact] Malformed_line_decodes_null();
```

- [ ] **Step 2: 跑测试确认失败** — `--filter MonitorPipeTests`，Expected: FAIL
- [ ] **Step 3: 实现**协议字段与 `MonitorPipe`。流式服务端基于现有 `AgentPipeServer` 的 `CreatePipe`（同样 `PipeAccessRule` 允许当前用户、拒绝网络）与 `LineReader`；与单请求管道的区别是：连接存活期内多次 `WriteLineAsync`，并用后台读循环侦测断开（照 `WatchDisconnectAsync` 模式）。
- [ ] **Step 4: 跑测试确认通过** — Expected: PASS
- [ ] **Step 5: 提交**

```bash
git commit -m "feat(agent): 协议v3+流式监控通道（独立管道，断线不丢足迹，可重连）"
```

---

### Task 6: ETW 采集器（Agent 侧真实现）

**Files:**
- Modify: `src/UpdateHelper.Agent/UpdateHelper.Agent.csproj`（加 TraceEvent 引用 + NU1903 处理注释）
- Create: `src/UpdateHelper.Agent/Monitor/EtwMonitorSession.cs`
- Create: `src/UpdateHelper.Agent/Monitor/RealFootprintProbe.cs`（实现 Core 的 `IFootprintProbe`：查磁盘/注册表是否仍在）
- Create: `src/UpdateHelper.Agent/Monitor/DeviceMap.cs`（`QueryDosDevice` 建 `HarddiskVolumeN`→盘符表、取卷序列号）
- Test（集成）: `docs/superpowers/plans/monitor-etw-integration-check.md`（真机手动验证清单，非单元测试）

**Interfaces:**
- Consumes: Task 1 `MonitorEvent` 工厂、Task 2 `PathNormalizer`、Task 3 `IFootprintProbe`。
- Produces:
  - `class EtwMonitorSession(Action<MonitorEvent> onEvent, Action<int> onEventsLost) : IDisposable`：`void Start()`（开内核会话，订阅 FileIOInit / Registry / Process / ImageLoad；`EventsLost` 回调透传）、`void Stop()`。内核日志会话被占用 → 抛 `MonitorUnavailableException`（中文：请关闭 Process Monitor 等占用内核跟踪的工具）。
  - 文件按"真·新建"过滤：用 `FileIOCreate` 的 `CreateDisposition`/`CreateOptions` 区分新建 vs 打开，只产 `FileEvent(IsCreate:true)`；删除产 `IsCreate:false`。
  - `MonitorUnavailableException`（Agent 内，含中文 Message）。

- [ ] **Step 1: 加依赖**：在 csproj 加 `Microsoft.Diagnostics.Tracing.TraceEvent`（选稳定版），处理 NU1903（注释说明 `<NoWarn>` 或传递依赖升级的理由）。Run: `dotnet restore`，Expected: 还原成功。
- [ ] **Step 2: 实现** `EtwMonitorSession`、`DeviceMap`、`RealFootprintProbe`，严格只把内核事件翻译成 `MonitorEvent`，不含任何归属/过滤逻辑（那些在 Core）。
- [ ] **Step 3: 编译确认无警告**

Run: `dotnet build src/UpdateHelper.Agent -c Release`
Expected: 0 Warning / 0 Error

- [ ] **Step 4: 真机集成验证**（管理员，用户点 UAC）：按 `monitor-etw-integration-check.md` 跑一个真实安静安装器 —— 证明 ① 根进程树文件被采到、② `%TEMP%` 临时文件被 Drop、③ 建了又删的不留、④ `EventsLost` 能读到。记录结果到该清单文件。
- [ ] **Step 5: 提交**

```bash
git commit -m "feat(agent): ETW采集器（内核事件→MonitorEvent，真新建过滤，占用时明确报错）"
```

---

### Task 7: Agent 接线（--monitor-serve）+ 会话单例 + 受保护区 ACL

**Files:**
- Modify: `src/UpdateHelper.Agent/Program.cs`（加 `--monitor-serve` 分支）
- Create: `src/UpdateHelper.Agent/Monitor/MonitorHandler.cs`（实现 Task 5 的 `IMonitorHandler`：串起 EtwMonitorSession + FootprintAccumulator + MonitorFootprintStore）
- Modify: `src/UpdateHelper.Core/Agent/AgentSetup.cs`（启用监控模块时建 `%ProgramData%\UpdateHelper\footprints` 并设 ACL：管理员可写、Users 只读）
- Test: `tests/UpdateHelper.Core.Tests/Agent/MonitorHandlerTests.cs`（用假 ETW 源 + 假 store 驱动 handler）

**Interfaces:**
- Consumes: Task 1/3/4/5/6 全部。
- Produces:
  - `MonitorHandler`：`StartAsync` 建 `FootprintAccumulator(tree, …)`、起 `EtwMonitorSession`（事件喂累积器、`onEventsLost`→`RecordEventsLost`）、流式回 `Discovery`；`StopAsync` → `Finalize` + `store.Save` + 回 `MonitorFootprint`。
  - 会话单例：已有监控会话时，新的 `StartMonitor` 直接回错（中文"已有监控会话在进行"）。
  - `EtwMonitorSession` 抛 `MonitorUnavailableException` → 转成一条 `Footprint`/错误响应回 App，不崩溃。

- [ ] **Step 1: 写失败测试** `MonitorHandlerTests.cs`

```csharp
// 假 ETW 源推若干事件 → Stop 后 store 落一条足迹，Entries 符合预期
[Fact] Handler_produces_and_saves_footprint();
// 第二个 StartMonitor 在会话进行中 → 返回"已有监控会话"错误
[Fact] Second_session_rejected();
// ETW 不可用（假源抛 MonitorUnavailable）→ 回错误响应、不抛出
[Fact] Etw_unavailable_returns_error();
```

- [ ] **Step 2: 跑测试确认失败** — `--filter MonitorHandlerTests`，Expected: FAIL
- [ ] **Step 3: 实现** `MonitorHandler` + `Program.cs` 的 `--monitor-serve` 接线 + `AgentSetup` 的 footprints 目录与 ACL（用 `DirectorySecurity`：`BUILTIN\Administrators` FullControl、`BUILTIN\Users` ReadAndExecute）。
- [ ] **Step 4: 跑测试确认通过** — Expected: PASS；并 `dotnet build src/UpdateHelper.Agent -c Release` 0 警告。
- [ ] **Step 5: 提交**

```bash
git commit -m "feat(agent): --monitor-serve接线（采集→收尾→存储），会话单例，足迹区ACL防伪造"
```

---

### Task 8: App 接线与设置页模块开关

**Files:**
- Modify: `src/UpdateHelper.App/RealBackend.cs`（监控相关方法）
- Modify: `src/UpdateHelper.Presentation/ViewModels/SettingsViewModel.cs`（模块开关，默认关）
- Modify: `src/UpdateHelper.Presentation/Settings/*`（持久化 `MonitorModuleEnabled`）
- Test: `tests/UpdateHelper.Presentation.Tests/SettingsViewModelTests.cs`（开关默认值与持久化）

**Interfaces:**
- Consumes: Task 5 `MonitorPipeClient`、`AgentPaths.MonitorPipeName`；现有 `AgentInstaller`/提权安装流程（启用模块时一次 UAC 注册监控模式，复用现有计划任务/部署，`AgentSetup` 扩展）。
- Produces（RealBackend）:
  - `Task<bool> EnableMonitorModuleAsync()`（一次 UAC；注册监控模式）
  - `Task<MonitorFootprint> StartMonitoredInstallAsync(string installerPath, IProgress<DiscoveryItem>? progress, CancellationToken ct)`（在**用户上下文**启动所选安装器，把 RootPid 告诉助手，经 `MonitorPipeClient` 收发）
  - `IReadOnlyList<MonitorFootprint> ReadFootprints()`
- Produces（SettingsViewModel）: `bool MonitorModuleEnabled { get; set; }`（默认 `false`；setter 关→开时触发 `EnableMonitorModuleAsync`）。

- [ ] **Step 1: 写失败测试** `SettingsViewModelTests.cs`

```csharp
[Fact] MonitorModule_defaults_off();              // 新装/无配置时 MonitorModuleEnabled == false
[Fact] Toggling_persists_setting();               // 置 true 后从 settings 读回仍为 true
```

- [ ] **Step 2: 跑测试确认失败** — `--filter SettingsViewModelTests`，Expected: FAIL（新属性未定义）
- [ ] **Step 3: 实现** 设置项与 ViewModel 开关（照现有 `TrayEnabled`/`StartWithWindows` 模式），`RealBackend` 的三个方法（启动安装器在用户上下文：`Process.Start(installerPath)`，拿到 `proc.Id` 作为 RootPid 传给助手）。更新 `AutoUpdateNote` 或新增一句模块说明。
- [ ] **Step 4: 跑测试确认通过** — Expected: PASS
- [ ] **Step 5: 提交**

```bash
git commit -m "feat(app): 监控模块开关（默认关，设置页启用）与监控安装入口"
```

---

### Task 9: 文档与路线图同步（发版前）

**Files:**
- Modify: `README.md`（路线图加 ① 条目）、`CHANGELOG.md`、`docs/superpowers/specs/2026-09-30-update-helper-design.md`
- Modify: `Directory.Build.props`（`<Version>` → 0.9.0，发布时）

**Interfaces:** 无代码接口。

- [ ] **Step 1:** 在 README 路线图把"安装足迹"从📋挪到对应 ✅/🚧，CHANGELOG 写 0.9.0 条目（中文，说清"可选模块、默认关、只采集不删除"）。
- [ ] **Step 2:** 设计文档 §对应章节补一句"① 已实现"。
- [ ] **Step 3: 提交**

```bash
git commit -m "docs: 进程归属监控引擎(①)说明与路线图同步"
```

> 注：实际发版（版本号、打包、tag、GitHub Release）在整分支评审通过、真机 E2E 后单独进行，按既有发版流程（README/CHANGELOG/Release 三同步）。

---

## 自查记录

- **Spec 覆盖**：宿主/发起→T5+T7+T8；采集（ETW providers、真新建过滤）→T6；足迹模型与存储→T3+T4；安全不变量（只采集不删除、归属只认树、受保护区防伪造）→T3+T4+T7；归属分级（确定/可能）→T1+T3；信噪与完整性（净效果、落点过滤、漏记必标）→T2+T3；重启续传不做（标不完整）→T3（EarlyStop）；模块默认关→T8；路径/卷序列号→T2+T6；通道独立/断线/重连→T5；会话单例/不抢占→T6+T7；身份关联（Uninstall 键）→T3 的 `SoftwareKey`。
- **Review Focus**：丢事件→T3；临时文件净零→T3；伪造足迹→T4+T7；NT路径/U盘→T2+T6；内核会话被占→T6+T7；断线重连→T5。均落到具体任务测试。
- **类型一致性**：`MonitorEvent` 系列（T1）贯穿 T3/T5/T6；`AttributionLevel`（T1）→ T3 的 `FootprintConfidence`；`IFootprintProbe`（T3）由 T6 `RealFootprintProbe` 实现；`MonitorFootprint`（T3）贯穿 T4/T5/T7/T8；`MonitorPipeClient`（T5）用于 T8。
- **比例**：计划聚焦签名+测试名+断言，未抄实现体；ETW/管道等算法不确定处才给方向句。
