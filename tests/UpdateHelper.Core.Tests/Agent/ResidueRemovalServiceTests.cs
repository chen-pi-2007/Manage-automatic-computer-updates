using UpdateHelper.Core.Agent;
using UpdateHelper.Core.Scanning;
using UpdateHelper.Core.Uninstall;

namespace UpdateHelper.Core.Tests.Agent;

public sealed class ResidueRemovalServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "uh-residue-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    /// <summary>共享事件日志：记录"跑卸载"和"删某项"的先后顺序。</summary>
    private sealed class Log
    {
        public List<string> Events { get; } = [];
    }

    /// <summary>假变更器：把被要求移走的原始路径记进共享日志。</summary>
    private sealed class RecordingMutator(Log log) : ILeftoverMutator
    {
        public List<string> Moved { get; } = [];
        public bool MoveToBackup(string originalPath, string storedPath)
        {
            Moved.Add(originalPath);
            log.Events.Add("remove:" + originalPath);
            return true;
        }
        public bool CopyToBackup(string originalPath, string storedPath) => false;
        public bool ScheduleDeleteOnReboot(string path) => false;
        public bool ExportRegistryKey(string keyPath, string storedRegFile) => true;
        public bool DeleteRegistryKey(string keyPath)
        {
            Moved.Add(keyPath);
            log.Events.Add("remove:" + keyPath);
            return true;
        }
        public bool MoveBack(string storedPath, string originalPath) => false;
        public bool ImportRegistryFile(string storedRegFile) => false;
    }

    /// <summary>假探针：默认一切都存在，可把指定路径标成"已不存在"（模拟卸载程序删掉的项）。</summary>
    private sealed class FakeProbe(HashSet<string> missing) : IFileProbe, IRegistryProbe
    {
        public bool DirectoryExists(string path) => !missing.Contains(path);
        public bool FileExists(string path) => !missing.Contains(path);
        public IReadOnlyList<string> GetChildDirectories(string parent) => [];
        public long? DirectorySize(string path) => null;
        public bool KeyExists(string path) => !missing.Contains(path);
    }

    private const string A = @"C:\SomeVendor\Foo\A";
    private const string B = @"C:\SomeVendor\Foo\B";
    private const string UninstallExe = @"C:\SomeVendor\Foo\uninstall.exe";

    private static LeftoverItem Dir(string path) =>
        new(LeftoverType.DataDir, path, LeftoverCategory.Owned, null, true, null);

    private static UninstallEntry Entry(string uninstallString) =>
        new("Foo", UninstallHive.LocalMachine64, "Foo", null, null, null, uninstallString, null, false, null, null, null);

    private static AgentRequest Req(bool runNative, params string[] selected) =>
        new(AgentProtocol.Version, AgentOp.RemoveResidue, UninstallKeyName: "Foo",
            Hive: runNative ? UninstallHive.LocalMachine64 : UninstallHive.CurrentUser,
            SelectedPaths: selected, RunNativeUninstaller: runNative);

    private (ResidueRemovalService Service, RecordingMutator Mutator, Log Log, List<UninstallCommand> Ran) Build(
        ResiduePreScan prescan, HashSet<string>? missing = null)
    {
        var log = new Log();
        var mutator = new RecordingMutator(log);
        var remover = new LeftoverRemover(mutator, new BackupStore(_root, () => DateTimeOffset.UnixEpoch), () => DateTimeOffset.UnixEpoch);
        var probe = new FakeProbe(missing ?? []);
        var ran = new List<UninstallCommand>();
        bool Run(UninstallCommand c) { log.Events.Add("uninstall:" + c.ExecutablePath); ran.Add(c); return true; }
        var service = new ResidueRemovalService((_, _) => prescan, remover, probe, probe, Run);
        return (service, mutator, log, ran);
    }

    [Fact]
    public void Handle_removes_only_intersection_of_scanned_and_selected()
    {
        var (service, mutator, _, _) = Build(new ResiduePreScan(null, [Dir(A), Dir(B)]));

        var result = service.Handle(Req(false, A.ToLowerInvariant(), @"C:\Windows\X-forged"));

        Assert.Equal([A], mutator.Moved);   // 只删自扫 ∩ 勾选（忽略大小写）；伪造路径被忽略；B 没勾选不删
        Assert.Equal(1, result.Removed);
    }

    [Fact]
    public void Handle_removes_nothing_when_rescan_empty()
    {
        var (service, mutator, _, _) = Build(new ResiduePreScan(null, []));

        var result = service.Handle(Req(false, A, B));

        Assert.Empty(mutator.Moved);
        Assert.Equal(0, result.Removed);
    }

    [Fact]
    public void Handle_passes_rule_defined_residue_through_intersection()
    {
        // 规则残留（注册表键）只有带规则扫描才会出现；服务应原样纳入交集并删除
        const string regKey = @"HKLM\Software\Foo";
        var (service, mutator, _, _) = Build(new ResiduePreScan(null,
            [new LeftoverItem(LeftoverType.RegistryKey, regKey, LeftoverCategory.Owned, null, true, null), Dir(A)]));

        var result = service.Handle(Req(false, regKey, A));

        Assert.Equal(2, result.Removed);
        Assert.Contains(A, mutator.Moved);
    }

    [Fact]
    public void Handle_runs_native_uninstaller_before_removal_using_scanned_entry_command()
    {
        // 卸载命令来自自扫到的注册表项（UninstallString），不是请求
        var (service, _, log, ran) = Build(
            new ResiduePreScan(Entry($"\"{UninstallExe}\" /S"), [Dir(A)]));

        var result = service.Handle(Req(true, A));

        var cmd = Assert.Single(ran);
        Assert.Equal(UninstallExe, cmd.ExecutablePath);         // 命令取自注册表登记项
        Assert.Equal("/S", cmd.Arguments);
        var uninstall = log.Events.IndexOf("uninstall:" + UninstallExe);
        var remove = log.Events.IndexOf("remove:" + A);
        Assert.True(uninstall >= 0 && remove > uninstall, "必须先跑卸载程序再删残留");
        Assert.Equal(1, result.Removed);
    }

    [Fact]
    public void Handle_does_not_run_uninstaller_when_flag_is_false()
    {
        var (service, _, _, ran) = Build(new ResiduePreScan(Entry($"\"{UninstallExe}\" /S"), [Dir(A)]));

        service.Handle(Req(false, A));

        Assert.Empty(ran);   // RunNativeUninstaller=false：绝不跑卸载程序
    }

    [Fact]
    public void Handle_skips_residue_the_uninstaller_already_removed()
    {
        // 卸前残留有 A、B；卸载程序把 B 删了 → 只删仍存在的 A，B 不计失败
        var (service, mutator, _, _) = Build(
            new ResiduePreScan(Entry($"\"{UninstallExe}\" /S"), [Dir(A), Dir(B)]),
            missing: [B]);

        var result = service.Handle(Req(true, A, B));

        Assert.Equal([A], mutator.Moved);
        Assert.Equal(1, result.Removed);
        Assert.Empty(result.Failed);
    }
}
