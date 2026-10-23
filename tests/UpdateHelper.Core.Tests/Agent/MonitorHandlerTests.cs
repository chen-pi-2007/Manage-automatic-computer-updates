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

        public Rig(bool unavailable = false, MonitorLimits? limits = null)
        {
            Unavailable = unavailable;
            Handler = new MonitorHandler(
                (onEvent, onLost) => Source = new FakeSource(onEvent, onLost, Unavailable),
                new MonitorFootprintStore(Dir.Path, () => DateTimeOffset.Now),
                new AllExist(), () => DateTimeOffset.Now,
                limits ?? new MonitorLimits(TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5),
                    TimeSpan.FromMilliseconds(20), TimeSpan.FromMinutes(5)));
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
}
