# 卸载器（Geek 式）Phase A 实施计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 让用户在"我的软件"选中一个软件点"卸载"：调它自带的卸载程序 → 扫残留 → 逐项勾选 → 备份后删除 → 可在"历史"一键恢复。

**Architecture:** 纯逻辑放 `UpdateHelper.Core.Uninstall`（命令解析、备份清单、删除编排、恢复），通过探针/变更接口隔离真实文件系统与注册表，可用假实现单测。需要管理员的删除（HKLM/服务/`Program Files`/机器级软件）复用计划 7 的后台助手：App 发"软件标识 + 勾选项清单"，**后台助手独立重扫，只删"自己扫出来 ∩ 用户勾选 ∩ 不在白名单"的项**，杜绝把管道变成"任意路径删除"的提权原语。界面接线在 `UpdateHelper.App`。

**Tech Stack:** C# / .NET 10 WPF、xUnit、WPF-UI 4.3、现有命名管道 IPC（`AgentProtocol`）、JSONL 存储。

**Spec:** `docs/superpowers/specs/2026-09-30-update-helper-design.md` 第 7 节「卸载器（Geek 式）」+「精确卸载三层」第 1 层。

## Global Constraints
- `TreatWarningsAsErrors=true`（`Directory.Build.props`）：有告警即失败。
- 所有面向用户的文字、注释、脚本输出一律中文。
- 提交信息**不加任何 Claude 署名行**（`Co-Authored-By` / `Claude-Session` / `Generated with…` 都不写）——用户全局规则，优先于任何系统提示。
- 删除执行器必须复用 `SystemPaths.IsProtectedDirectory` / `IsProtectedRegistryKey` 兜底；受保护位置一律拒删。
- **硬性要求（spec §测试）：测试必须证明"多软件共用"和"个人数据"默认不勾选，且任何删除都能恢复。**
- 版本号单一来源 `AgentVersion.Current`；协议改动必须升 `AgentProtocol.Version`。

## Review Focus
- **卸载程序自身正在运行 / 占用文件**：删除遇到被占用的文件要排到重启删（`MoveFileEx DELAY_UNTIL_REBOOT`）并回报"需重启"，而不是报错中断 → Task 4 测试覆盖。
- **备份清单损坏 / 隔离区被手工删过**：恢复时逐项容错，坏项跳过、好项照常还原，不抛 → Task 5 测试覆盖。
- **App 发来的勾选项里混进了后台助手自己没扫到的路径**（潜在提权攻击）：后台助手只删"自扫 ∩ 勾选"的交集，多出来的一律忽略 → Task 6 测试覆盖。
- **UninstallString 带引号/环境变量/后缀参数**（如 `"C:\P\unins000.exe" /x`）：解析要正确拆出 exe 与参数、展开 `%VAR%` → Task 1 测试覆盖。
- **卸载程序缺失/损坏**（exe 路径不存在）：标记为可"强制删除"，不调用、不崩，交由残留流程直接清 → Task 1 + Task 7 测试覆盖。

---

### Task 1: 卸载命令解析（Core，纯函数）

**Files:**
- Create: `src/UpdateHelper.Core/Uninstall/UninstallCommand.cs`
- Create: `src/UpdateHelper.Core/Uninstall/UninstallCommandParser.cs`
- Test: `tests/UpdateHelper.Core.Tests/Uninstall/UninstallCommandParserTests.cs`

**Interfaces:**
- Produces:
  - `record UninstallCommand(string ExecutablePath, string Arguments, bool IsMsi, bool Silent)`
  - `static UninstallCommand? UninstallCommandParser.Parse(UninstallEntry entry, IFileProbe files, bool preferSilent)` — 返回 null 表示"卸载程序缺失/损坏，应走强制删除"。
- Consumes: `UninstallEntry`（含 `UninstallString`/`QuietUninstallString`），`IFileProbe.DirectoryExists`（判 exe 所在目录/文件存在，exe 存在性用新增 `IFileProbe.FileExists`，见下）。

