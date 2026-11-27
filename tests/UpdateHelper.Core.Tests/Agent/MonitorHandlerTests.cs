using UpdateHelper.Core.Agent;
using UpdateHelper.Core.Monitor;

namespace UpdateHelper.Core.Tests.Agent;

public class MonitorHandlerTests
{
    private const int Root = 4242;
    private const string Installed = @"C:\Program Files\Foo\a.exe";

    // 假 ETW 源：测试手动推事件；可设成启动即抛“不可用”
    private sealed class FakeSource(Action<MonitorEvent> onEvent, Action<int> onLost, bool unavailable) : IMonitorSource
    {
        public bool Stopped;
        public void Push(MonitorEvent e) => onEvent(e);
        public void Lose(int n) => onLost(n);
        public void Start()
        {
            if (unavailable) throw new MonitorUnavailableException("内核跟踪被占用");
        }
        public void Stop() => Stopped = true;
        public void Dispose() => Stop();
    }

    private sealed class AllExist : IFootprintProbe
    {
        public bool StillExists(FootprintEntry e) => true;
    }

    private sealed class Rig : IDisposable
    {
        public readonly TempDir Dir = new();
        public FakeSource? Source;
        public bool Unavailable;   // 可变：ETW 恢复后可切回可用，验证失败会话不占单例名额
        public readonly List<AgentResponse> Sent = [];
        public readonly MonitorHandler Handler;

        public Rig(bool unavailable = false, MonitorLimits? limits = null, ISystemInventory? inventory = null)
        {
            Unavailable = unavailable;
            Handler = new MonitorHandler(
                (onEvent, onLost) => Source = new FakeSource(onEvent, onLost, Unavailable),
                new MonitorFootprintStore(Dir.Path, () => DateTimeOffset.Now),
                new AllExist(), () => DateTimeOffset.Now,
                limits ?? new MonitorLimits(TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5),
                    TimeSpan.FromMilliseconds(20), TimeSpan.FromMinutes(5)),
                inventory);
        }

        public Task Send(AgentResponse r)
        {
            lock (Sent) Sent.Add(r);
            return Task.CompletedTask;
        }

        public MonitorFootprintStore Store => new(Dir.Path, () => DateTimeOffset.Now);

        public async Task WaitStarted()
        {
            for (var i = 0; i < 200 && Source is null; i++) await Task.Delay(10);
            Assert.NotNull(Source);
        }

        // 回执 Ready（ETW 就绪）后才轮到 App 启动安装器并补根 PID
        public async Task WaitReady()
        {
            for (var i = 0; i < 200; i++)
            {
                lock (Sent) if (Sent.Any(r => r.Type == AgentMessageType.Ready)) return;
                await Task.Delay(10);
            }
            Assert.Fail("没有收到 Ready");
        }

        public bool Has(AgentMessageType t) { lock (Sent) return Sent.Any(r => r.Type == t); }

