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
        public bool CopyToBackup(string originalPath, string storedPath) => Do($"Copy|{originalPath}|{storedPath}", "Copy:" + originalPath);
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
    public void Remove_copies_then_schedules_reboot_delete_when_move_fails_as_locked()
    {
        var (remover, mutator, store) = Create();
        mutator.FailOn.Add("MoveToBackup:" + Dir);

        var result = remover.Remove("示例", [Item(LeftoverType.InstallDir, Dir)]);

        var copy = mutator.Calls.FindIndex(c => c.StartsWith("Copy|" + Dir));
        var schedule = mutator.Calls.FindIndex(c => c == "Schedule|" + Dir);
        Assert.True(copy >= 0 && schedule > copy, "必须先复制备份再排重启删");
        Assert.Equal(1, result.RebootPending);
        Assert.Equal(0, result.Removed);
        Assert.Empty(result.Failed);
        var manifest = store.Find(result.BackupId!)!;
        Assert.Single(manifest.Entries, e => e.OriginalPath == Dir);
    }

    [Fact]
    public void Remove_fails_without_scheduling_when_move_and_copy_both_fail()
    {
        var (remover, mutator, store) = Create();
        mutator.FailOn.Add("MoveToBackup:" + Dir);
        mutator.FailOn.Add("Copy:" + Dir);

        var result = remover.Remove("示例", [Item(LeftoverType.File, Dir)]);

        Assert.DoesNotContain(mutator.Calls, c => c.StartsWith("Schedule|"));
        Assert.Equal(0, result.RebootPending);
        Assert.Contains(Dir, result.Failed);
        Assert.Null(result.BackupId);
        Assert.Empty(store.ReadAll());
    }

    [Fact]
    public void Remove_does_not_record_registry_entry_when_delete_fails()
    {
        var (remover, mutator, store) = Create();
        mutator.FailOn.Add("DeleteKey:" + Key);

        var result = remover.Remove("示例", [Item(LeftoverType.RegistryKey, Key)]);

        Assert.Contains(Key, result.Failed);
        Assert.Equal(0, result.Removed);
        Assert.Null(result.BackupId);
        Assert.Empty(store.ReadAll());
    }

    [Fact]
    public void Restore_resolves_relative_stored_paths_against_backup_directory()
    {
        var (remover, mutator, store) = Create();
        var dir = store.NewBackupDirectory("abc123");
        var manifest = new BackupManifest("abc123", _now, "示例",
        [
            new BackupEntry(LeftoverType.DataDir, Dir, @"files\x1", BackupEntryKind.FileOrDir),
            new BackupEntry(LeftoverType.RegistryKey, Key, "0.reg", BackupEntryKind.RegistryKey),
        ]);

        var restored = remover.Restore(manifest);

        Assert.Equal(2, restored);
        Assert.Contains($"MoveBack|{Path.Combine(dir, "files", "x1")}|{Dir}", mutator.Calls);
        Assert.Contains($"Import|{Path.Combine(dir, "0.reg")}", mutator.Calls);
    }

    [Fact]
    public void Restore_skips_entries_whose_stored_path_escapes_backup_directory()
    {
        var (remover, mutator, store) = Create();
        store.NewBackupDirectory("abc123");
        var manifest = new BackupManifest("abc123", _now, "示例",
        [
            new BackupEntry(LeftoverType.DataDir, Dir, @"..\..\evil", BackupEntryKind.FileOrDir),
            new BackupEntry(LeftoverType.DataDir, Dir, @"C:\evil", BackupEntryKind.FileOrDir),
        ]);

        Assert.Equal(0, remover.Restore(manifest));
        Assert.Empty(mutator.Calls);
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
        Assert.False(Path.IsPathRooted(file.StoredPath));   // 存相对备份目录的路径
        Assert.False(Path.IsPathRooted(reg.StoredPath));
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
        var backupDir = store.GetBackupDirectory(manifest.Id)!;
        Assert.Contains($"MoveBack|{Path.Combine(backupDir, file.StoredPath)}|{Dir}", mutator.Calls);
        Assert.Contains($"Import|{Path.Combine(backupDir, reg.StoredPath)}", mutator.Calls);
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
