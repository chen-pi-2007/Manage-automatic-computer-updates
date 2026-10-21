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

    /// <summary>假变更器：只记录被要求移走的原始路径。</summary>
    private sealed class RecordingMutator : ILeftoverMutator
    {
        public List<string> Moved { get; } = [];
        public bool MoveToBackup(string originalPath, string storedPath) { Moved.Add(originalPath); return true; }
        public bool CopyToBackup(string originalPath, string storedPath) => false;
        public bool ScheduleDeleteOnReboot(string path) => false;
        public bool ExportRegistryKey(string keyPath, string storedRegFile) => false;
        public bool DeleteRegistryKey(string keyPath) => false;
        public bool MoveBack(string storedPath, string originalPath) => false;
        public bool ImportRegistryFile(string storedRegFile) => false;
    }

    private const string A = @"C:\SomeVendor\Foo\A";
    private const string B = @"C:\SomeVendor\Foo\B";

    private static LeftoverItem Item(string path) =>
        new(LeftoverType.DataDir, path, LeftoverCategory.Owned, null, true, null);

    private static AgentRequest Req(params string[] selected) =>
        new(AgentProtocol.Version, AgentOp.RemoveResidue, UninstallKeyName: "Foo", Hive: UninstallHive.CurrentUser, SelectedPaths: selected);

    private (ResidueRemovalService Service, RecordingMutator Mutator) Build(params LeftoverItem[] scanned)
    {
        var mutator = new RecordingMutator();
        var remover = new LeftoverRemover(mutator, new BackupStore(_root, () => DateTimeOffset.UnixEpoch), () => DateTimeOffset.UnixEpoch);
        return (new ResidueRemovalService((_, _) => scanned, remover), mutator);
    }

    [Fact]
    public void Handle_removes_only_intersection_of_scanned_and_selected()
    {
        var (service, mutator) = Build(Item(A), Item(B));

        var result = service.Handle(Req(A.ToLowerInvariant(), @"C:\Windows\X-forged"));

        Assert.Equal([A], mutator.Moved);   // 只删自扫 ∩ 勾选（忽略大小写）；伪造路径被忽略；B 没勾选不删
        Assert.Equal(1, result.Removed);
    }

    [Fact]
    public void Handle_removes_nothing_when_rescan_empty()
    {
        var (service, mutator) = Build();

        var result = service.Handle(Req(A, B));

        Assert.Empty(mutator.Moved);
        Assert.Equal(0, result.Removed);
    }
}
