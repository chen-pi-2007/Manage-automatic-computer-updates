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
        // 仅构造时的根化身为 true；按化身而非 PID 值判定，根退出后 PID 被复用的新进程不会继承它
        public bool IsRoot;
    }

    private readonly IReadOnlySet<string> _delegateImageNames;
    private readonly Dictionary<int, List<Incarnation>> _byPid = new();
    private readonly object _lock = new();

    public ProcessTree(int rootPid, IReadOnlySet<string> delegateImageNames)
    {
        _delegateImageNames = delegateImageNames;
        // 根进程在构造时即视为树内：从最早时刻存活，不需要等到观察到它的启动事件
        _byPid[rootPid] = new List<Incarnation>
        {
            new() { Pid = rootPid, ParentPid = 0, Start = DateTimeOffset.MinValue, IsRoot = true },
        };
    }

    public void Observe(ProcessStartEvent e)
    {
        lock (_lock)
        {
            if (!_byPid.TryGetValue(e.Pid, out var list))
                _byPid[e.Pid] = list = new List<Incarnation>();
            if (list.Count > 0 && list[^1].Stop is null)
            {
                var last = list[^1];
                if (last.IsRoot && last.Start == DateTimeOffset.MinValue)
                {
                    // 根进程的首个启动事件：补全构造时的占位化身，不新开一个
                    last.ParentPid = e.ParentPid;
                    last.ImagePath = e.ImagePath;
                    return;
                }
                // 同 PID 又来一次启动而没收到退出事件：旧化身在此刻结束（PID 已被复用）
                last.Stop = e.Time;
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
                if (cur.IsRoot) return AttributionLevel.Tree;
                cur = Find(cur.ParentPid, cur.Start);
            }

            var name = Path.GetFileName(self.ImagePath).ToLowerInvariant();
            return name.Length > 0 && _delegateImageNames.Contains(name)
                ? AttributionLevel.Delegated
                : AttributionLevel.Outside;
        }
    }

    /// <summary>
    /// 根进程树是否已整体退出：根化身已 Stop，且树内所有“曾经进入过树”的后代化身在 <paramref name="at"/> 时刻都已退出
    /// （没有仍存活的树内进程）。用于监控的“正常完成”提前收尾——根树干净退出即收尾，不必空等满 Idle 静默。
    /// 要点：
    ///   · 判定基于化身（带 Start/Stop 区间）而非裸 PID，PID 复用不会误判（复用根 PID 的外来进程不算树内）；
    ///   · 只认“曾进入过树”的后代；从未出现过子进程时，根一退出即算整树退出；
    ///   · 根 ProcessStop 可能晚到或丢失——此时返回 false，交由 Idle/Max 兜底，两者不冲突。
    /// </summary>
    public bool RootTreeHasExited(DateTimeOffset at)
    {
        lock (_lock)
        {
            Incarnation? root = null;
            foreach (var list in _byPid.Values)
                foreach (var inc in list)
                    if (inc.IsRoot) root = inc;

            // 根化身尚未退出（或根 ProcessStop 事件还没到/丢了）→ 不算整树退出
            if (root is null || root.Stop is null || root.Stop > at) return false;

            // 还有任何“树内”化身在 at 时刻仍存活 → 整树尚未退出
            foreach (var list in _byPid.Values)
                foreach (var inc in list)
                {
                    var aliveAtAt = inc.Start <= at && (inc.Stop is null || inc.Stop > at);
                    if (aliveAtAt && InTree(inc)) return false;
                }
            return true;
        }
    }

    // 该化身是否属于根进程树：沿父链上溯（父按子的启动时刻定位，避开复用 PID）能到达根化身
    private bool InTree(Incarnation inc)
    {
        var cur = inc;
        for (var depth = 0; cur is not null && depth < 256; depth++)
        {
            if (cur.IsRoot) return true;
            cur = Find(cur.ParentPid, cur.Start);
        }
        return false;
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
