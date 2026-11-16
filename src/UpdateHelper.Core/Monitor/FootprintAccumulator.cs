namespace UpdateHelper.Core.Monitor;

/// <summary>
/// 足迹累积器：监控期间只“记账”（每个路径一本账，记下谁、何时、建还是删），
/// 不在途中下结论；Finalize 时才按固定顺序判定：归属 → 落点 → 净效果 → 复验存在。
/// 先记后判，是因为归属依赖完整的进程树，净效果依赖最后一次动作，中途判断都会误判。
/// 事件可能来自 ETW 的多个线程，所以所有状态访问都加锁。
/// </summary>
public sealed class FootprintAccumulator
{
    // 一次动作：谁在何时做的。IsCreate = 真的新建；IsDelete = 删除；两者都为 false = 只是动过（改名落到这里、改写已有值）
    private sealed record Act(int Pid, DateTimeOffset Time, bool IsCreate, bool IsDelete);

    // 一本账：同一对象（路径/键/值/服务名）的所有动作
    private sealed class Ledger
    {
        public FootprintEntryType Type;
        public string Path = "";
        public string? VolumeSerial;
        public readonly List<Act> Acts = new();
    }

    private readonly ProcessTree _tree;
    private readonly DateTimeOffset _start;
    private readonly int _rootPid;
    private readonly string _rootExe;
    private readonly KnownFolders? _folders; // null 表示取本机真实目录；测试可注入固定值

    private readonly object _lock = new();
    private readonly Dictionary<string, Ledger> _ledgers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<int, string> _imageByPid = new(); // PID → 最近一次映像路径，用于填 Exe
    private readonly List<(int Pid, DateTimeOffset Time)> _unresolvedRegistry = new(); // 没能确定键路径的注册表改动
    private readonly List<(InventoryItem Item, DateTimeOffset At)> _inventoryAdditions = new();
    private bool _inventoryUnavailable;
    private int _eventsLost;
    private string? _earlyStop;

    public FootprintAccumulator(ProcessTree tree, DateTimeOffset start, int rootPid, string rootExe,
        KnownFolders? folders = null)
    {
        _tree = tree;
        _start = start;
        _rootPid = rootPid;
        _rootExe = rootExe;
        _folders = folders;
        _imageByPid[rootPid] = rootExe;
    }

    public void Observe(MonitorEvent e)
    {
        lock (_lock)
        {
            switch (e)
            {
                case ProcessStartEvent ps:
                    _tree.Observe(ps);
                    // 采集侧取不到映像路径时给空串：别用它盖掉已知的路径（比如根进程的安装包路径）
                    if (ps.ImagePath.Length > 0) _imageByPid[ps.Pid] = ps.ImagePath;
                    break;
                case ProcessStopEvent pe:
                    _tree.Observe(pe);
                    break;
                case FileEvent fe:
                    Add(FootprintEntryType.File, fe.Path, fe.VolumeSerial,
                        new Act(fe.Pid, fe.Time, fe.IsCreate, !fe.IsCreate));
                    break;
                case FileRenameEvent fr:
                    Rename(fr);
                    break;
                case RegistryEvent re:
                    {
                        // 采集侧没能确定落在哪个键：不编路径，先记下，收尾时若属于树内动作就标不完整
                        if (string.IsNullOrEmpty(re.KeyPath))
                        {
                            _unresolvedRegistry.Add((re.Pid, re.Time));
                            break;
                        }
                        // 带值名的是“值”，否则是“键”；值的路径 = 键路径\值名
                        var isValue = !string.IsNullOrEmpty(re.ValueName);
                        var path = isValue ? re.KeyPath + "\\" + re.ValueName : re.KeyPath;
                        Add(isValue ? FootprintEntryType.RegistryValue : FootprintEntryType.RegistryKey, path, null,
                            new Act(re.Pid, re.Time, re.Kind == MonitorEventKind.RegistryCreate || re.IsNew,
                                re.Kind == MonitorEventKind.RegistryDelete));
                        break;
                    }
                case ServiceOrDriverEvent se:
                    Add(se.IsDriver ? FootprintEntryType.Driver : FootprintEntryType.Service, se.Name, null,
                        new Act(se.Pid, se.Time, true, false));
                    break;
            }
        }
    }

    public void RecordEventsLost(int count)
    {
        lock (_lock) _eventsLost += count;
    }

