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
        public readonly List<AgentResponse> Sent = [];
        public readonly MonitorHandler Handler;

        public Rig(bool unavailable = false, MonitorLimits? limits = null)
        {
            Handler = new MonitorHandler(
                (onEvent, onLost) => Source = new FakeSource(onEvent, onLost, unavailable),
                new MonitorFootprintStore(Dir.Path, () => DateTimeOffset.Now),
                new AllExist(), () => DateTimeOffset.Now,
                limits ?? new MonitorLimits(TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5), TimeSpan.FromMilliseconds(20)));
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

        public void Dispose() => Dir.Dispose();
    }

    [Fact]
    public async Task Handler_produces_and_saves_footprint()
    {
        using var rig = new Rig();
        var run = rig.Handler.StartAsync(@"C:\setup.exe", Root, rig.Send, CancellationToken.None, "s1");
        await rig.WaitStarted();
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

    [Fact]
    public async Task Second_session_rejected()
    {
        using var rig = new Rig();
        var run = rig.Handler.StartAsync(@"C:\setup.exe", Root, rig.Send, CancellationToken.None, "s1");
        await rig.WaitStarted();

        var second = new List<AgentResponse>();
        await rig.Handler.StartAsync(@"C:\other.exe", 99, r => { second.Add(r); return Task.CompletedTask; },
            CancellationToken.None, "s2");

        var err = Assert.Single(second);
        Assert.Equal(AgentMessageType.Result, err.Type);
        Assert.Contains("已有监控会话在进行", err.ErrorMessage);
        Assert.False(run.IsCompleted);   // 第一个会话不受影响

        await rig.Handler.StopAsync("s1");
        await run;
    }

    [Fact]
    public async Task Etw_unavailable_returns_error()
    {
        using var rig = new Rig(unavailable: true);
        await rig.Handler.StartAsync(@"C:\setup.exe", Root, rig.Send, CancellationToken.None, "s1");   // 不抛

        var err = Assert.Single(rig.Sent);
        Assert.Equal(AgentMessageType.Result, err.Type);
        Assert.Contains("内核跟踪被占用", err.ErrorMessage);
        // 失败的会话不能占着单例名额：能再开
        Assert.Empty(rig.Store.ReadAll());
    }

    [Fact]
    public async Task Idle_timeout_finalizes_as_incomplete()
    {
        using var rig = new Rig(limits: new MonitorLimits(TimeSpan.FromMilliseconds(150), TimeSpan.FromMinutes(5), TimeSpan.FromMilliseconds(20)));
        await rig.Handler.StartAsync(@"C:\setup.exe", Root, rig.Send, CancellationToken.None, "s1");   // 无事件 → 空闲到期自行结束

        var fp = Assert.Single(rig.Store.ReadAll());
        Assert.False(fp.Completeness.IsComplete);
        Assert.Contains(rig.Sent, r => r.Type == AgentMessageType.Footprint);
        Assert.True(rig.Source!.Stopped);
    }

    [Fact]
    public async Task Max_duration_finalizes_even_when_busy()
    {
        using var rig = new Rig(limits: new MonitorLimits(TimeSpan.FromMinutes(5), TimeSpan.FromMilliseconds(200), TimeSpan.FromMilliseconds(20)));
        var run = rig.Handler.StartAsync(@"C:\setup.exe", Root, rig.Send, CancellationToken.None, "s1");
        await rig.WaitStarted();
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
    public async Task Stop_unknown_session_reads_back_from_store()
    {
        using var rig = new Rig();
        Assert.Null(await rig.Handler.StopAsync("nope"));
    }
}