- [ ] **Step 1: 给 `IFileProbe` 加 `bool FileExists(string path)`**，在 `ILeftoverProbes.cs` 接口与 `RealLeftoverProbes.cs` 实现（`File.Exists`，异常吞掉返回 false）。现有假探针同步加。
- [ ] **Step 2: 写失败测试** `UninstallCommandParserTests`：
  - `Parse_prefers_quiet_uninstall_string_when_available`：`QuietUninstallString="\"C:\\P\\unins000.exe\" /VERYSILENT"` → `ExecutablePath=="C:\P\unins000.exe"`、`Silent==true`。
  - `Parse_splits_quoted_exe_and_args`：`UninstallString="\"C:\\P\\unins000.exe\" /x"` → exe 与 `/x` 正确拆分。
  - `Parse_expands_environment_variables`：含 `%ProgramFiles%` 的路径被展开。
  - `Parse_detects_msi`：`UninstallString="MsiExec.exe /I{GUID}"` → `IsMsi==true`，`preferSilent` 时参数把 `/I` 规整为 `/X{GUID} /qn`。
  - `Parse_returns_null_when_executable_missing`：exe 文件不存在（假探针 `FileExists` 返回 false）→ `null`。
- [ ] **Step 3: 运行测试确认失败**  Run: `dotnet test --filter UninstallCommandParserTests`  Expected: FAIL（类型不存在）。
- [ ] **Step 4: 实现** `UninstallCommand` 与 `Parse`。拆 exe/args 用首个带引号段或首个空格前的 token；MSI 识别 `msiexec`（大小写无关）并把 `/I`→`/X`、补 `/qn`；非 MSI 且 `preferSilent` 时若来自 `QuietUninstallString` 视为已静默，否则 `Silent=false`（不猜 `/S`，避免误传）。
- [ ] **Step 5: 运行测试确认通过**  Run: `dotnet test --filter UninstallCommandParserTests`  Expected: PASS。
- [ ] **Step 6: 提交**  `feat(uninstall): 解析软件自带卸载命令`

---

### Task 2: 备份清单与隔离区（Core）

**Files:**
- Create: `src/UpdateHelper.Core/Uninstall/BackupModels.cs`
- Create: `src/UpdateHelper.Core/Uninstall/BackupStore.cs`
- Test: `tests/UpdateHelper.Core.Tests/Uninstall/BackupStoreTests.cs`

**Interfaces:**
- Produces:
  - `enum BackupEntryKind { FileOrDir, RegistryKey }`
  - `record BackupEntry(LeftoverType Type, string OriginalPath, string StoredPath, BackupEntryKind Kind)`
  - `record BackupManifest(string Id, DateTimeOffset Time, string SoftwareName, IReadOnlyList<BackupEntry> Entries)`
  - `class BackupStore(string root, Func<DateTimeOffset> now)`：
    - `string NewBackupDirectory(string id)` — 在 `root/<id>/` 建隔离目录并返回。
    - `void Save(BackupManifest manifest)` / `IReadOnlyList<BackupManifest> ReadAll()` / `BackupManifest? Find(string id)`（坏行跳过，沿用 `JsonLinesUpdateHistory` 风格）。
    - `int PruneExpired(TimeSpan retention)` — 删过期隔离目录并从清单剔除，返回删除条数。
    - `static string DefaultRoot { get; }` = `%LOCALAPPDATA%\UpdateHelper\backups`。
- Consumes: 无（独立）。

- [ ] **Step 1: 写失败测试** `BackupStoreTests`（用临时目录 + 可控 `now`）：
  - `Save_then_ReadAll_round_trips`：存 2 条清单，读回字段一致、时间倒序。
  - `Find_returns_matching_manifest_by_id`。
  - `ReadAll_skips_corrupt_lines`：手写一行坏 JSON，照常读出好行。
  - `PruneExpired_removes_old_backups_and_their_dirs`：一条 40 天前、一条今天，保留期 30 天 → 删 1、旧隔离目录不存在、新的还在。
- [ ] **Step 2: 运行测试确认失败**  Run: `dotnet test --filter BackupStoreTests`  Expected: FAIL。
- [ ] **Step 3: 实现** `BackupStore`（清单文件 `root/manifests.jsonl`，UTF-8 无 BOM、`UnsafeRelaxedJsonEscaping`、`JsonStringEnumConverter`，与 `JsonLinesUpdateHistory` 一致）。
- [ ] **Step 4: 运行测试确认通过**  Expected: PASS。
- [ ] **Step 5: 提交**  `feat(uninstall): 备份清单与 30 天隔离区`

---

### Task 3: 删除/恢复的变更接口与真实实现（Core + App 探针）