    /// <summary>非自然收尾（用户停止/超时/检测到重启）：之后的动作没被看到，必须标不完整。</summary>
    public void RecordEarlyStop(string reason)
    {
        lock (_lock) _earlyStop = reason;
    }

    /// <summary>
    /// 监控前后系统名册的差集：这段时间里新出现的服务 / 驱动 / 计划任务。
    /// 它们由系统进程代写、按进程树归不到安装器，所以一律记「可能」档，由用户确认。
    /// </summary>
    public void AddInventoryAdditions(IEnumerable<InventoryItem> items, DateTimeOffset at)
    {
        lock (_lock)
            foreach (var i in items) _inventoryAdditions.Add((i, at));
    }

    /// <summary>名册没取到（取快照出错）：服务 / 驱动 / 计划任务这几类可能漏记，必须标不完整。</summary>
    public void RecordInventoryUnavailable()
    {
        lock (_lock) _inventoryUnavailable = true;
    }

    private static string Key(FootprintEntryType type, string path) => (int)type + "|" + path;   // 键里带类型，避免服务名与文件路径等碰撞

    private void Add(FootprintEntryType type, string path, string? volumeSerial, Act act)
    {
        var key = Key(type, path);
        if (!_ledgers.TryGetValue(key, out var l))
            _ledgers[key] = l = new Ledger { Type = type, Path = path };
        l.VolumeSerial ??= volumeSerial;
        l.Acts.Add(act);
    }

    /// <summary>
    /// 改名：把旧路径那本账【整本搬】到新路径，再记一次“动过”。
    /// 搬账而不是另起一本，是为了让“是不是本次新建的”跟着文件走：
    /// 临时名是树内新建的 → 最终名也算新建；改的是原本就有的文件 → 最终名只算动过，不算新建。
    /// 改的若是目录，目录下已经记了账的文件一起搬，否则它们会因旧路径不存在而在复验时被当成幽灵丢掉。
    /// </summary>
    private void Rename(FileRenameEvent fr)
    {
        if (!string.IsNullOrEmpty(fr.OldPath) && !string.Equals(fr.OldPath, fr.NewPath, StringComparison.OrdinalIgnoreCase))
        {
            if (_ledgers.Remove(Key(FootprintEntryType.File, fr.OldPath), out var self))
                MoveLedger(self, fr.NewPath);

            var oldPrefix = fr.OldPath.TrimEnd('\\') + "\\";
            var newPrefix = fr.NewPath.TrimEnd('\\') + "\\";
            var children = _ledgers
                .Where(kv => kv.Value.Type == FootprintEntryType.File
                             && kv.Value.Path.StartsWith(oldPrefix, StringComparison.OrdinalIgnoreCase))
                .ToList();
            foreach (var (key, child) in children)
            {
                _ledgers.Remove(key);
                MoveLedger(child, newPrefix + child.Path[oldPrefix.Length..]);
            }
        }
        Add(FootprintEntryType.File, fr.NewPath, fr.VolumeSerial, new Act(fr.Pid, fr.Time, false, false));
    }

    private void MoveLedger(Ledger source, string newPath)
    {
        var key = Key(source.Type, newPath);
        if (_ledgers.TryGetValue(key, out var existing))
        {
            // 目标路径已有账（比如被覆盖的旧文件）：并进去，保持时间先后
            existing.Acts.AddRange(source.Acts);
            existing.VolumeSerial ??= source.VolumeSerial;
        }
        else
        {
            source.Path = newPath;
            _ledgers[key] = source;
        }
    }