        public void Dispose() => Dir.Dispose();
    }

    [Fact]
    public async Task Handler_produces_and_saves_footprint()
    {
        using var rig = new Rig();
        var run = rig.Handler.StartAsync(@"C:\setup.exe", rig.Send, CancellationToken.None, "s1");
        await rig.WaitStarted();
        await rig.WaitReady();
        rig.Handler.SetRoot("s1", Root);
        rig.Source!.Push(new FileEvent(Root, Installed, true, DateTimeOffset.Now));

        var fp = await rig.Handler.StopAsync("s1");
        await run;

        Assert.NotNull(fp);
        Assert.True(rig.Source.Stopped);
        Assert.Contains(fp.Entries, e => e.Path == Installed && e.IsNew);
        // 已落盘，且最终足迹和流式发现都发给了客户端
        Assert.Equal("s1", Assert.Single(rig.Store.ReadAll()).Id);
        Assert.Contains(rig.Sent, r => r.Type == AgentMessageType.Discovery && r.Discovery!.Path == Installed);
        Assert.Contains(rig.Sent, r => r.Type == AgentMessageType.Footprint);
    }

    // 核心回归：在拿到根 PID 之前就到达的事件（含根进程树里子进程的 ProcessStart 与写盘），
    // 必须被缓冲、在 SetRoot 后回放并正确按根归属为「确定」，且不得因“早于根”把足迹误标不完整。
    [Fact]
    public async Task Events_before_setroot_are_buffered_replayed_and_attributed()
    {
        const int child = 5555;
        const string rootFile = @"C:\Program Files\Foo\root.dll";
        const string childFile = @"C:\Program Files\Foo\child.dll";

        using var rig = new Rig();
        var run = rig.Handler.StartAsync(@"C:\setup.exe", rig.Send, CancellationToken.None, "s1");
        await rig.WaitStarted();
        await rig.WaitReady();

        // 全部早于 SetRoot（模拟 ETW 已在抓、而根 PID 还没从 App 发回来的那几百毫秒）
        rig.Source!.Push(new FileEvent(Root, rootFile, true, DateTimeOffset.Now));
        rig.Source.Push(new ProcessStartEvent(child, Root, @"C:\setup_child.exe", DateTimeOffset.Now));
        rig.Source.Push(new FileEvent(child, childFile, true, DateTimeOffset.Now));

        rig.Handler.SetRoot("s1", Root);
        var fp = await rig.Handler.StopAsync("s1");
        await run;

        Assert.NotNull(fp);
        // ① 早到的事件没被丢：确实进了足迹，且按根正确归属为「确定」（含靠回放的子进程 ProcessStart 建立的父子关系）
        var r = Assert.Single(fp.Entries, e => e.Path == rootFile);
        Assert.Equal(FootprintConfidence.Certain, r.Confidence);
        var c = Assert.Single(fp.Entries, e => e.Path == childFile);
        Assert.Equal(FootprintConfidence.Certain, c.Confidence);   // 子进程的 ProcessStart 也在缓冲里回放了
        // ② 绝不因“早于根”而凭空多出“丢事件/漏记/未收到根进程号”这类不完整原因（修复前开头窗口要么漏记要么会误标）
        Assert.DoesNotContain(fp.Completeness.Reasons, s => s.Contains("丢事件") || s.Contains("未收到根进程号"));
    }

    [Fact]
    public async Task Ready_is_sent_before_root_arrives()
    {
        using var rig = new Rig();
        var run = rig.Handler.StartAsync(@"C:\setup.exe", rig.Send, CancellationToken.None, "s1");
        await rig.WaitStarted();
        await rig.WaitReady();

        // 还没 SetRoot：会话仍在进行，且尚未出任何足迹
        Assert.False(run.IsCompleted);
        Assert.False(rig.Has(AgentMessageType.Footprint));

        rig.Handler.SetRoot("s1", Root);
        await rig.Handler.StopAsync("s1");
        await run;
    }

    [Fact]
    public async Task No_root_before_timeout_finalizes_as_incomplete()
    {
        using var rig = new Rig(limits: new MonitorLimits(
            TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5), TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(150)));
        // 回了 Ready 但一直收不到根 PID（App 启动安装器失败/用户取消/断线）→ 到超时自行收尾
        await rig.Handler.StartAsync(@"C:\setup.exe", rig.Send, CancellationToken.None, "s1");

        var fp = Assert.Single(rig.Store.ReadAll());
        Assert.False(fp.Completeness.IsComplete);
        Assert.Contains(fp.Completeness.Reasons, s => s.Contains("未收到根进程号"));
        Assert.Contains(rig.Sent, r => r.Type == AgentMessageType.Footprint);
        Assert.True(rig.Source!.Stopped);
    }

    [Fact]
    public async Task Second_session_rejected()
    {
        using var rig = new Rig();
        var run = rig.Handler.StartAsync(@"C:\setup.exe", rig.Send, CancellationToken.None, "s1");
        await rig.WaitStarted();
        await rig.WaitReady();

        var second = new List<AgentResponse>();
        await rig.Handler.StartAsync(@"C:\other.exe", r => { second.Add(r); return Task.CompletedTask; },
            CancellationToken.None, "s2");

        var err = Assert.Single(second);
        Assert.Equal(AgentMessageType.Result, err.Type);
        Assert.Contains("已有监控会话在进行", err.ErrorMessage);
        Assert.False(run.IsCompleted);   // 第一个会话不受影响

        rig.Handler.SetRoot("s1", Root);
        await rig.Handler.StopAsync("s1");
        await run;
    }

    [Fact]
    public async Task Etw_unavailable_returns_error()
    {
        using var rig = new Rig(unavailable: true);
        await rig.Handler.StartAsync(@"C:\setup.exe", rig.Send, CancellationToken.None, "s1");   // 不抛

        var err = Assert.Single(rig.Sent);
        Assert.Equal(AgentMessageType.Result, err.Type);
        Assert.Contains("内核跟踪被占用", err.ErrorMessage);
        Assert.Empty(rig.Store.ReadAll());

        // 失败的会话不能占着单例名额：ETW 恢复后再开一个正常会话应当能成功落盘（证明名额确实释放了）
        rig.Unavailable = false;
        rig.Source = null;
        var run = rig.Handler.StartAsync(@"C:\setup.exe", rig.Send, CancellationToken.None, "s2");
        await rig.WaitStarted();
        await rig.WaitReady();
        rig.Handler.SetRoot("s2", Root);
        rig.Source!.Push(new FileEvent(Root, Installed, true, DateTimeOffset.Now));

        var fp = await rig.Handler.StopAsync("s2");
        await run;

        Assert.NotNull(fp);
        Assert.Equal("s2", Assert.Single(rig.Store.ReadAll()).Id);
    }

    [Fact]
    public async Task Root_tree_exit_finalizes_as_complete_without_idle_wait()
    {
        // Idle/Max/RootWait 都设成 5 分钟：只有“根进程树退出”这条正常完成路径能在短时间内收尾。
        // 若该路径没接上，测试会卡到 5 分钟（WaitAsync 兜底防止真卡住）。
        using var rig = new Rig(limits: new MonitorLimits(
            TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5), TimeSpan.FromMilliseconds(20), TimeSpan.FromMinutes(5)));
        var run = rig.Handler.StartAsync(@"C:\setup.exe", rig.Send, CancellationToken.None, "s1");
        await rig.WaitStarted();
        await rig.WaitReady();
        rig.Handler.SetRoot("s1", Root);

        // 假安装器写了一个文件就整体退出（无子进程）：根 ProcessStop 一到，会话应在根退出后立即收尾
        rig.Source!.Push(new FileEvent(Root, Installed, true, DateTimeOffset.Now));
        rig.Source.Push(new ProcessStopEvent(Root, DateTimeOffset.Now));

        await run.WaitAsync(TimeSpan.FromSeconds(10));

        var fp = Assert.Single(rig.Store.ReadAll());
        // ① 正常完成：足迹标「完整」，且不含“静默/提前结束”这类不完整原因
        Assert.True(fp.Completeness.IsComplete);
        Assert.DoesNotContain(fp.Completeness.Reasons, s => s.Contains("静默") || s.Contains("提前结束"));
        // ② 该记的照样记：安装文件按根归属为「确定」
        Assert.Contains(fp.Entries, e => e.Path == Installed && e.Confidence == FootprintConfidence.Certain);
        Assert.True(rig.Source.Stopped);
        Assert.Contains(rig.Sent, r => r.Type == AgentMessageType.Footprint);
    }

    [Fact]
    public async Task Root_tree_exit_still_incomplete_when_events_lost()
    {
        using var rig = new Rig(limits: new MonitorLimits(
            TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5), TimeSpan.FromMilliseconds(20), TimeSpan.FromMinutes(5)));
        var run = rig.Handler.StartAsync(@"C:\setup.exe", rig.Send, CancellationToken.None, "s1");
        await rig.WaitStarted();
        await rig.WaitReady();
        rig.Handler.SetRoot("s1", Root);

        rig.Source!.Lose(3);   // 期间丢了事件
        rig.Source.Push(new FileEvent(Root, Installed, true, DateTimeOffset.Now));
        rig.Source.Push(new ProcessStopEvent(Root, DateTimeOffset.Now));

        await run.WaitAsync(TimeSpan.FromSeconds(10));

        var fp = Assert.Single(rig.Store.ReadAll());
        // 根树干净退出走正常完成路径（不塞 early-stop），但既有完整性逻辑因丢事件仍判不完整——诚实标注
        Assert.False(fp.Completeness.IsComplete);
        Assert.Contains(fp.Completeness.Reasons, s => s.Contains("丢事件"));
        Assert.DoesNotContain(fp.Completeness.Reasons, s => s.Contains("静默") || s.Contains("提前结束"));
    }

    [Fact]
    public async Task Idle_timeout_finalizes_as_incomplete()
    {
        using var rig = new Rig(limits: new MonitorLimits(
            TimeSpan.FromMilliseconds(150), TimeSpan.FromMinutes(5), TimeSpan.FromMilliseconds(20), TimeSpan.FromMinutes(5)));
        var run = rig.Handler.StartAsync(@"C:\setup.exe", rig.Send, CancellationToken.None, "s1");
        await rig.WaitStarted();
        await rig.WaitReady();
        rig.Handler.SetRoot("s1", Root);   // 已拿到根、无后续事件 → 空闲到期自行结束
        await run;

        var fp = Assert.Single(rig.Store.ReadAll());
        Assert.False(fp.Completeness.IsComplete);
        Assert.Contains(rig.Sent, r => r.Type == AgentMessageType.Footprint);
        Assert.True(rig.Source!.Stopped);
    }

    [Fact]
    public async Task Max_duration_finalizes_even_when_busy()
    {
        using var rig = new Rig(limits: new MonitorLimits(
            TimeSpan.FromMinutes(5), TimeSpan.FromMilliseconds(200), TimeSpan.FromMilliseconds(20), TimeSpan.FromMinutes(5)));
        var run = rig.Handler.StartAsync(@"C:\setup.exe", rig.Send, CancellationToken.None, "s1");
        await rig.WaitStarted();
        await rig.WaitReady();
        rig.Handler.SetRoot("s1", Root);
        // 一直有树内活动，空闲永远不到期；只有最长时长能收尾
        while (!run.IsCompleted)
        {
            rig.Source!.Push(new FileEvent(Root, Installed, true, DateTimeOffset.Now));
            await Task.Delay(10);
        }
        var fp = Assert.Single(rig.Store.ReadAll());
        Assert.False(fp.Completeness.IsComplete);
    }

    [Fact]
    public async Task Cancellation_finalizes_and_saves_promptly()
    {
        using var rig = new Rig();   // 空闲/最长都是 5 分钟：若不处理取消，测试会卡到超时
        using var cts = new CancellationTokenSource();
        var run = rig.Handler.StartAsync(@"C:\setup.exe", rig.Send, cts.Token, "s1");
        await rig.WaitStarted();
        await rig.WaitReady();
        rig.Handler.SetRoot("s1", Root);

        cts.Cancel();
        await run.WaitAsync(TimeSpan.FromSeconds(5));   // 迅速返回，不空转

        var fp = Assert.Single(rig.Store.ReadAll());
        Assert.False(fp.Completeness.IsComplete);
        Assert.True(rig.Source!.Stopped);
    }

    [Fact]
    public async Task Stop_unknown_session_reads_back_from_store()
    {
        using var rig = new Rig();
        Assert.Null(await rig.Handler.StopAsync("nope"));
    }

    // 假名册：每次 Snapshot 依次返回预设的一组；Throw 时抛出
    private sealed class FakeInventory(params InventoryItem[][] snapshots) : ISystemInventory
    {
        private int _i;
        public bool Throw;
        public IReadOnlyCollection<InventoryItem> Snapshot()
        {
            if (Throw) throw new IOException("读不到");
            return snapshots[Math.Min(_i++, snapshots.Length - 1)];
        }
    }

    private async Task<MonitorFootprint> RunToRootExit(Rig rig)
    {
        var run = rig.Handler.StartAsync(@"C:\setup.exe", rig.Send, CancellationToken.None, "s1");
        await rig.WaitStarted();
        await rig.WaitReady();
        rig.Handler.SetRoot("s1", Root);
        rig.Source!.Push(new ProcessStopEvent(Root, DateTimeOffset.Now));
        await run.WaitAsync(TimeSpan.FromSeconds(10));
        return Assert.Single(rig.Store.ReadAll());
    }

    [Fact]
    public async Task Services_and_tasks_that_appear_during_monitoring_are_possible_entries()
    {
        var svc = new InventoryItem(FootprintEntryType.Service, "Spooler");
        var task = new InventoryItem(FootprintEntryType.ScheduledTask, @"\FooUpdater");
        var inv = new FakeInventory([svc], [svc, task]);   // 开始时只有 Spooler，结束时多了一个任务
        using var rig = new Rig(limits: new MonitorLimits(
            TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5), TimeSpan.FromMilliseconds(20), TimeSpan.FromMinutes(5)), inventory: inv);

        var fp = await RunToRootExit(rig);

        var e = Assert.Single(fp.Entries);
        Assert.Equal(@"\FooUpdater", e.Path);
        Assert.Equal(FootprintConfidence.Possible, e.Confidence);
        Assert.True(fp.Completeness.IsComplete);
    }

    [Fact]
    public async Task Inventory_failure_marks_footprint_incomplete()
    {
        var inv = new FakeInventory([]) { Throw = true };
        using var rig = new Rig(limits: new MonitorLimits(
            TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5), TimeSpan.FromMilliseconds(20), TimeSpan.FromMinutes(5)), inventory: inv);

        var fp = await RunToRootExit(rig);

        Assert.False(fp.Completeness.IsComplete);
        Assert.Contains(fp.Completeness.Reasons, r => r.Contains("计划任务"));
    }

    [Fact]
    public async Task Existing_service_whose_type_changed_is_not_reported_as_new_driver()
    {
        // 同名的服务在两次快照之间被读成了驱动（重新配置 / 某次读不到类型）：它是原有的，不是新增
        var inv = new FakeInventory(
            [new InventoryItem(FootprintEntryType.Service, "Foo")],
            [new InventoryItem(FootprintEntryType.Driver, "foo")]);
        using var rig = new Rig(limits: new MonitorLimits(
            TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5), TimeSpan.FromMilliseconds(20), TimeSpan.FromMinutes(5)), inventory: inv);

        var fp = await RunToRootExit(rig);

        Assert.Empty(fp.Entries);
    }

    [Fact]
    public async Task User_stop_before_installer_started_saves_nothing()
    {
        using var rig = new Rig();
        var run = rig.Handler.StartAsync(@"C:\setup.exe", rig.Send, CancellationToken.None, "s1");
        await rig.WaitStarted();
        await rig.WaitReady();

        await rig.Handler.StopAsync("s1");   // 还没 SetRoot：安装器没启动
        await run.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Empty(rig.Store.ReadAll());   // 空足迹不落盘，免得顶掉"最近一次"的真实结果
    }
}
