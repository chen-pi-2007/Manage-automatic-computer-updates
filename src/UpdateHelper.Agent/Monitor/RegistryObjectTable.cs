namespace UpdateHelper.Agent.Monitor;

/// <summary>
/// 注册表“键对象 → 键全路径”表，纯逻辑、无 ETW 依赖，可单测。
///
/// 为什么需要它：内核的注册表写值/删值/删键事件里只有键对象的地址，不带键路径（真机探针确认 KeyName 恒为空）；
/// 而打开/新建键的事件带“基键对象 + 相对名”。所以要自己把每次打开记下来，顺着基键一层层拼出全路径。
/// 被监控的进程都是在监控开始之后才启动的，它们手里的每个键都是在监控期间打开的，所以这条链是接得上的；
/// 接不上（基键不认识）就返回 null，绝不编造路径。
///
/// 容量：只记不删会无限增长（全系统每秒上千次打开）。用两代轮换：当前代写满就丢掉上一代、当前代降为上一代；
/// 查询命中上一代的会被提回当前代。这样一直在用的长寿命键不会被淘汰，早就不用的才会被挤掉。
/// 线程：ETW 事件在同一个 pump 线程上串行回调，不需要加锁。
/// </summary>
public sealed class RegistryObjectTable(int generationSize = 100_000)
{
    private Dictionary<ulong, string> _current = new();
    private Dictionary<ulong, string> _previous = new();

    /// <summary>
    /// 记下一次“打开/新建键”：返回拼出来的全路径（内核写法，如 \REGISTRY\MACHINE\SOFTWARE\X）；拼不出返回 null。
    /// 相对名以 \REGISTRY 开头就是绝对路径；否则接在基键后面（事件自带基键名时优先用它，没有就查表）。
    /// </summary>
    public string? Record(ulong baseObject, ulong keyObject, int status, string? baseName, string? relativeName)
    {
        if (status != 0 || keyObject == 0) return null;   // 没打开成功：没有对象可记

        var relative = relativeName ?? "";
        string? full;
        if (relative.StartsWith(@"\REGISTRY", StringComparison.OrdinalIgnoreCase))
        {
            full = relative;
        }
        else
        {
            var basePath = !string.IsNullOrEmpty(baseName) ? baseName : Resolve(baseObject);
            full = basePath is null ? null
                : relative.Length == 0 ? basePath
                : basePath.TrimEnd('\\') + "\\" + relative.TrimStart('\\');
        }

        if (full is null)
        {
            // 这个地址现在指向一个我们说不清路径的键：必须抹掉旧记录，否则会拿上一个键的路径冒充它
            _current.Remove(keyObject);
            _previous.Remove(keyObject);
            return null;
        }

        if (_current.Count >= generationSize && !_current.ContainsKey(keyObject))
        {
            _previous = _current;
            _current = new Dictionary<ulong, string>();
        }
        _current[keyObject] = full;
        _previous.Remove(keyObject);
        return full;
    }

    /// <summary>查键对象的全路径；不认识返回 null。</summary>
    public string? Resolve(ulong keyObject)
    {
        if (keyObject == 0) return null;
        if (_current.TryGetValue(keyObject, out var path)) return path;
        if (_previous.Remove(keyObject, out path))
        {
            _current[keyObject] = path;   // 还在用：提回当前代
            return path;
        }
        return null;
    }
}
