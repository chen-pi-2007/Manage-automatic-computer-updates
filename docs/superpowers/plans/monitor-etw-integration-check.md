# ETW 采集器真机集成验证清单

状态：**待执行**（需管理员权限 + 真实安静安装器，子代理做不了；由主控会话 + 用户点 UAC 时执行）。
对象：`src/UpdateHelper.Agent/Monitor/EtwMonitorSession.cs`（内核事件 → `MonitorEvent` 的翻译层）。

## 前置

1. 关闭 Process Monitor、Resource Monitor 等一切占用 NT Kernel Logger 的工具。
   用 `logman query -ets` 确认列表里没有 `NT Kernel Logger`。
2. 用管理员 PowerShell/控制台跑验证用的临时宿主（控制台小程序或 Agent 内的临时入口，
   引用 `EtwMonitorSession`，`onEvent` 把事件逐行打印/写文件，`onEventsLost` 打印丢失数）。
3. 选一个真实的安静安装器（例如 7-Zip：`7z*.exe /S /D=C:\Temp\etwcheck\7z`），事先记下它会装到哪里。

## 步骤与期望

### 0. 占用检测（先做，几秒）
- 先手动 `logman start "NT Kernel Logger" -p "Windows Kernel Trace" 0x10 -ets`，
  再调 `Start()`。
- 期望：抛 `MonitorUnavailableException`，中文消息提示关闭 Process Monitor 等；
  **且原来的会话没有被我们停掉**（`logman query -ets` 仍能看到它）。
- 清理：`logman stop "NT Kernel Logger" -ets`。

### 1. 根进程树文件被采到
- `Start()` 后以安装器为根进程启动安装，等待结束，`Stop()`。
- 期望：事件流里有 `ProcessStartEvent`（根 PID，`ImagePath` 是完整路径而非仅文件名）；
  有 `FileEvent(IsCreate:true)` 落在安装目标目录（路径是 `C:\...` 盘符形式，
  **不含** `\Device\HarddiskVolume`、`\??\` 前缀）；子进程（如解包进程）的文件事件 PID 与其
  `ProcessStartEvent.ParentPid` 链可连回根 PID。
- 期望：安装目录中只被"打开读取"的文件（`CreateDisposition=OPEN`）**没有**产出 `IsCreate:true`；
  开目录（`FILE_DIRECTORY_FILE`）没有产出事件。

### 2. `%TEMP%` 临时文件
- 同一次运行的事件里应能看到安装器在 `%TEMP%` 下的创建事件。
- 期望：采集层原样产出（采集层不过滤）；经 Core 累积后这些条目被归为噪声/Dropped，
  最终 `MonitorFootprint.Entries` 中没有 `%TEMP%` 条目。

### 3. 建了又删不留
- 在被监控进程树里（如安装器的临时文件，或手写脚本 `echo x > %TEMP%\a.tmp & del %TEMP%\a.tmp`）
  找一个建后即删的文件。
- 期望：事件流里先 `IsCreate:true` 后 `IsCreate:false`；经 Core + `RealFootprintProbe`
  最终足迹里没有该文件。再确认 `RealFootprintProbe.StillExists` 对已删文件返回 false、
  对已装文件返回 true、对已装的 `HKLM\...\Uninstall\<子键>` 返回 true。

### 4. EventsLost 可读
- 制造高压：`Start()` 后在另一窗口大量文件操作（如 `robocopy C:\Windows\System32 C:\Temp\etwflood /E`
  或解压大 zip），同时让回调慢一点（`onEvent` 里加 `Thread.Sleep(1)`）。
- 期望：`onEventsLost` 至少被调用一次且数值 > 0；`Stop()` 后总和与 `logman query -ets` /
  会话统计中的 Events Lost 大致相符。若始终为 0，记录下来并说明负载大小（可能压力不够）。

### 5. 卷序列号（Ruling-1）
- 插一个 U 盘，在其上创建文件（盘符例如 `E:`）。
- 期望：该文件的 `FileEvent.VolumeSerial` 非空（8 位十六进制），系统盘文件的 `VolumeSerial` 为 null。
- 更换盘符后（磁盘管理里改盘符）再创建文件：新事件序列号不变，路径盘符变了。

### 6. 收尾
- `Stop()` / `Dispose()` 后 `logman query -ets` 里不再有 `NT Kernel Logger`（没有残留占用）。
- 进程退出无异常、无挂起（泵线程 5 秒内退出）。

## 需要现场核对的不确定点（实现时未能验证）
- `CreateOptions` 的 `FILE_DIRECTORY_FILE(0x1)` 位在 TraceEvent 3.1.9 里的取值是否与预期一致
  （官方文档注明"尚未验证"）。验证法：创建目录时看是否仍有 `IsCreate:true` 事件漏出。
- `CreateDisposition` 数值是否为 NT 语义（0 SUPERSEDE、1 OPEN、2 CREATE、3 OPEN_IF、4 OVERWRITE、5 OVERWRITE_IF）。
- 内核 Create 事件不含结果，`OPEN_IF/OVERWRITE_IF` 打开已有文件也会被报为 `IsCreate:true`；
  确认 Core 收尾复验/IsNew 能处理（例如覆盖安装场景）。
- 重命名（先写临时名再 rename 成正式名）在本采集层未翻译：看安装器的最终文件是否因此漏记。
- 注册表 `KeyName` 的实际形态（`\REGISTRY\MACHINE\...` 还是已相对化），确认 `RegPath` 翻成 `HKLM\...` / `HKCU\...`。

## 记录区（执行时填写）

| 项 | 结果 | 备注 |
|---|---|---|
| 0 占用检测 | | |
| 1 根树文件 | | |
| 2 %TEMP% | | |
| 3 建了又删 | | |
| 4 EventsLost | | |
| 5 卷序列号 | | |
| 6 收尾无残留 | | |
