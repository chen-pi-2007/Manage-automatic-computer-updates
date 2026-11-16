using System.Diagnostics;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Session;
using UpdateHelper.Core.Monitor;

namespace UpdateHelper.Agent.Monitor;

/// <summary>
/// ETW 采集器：订阅三个现代内核清单提供程序（Kernel-File / Kernel-Registry / Kernel-Process），
/// 取出原始字段交给 <see cref="KernelEventTranslator"/> 翻译成 Core 的 MonitorEvent。
/// 不做任何归属 / 落点分类 / 足迹累积（那些全在 Core）。需要管理员权限。
///
/// 为什么不用传统内核提供程序（NT Kernel Logger / system logger）：
///   · 它的文件创建事件只有"打开意图"，分不清"新建"和"打开已有"——用户原有的文件会被当成软件新建的；
///     清单提供程序有专门的 CreateNewFile 事件，只在磁盘上真的新建文件时才发；
///   · 它的改名事件拿不到新名，最终文件会漏记；清单提供程序的 RenamePath 直接给出新路径；
///   · 它的注册表新建键事件不区分"新建 / 打开已有"；清单提供程序带 Disposition；写值事件还带旧值的类型和大小，能判断是不是新值；
///   · 普通会话名即可启用，不占 NT Kernel Logger、也不占有限的 system logger 槽位，与反作弊/游戏守护天然并存。
/// 这些都在真机探针里逐一确认过字段与语义。
/// </summary>
public sealed class EtwMonitorSession : IMonitorSource
{
    /// <summary>监控专用的会话名：应用专属，全系统只有我们自己用；同名会话只可能是我们上次崩溃留下的残留。</summary>
    public const string MonitorSessionName = "UpdateHelperMonitor";

    internal static readonly Guid KernelFile = new("EDD08927-9CC4-4E65-B970-C2560FB5C289");
    internal static readonly Guid KernelRegistry = new("70EB4F03-C1DE-4F73-A051-33D13D5413BD");
    internal static readonly Guid KernelProcess = new("22FB2CD6-0E7B-422B-A0C7-2FAD1FD0E716");

    // Kernel-File：CREATE(打开事件，只用来给改名找旧名) | DELETE_PATH | RENAME_SETLINK_PATH | CREATE_NEW_FILE
    private const ulong FileKeywords = 0x80 | 0x400 | 0x800 | 0x1000;
    // Kernel-Registry：SetValueKey | DeleteValueKey | CreateKey | OpenKey(写已有键的值时靠它知道键路径) | DeleteKey
    private const ulong RegistryKeywords = 0x100 | 0x200 | 0x1000 | 0x2000 | 0x4000;
    // Kernel-Process：WINEVENT_KEYWORD_PROCESS（进程起止，建进程树）
    private const ulong ProcessKeywords = 0x10;

    private readonly Action<MonitorEvent> _onEvent;
    private readonly Action<int> _onEventsLost;
    private readonly DeviceMap _devices = new();
    private readonly KernelEventTranslator _translator;
    private TraceEventSession? _session;
    private Thread? _pump;
    private Timer? _lostPoller;
    private int _lostReported;
    private readonly object _lostGate = new();
    private volatile bool _stopped;

    public EtwMonitorSession(Action<MonitorEvent> onEvent, Action<int> onEventsLost)
    {
        _onEvent = onEvent;
        _onEventsLost = onEventsLost;
        _translator = new KernelEventTranslator(_devices.Resolve, CurrentSid, FullImagePath);
    }

