using UpdateHelper.Core.Uninstall;

namespace UpdateHelper.Core.Tests.Uninstall;

public sealed class LeftoverRemoverTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "uh-remover-" + Guid.NewGuid().ToString("N"));
    private readonly DateTimeOffset _now = new(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    /// <summary>假变更器：按顺序记录每次调用，可指定哪些操作返回 false。</summary>
    private sealed class FakeMutator : ILeftoverMutator
    {
        public List<string> Calls { get; } = [];
        public HashSet<string> FailOn { get; } = [];

        private bool Do(string call, string key)
        {
            Calls.Add(call);
            return !FailOn.Contains(key);
        }

        public bool MoveToBackup(string originalPath, string storedPath) => Do($"MoveToBackup|{originalPath}|{storedPath}", "MoveToBackup:" + originalPath);
        public bool ScheduleDeleteOnReboot(string path) => Do($"Schedule|{path}", "Schedule:" + path);
        public bool ExportRegistryKey(string keyPath, string storedRegFile) => Do($"Export|{keyPath}|{storedRegFile}", "Export:" + keyPath);
        public bool DeleteRegistryKey(string keyPath) => Do($"DeleteKey|{keyPath}", "DeleteKey:" + keyPath);
        public bool MoveBack(string storedPath, string originalPath) => Do($"MoveBack|{storedPath}|{originalPath}", "MoveBack:" + originalPath);
        public bool ImportRegistryFile(string storedRegFile) => Do($"Import|{storedRegFile}", "Import:" + storedRegFile);
    }

    private const string Dir = @"C:\SomeVendor\Foo";
    private const string Key = @"HKCU\Software\SomeVendor\Foo";

    private static LeftoverItem Item(LeftoverType type, string path) =>
        new(type, path, LeftoverCategory.Owned, null, true, null);

    private (LeftoverRemover Remover, FakeMutator Mutator, BackupStore Store) Create()
    {
        var mutator = new FakeMutator();
        var store = new BackupStore(_root, () => _now);
        return (new LeftoverRemover(mutator, store, () => _now), mutator, store);
    }

    [Fact]
    public void Remove_backs_up_before_deleting_each_item()
    {
        var (remover, mutator, _) = Create();

        var result = remover.Remove("示例", [Item(LeftoverType.RegistryKey, Key), Item(LeftoverType.DataDir, Dir)]);

        Assert.Equal(2, result.Removed);
        var export = mutator.Calls.FindIndex(c => c.StartsWith("Export|"));
        var delete = mutator.Calls.FindIndex(c => c.StartsWith("DeleteKey|"));
        Assert.True(export >= 0 && delete > export, "注册表键必须先导出再删除");
        Assert.Contains(mutator.Calls, c => c.StartsWith("MoveToBackup|" + Dir));
    }

    [Fact]
    public void Remove_does_not_delete_registry_key_when_export_fails()
    {
        var (remover, mutator, _) = Create();
        mutator.FailOn.Add("Export:" + Key);

        var result = remover.Remove("示例", [Item(LeftoverType.RegistryKey, Key)]);

        Assert.DoesNotContain(mutator.Calls, c => c.StartsWith("DeleteKey|"));
        Assert.Equal(0, result.Removed);
        Assert.Contains(Key, result.Failed);
    }

    [Fact]
    public void Remove_skips_protected_paths()
    {
        var (remover, mutator, _) = Create();
        var windir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);

        var result = remover.Remove("示例",
            [Item(LeftoverType.DataDir, Path.Combine(windir, "Foo")), Item(LeftoverType.RegistryKey, @"HKLM\SYSTEM\Foo")]);

        Assert.Empty(mutator.Calls);
        Assert.Equal(0, result.Removed);
        Assert.Equal(2, result.Failed.Count);
    }

    [Fact]
    public void Remove_marks_service_items_failed_without_touching_mutator()
    {
        var (remover, mutator, _) = Create();

        var result = remover.Remove("示例", [Item(LeftoverType.Service, "FooSvc")]);

        Assert.Empty(mutator.Calls);
        Assert.Contains("FooSvc", result.Failed);
        Assert.Equal(0, result.Removed);
    }

    [Fact]
    public void Remove_schedules_reboot_delete_when_move_fails_as_locked()
    {
        var (remover, mutator, _) = Create();
        mutator.FailOn.Add("MoveToBackup:" + Dir);

        var result = remover.Remove("示例", [Item(LeftoverType.InstallDir, Dir)]);

        Assert.Contains(mutator.Calls, c => c == "Schedule|" + Dir);
        Assert.Equal(1, result.RebootPending);
        Assert.Equal(0, result.Removed);
        Assert.Empty(result.Failed);
    }

    [Fact]
    public void Remove_reports_failed_when_reboot_schedule_also_fails()
    {
        var (remover, mutator, _) = Create();
        mutator.FailOn.Add("MoveToBackup:" + Dir);
        mutator.FailOn.Add("Schedule:" + Dir);

        var result = remover.Remove("示例", [Item(LeftoverType.File, Dir)]);

        Assert.Equal(0, result.RebootPending);
        Assert.Contains(Dir, result.Failed);
    }

    [Fact]
    public void Remove_writes_a_backup_manifest_with_all_entries()
    {
        var (remover, _, store) = Create();

        var result = remover.Remove("示例", [Item(LeftoverType.DataDir, Dir), Item(LeftoverType.RegistryKey, Key)]);

        Assert.NotNull(result.BackupId);
        var manifest = store.Find(result.BackupId!);
        Assert.NotNull(manifest);
        Assert.Equal("示例", manifest!.SoftwareName);
        Assert.Equal(_now, manifest.Time);
        Assert.Equal(2, manifest.Entries.Count);
        var file = Assert.Single(manifest.Entries, e => e.Kind == BackupEntryKind.FileOrDir);
        Assert.Equal(Dir, file.OriginalPath);
        var reg = Assert.Single(manifest.Entries, e => e.Kind == BackupEntryKind.RegistryKey);
        Assert.Equal(Key, reg.OriginalPath);
        Assert.EndsWith(".reg", reg.StoredPath);
        Assert.StartsWith(Path.Combine(_root, result.BackupId!), file.StoredPath, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith(Path.Combine(_root, result.BackupId!), reg.StoredPath, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Remove_with_nothing_backed_up_writes_no_manifest()
    {
        var (remover, _, store) = Create();

        var result = remover.Remove("示例", [Item(LeftoverType.Service, "FooSvc")]);

        Assert.Null(result.BackupId);
        Assert.Empty(store.ReadAll());
    }

    [Fact]
    public void Restore_moves_every_entry_back()
    {
        var (remover, mutator, store) = Create();
        var result = remover.Remove("示例", [Item(LeftoverType.DataDir, Dir), Item(LeftoverType.RegistryKey, Key)]);
        var manifest = store.Find(result.BackupId!)!;
        mutator.Calls.Clear();

        var restored = remover.Restore(manifest);

        Assert.Equal(2, restored);
        var file = manifest.Entries.Single(e => e.Kind == BackupEntryKind.FileOrDir);
        var reg = manifest.Entries.Single(e => e.Kind == BackupEntryKind.RegistryKey);
        Assert.Contains($"MoveBack|{file.StoredPath}|{Dir}", mutator.Calls);
        Assert.Contains($"Import|{reg.StoredPath}", mutator.Calls);
    }

    [Fact]
    public void Restore_skips_entries_whose_backup_is_gone()
    {
        var (remover, mutator, store) = Create();
        const string other = @"C:\SomeVendor\Bar";
        var result = remover.Remove("示例", [Item(LeftoverType.DataDir, Dir), Item(LeftoverType.DataDir, other)]);
        var manifest = store.Find(result.BackupId!)!;
        mutator.FailOn.Add("MoveBack:" + Dir);   // 第一项的备份已丢失
        mutator.Calls.Clear();

        var restored = remover.Restore(manifest);

        Assert.Equal(1, restored);
        Assert.Contains(mutator.Calls, c => c.StartsWith("MoveBack|") && c.EndsWith("|" + other));
    }
}
