namespace UpdateHelper.Core.Monitor;

/// <summary>动作的归属等级：确定（进程树内）/ 可能（已知代办进程）/ 不记。</summary>
public enum AttributionLevel { Tree, Delegated, Outside }

/// <summary>常见的“代办”进程：安装器常把真正的写盘/注册表动作交给它们做。文件名一律小写。</summary>
public static class DelegateImages
{
    public static IReadOnlySet<string> Default { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "msiexec.exe", "services.exe", "trustedinstaller.exe",
    };
}

/// <summary>
/// 安装器进程树。每个 PID 记录一串“化身”（因为 Windows 会复用 PID），
/// 按时间定位到当时存活的那个，再沿父链上溯判断是否属于根进程的子孙。
/// </summary>
public sealed class ProcessTree
{
    private sealed class Incarnation
    {
        public int Pid;
        public int ParentPid;
        public string ImagePath = "";
        public DateTimeOffset Start;
        public DateTimeOffset? Stop;
    }

    private readonly int _rootPid;
    private readonly IReadOnlySet<string> _delegateImageNames;
    private readonly Dictionary<int, List<Incarnation>> _byPid = new();
    private readonly object _lock = new();

    public ProcessTree(int rootPid, IReadOnlySet<string> delegateImageNames)
    {
        _rootPid = rootPid;
        _delegateImageNames = delegateImageNames;
        // 根进程在构造时即视为树内：从最早时刻存活，不需要等到观察到它的启动事件
        _byPid[rootPid] = new List<Incarnation>
        {
            new() { Pid = rootPid, ParentPid = 0, Start = DateTimeOffset.MinValue },
        };
    }

    public void Observe(ProcessStartEvent e)
    {
        lock (_lock)
        {
            if (!_byPid.TryGetValue(e.Pid, out var list))
                _byPid[e.Pid] = list = new List<Incarnation>();
            // 同 PID 又来一次启动：上一个化身若没收到退出事件，就在此刻视为结束（PID 已被复用）
            if (list.Count > 0 && list[^1].Stop is null && e.Pid != _rootPid)
                list[^1].Stop = e.Time;
            else if (list.Count > 0 && list[^1].Stop is null && e.Pid == _rootPid && list[^1].Start == DateTimeOffset.MinValue)
            {
                // 根进程的启动事件：补全构造时的占位化身，不新开一个
                list[^1].ParentPid = e.ParentPid;
                list[^1].ImagePath = e.ImagePath;
                return;
            }
            list.Add(new Incarnation { Pid = e.Pid, ParentPid = e.ParentPid, ImagePath = e.ImagePath, Start = e.Time });
        }
    }

    public void Observe(ProcessStopEvent e)
    {
        lock (_lock)
        {
            if (!_byPid.TryGetValue(e.Pid, out var list)) return;
            for (var i = list.Count - 1; i >= 0; i--)
            {
                if (list[i].Stop is null && list[i].Start <= e.Time) { list[i].Stop = e.Time; return; }
            }
        }
    }

    public AttributionLevel Attribute(int pid, DateTimeOffset at)
    {
        lock (_lock)
        {
            var self = Find(pid, at);
            if (self is null) return AttributionLevel.Outside;

            // 沿父链上溯；父进程的化身也按“子进程启动时刻”定位，避免被复用的 PID 误导
            var cur = self;
            for (var depth = 0; cur is not null && depth < 256; depth++)
            {
                if (cur.Pid == _rootPid) return AttributionLevel.Tree;
                cur = Find(cur.ParentPid, cur.Start);
            }

            var name = Path.GetFileName(self.ImagePath).ToLowerInvariant();
            return name.Length > 0 && _delegateImageNames.Contains(name)
                ? AttributionLevel.Delegated
                : AttributionLevel.Outside;
        }
    }

    // 找 at 时刻存活的化身：Start <= at 且（未退出 或 at <= Stop）；多个时取最晚启动的
    private Incarnation? Find(int pid, DateTimeOffset at)
    {
        if (!_byPid.TryGetValue(pid, out var list)) return null;
        for (var i = list.Count - 1; i >= 0; i--)
        {
            var inc = list[i];
            if (inc.Start <= at && (inc.Stop is null || at <= inc.Stop)) return inc;
        }
        return null;
    }
}