    public void Start()
    {
        try
        {
            // 默认创建模式会先停掉【同名】会话再重建：名字是我们专属的，被停的只可能是自己上次的残留
            _session = new TraceEventSession(MonitorSessionName)
            {
                StopOnDispose = true,   // 退出时一定关掉会话
                BufferSizeMB = 256,     // 全系统事件，忙碌机器上给足缓冲，减少丢事件（必须在启用提供程序之前设）
            };
            _session.EnableProvider(KernelFile, TraceEventLevel.Verbose, FileKeywords);
            _session.EnableProvider(KernelRegistry, TraceEventLevel.Verbose, RegistryKeywords);
            _session.EnableProvider(KernelProcess, TraceEventLevel.Verbose, ProcessKeywords);
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException
                                       or UnauthorizedAccessException or System.ComponentModel.Win32Exception
                                       or NotSupportedException)
        {
            _session?.Dispose();
            _session = null;
            throw new MonitorUnavailableException(UnavailableMessage(ex.Message), ex);
        }

        var parser = new RegisteredTraceEventParser(_session.Source);
        parser.All += OnRawEvent;

        // 事件丢失：定时读会话的累计丢失数，把增量透传出去（Core 据此标不完整）
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

    /// <summary>会话创建失败时面向用户的中文提示。</summary>
    internal static string UnavailableMessage(string detail) =>
        "无法创建监控用的内核跟踪会话（可能权限不足，或当前系统不支持）：" + detail + "。请稍后重试，或重启后再试。";

    private void OnRawEvent(TraceEvent e)
    {
        MonitorEvent? m;
        try
        {
            m = Translate(e);
        }
        catch (Exception)
        {
            // 某个字段格式和预期不符：这一条没法用，按丢失上报（Core 标不完整），绝不能让提权进程崩
            SafeLost(1);
            return;
        }
        if (m is null) return;
        try { _onEvent(m); }
        catch (Exception) { SafeLost(1); }
    }

    private MonitorEvent? Translate(TraceEvent e)
    {
        var id = (int)e.ID;
        var pid = e.ProcessID;
        var time = new DateTimeOffset(e.TimeStamp);

        if (e.ProviderGuid == KernelFile)
        {
            switch (id)
            {
                case 12:   // Create：每次打开都有，只记 文件对象→路径，给改名找旧名
                    _translator.FileOpened(U64(e, "FileObject"), Str(e, "FileName"));
                    return null;
                case 30:   // CreateNewFile：磁盘上真的新建了文件
                    _translator.FileOpened(U64(e, "FileObject"), Str(e, "FileName"));
                    return _translator.FileCreatedNew(pid, Str(e, "FileName"), (uint)U64(e, "CreateOptions"), time);
                case 26:   // DeletePath
                    return _translator.FileDeleted(pid, Str(e, "FilePath"), time);
                case 27:   // RenamePath：FilePath 是新路径
                    return _translator.FileRenamed(pid, U64(e, "FileObject"), Str(e, "FilePath"), time);
            }
            return null;
        }

        if (e.ProviderGuid == KernelRegistry)
        {
            switch (id)
            {
                case 1:    // CreateKey（RegCreateKeyEx：可能新建也可能打开已有，看 Disposition）
                case 2:    // OpenKey
                    return _translator.KeyOpenedOrCreated(pid, U64(e, "BaseObject"), U64(e, "KeyObject"),
                        I32(e, "Status"), id == 1 ? I32(e, "Disposition") : 0, Str(e, "BaseName"), Str(e, "RelativeName"), time);
                case 3:    // DeleteKey
                    return _translator.KeyDeleted(pid, U64(e, "KeyObject"), I32(e, "Status"), time);
                case 5:    // SetValueKey
                    return _translator.ValueSet(pid, U64(e, "KeyObject"), I32(e, "Status"), Str(e, "ValueName"),
                        I32(e, "PreviousDataType"), I32(e, "PreviousDataSize"), time);
                case 6:    // DeleteValueKey
                    return _translator.ValueDeleted(pid, U64(e, "KeyObject"), I32(e, "Status"), Str(e, "ValueName"), time);
            }
            return null;
        }

        if (e.ProviderGuid == KernelProcess)
        {
            switch (id)
            {
                case 1:    // ProcessStart：事件头的 PID 是父进程，子进程在载荷里
                    return _translator.ProcessStarted(I32(e, "ProcessID"), I32(e, "ParentProcessID"), time);
                case 2:    // ProcessStop
                    return _translator.ProcessStopped(I32(e, "ProcessID"), time);
            }
        }
        return null;
    }

    private static string? Str(TraceEvent e, string name) => e.PayloadByName(name) as string;

    private static ulong U64(TraceEvent e, string name) => e.PayloadByName(name) switch
    {
        null => 0,
        ulong u => u,
        long l => unchecked((ulong)l),
        uint u => u,
        int i => unchecked((uint)i),
        var o => Convert.ToUInt64(o),
    };

    private static int I32(TraceEvent e, string name) => e.PayloadByName(name) switch
    {
        null => 0,
        int i => i,
        uint u => unchecked((int)u),
        long l => unchecked((int)l),
        ulong u => unchecked((int)u),
        var o => Convert.ToInt32(o),
    };

    private void SafeLost(int n)
    {
        try { _onEventsLost(n); }
        catch (Exception) { /* 上报通道本身坏了也不能崩 */ }
    }

    /// <summary>进程刚启动时取完整映像路径；取不到（已退出/无权限）给空串，Core 不会用它覆盖已知路径。</summary>
    private static string FullImagePath(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return p.MainModule?.FileName ?? "";
        }
        catch (Exception) { return ""; }
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
        _session?.Dispose();   // StopOnDispose：关闭会话，Source.Process() 随之返回
        _pump?.Join(TimeSpan.FromSeconds(5));
    }

    public void Dispose() => Stop();
}
