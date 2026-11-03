using System.Diagnostics;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Session;
using UpdateHelper.Core.Monitor;

namespace UpdateHelper.Agent.Monitor;

/// <summary>
/// ETW 内核事件采集器：只负责把内核事件【翻译】成 Core 认识的 MonitorEvent，
/// 不做任何归属 / 落点分类 / 足迹累积（那些全在 Core，可脱离系统 API 单测）。
/// 需要管理员权限；事件按 ETW 投递的时间顺序原样回调，不重排不缓冲
/// （Core 的净效果计算依赖插入顺序）。
/// </summary>
public sealed class EtwMonitorSession(Action<MonitorEvent> onEvent, Action<int> onEventsLost) : IMonitorSource
{
    private readonly DeviceMap _devices = new();
    // KCB 句柄 → 键全路径 的兜底关联表（见 RegistryKeyPathTable 注释）。
    // 只在 pump 线程上被各注册表回调串行读写，无需加锁。
    private readonly RegistryKeyPathTable _regKeys = new();
    private TraceEventSession? _session;
    private Thread? _pump;
    private Timer? _lostPoller;
    private int _lostReported;
    private readonly object _lostGate = new();
    private bool _stopped;

    // NtCreateFile 的 CreateDisposition 取值（高 8 位）。
    // 内核 Create 事件不告诉我们"最终是否真的新建了"，只能按意图过滤：
    //   OPEN(1)/OVERWRITE(4) 要求文件已存在 → 绝不会新建，丢弃；
    //   SUPERSEDE(0)/CREATE(2)/OPEN_IF(3)/OVERWRITE_IF(5) 有可能新建 → 产出 IsCreate:true，
    //   其中"其实是打开已有文件"的误报由 Core 收尾复验与净效果处理。
    private static bool MayCreate(int disposition) => disposition is 0 or 2 or 3 or 5;

    // FILE_DIRECTORY_FILE：这是在开目录。Ruling-4：本阶段不记目录，目录靠里面的文件体现
    private const uint FileDirectoryFile = 0x1;

    /// <summary>
    /// 监控专用的内核会话名：故意【不用】KernelTraceEventParser.KernelSessionName（"NT Kernel Logger"）。
    /// 在 Win8+ 上，TraceEvent 对"非 NT Kernel Logger 名 + EnableKernelProvider"会走私有 system-logger
    /// 槽位（EVENT_TRACE_SYSTEM_LOGGER_MODE），可与反作弊/游戏守护（腾讯 ACE/TP、douyin_guard 等）
    /// 占用的 NT Kernel Logger 【并存】——那正是真机冒烟暴露、本次改动要解决的问题。
    /// 名字取应用专属常量，确保全系统只有我们自己会用这个名字。
    /// </summary>
    public const string MonitorSessionName = "UpdateHelperMonitorKernel";

