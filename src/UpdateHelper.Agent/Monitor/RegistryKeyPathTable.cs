namespace UpdateHelper.Agent.Monitor;

/// <summary>
/// 注册表 KCB（Key Control Block）句柄 → 键全路径的关联表，纯逻辑、无任何 ETW 依赖，可单测。
///
/// 为什么需要它：内核 RegistrySetValue / RegistryDeleteValue 事件的载荷里只有【值名】，
/// 键本身只由 KCB 句柄（KeyHandle）标识，事件里并不直接带键的全路径。TraceEvent 正常会用它
/// 自己内部的 KCB 映射（state.FileIDToName(KeyHandle)）把句柄解析成键全路径填进 KeyName；
/// 但当 KCB 的创建/枚举事件因缓冲溢出被丢掉、或句柄被复用导致内部映射解析不出时，KeyName 会变成空串
/// —— 真机冒烟里"条目只剩 \DisplayName、SoftwareKey=null"正是这个空键路径导致的。
///
/// 本表作为【兜底】：从那些 NameIsKeyName=true、载荷自带键全路径的事件
/// （RegistryCreate / RegistryOpen / RegistryKCBCreate / RegistryKCBRundownBegin）里，
/// 自己记下 句柄→全路径；SetValue/DeleteValue 若事件自带 KeyName 为空，就回查本表。
///
/// 线程：ETW 事件在同一 pump 线程上串行回调，Record/Resolve/Forget 不并发，无需加锁。
/// 注意：兜底只在"我们确实见过该句柄的键事件"时生效；若连键事件本身都被丢了，仍解析不出
/// —— 那种情况只能靠调大缓冲（减少丢事件）解决，本表不会凭空编造路径。
/// </summary>
public sealed class RegistryKeyPathTable
{
    private readonly Dictionary<ulong, string> _map = new();

    /// <summary>
    /// 记下一个 KCB 句柄对应的键全路径。后写覆盖——句柄被内核复用给新键时，新键的 KCBCreate
    /// 会自然覆盖旧映射，无需显式清理。空句柄或空名直接忽略（没有可记的信息）。
    /// </summary>
    public void Record(ulong keyHandle, string? keyName)
    {
        if (keyHandle == 0 || string.IsNullOrEmpty(keyName)) return;
        _map[keyHandle] = keyName;
    }

    /// <summary>
    /// 解析键全路径：优先用事件自带的 KeyName（TraceEvent 已解析成功的常态路径）；
    /// 自带为空时，用 KCB 句柄回查本表；两者都没有则返回空串（交由上层按空路径处理，绝不瞎猜）。
    /// </summary>
    public string Resolve(ulong keyHandle, string? eventKeyName)
    {
        if (!string.IsNullOrEmpty(eventKeyName)) return eventKeyName;
        if (keyHandle != 0 && _map.TryGetValue(keyHandle, out var name)) return name;
        return "";
    }

    /// <summary>
    /// KCB 被删除时移除映射，避免句柄长期不复用时留下陈旧项、也顺手给表瘦身。
    /// （真正的句柄复用由 Record 的后写覆盖兜住，这里主要是释放内存。）
    /// </summary>
    public void Forget(ulong keyHandle) => _map.Remove(keyHandle);
}
