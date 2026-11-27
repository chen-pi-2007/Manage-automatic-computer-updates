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
        // 这本账是被改名搬到一个本次监控里从没记录过的路径上的：那个路径原来有没有文件说不清
        // （MoveFileEx 带"替换已有"覆盖时，被替换的文件不留任何事件，真机实测创建时间也不继承）。
        // 收尾时只有落在本次新建的目录里才认它是新增
        public bool LandedOnUnknownPath;
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
    // 本次监控里新建的目录（谁、何时建的）：不进足迹，只用来判断改名目标原来是否存在
    private readonly Dictionary<string, (int Pid, DateTimeOffset Time)> _createdDirs = new(StringComparer.OrdinalIgnoreCase);
    // 外来目录：不是本次新建、而是从别处改名搬来的目录（里面可能是用户原有的文件）。值不用，只用键
    private readonly Dictionary<string, (int Pid, DateTimeOffset Time)> _foreignDirs = new(StringComparer.OrdinalIgnoreCase);
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
                case FileEvent { IsDirectory: true } de:
                    {
                        var dir = de.Path.TrimEnd('\\');
                        ForgetDirsUnder(dir);   // 这里原来的目录（及其下级的记录）已经不是现在这个了
                        // 只记树内/代办进程建的目录：判断时也只认它们，树外的记了白占地方
                        if (de.IsCreate && _tree.Attribute(de.Pid, de.Time) != AttributionLevel.Outside)
                            _createdDirs[dir] = (de.Pid, de.Time);
                        break;
                    }
                case FileEvent fe:
                    // 删除事件不带目录标志：删的可能是目录，它和下级目录的记录一并作废（否则过期记录会被冒用）
                    if (!fe.IsCreate) ForgetDirsUnder(fe.Path.TrimEnd('\\'));
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
    /// 无论旧名自己有没有账，都按前缀把它下面已记账的文件一起搬：旧名有账不代表它是文件
    /// （目录改名也会在新路径记一笔，连续改名两次时旧名就有账了），漏搬会让下级文件在复验时被当成幽灵丢掉。
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
            var oldPath = fr.OldPath.TrimEnd('\\');
            var newPath = fr.NewPath.TrimEnd('\\');
            var hadSelf = RemoveFileLedger(fr.OldPath, out var self);
            if (hadSelf) MoveLedger(self, fr.NewPath, markUnknownTarget: true);

            var oldPrefix = oldPath + "\\";
            var newPrefix = newPath + "\\";
            // 下级文件跟着目录走：目录改名不可能覆盖已有目录，所以下级不打"目标去向不明"的标记
            var children = _filePaths.GetViewBetween(oldPrefix, oldPrefix + '￿').ToList();
            foreach (var childPath in children)
                if (RemoveFileLedger(childPath, out var child))
                    MoveLedger(child, newPrefix + childPath[oldPrefix.Length..], markUnknownTarget: false);

            // 目录记录（新建的 / 外来的）跟着改名走
            ForgetDirsUnder(newPath);   // 目标位置原有的记录已经不是现在这个了
            var isCreatedDir = MigrateDirs(_createdDirs, oldPath, newPath);
            var isForeignDir = MigrateDirs(_foreignDirs, oldPath, newPath);
            // 不是本次新建的东西被搬进了某个新建目录里：若它是个目录，就是"外来目录"，里面装的可能是用户原有的文件，
            // 往上找父目录时先碰到它就不能算"在新建目录里"（是文件也无妨：文件路径不会成为别人的父目录）。
            // 没落进新建目录的不用标：往上本来就找不到新建目录（这样全系统的改名不会把这份记录撑大）
            if (!isCreatedDir && !isForeignDir && UnderCreatedDir(newPath)) _foreignDirs[newPath] = default;

            // 旧名在本次监控里从没出现过，又不是目录：那是监控前就有的文件被改名挪走（典型是先改成 .bak 再放新文件）。
            // 在旧路径留一笔"动过"：之后同一路径上的新建，按"最早的树内动作"就判成改动而不是新增——不依赖文件名隧道。
            // 只给树内/代办的改名留（全系统每次改名都留会白白撑大账本；树外进程的情况由合并时的保守处理兜住）
            if (!hadSelf && children.Count == 0 && !isCreatedDir
                && _tree.Attribute(fr.Pid, fr.Time) != AttributionLevel.Outside)
                Add(FootprintEntryType.File, fr.OldPath, fr.VolumeSerial, new Act(fr.Pid, fr.Time, false, false));
        }
        Add(FootprintEntryType.File, fr.NewPath, fr.VolumeSerial, new Act(fr.Pid, fr.Time, false, false));
    }

    /// <param name="markUnknownTarget">
    /// 文件改名到 newPath 时为 true：目标处原来有没有文件说不清（可能是"改名覆盖"），收尾要额外确认。
    /// 目录改名带着下级搬时为 false：目录改名不可能覆盖已有目录，下级不会覆盖用户文件。
    /// </param>
    private void MoveLedger(Ledger source, string newPath, bool markUnknownTarget)
    {
        var key = Key(source.Type, newPath);
        if (_ledgers.TryGetValue(key, out var existing))
        {
            // 目标路径已有账：并进去，保持时间先后。
            // 只有已有账的最后一个动作是"删除"（确定此刻那里是空的）才信得过；否则那里此刻可能有文件
            // （比如树外程序先把用户文件改名挪走又放回来），这次改名可能是覆盖，同样要额外确认
            if (markUnknownTarget && !(existing.Acts.Count > 0 && existing.Acts[^1].IsDelete))
                existing.LandedOnUnknownPath = true;
            existing.Acts.AddRange(source.Acts);
            existing.VolumeSerial ??= source.VolumeSerial;
            existing.LandedOnUnknownPath |= source.LandedOnUnknownPath;
        }
        else
        {
            source.Path = newPath;
            source.LandedOnUnknownPath |= markUnknownTarget;   // 目标在本次监控里没有任何记录：原来有没有文件说不清
            _ledgers[key] = source;
            if (source.Type == FootprintEntryType.File) _filePaths.Add(newPath);
        }
    }

    /// <summary>作废某目录及其下级的目录记录（新建的、外来的都清）。</summary>
    private void ForgetDirsUnder(string dir)
    {
        var prefix = dir + "\\";
        foreach (var map in new IDictionary<string, (int, DateTimeOffset)>[] { _createdDirs, _foreignDirs })
            foreach (var d in map.Keys.Where(d => d.Equals(dir, StringComparison.OrdinalIgnoreCase)
                                                  || d.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToList())
                map.Remove(d);
    }

    /// <summary>把 oldDir 及其下级的目录记录改到 newDir 下；返回 oldDir 本身是否在记录里。</summary>
    private static bool MigrateDirs(Dictionary<string, (int Pid, DateTimeOffset Time)> map, string oldDir, string newDir)
    {
        var prefix = oldDir + "\\";
        var self = false;
        foreach (var d in map.Keys.Where(d => d.Equals(oldDir, StringComparison.OrdinalIgnoreCase)
                                              || d.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToList())
        {
            self |= d.Equals(oldDir, StringComparison.OrdinalIgnoreCase);
            var info = map[d];
            map.Remove(d);
            map[newDir + d[oldDir.Length..]] = info;
        }
        return self;
    }

    /// <summary>
    /// 路径落在本次监控里由树内/代办进程新建的目录之下（新目录里的东西不可能是用户原有的）。
    /// 往上找时先碰到"外来目录"（别处搬来的、可能装着用户原有文件）就不算。
    /// </summary>
    private bool UnderCreatedDir(string path)
    {
        for (var dir = Path.GetDirectoryName(path); !string.IsNullOrEmpty(dir); dir = Path.GetDirectoryName(dir))
        {
            var d = dir.TrimEnd('\\');
            if (_foreignDirs.ContainsKey(d)) return false;
            if (_createdDirs.TryGetValue(d, out var who) && _tree.Attribute(who.Pid, who.Time) != AttributionLevel.Outside)
                return true;
        }
        return false;
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
                // 改名搬到一个本次没记录过的路径上：可能覆盖了用户原有的文件，只有落在本次新建的目录里才确定是新的
                if (isNew && l.LandedOnUnknownPath && !UnderCreatedDir(l.Path)) isNew = false;

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