    public void Start()
    {
        // 走 Win8+ 私有内核会话：用我们自己的专属会话名，与反作弊占着的 NT Kernel Logger 并存。
        // 我们这里采集的四类 Keywords（FileIOInit / Registry / Process / ImageLoad）都是 OS 内核关键字，
        // 不属于 TraceEvent 的 NonOSKeywords，私有 system-logger 会话【全部受支持】，不会因并存而漏记。
        //
        // 关于同名残留与"不抢占"性质：
        //   · TraceEventSession 的默认（创建）模式在 EnableKernelProvider 时，会把【同名】会话先停掉再重建，
        //     由于 MonitorSessionName 是我们专属的名字，被停掉的只可能是上次崩溃/被杀留下的【我们自己】的残留会话——停自己安全；
        //   · TraceEventSession.Stop() 只针对本会话自己的名字，从不枚举、从不停别人的会话（包括 NT Kernel Logger）。
        //   因此旧代码里"先查 NT Kernel Logger 是否被占、被占就拒绝"的逻辑【整段删除】：NT Kernel Logger 被反作弊占着是常态，正是我们要共存的对象。
        try
        {
            _session = new TraceEventSession(MonitorSessionName)
            {
                StopOnDispose = true,   // 我们退出时一定要关掉内核会话，否则会一直占着私有槽位

                // 调大 OS 为会话预留的缓冲总量，减少"忙碌机器 + 全系统内核事件"下的缓冲溢出丢事件。
                // 真机冒烟里实时会话用的是默认的小缓冲，一忙就 EventsLost>0、足迹几乎总被标不完整。
                //   · BufferSizeMB 是【必须在 EnableKernelProvider 之前】设置的属性（之后设无效）；
                //     文档语义是"OS 为避免丢包而预留的缓冲大小(MB)"——对文件会话默认 64MB，实时会话默认更小，
                //     这里显式拉到 256MB，给一次正常小安装在忙碌机器上留出足够余量，又不至于吃光内存。
                //   · 私有 system-logger / 内核会话对 BufferSizeMB、BufferQuantumKB 这类标准 ETW 缓冲属性
                //     是支持的（它们最终落到 EVENT_TRACE_PROPERTIES 的 BufferSize/Min/MaxBuffers）；
                //     BufferQuantumKB 保持默认 64KB（每缓冲单元大小），不动以免限制单条事件可承载的大小。
                // 注意：缓冲再大也只能降低、不能保证消除丢失——平均到达速率长期超过处理速率时仍会丢。
                // 因此完整性判定【保持诚实】：下面 ReportLost 仍按 EventsLost>0 如实标"不完整"，不靠放宽判定掩盖丢失。
                BufferSizeMB = 256,
            };
            _session.EnableKernelProvider(
                KernelTraceEventParser.Keywords.FileIOInit
                | KernelTraceEventParser.Keywords.Registry
                | KernelTraceEventParser.Keywords.Process
                | KernelTraceEventParser.Keywords.ImageLoad);
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException
                                       or UnauthorizedAccessException or System.ComponentModel.Win32Exception
                                       or NotSupportedException)
        {
            _session?.Dispose();
            _session = null;
            throw new MonitorUnavailableException(UnavailableMessage(ex.Message), ex);
        }

        Subscribe(_session.Source.Kernel);

        // 事件丢失：定时读会话的累计丢失数，把增量透传出去（不丢"丢了"这件事本身，Core 据此标不完整）
        _lostPoller = new Timer(_ => ReportLost(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));

        _pump = new Thread(() =>
        {
            try { _session.Source.Process(); }   // 阻塞到会话停止
            catch (Exception)
            {
                // 停止过程中的异常无意义；非停止时的异常说明采集中断，按丢失上报，但绝不重抛（会崩掉提权进程）
                if (!_stopped) SafeLost(1);
            }
        })
        { IsBackground = true, Name = "EtwMonitorPump" };
        _pump.Start();
    }

    /// <summary>
    /// 私有内核会话创建失败时面向用户的中文提示：私有 system-logger 槽位系统上限约 8 个，
    /// 槽位耗尽、当前系统不支持私有内核跟踪、或无管理员权限等都可能导致失败。
    /// 不再误导用户去关 Process Monitor——本会话与 NT Kernel Logger 井水不犯河水，关不关它都不影响我们。
    /// </summary>
    internal static string UnavailableMessage(string detail) =>
        "无法创建监控用的内核跟踪会话（可能系统内核跟踪槽位已满，或当前系统/权限不支持私有内核跟踪）："
        + detail + "。请稍后重试，或重启后再试。";

        /// <summary>
    /// 回调里 Core/通道抛异常时绝不能让提权 Agent 崩：吞掉并按一次丢失上报（Core 据此标不完整）。
    /// </summary>
    private void Emit(MonitorEvent e)
    {
        try { onEvent(e); }
        catch (Exception) { SafeLost(1); }
    }

    private void SafeLost(int n)
    {
        try { onEventsLost(n); }
        catch (Exception) { /* 上报通道本身坏了也不能崩 */ }
    }