**Files:**
- Create: `src/UpdateHelper.Core/Uninstall/ILeftoverMutator.cs`
- Create: `src/UpdateHelper.Core/Uninstall/RealLeftoverMutator.cs`
- Test: `tests/UpdateHelper.Core.Tests/Uninstall/RealLeftoverMutatorTests.cs`（只测文件系统分支，注册表分支在集成/沙盒手测）

**Interfaces:**
- Produces `interface ILeftoverMutator`：
  - `bool MoveToBackup(string originalPath, string storedPath)` — 文件/目录移动到隔离区；成功 true。
  - `bool ScheduleDeleteOnReboot(string path)` — 占用无法移动时排到重启删（`MoveFileEx`）。
  - `bool ExportRegistryKey(string keyPath, string storedRegFile)` / `bool DeleteRegistryKey(string keyPath)`。
  - `bool MoveBack(string storedPath, string originalPath)` / `bool ImportRegistryFile(string storedRegFile)` —（恢复用）。
  - 所有方法失败返回 false、不抛。
- `RealLeftoverMutator : ILeftoverMutator`（文件用 `Directory.Move`/`File.Move`；重启删用 `MoveFileEx` P/Invoke；注册表用 `reg.exe export/import` + `Microsoft.Win32.Registry` 删键）。

- [ ] **Step 1: 写失败测试** `RealLeftoverMutatorTests`（临时目录真实文件）：
  - `MoveToBackup_moves_directory_and_leaves_original_gone`。
  - `MoveBack_restores_directory_to_original_location`。
  - `MoveToBackup_returns_false_when_source_missing`（不抛）。
- [ ] **Step 2: 运行测试确认失败**  Expected: FAIL。
- [ ] **Step 3: 实现** `ILeftoverMutator` + `RealLeftoverMutator`。`MoveFileEx` 签名：`[DllImport("kernel32", SetLastError=true, CharSet=CharSet.Unicode)] static extern bool MoveFileEx(string? lpExisting, string? lpNew, uint flags)`，flags=`MOVEFILE_DELAY_UNTIL_REBOOT(0x4)`，lpNew=null。
- [ ] **Step 4: 运行测试确认通过**  Expected: PASS。
- [ ] **Step 5: 提交**  `feat(uninstall): 残留删除与恢复的文件/注册表变更器`

---

### Task 4: 删除编排（Core，纯逻辑 + 白名单兜底 + 占用重启）

**Files:**
- Create: `src/UpdateHelper.Core/Uninstall/LeftoverRemover.cs`
- Create: `src/UpdateHelper.Core/Uninstall/RemovalModels.cs`
- Test: `tests/UpdateHelper.Core.Tests/Uninstall/LeftoverRemoverTests.cs`

**Interfaces:**
- Produces:
  - `record RemovalResult(int Removed, int RebootPending, IReadOnlyList<string> Failed, string? BackupId)`
  - `class LeftoverRemover(ILeftoverMutator mutator, BackupStore backups, Func<DateTimeOffset> now)`
  - `RemovalResult Remove(string softwareName, IReadOnlyList<LeftoverItem> selected)`：对每项 **先备份后删**；受 `SystemPaths` 保护的项直接跳过并记入 `Failed`；文件移动失败且判定占用 → `ScheduleDeleteOnReboot` 且 `RebootPending++`；全部完成后把本次备份写一条 `BackupManifest`。
  - `RemovalResult`（恢复复用）：`int Restore(BackupManifest manifest)` 还原每项（文件 `MoveBack`、注册表 `ImportRegistryFile`），坏项跳过。
- Consumes: Task 2 `BackupStore`、Task 3 `ILeftoverMutator`、现有 `SystemPaths`、`LeftoverItem`。

- [ ] **Step 1: 写失败测试** `LeftoverRemoverTests`（假 `ILeftoverMutator` 记录调用）：
  - `Remove_backs_up_before_deleting_each_item`（断言调用顺序：先 `MoveToBackup/ExportRegistryKey` 后 `DeleteRegistryKey`）。
  - `Remove_skips_protected_paths`：选中一个 `C:\Windows\...` → 不调用 mutator、计入 `Failed`。
  - `Remove_schedules_reboot_delete_when_move_fails_as_locked`：mutator `MoveToBackup` 返回 false → 调 `ScheduleDeleteOnReboot`，`RebootPending==1`。
  - `Remove_writes_a_backup_manifest_with_all_entries`。
  - `Restore_moves_every_entry_back`：对上一步的 manifest 调 `Restore`，断言 `MoveBack`/`ImportRegistryFile` 被逐项调用。
  - `Restore_skips_entries_whose_backup_is_gone`（mutator 返回 false）→ 不抛、继续其余。
