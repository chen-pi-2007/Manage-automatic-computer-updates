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
/// Idle：进程树静默多久算“装完了/没动静了”；Max：无论多忙的总时长上限；Poll：检查间隔。
/// </summary>
public sealed record MonitorLimits(TimeSpan Idle, TimeSpan Max, TimeSpan Poll)
{
    /// <summary>默认：树内 2 分钟没动静就收尾；总共最长 30 分钟（大型安装包也够用）。</summary>
    public static MonitorLimits Default { get; } = new(TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(30), TimeSpan.FromSeconds(1));
}

/// <summary>
/// 监控处理器：串起 事件源 → 累积器 → 收尾 → 存储。逻辑放在 Core（而非 Agent）是为了能用假事件源脱离管理员单测；
/// Agent 只负责传入真实的 ETW 工厂、真实复验和受保护区存储。
/// 同一时刻只允许一个会话（单例）：第二个 StartMonitor 直接回错。
/// </summary>
public sealed class MonitorHandler(
    MonitorSourceFactory sourceFactory, MonitorFootprintStore store, IFootprintProbe probe,
    Func<DateTimeOffset> now, MonitorLimits limits) : IMonitorHandler
{
    // 流式发现的上限：安装包可能创建成千上万个文件，全推给 App 既没意义也会拖慢管道；完整信息在最终足迹里
    private const int MaxDiscoveries = 2000;

    private sealed class Session
    {
        public Session(string id, string rootExe, int rootPid, DateTimeOffset start)
        {
            Id = id;
            Start = start;
            Tree = new ProcessTree(rootPid, DelegateImages.Default);
            Acc = new FootprintAccumulator(Tree, start, rootPid, rootExe);
            LastActivityTicks = start.UtcTicks;
        }

        public string Id { get; }
        public DateTimeOffset Start { get; }
        public ProcessTree Tree { get; }
        public FootprintAccumulator Acc { get; }
        public IMonitorSource? Source;
        public readonly HashSet<string> Announced = new(StringComparer.OrdinalIgnoreCase);
        public long LastActivityTicks;
        public int Announcements;
        public bool Finished;   // 写入受 _gate 保护；事件回调里用 Volatile 读
        public readonly TaskCompletionSource<MonitorFootprint?> Done = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private readonly object _gate = new();
    private Session? _active;

    public async Task StartAsync(string installerPath, int rootPid, Func<AgentResponse, Task> send,
        CancellationToken cancellationToken, string? sessionId = null)
    {
        sessionId = string.IsNullOrEmpty(sessionId) ? Guid.NewGuid().ToString("N") : sessionId;
        var session = new Session(sessionId, installerPath, rootPid, now());

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
            session.Acc.Observe(e);
            // 活动 = 树内（含代办）的进程有动作。无关进程的海量事件不能续命，否则空闲超时永远不到
            if (session.Tree.Attribute(e.Pid, e.Time) == AttributionLevel.Outside) return;
            Interlocked.Exchange(ref session.LastActivityTicks, now().UtcTicks);

            if (DiscoveryOf(e) is { } item && session.Announcements < MaxDiscoveries && session.Announced.Add(item.Path))
            {
                session.Announcements++;
                // 事件回调在同步的 ETW 线程上：不能等管道写；send 自身线程安全，断线后是空操作
                _ = send(new AgentResponse(AgentMessageType.Discovery, Discovery: item));
            }
        }

        try
        {
            session.Source = sourceFactory(OnEvent, session.Acc.RecordEventsLost);
            session.Source.Start();
        }
        catch (Exception ex)
        {
            // ETW 不可用（如 Process Monitor 占着内核会话）等：回错给 App，释放单例名额，绝不崩提权进程
            ReleaseWithoutSaving(session);
            var message = ex is MonitorUnavailableException ? ex.Message : $"无法开始监控：{ex.Message}";
            await send(new AgentResponse(AgentMessageType.Result, ErrorMessage: message));
            return;
        }

        // 等待结束：被 StopAsync 收尾 / 空闲超时 / 最长时长 / 助手退出
        try
        {
            while (!session.Done.Task.IsCompleted)
            {
                var finished = await Task.WhenAny(session.Done.Task, Task.Delay(limits.Poll, cancellationToken));
                if (finished == session.Done.Task) break;

                var t = now();
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
    /// 唯一的收尾入口（幂等）：停源 → 标提前结束原因 → Finalize → 落盘 → 释放单例。
    /// 多个路径（Stop/超时/退出）可能同时来，只有第一个执行，其余等同一份结果。
    /// </summary>
    private MonitorFootprint? Finish(Session s, string earlyStopReason)
    {
        lock (_gate)
        {
            if (s.Finished) return s.Done.Task.IsCompletedSuccessfully ? s.Done.Task.Result : null;
            Volatile.Write(ref s.Finished, true);
        }

        try { s.Source?.Stop(); } catch (Exception) { /* 停源失败不影响已收集数据的收尾 */ }
        s.Acc.RecordEarlyStop(earlyStopReason);
        var footprint = s.Acc.Finalize(s.Id, probe, now());
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
        RegistryEvent { Kind: MonitorEventKind.RegistryCreate } r => new DiscoveryItem(
            string.IsNullOrEmpty(r.ValueName) ? FootprintEntryType.RegistryKey : FootprintEntryType.RegistryValue,
            string.IsNullOrEmpty(r.ValueName) ? r.KeyPath : r.KeyPath + "\\" + r.ValueName, FootprintConfidence.Possible),
        ServiceOrDriverEvent s => new DiscoveryItem(s.IsDriver ? FootprintEntryType.Driver : FootprintEntryType.Service,
            s.Name, FootprintConfidence.Possible),
        _ => null,
    };
}