    private void Subscribe(KernelTraceEventParser k)
    {
        k.ProcessStart += d =>
            Emit(new ProcessStartEvent(d.ProcessID, d.ParentID, FullImagePath(d.ProcessID, d.ImageFileName), Ts(d.TimeStamp)));
        k.ProcessStop += d => Emit(new ProcessStopEvent(d.ProcessID, Ts(d.TimeStamp)));

        k.FileIOCreate += d =>
        {
            if ((Convert.ToUInt32(d.CreateOptions) & FileDirectoryFile) != 0) return;
            if (!MayCreate((int)Convert.ToUInt32(d.CreateDisposition))) return;
            var (path, serial) = _devices.Resolve(d.FileName);
            Emit(new FileEvent(d.ProcessID, path, IsCreate: true, Ts(d.TimeStamp), serial));
        };
        // 改名：写临时名 → 改成最终名是常见安装/更新模式。
        // TraceEvent 的 FileIORename 载荷只有旧名（FileObject 解析出来的名字）+ ExtraInfo（不是目标路径），
        // 内核事件本身不带目标新名，所以拿不到最终名：
        //   ① 旧名发 IsCreate:false，让临时名正确净零；
        //   ② 最终名无法补回，必须发"漏记"信号（经 onEventsLost 计 1 次丢失）让 Core 把足迹标"不完整"，绝不静默漏。
        k.FileIORename += d =>
        {
            var (path, serial) = _devices.Resolve(d.FileName);
            Emit(new FileEvent(d.ProcessID, path, IsCreate: false, Ts(d.TimeStamp), serial));
            SafeLost(1);
        };
        k.FileIODelete += d =>
        {
            var (path, serial) = _devices.Resolve(d.FileName);
            Emit(new FileEvent(d.ProcessID, path, IsCreate: false, Ts(d.TimeStamp), serial));
        };

        // 键事件（NameIsKeyName=true，载荷自带键全路径 + KCB 句柄）：既产出足迹，也【喂养】KCB 关联表，
        // 供随后同一句柄上的 SetValue/DeleteValue 在自带 KeyName 为空时回查。
        k.RegistryCreate += d =>
        {
            _regKeys.Record(d.KeyHandle, d.KeyName);
            Emit(new RegistryEvent(d.ProcessID, RegPath(d.KeyName), null, MonitorEventKind.RegistryCreate, Ts(d.TimeStamp)));
        };
        // 打开已有键：不是改动，不产出足迹，但要记下 句柄→全路径，让后续对【已存在键】的 SetValue 也能解析出键路径。
        k.RegistryOpen += d => _regKeys.Record(d.KeyHandle, d.KeyName);
        // KCB 创建/枚举(rundown) 事件：TraceEvent 内部也靠它们建映射；我们同样记一份作兜底
        // （内部映射因丢事件/句柄复用解析不出时顶上）。这些事件由 Registry 关键字自带，订阅它们不增加 ETW 事件量。
        k.RegistryKCBCreate += d => _regKeys.Record(d.KeyHandle, d.KeyName);
        k.RegistryKCBRundownBegin += d => _regKeys.Record(d.KeyHandle, d.KeyName);
        k.RegistryKCBDelete += d => _regKeys.Forget(d.KeyHandle);

        // 值事件（NameIsKeyName=false，载荷只有值名，键靠 KCB 句柄）：键路径优先用事件自带 KeyName
        // （TraceEvent 已解析成功的常态），为空时用句柄回查兜底表——这正是修复"条目只剩值名、键路径丢失"的关键。
        k.RegistrySetValue += d =>
            Emit(new RegistryEvent(d.ProcessID, RegPath(_regKeys.Resolve(d.KeyHandle, d.KeyName)), NullIfEmpty(d.ValueName), MonitorEventKind.RegistrySet, Ts(d.TimeStamp)));
        // 删除键：键事件，KeyName 常态自带；仍走 Resolve 以防自带为空时用兜底表补上。
        k.RegistryDelete += d =>
            Emit(new RegistryEvent(d.ProcessID, RegPath(_regKeys.Resolve(d.KeyHandle, d.KeyName)), null, MonitorEventKind.RegistryDelete, Ts(d.TimeStamp)));
        k.RegistryDeleteValue += d =>
            Emit(new RegistryEvent(d.ProcessID, RegPath(_regKeys.Resolve(d.KeyHandle, d.KeyName)), NullIfEmpty(d.ValueName), MonitorEventKind.RegistryDelete, Ts(d.TimeStamp)));

        // 驱动加载：.sys 映像被加载（ProcessID 通常是 4 = System，归属由 Core 判断）
        k.ImageLoad += d =>
        {
            if (!d.FileName.EndsWith(".sys", StringComparison.OrdinalIgnoreCase)) return;
            var (path, _) = _devices.Resolve(d.FileName);
            Emit(new ServiceOrDriverEvent(d.ProcessID, Path.GetFileNameWithoutExtension(path), path, IsDriver: true, Ts(d.TimeStamp)));
        };
    }

