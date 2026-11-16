using UpdateHelper.Core.Agent;

namespace UpdateHelper.Core.Monitor;

/// <summary>事件源抽象：真实现是 Agent 里的 EtwMonitorSession；测试用假源，所以不需要管理员/ETW 就能测处理器。</summary>
public interface IMonitorSource : IDisposable
{
    /// <summary>开始采集。内核跟踪不可用时抛 <see cref="MonitorUnavailableException"/>。</summary>
    void Start();
    void Stop();
}

/// <summary>造一个事件源：事件喂 onEvent，丢失数喂 onEventsLost。</summary>
public delegate IMonitorSource MonitorSourceFactory(Action<MonitorEvent> onEvent, Action<int> onEventsLost);

/// <summary>
/// 会话生命周期上限。为什么必须有：监控管道是单实例、一次只服务一个会话，
/// 客户端连上后卡住（或安装包挂住）不能让提权助手永远被占着。
/// Idle：进程树静默多久算“装完了/没动静了”；Max：无论多忙的总时长上限；Poll：检查间隔；
/// RootWait：两阶段握手里回了 Ready 后、等 App 补根 PID 的上限（正常是管道往返+启动安装器的亚秒级；
/// 迟迟不来说明 App 侧启动失败/用户取消/断线，到期收尾并把足迹标「未收到根进程号」的不完整，绝不静默）。
/// </summary>
public sealed record MonitorLimits(TimeSpan Idle, TimeSpan Max, TimeSpan Poll, TimeSpan RootWait)
{
    /// <summary>默认：树内 2 分钟没动静就收尾；总共最长 30 分钟（大型安装包也够用）；根 PID 最多等 30 秒。</summary>
    public static MonitorLimits Default { get; } =
        new(TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(30), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(30));
}

