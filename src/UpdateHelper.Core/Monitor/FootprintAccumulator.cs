namespace UpdateHelper.Core.Monitor;

/// <summary>
/// 足迹累积器：监控期间只“记账”（每个路径一本账，记下谁、何时、建还是删），
/// 不在途中下结论；Finalize 时才按固定顺序判定：归属 → 落点 → 净效果 → 复验存在。
/// 先记后判，是因为归属依赖完整的进程树，净效果依赖最后一次动作，中途判断都会误判。
/// 事件可能来自 ETW 的多个线程，所以所有状态访问都加锁。
/// </summary>
public sealed class FootprintAccumulator
{
    // 一次动作：谁在何时做的。IsCreate = 真的新建；IsDelete = 删除；两者都为 false = 只是动过（改名落到这里、改写已有值）。
    // Uncertain：IsCreate 是“可能新建”（空 REG_NONE 值，见 RegistryEvent），收尾时要所在的键是本次新建的才认
    private sealed record Act(int Pid, DateTimeOffset Time, bool IsCreate, bool IsDelete, bool Uncertain = false);

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
    private readonly List<(int Pid, DateTimeOffset Time)> _unresolvedRenames = new();  // 查不到原文件名的改名
    // 文件账的路径有序索引：目录改名时按前缀区间找下级文件，不用每次扫全部账本（账本里有全系统所有进程的记录）
    private readonly SortedSet<string> _filePaths = new(StringComparer.OrdinalIgnoreCase);
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
                        // ValueName 非 null 就是“值”（空串是默认值，也是值，路径以 \ 结尾）；null 才是“键”。
                        // 值的路径 = 键路径\值名
                        var isValue = re.ValueName is not null;
                        var path = isValue ? re.KeyPath + "\\" + re.ValueName : re.KeyPath;
                        Add(isValue ? FootprintEntryType.RegistryValue : FootprintEntryType.RegistryKey, path, null,
                            new Act(re.Pid, re.Time, re.Kind == MonitorEventKind.RegistryCreate || re.IsNew,
                                re.Kind == MonitorEventKind.RegistryDelete, re.IsNewUncertain));
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
        {
            _ledgers[key] = l = new Ledger { Type = type, Path = path };
            if (type == FootprintEntryType.File) _filePaths.Add(path);
        }
        l.VolumeSerial ??= volumeSerial;
        l.Acts.Add(act);
    }

    private bool RemoveFileLedger(string path, out Ledger ledger)
    {
        if (!_ledgers.Remove(Key(FootprintEntryType.File, path), out ledger!)) return false;
        _filePaths.Remove(path);
        return true;
    }

    /// <summary>
    /// 改名：把旧路径那本账【整本搬】到新路径，再记一次“动过”。
    /// 搬账而不是另起一本，是为了让“是不是本次新建的”跟着文件走：
    /// 临时名是树内新建的 → 最终名也算新建；改的是原本就有的文件 → 最终名只算动过，不算新建。
    /// 改的若是目录，目录下已经记了账的文件一起搬，否则它们会因旧路径不存在而在复验时被当成幽灵丢掉。
    /// </summary>
    private void Rename(FileRenameEvent fr)
    {
        if (string.IsNullOrEmpty(fr.OldPath))
        {
            // 查不到原名：若原名是本次新建的临时文件，新建身份就丢了。不编，记下来，属于树内就标不完整
            _unresolvedRenames.Add((fr.Pid, fr.Time));
        }
        else if (!string.Equals(fr.OldPath, fr.NewPath, StringComparison.OrdinalIgnoreCase))
        {
            if (RemoveFileLedger(fr.OldPath, out var self))
            {
                MoveLedger(self, fr.NewPath);   // 旧名本身有账 → 它是文件（目录不记账），没有下级可搬
            }
            else
            {
                // 可能是目录改名：按前缀区间找已记账的下级文件，一起搬
                var oldPrefix = fr.OldPath.TrimEnd('\\') + "\\";
                var newPrefix = fr.NewPath.TrimEnd('\\') + "\\";
                var children = _filePaths.GetViewBetween(oldPrefix, oldPrefix + '￿').ToList();
                foreach (var childPath in children)
                    if (RemoveFileLedger(childPath, out var child))
                        MoveLedger(child, newPrefix + childPath[oldPrefix.Length..]);
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
            if (source.Type == FootprintEntryType.File) _filePaths.Add(newPath);
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
                var isNew = owner.IsCreate;
                // 空 REG_NONE 值：只有所在的键是本次（树内/代办）新建的，才能确定这个值是新的
                if (isNew && owner.Uncertain && !KeyCreatedInSession(l.Path)) isNew = false;

                var entry = new FootprintEntry(
                    l.Type, l.Path, l.Type is FootprintEntryType.File or FootprintEntryType.Directory ? l.VolumeSerial : null,
                    confidence, owner.Pid,
                    _imageByPid.TryGetValue(owner.Pid, out var exe) ? exe : null,
                    owner.Time, isNew);

                // ④ 复验：收尾时磁盘/注册表里已不存在的是幽灵记录
                if (!probe.StillExists(entry)) continue;

                // ⑤ “新建”的文件其实早就有了（改名覆盖了用户原有的文件，隧道机制让它继承了原创建时间）→ 只算改动
                if (entry.IsNew && probe.ExistedBefore(entry, _start)) entry = entry with { IsNew = false };

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
            var unresolvedRenames = _unresolvedRenames.Count(u => _tree.Attribute(u.Pid, u.Time) != AttributionLevel.Outside);

            return new MonitorFootprint(id, _start, end, _rootPid, _rootExe,
                FindSoftwareKey(entries), BuildCompleteness(hasAmbiguous, unresolved, unresolvedRenames), entries,
                MonitorFootprint.CurrentEngineVersion);
        }
    }

    /// <summary>值路径（键路径\值名）所在的键，是否在本次监控里由树内/代办进程新建。</summary>
    private bool KeyCreatedInSession(string valuePath)
    {
        var slash = valuePath.LastIndexOf('\\');
        if (slash <= 0) return false;
        if (!_ledgers.TryGetValue(Key(FootprintEntryType.RegistryKey, valuePath[..slash]), out var key)) return false;
        return key.Acts.Any(a => a.IsCreate && _tree.Attribute(a.Pid, a.Time) != AttributionLevel.Outside);
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

    private FootprintCompleteness BuildCompleteness(bool hasAmbiguous, int unresolvedRegistry, int unresolvedRenames)
    {
        var reasons = new List<string>();
        if (_eventsLost > 0) reasons.Add($"监控丢事件 {_eventsLost} 个，部分改动可能没有被记录");
        if (_earlyStop is not null) reasons.Add($"监控提前结束：{_earlyStop}，之后的改动不在记录内");
        if (unresolvedRegistry > 0) reasons.Add($"有 {unresolvedRegistry} 处注册表改动没能确定所在的键，没有被记录");
        if (unresolvedRenames > 0) reasons.Add($"有 {unresolvedRenames} 次改名没能查到原来的文件名，改名后的文件可能被记成了“改动”而不是“新增”");
        if (_inventoryUnavailable) reasons.Add("没能对比监控前后的服务 / 驱动 / 计划任务清单，这几类改动可能没有被记录");
        if (hasAmbiguous) reasons.Add("存在落点无法确定的项，需要你确认是否属于该软件");
        return new FootprintCompleteness(reasons.Count == 0, reasons);
    }
}