    private static DateTimeOffset Ts(DateTime t) => new(t);
    private static string? NullIfEmpty(string? s) => string.IsNullOrEmpty(s) ? null : s;

    /// <summary>ProcessStart 只给文件名（setup.exe），尽力取完整路径；取不到就用文件名。</summary>
    private static string FullImagePath(int pid, string imageFileName)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            var full = p.MainModule?.FileName;
            if (!string.IsNullOrEmpty(full)) return full;
        }
        catch (Exception) { /* 进程已退出 / 无权访问：退回文件名 */ }
        return imageFileName;
    }

    /// <summary>
    /// 内核注册表路径 \REGISTRY\MACHINE\... → HKLM\...；\REGISTRY\USER\&lt;当前用户SID&gt;\... → HKCU\...；
    /// 其他用户 → HKU\&lt;sid&gt;\...。与 Core 里 RealRegistryProbe 的写法一致。
    /// </summary>
    internal static string RegPath(string? keyName)
    {
        if (string.IsNullOrEmpty(keyName)) return "";
        var p = keyName.TrimStart('\\');
        if (p.StartsWith("REGISTRY\\", StringComparison.OrdinalIgnoreCase)) p = p["REGISTRY\\".Length..];

        if (p.StartsWith("MACHINE", StringComparison.OrdinalIgnoreCase)
            && (p.Length == 7 || p[7] == '\\'))
            return "HKLM" + p[7..];

        if (p.StartsWith("USER\\", StringComparison.OrdinalIgnoreCase))
        {
            var rest = p["USER\\".Length..];
            var sid = CurrentSid;
            if (sid is not null && rest.StartsWith(sid, StringComparison.OrdinalIgnoreCase)
                && (rest.Length == sid.Length || rest[sid.Length] == '\\'))
                return "HKCU" + rest[sid.Length..];
            return "HKU\\" + rest;
        }
        return p;   // 认不出的形式原样交给 Core，不瞎猜
    }

    private static readonly string? CurrentSid =
        System.Security.Principal.WindowsIdentity.GetCurrent().User?.Value;

    private void ReportLost()
    {
        var s = _session;
        if (s is null) return;
        try
        {
            var total = s.EventsLost;
            int delta;
            // 定时器线程与 Stop() 线程都会进来，锁住"读-算增量-写回"，避免重复上报
            lock (_lostGate)
            {
                delta = total - _lostReported;
                if (delta > 0) _lostReported = total;
            }
            if (delta > 0) SafeLost(delta);
        }
        catch (Exception) { /* 会话正在停止，忽略 */ }
    }

    public void Stop()
    {
        if (_stopped) return;
        _stopped = true;
        _lostPoller?.Dispose();
        ReportLost();   // 停之前最后读一次，别漏掉最后一秒的丢失
        _session?.Dispose();   // StopOnDispose：关闭内核会话，Source.Process() 随之返回
        _pump?.Join(TimeSpan.FromSeconds(5));
    }

    public void Dispose() => Stop();
}