/// <summary>
/// 监控处理器：串起 事件源 → 累积器 → 收尾 → 存储。逻辑放在 Core（而非 Agent）是为了能用假事件源脱离管理员单测；
/// Agent 只负责传入真实的 ETW 工厂、真实复验和受保护区存储。
/// 同一时刻只允许一个会话（单例）：第二个 StartMonitor 直接回错。
///
/// 两阶段握手（设计文档「启动模型」）：StartAsync 先把 ETW 内核会话起起来【立刻开始抓】，拿不到根 PID 的这段时间
/// 把事件【先缓冲在内存】，并回一条 Ready；App 收到 Ready 才去启动安装器、拿到 PID 后用 SetRoot 补进来；
/// SetRoot 时才建进程树与累积器，把缓冲事件【回放】进去，之后实时摄入。这样安装器从第一刻起的动作都被抓到并正确归属，
/// 不再有「内核会话晚于安装器启动」的开头漏记窗口。
/// </summary>
public sealed class MonitorHandler(
    MonitorSourceFactory sourceFactory, MonitorFootprintStore store, IFootprintProbe probe,
    Func<DateTimeOffset> now, MonitorLimits limits, ISystemInventory? inventory = null) : IMonitorHandler
{
    // 流式发现的上限：安装包可能创建成千上万个文件，全推给 App 既没意义也会拖慢管道；完整信息在最终足迹里
    private const int MaxDiscoveries = 2000;

    // 拿到根 PID 之前的缓冲上界：正常窗口只有几百毫秒、事件有限；设上限是为了极端情况下（根 PID 迟迟不来、
    // ETW 海量事件）绝不无界堆到 OOM。超限丢弃并计数，收尾时按“丢事件”标不完整（参考 EventsLost 风格）。
    private const int MaxBufferedBeforeRoot = 200_000;

    private sealed class Session
    {
        public Session(string id, string rootExe, DateTimeOffset start)
        {
            Id = id;
            RootExe = rootExe;
            Start = start;
            LastActivityTicks = start.UtcTicks;
        }

        public string Id { get; }
        public string RootExe { get; }
        public DateTimeOffset Start { get; }

        // Ingest 锁：串行化“缓冲 / 建树回放 / 实时摄入”，保证回放与并发到达的实时事件之间不乱序、不丢、不重复。
        public readonly object Ingest = new();
        public readonly List<MonitorEvent> Buffer = new();   // 根 PID 到来前的事件（Acc 建好后清空）
        public int DroppedBeforeRoot;                        // 缓冲超限丢弃的事件数
        public int LostBeforeRoot;                           // 建树前 ETW 丢失数（建树后转入 Acc）
        public ProcessTree? Tree;                            // SetRoot 后建立
        public bool HasRealRoot;                             // 拿到了真实根 PID（占位根 0 不算）
        // 监控开始前的系统名册（服务/驱动/计划任务）；收尾时再取一次求差集。取失败则 InventoryFailed=true
        public HashSet<(FootprintEntryType Type, string Name)>? InventoryBefore;
        public bool InventoryFailed;
        public FootprintAccumulator? Acc;                    // 非空即“已拿到根 PID”，实时事件走摄入而非缓冲

        public Func<AgentResponse, Task> Send = _ => Task.CompletedTask;
        public IMonitorSource? Source;
        public readonly HashSet<string> Announced = new(StringComparer.OrdinalIgnoreCase);
        public long LastActivityTicks;
        // 首次观察到“根进程树已整体退出”的时刻（UtcTicks，0=尚未观察到）。根树一旦退出不会复活，无需重置。
        // 用于在根退出后略等一个“收尾静默”窗口，给慢到的尾随事件一点落袋时间；仅轮询循环单线程读写。
        public long RootExitedSinceTicks;
        public int Announcements;
        public bool Finished;   // 写入受 _gate 保护；事件回调里用 Volatile 读
        public readonly TaskCompletionSource<MonitorFootprint?> Done = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private readonly object _gate = new();
    private Session? _active;

    public async Task StartAsync(string installerPath, Func<AgentResponse, Task> send,
        CancellationToken cancellationToken, string? sessionId = null)
    {
        sessionId = string.IsNullOrEmpty(sessionId) ? Guid.NewGuid().ToString("N") : sessionId;
        var session = new Session(sessionId, installerPath, now()) { Send = send };

        bool taken;
        lock (_gate)
        {
            taken = _active is not null;
            if (!taken) _active = session;
        }
        if (taken)
        {
            // 单例：不排队、不抢占，直接告诉客户端
            await send(new AgentResponse(AgentMessageType.Result, ErrorMessage: "已有监控会话在进行，请等它结束后再试"));
            return;
        }

        void OnEvent(MonitorEvent e)
        {
            // 收尾后晚到的事件直接丢：足迹已经定稿
            if (Volatile.Read(ref session.Finished)) return;
            lock (session.Ingest)
            {
                if (session.Acc is null)
                {
                    // 还没拿到根 PID：先缓冲（有上界，超限丢弃并计数，绝不无界堆到 OOM）
                    if (session.Buffer.Count >= MaxBufferedBeforeRoot) { session.DroppedBeforeRoot++; return; }
                    session.Buffer.Add(e);
                    return;
                }
                Ingest(session, e);
            }
        }

        void OnLost(int n)
        {
            lock (session.Ingest)
            {
                if (session.Acc is null) session.LostBeforeRoot += n;
                else session.Acc.RecordEventsLost(n);
            }
        }

        // 先取一次系统名册，再开始抓事件：安装器是收到 Ready 之后才启动的，这里取到的一定是“装之前”的样子
        if (inventory is not null)
        {
            try { session.InventoryBefore = ToSet(inventory.Snapshot()); }
            catch (Exception) { session.InventoryFailed = true; }
        }

        try
        {
            session.Source = sourceFactory(OnEvent, OnLost);
            session.Source.Start();   // 内核会话此刻就开始抓——先于安装器启动，这正是修复点
        }
        catch (Exception ex)
        {
            // ETW 不可用（如 Process Monitor 占着内核会话）等：回错给 App，释放单例名额，绝不崩提权进程
            ReleaseWithoutSaving(session);
            var message = ex is MonitorUnavailableException ? ex.Message : $"无法开始监控：{ex.Message}";
            await send(new AgentResponse(AgentMessageType.Result, ErrorMessage: message));
            return;
        }

        // ETW 就绪、事件已在缓冲：回执 Ready。App 收到后才去启动安装器，并用 SetRoot 把根 PID 送回来
        await send(new AgentResponse(AgentMessageType.Ready));

        // 等待结束：拿到根 PID 后按 Idle/Max 收尾；始终拿不到则 RootWait 到期收尾；也可能被 StopAsync / 助手退出打断
        try
        {
            while (!session.Done.Task.IsCompleted)
            {
                var delay = Task.Delay(limits.Poll, cancellationToken);
                var finished = await Task.WhenAny(session.Done.Task, delay);
                if (finished == session.Done.Task) break;
                // WhenAny 不会把 delay 的取消抛出来；不显式 await 的话，取消后每轮都得到一个已取消的 delay，
                // 既不 break 也不抛，会热自旋到最长时长。这里 await 它让取消变成异常，走下面的收尾落盘
                await delay;

                var t = now();
                bool rooted;
                lock (session.Ingest) rooted = session.Acc is not null;

                if (!rooted)
                {
                    // 阶段一：回了 Ready 但还没拿到根 PID。到了 RootWait 就收尾并明确标“未收到根进程号”，绝不静默当完整；
                    // 这期间不做 Idle 判定（否则一开始就“静默”误收尾），但 Max 仍兜底
                    if (t - session.Start >= limits.RootWait)
                        Finish(session, "未收到根进程号（安装器可能启动失败、被取消或连接断开）");
                    else if (t - session.Start >= limits.Max)
                        Finish(session, $"监控达到最长时长（{limits.Max.TotalMinutes:0.#} 分钟），已自动结束");
                    continue;
                }

                // 正常完成（Ruling-9）：根进程树已整体退出（根及所有已观察到的后代化身都已 Stop）→ 立即收尾并标「完整」，
                // 不塞任何 early-stop 原因（完整与否仍由累积器既有逻辑按 EventsLost/模糊项判定，这里只是“不额外塞原因”）。
                // 根退出即收尾（秒级），不再空转到 Idle 才结束，顺带把抓取窗口缩到最短、避免吸全系统内核噪声（EventsLost 应趋近 0）。
                // 根 ProcessStop 若晚到/丢失则此路径不触发，仍由下方 Idle/Max 兜底，两者不冲突。
                if (session.Tree!.RootTreeHasExited(t))
                {
                    if (session.RootExitedSinceTicks == 0)
                        session.RootExitedSinceTicks = t.UtcTicks;                 // 记录首次观察到退出
                    if (t.UtcTicks - session.RootExitedSinceTicks >= limits.Poll.Ticks)
                        Finish(session, null);                                     // 收尾静默已过：正常完成、标「完整」
                    continue;   // 根树已退出，这一轮不再走 Idle/Max（收尾只等那一个很短的静默窗口）
                }

                if (t - session.Start >= limits.Max)
                    Finish(session, $"监控达到最长时长（{limits.Max.TotalMinutes:0.#} 分钟），已自动结束");
                else if (t.UtcTicks - Interlocked.Read(ref session.LastActivityTicks) >= limits.Idle.Ticks)
                    Finish(session, $"进程树已静默 {limits.Idle.TotalMinutes:0.#} 分钟，已自动结束");
            }
        }
        catch (OperationCanceledException)
        {
            // 助手要退出：尽力收尾落盘，不丢已记录的足迹
            Finish(session, "后台助手退出，监控被中断");
            return;
        }

        if (await session.Done.Task is { } footprint)
            await send(new AgentResponse(AgentMessageType.Footprint, FootprintJson: AgentProtocol.Encode(footprint)));
    }

    public void SetRoot(string sessionId, int rootPid)
    {
        Session? s;
        lock (_gate) s = _active is { } a && a.Id == sessionId ? a : null;
        if (s is null) return;   // 会话不存在/已结束：忽略（迟到的 SetRoot）

        lock (s.Ingest)
        {
            // 已设过（重复的 SetMonitorRoot）或已在收尾：忽略，绝不重建树覆盖已有账
            if (s.Acc is not null || Volatile.Read(ref s.Finished)) return;
            EnsureRooted(s, rootPid);
        }
    }

    public Task<MonitorFootprint?> StopAsync(string sessionId)
    {
        Session? active;
        lock (_gate) active = _active is { } a && a.Id == sessionId ? a : null;

        if (active is not null)
            return Task.FromResult(Finish(active, "用户手动停止，之后的动作没有被记录"));

        // 会话已结束（含超时自动结束）：重连读回已落盘的足迹
        try { return Task.FromResult(store.Find(sessionId)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return Task.FromResult<MonitorFootprint?>(null); }
    }

    public void OnClientDisconnected()
    {
        // 仅通知：会话继续记录，受生命周期上限约束；之后凭会话 id 重连读回
    }

    /// <summary>
    /// 建进程树与累积器，并把缓冲事件回放进去（含安装器自己的 ProcessStart——ETW 早就在抓了）。
    /// 调用者必须已持有 s.Ingest 锁：建树与回放期间并发到达的实时事件会阻塞在锁上，待回放完再按序摄入，不乱序不重复。
    /// rootPid 为 0 表示“收尾时仍没拿到根 PID”的占位根：所有真实事件都归不到树（无归属 → 不进足迹），足迹随后被标不完整。
    /// </summary>
    private void EnsureRooted(Session s, int rootPid)
    {
        s.HasRealRoot = rootPid != 0;
        s.Tree = new ProcessTree(rootPid, DelegateImages.Default);
        s.Acc = new FootprintAccumulator(s.Tree, s.Start, rootPid, s.RootExe);
        if (s.LostBeforeRoot > 0) s.Acc.RecordEventsLost(s.LostBeforeRoot);
        if (s.DroppedBeforeRoot > 0) s.Acc.RecordEventsLost(s.DroppedBeforeRoot);   // 缓冲超限丢的也算“丢事件”，标不完整
        foreach (var e in s.Buffer) Ingest(s, e);
        s.Buffer.Clear();
        s.Buffer.TrimExcess();
    }

    /// <summary>摄入一个事件（记账 + 续活 + 流式发现）。调用者持有 s.Ingest 锁，且 s.Acc 非空。</summary>
    private void Ingest(Session s, MonitorEvent e)
    {
        s.Acc!.Observe(e);
        // 活动 = 树内（含代办）的进程有动作。无关进程的海量事件不能续命，否则空闲超时永远不到
        if (s.Tree!.Attribute(e.Pid, e.Time) == AttributionLevel.Outside) return;
        Interlocked.Exchange(ref s.LastActivityTicks, now().UtcTicks);

        if (DiscoveryOf(e) is { } item && s.Announcements < MaxDiscoveries && s.Announced.Add(item.Path))
        {
            s.Announcements++;
            // send 自身线程安全，断线后是空操作；不等它（可能在同步的 ETW 线程上）
            _ = s.Send(new AgentResponse(AgentMessageType.Discovery, Discovery: item));
        }
    }

    /// <summary>
    /// 唯一的收尾入口（幂等）：停源 →（可选）标提前结束原因 → Finalize → 落盘 → 释放单例。
    /// 多个路径（Stop/超时/退出/根树退出）可能同时来，只有第一个执行，其余等同一份结果。
    /// earlyStopReason 为 null 表示“正常完成”（根进程树干净退出）：不记任何 early-stop 原因，完整与否
    /// 仍由 FootprintAccumulator 的既有逻辑（EventsLost/模糊项）决定；非 null 则是 Idle/Max/Stop/未收到根 PID/
    /// 助手退出等“不完整”兜底路径，诚实标注之后动作可能漏记。
    /// 注意锁序：绝不同时持有 _gate 与 s.Ingest（SetRoot 先 _gate 再 Ingest，这里分段持有，避免 AB-BA 死锁）。
    /// </summary>
    private MonitorFootprint? Finish(Session s, string? earlyStopReason)
    {
        lock (_gate)
        {
            if (s.Finished) return s.Done.Task.IsCompletedSuccessfully ? s.Done.Task.Result : null;
            Volatile.Write(ref s.Finished, true);
        }

        try { s.Source?.Stop(); } catch (Exception) { /* 停源失败不影响已收集数据的收尾 */ }

        // 再取一次系统名册（磁盘/注册表 IO，放在锁外）：和开始时的差集就是这段时间新出现的服务/驱动/计划任务
        List<InventoryItem>? added = null;
        var inventoryFailed = s.InventoryFailed;
        if (inventory is not null && s.InventoryBefore is { } before)
        {
            try { added = inventory.Snapshot().Where(i => !before.Contains((i.Type, i.Name.ToUpperInvariant()))).ToList(); }
            catch (Exception) { inventoryFailed = true; }
        }

        MonitorFootprint footprint;
        lock (s.Ingest)
        {
            // 始终没拿到根 PID：用占位根（0）建空账，缓冲里的真实事件都归不到树，得到一份“未收到根进程号”的不完整足迹
            if (s.Acc is null) EnsureRooted(s, 0);
            // 安装器根本没跑起来时，名册差集和它无关，不往足迹里记
            if (s.HasRealRoot)
            {
                if (added is not null) s.Acc!.AddInventoryAdditions(added, now());
                if (inventoryFailed) s.Acc!.RecordInventoryUnavailable();
            }
            if (earlyStopReason is not null) s.Acc!.RecordEarlyStop(earlyStopReason);
            footprint = s.Acc!.Finalize(s.Id, probe, now());
        }

        try { store.Save(footprint); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 落盘失败（磁盘满/ACL 异常）：仍把足迹交给客户端，别让用户白等；之后重连读回会找不到，可接受的降级
        }
        try { s.Source?.Dispose(); } catch (Exception) { }

        lock (_gate)
        {
            if (ReferenceEquals(_active, s)) _active = null;
        }
        s.Done.TrySetResult(footprint);
        return footprint;
    }

    // 名册项的身份：类型 + 名字（不分大小写）
    private static HashSet<(FootprintEntryType, string)> ToSet(IEnumerable<InventoryItem> items) =>
        items.Select(i => (i.Type, i.Name.ToUpperInvariant())).ToHashSet();

    private void ReleaseWithoutSaving(Session s)
    {
        lock (_gate)
        {
            s.Finished = true;
            if (ReferenceEquals(_active, s)) _active = null;
        }
        try { s.Source?.Dispose(); } catch (Exception) { }
        s.Done.TrySetResult(null);
    }

    /// <summary>只把“新建类”事件投影成流式发现（给 App 实时展示进度感）；确定性由收尾时的完整分析给出。</summary>
    private static DiscoveryItem? DiscoveryOf(MonitorEvent e) => e switch
    {
        FileEvent { IsCreate: true } f => new DiscoveryItem(FootprintEntryType.File, f.Path, FootprintConfidence.Possible),
        FileRenameEvent m => new DiscoveryItem(FootprintEntryType.File, m.NewPath, FootprintConfidence.Possible),
        RegistryEvent { KeyPath.Length: > 0 } r when r.Kind == MonitorEventKind.RegistryCreate || r.IsNew => new DiscoveryItem(
            string.IsNullOrEmpty(r.ValueName) ? FootprintEntryType.RegistryKey : FootprintEntryType.RegistryValue,
            string.IsNullOrEmpty(r.ValueName) ? r.KeyPath : r.KeyPath + "\\" + r.ValueName, FootprintConfidence.Possible),
        ServiceOrDriverEvent s => new DiscoveryItem(s.IsDriver ? FootprintEntryType.Driver : FootprintEntryType.Service,
            s.Name, FootprintConfidence.Possible),
        _ => null,
    };
}