    public MonitorFootprint Finalize(string id, IFootprintProbe probe, DateTimeOffset end)
    {
        lock (_lock)
        {
            var entries = new List<FootprintEntry>();
            var hasAmbiguous = false;

            foreach (var l in _ledgers.Values)
            {
                // 系统自己顺手写的注册表噪声（执行记录、界面缓存等）不是软件的足迹
                if (l.Type is FootprintEntryType.RegistryKey or FootprintEntryType.RegistryValue
                    && RegistryNoise.IsNoise(l.Path))
                    continue;

                // ① 归属：只认树内/代办进程的动作，且以最早的“树内”动作为准（没有再取代办）。
                //    树外进程碰过同一路径，不影响树内动作的认定。
                Act? owner = null;
                var tier = AttributionLevel.Outside;
                foreach (var a in l.Acts)
                {
                    var lv = _tree.Attribute(a.Pid, a.Time);
                    if (lv == AttributionLevel.Outside) continue;
                    if (owner is null || (lv == AttributionLevel.Tree && tier == AttributionLevel.Delegated))
                    {
                        owner = a;
                        tier = lv;
                    }
                }
                if (owner is null) continue;
                var confidence = tier == AttributionLevel.Tree ? FootprintConfidence.Certain : FootprintConfidence.Possible;

                // ② 落点：仅文件类按路径分类。注册表/服务/驱动不能走 LocationClassifier
                //    （它只认文件路径，注册表键会全部判成模糊而被错误降级），
                //    它们按自身类型视为驻留，只受归属 + 净效果 + 复验约束。
                var ambiguous = false;
                if (l.Type is FootprintEntryType.File or FootprintEntryType.Directory)
                {
                    var cls = _folders is null
                        ? LocationClassifier.Classify(l.Path)
                        : LocationClassifier.Classify(l.Path, _folders);
                    if (cls == LocationClass.Drop) continue;
                    if (cls == LocationClass.Ambiguous)
                    {
                        ambiguous = true;
                        confidence = FootprintConfidence.Possible; // 不丢、也不当确定
                    }
                }

                // ③ 净效果：最后一次动作是删除 → 建了又删（或根本是删旧的），净零，不进足迹
                if (l.Acts[^1].IsDelete) continue;

                // 这条是否本次新建：以认定归属的那次动作为准
                var entry = new FootprintEntry(
                    l.Type, l.Path, l.Type is FootprintEntryType.File or FootprintEntryType.Directory ? l.VolumeSerial : null,
                    confidence, owner.Pid,
                    _imageByPid.TryGetValue(owner.Pid, out var exe) ? exe : null,
                    owner.Time, owner.IsCreate);

                // ④ 复验：收尾时磁盘/注册表里已不存在的是幽灵记录
                if (!probe.StillExists(entry)) continue;

                if (ambiguous) hasAmbiguous = true;
                entries.Add(entry);
            }

            // 系统名册差集：没有可归属的进程（Pid=0），一律「可能」；同样要过复验
            foreach (var (item, at) in _inventoryAdditions)
            {
                var entry = new FootprintEntry(item.Type, item.Name, null, FootprintConfidence.Possible, 0, null, at, true);
                if (probe.StillExists(entry)) entries.Add(entry);
            }

            // ⑤ 同路径合并已由“一路径一本账”保证；按首次出现时间排序让输出稳定
            entries.Sort((a, b) => a.FirstSeen.CompareTo(b.FirstSeen));

            var unresolved = _unresolvedRegistry.Count(u => _tree.Attribute(u.Pid, u.Time) != AttributionLevel.Outside);

            return new MonitorFootprint(id, _start, end, _rootPid, _rootExe,
                FindSoftwareKey(entries), BuildCompleteness(hasAmbiguous, unresolved), entries);
        }
    }

    // 树内新建的 ...\Uninstall\<子键>：取到子键为止，作为本软件的卸载登记键
    private static string? FindSoftwareKey(List<FootprintEntry> entries)
    {
        const string seg = "\\Uninstall\\";
        foreach (var e in entries)
        {
            if (e.Type != FootprintEntryType.RegistryKey || !e.IsNew) continue;
            var i = e.Path.IndexOf(seg, StringComparison.OrdinalIgnoreCase);
            if (i < 0) continue;
            var rest = e.Path.AsSpan(i + seg.Length);
            if (rest.Length == 0) continue;
            var slash = rest.IndexOf('\\');
            return slash < 0 ? e.Path : e.Path[..(i + seg.Length + slash)];
        }
        return null;
    }

    private FootprintCompleteness BuildCompleteness(bool hasAmbiguous, int unresolvedRegistry)
    {
        var reasons = new List<string>();
        if (_eventsLost > 0) reasons.Add($"监控丢事件 {_eventsLost} 个，部分改动可能没有被记录");
        if (_earlyStop is not null) reasons.Add($"监控提前结束：{_earlyStop}，之后的改动不在记录内");
        if (unresolvedRegistry > 0) reasons.Add($"有 {unresolvedRegistry} 处注册表改动没能确定所在的键，没有被记录");
        if (_inventoryUnavailable) reasons.Add("没能对比监控前后的服务 / 驱动 / 计划任务清单，这几类改动可能没有被记录");
        if (hasAmbiguous) reasons.Add("存在落点无法确定的项，需要你确认是否属于该软件");
        return new FootprintCompleteness(reasons.Count == 0, reasons);
    }
}