- [ ] **Step 2: 运行测试确认失败**  Expected: FAIL。
- [ ] **Step 3: 实现** `LeftoverRemover`。隔离文件名用 `Guid` 避免重名；注册表项备份成 `<id>/<n>.reg`。
- [ ] **Step 4: 运行测试确认通过**  Expected: PASS。
- [ ] **Step 5: 提交**  `feat(uninstall): 残留删除编排（先备份后删、白名单兜底、占用重启删）`

---

### Task 5: 后台助手协议扩展——卸载/清残留操作（Core）

**Files:**
- Modify: `src/UpdateHelper.Core/Agent/AgentProtocol.cs`（新增 `AgentOp.RemoveResidue`、`AgentRequest` 增字段、升 `Version`→2）
- Modify: `src/UpdateHelper.Core/Agent/AgentRequestValidator.cs`
- Test: `tests/UpdateHelper.Core.Tests/Agent/AgentRequestValidatorTests.cs`（在现有文件追加）

**Interfaces:**
- Produces：`AgentOp.RemoveResidue`；`AgentRequest` 增 `string? UninstallKeyName`、`UninstallHive? Hive`、`IReadOnlyList<string>? SelectedPaths`、`bool RunNativeUninstaller`。
- Consumes：无。

- [ ] **Step 1: 写失败测试**（追加）：
  - `Validate_rejects_removeresidue_without_uninstall_key`。
  - `Validate_rejects_selected_paths_over_limit`（上限如 5000 条，防超大请求）。
  - `Validate_accepts_valid_removeresidue_request`。
  - `Validate_still_rejects_version_mismatch`（沿用）。
- [ ] **Step 2: 运行确认失败**  Expected: FAIL。
- [ ] **Step 3: 实现** 协议字段与校验（键名长度上限、`Enum.IsDefined(Hive)`、`SelectedPaths` 条数上限、每条长度上限）。升 `AgentProtocol.Version` 到 2。
- [ ] **Step 4: 运行确认通过**  Expected: PASS。
- [ ] **Step 5: 提交**  `feat(agent): 协议新增清残留操作并校验`

---

### Task 6: 后台助手执行清残留——独立重扫 + 交集删除（Core + Agent）

**Files:**
- Modify: `src/UpdateHelper.Core/Agent/AgentRequestHandler.cs`（`RemoveResidue` 分支）
- Create: `src/UpdateHelper.Core/Agent/ResidueRemovalService.cs`（重扫 + 求交集 + 调 `LeftoverRemover`，可单测）
- Modify: `src/UpdateHelper.Agent/Program.cs`（`--serve` 组装该服务）
- Test: `tests/UpdateHelper.Core.Tests/Agent/ResidueRemovalServiceTests.cs`

**Interfaces:**
- Produces：`class ResidueRemovalService(Func<...LeftoverScanner> rescan, LeftoverRemover remover)` 提供 `RemovalResult Handle(AgentRequest req)`：用 `UninstallKeyName/Hive` 独立重扫出该软件的残留 → 只保留 `SelectedPaths` 里也出现、且落在重扫结果内的项 → 交 `LeftoverRemover.Remove`。
- Consumes：Task 4 `LeftoverRemover`、现有 `LeftoverScanner`、`AgentRequest`。

- [ ] **Step 1: 写失败测试** `ResidueRemovalServiceTests`（假重扫返回已知残留集）：
  - `Handle_removes_only_intersection_of_scanned_and_selected`：重扫出 {A,B}，勾选 {A,X}（X 是伪造路径）→ 只删 A，X 被忽略。
  - `Handle_removes_nothing_when_rescan_empty`。
- [ ] **Step 2: 运行确认失败**  Expected: FAIL。
- [ ] **Step 3: 实现** 服务与 handler 分支（沿用现有"单请求单结果"回传；进度可选）。
- [ ] **Step 4: 运行确认通过**  Expected: PASS。
- [ ] **Step 5: 提交**  `feat(agent): 清残留只删"自扫∩勾选"，杜绝任意路径删除`

---

### Task 7: App 接线——卸载流程 + 历史恢复 + 去"待开发"

**Files:**
- Modify: `src/UpdateHelper.Presentation/IAppBackend.cs`（新增 `Task<UninstallOutcome> UninstallAsync(SoftwareGroup, IReadOnlyList<LeftoverItem> selected, ...)` 与 `Task<int> RestoreBackupAsync(string backupId)`、`IReadOnlyList<BackupManifest> ReadBackups()`）
- Modify: `src/UpdateHelper.App/RealBackend.cs`（实现：per-user 项进程内 `LeftoverRemover`；机器级项经 `AgentController` 发 `RemoveResidue`；native 卸载 per-user 直接跑、机器级经助手）
- Modify: `src/UpdateHelper.App/Pages/SoftwarePage.xaml(.cs)`（启用"卸载"按钮，去掉"（待开发）"）
- Modify: `src/UpdateHelper.App/Pages/LeftoversWindow.xaml(.cs)`（启用"删除所选"，勾选可编辑，接 `UninstallAsync`）
- Modify: 历史页（加"一键恢复"按钮 → `RestoreBackupAsync`）
- Modify: `src/UpdateHelper.Presentation/ViewModels/LeftoverViewModel.cs`（`Checked` 可写、暴露选中项；改 `DisclaimerText` 去掉"删除和备份功能将在后台助手完成后提供"）
- Test: `tests/UpdateHelper.Presentation.Tests/`（`LeftoverViewModel` 勾选切换、选中项收集；`FakeBackend` 加对应方法；历史恢复行为）

**Interfaces:**
- Consumes：Task 1–6 全部。
- Produces：`record UninstallOutcome(bool NativeUninstallRan, RemovalResult Removal, string Message)`。

- [ ] **Step 1: 写失败测试**（Presentation）：
  - `Leftover_rows_toggle_checked_and_collect_selection`。
  - `Uninstall_note_no_longer_says_coming_soon`（断言 `DisclaimerText` 不含"将在后台助手完成后提供"）。
  - `FakeBackend.UninstallAsync` 被调用时传入的选中项正确。
- [ ] **Step 2: 运行确认失败**  Expected: FAIL。
- [ ] **Step 3: 实现** backend 分流（per-user vs 机器级）、ViewModel 勾选、XAML 启用按钮、历史恢复。机器级删除弹一次 UAC（经计划 7 的助手；助手未启用则提示去设置启用，或当场提权跑一次性卸载）。
- [ ] **Step 4: 运行确认通过**  Run: `dotnet test`  Expected: 全绿。
- [ ] **Step 5: 提交**  `feat(uninstall): 卸载流程接线、历史一键恢复、移除"待开发"标注`

---

### Task 8: 启动时清理过期备份 + 文档

**Files:**
- Modify: `src/UpdateHelper.App/RealBackend.cs` 或启动处（构造时 `BackupStore.PruneExpired(TimeSpan.FromDays(30))`，吞异常）
- Modify: `src/UpdateHelper.App/Pages/SettingsPage.xaml` + `SettingsViewModel.cs`（`AutoUpdateNote`：卸载器已可用，自动安装仍在开发中——文案更新）
- Modify: `README.md` 路线图、`CHANGELOG.md`（0.8.0 段）、spec 第 7 节标注"Phase A 完成"
- Test: 无新增（文案断言并入 Task 7）

- [ ] **Step 1: 实现** 过期清理与文档。
- [ ] **Step 2: 运行**  Run: `dotnet test`  Expected: 全绿。
- [ ] **Step 3: 提交**  `chore(uninstall): 启动清过期备份；文档与路线图`

---

## 发版（Phase A 合并后，与 Phase B 可一起发 0.8.0）
合并到 main → 升版本号 0.8.0（`Directory.Build.props`）→ README/CHANGELOG/GitHub Release 同步 → 打 tag `v0.8.0` → `gh release create` 上传便携版 + 小体积版两个 zip（Release 版先冒烟：启动、扫描、设置页正常）。

## 端到端验证（真机/沙盒）
装一个带残留的测试软件 → "卸载" → 残留列表正确、共用/个人默认不勾 → 勾选删除（机器级触发一次 UAC，经后台助手）→ 复查已删 → 历史"一键恢复" → 复查已还原。顽固软件（360 一类，用户自备、用户主导）：走其官方卸载、残留逐项备份删除、界面如实提示可能需官方卸载/安全模式。
