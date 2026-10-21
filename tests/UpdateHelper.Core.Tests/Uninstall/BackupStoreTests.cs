using UpdateHelper.Core.Uninstall;

namespace UpdateHelper.Core.Tests.Uninstall;

public sealed class BackupStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "uh-backup-" + Guid.NewGuid().ToString("N"));
    private DateTimeOffset _now = new(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    private BackupStore Create() => new(_root, () => _now);

    private static BackupManifest Manifest(string id, DateTimeOffset time, string name = "示例软件") => new(
        id, time, name,
        [
            new BackupEntry(LeftoverType.DataDir, @"C:\Users\x\AppData\Roaming\示例", @"files\0", BackupEntryKind.FileOrDir),
            new BackupEntry(LeftoverType.RegistryKey, @"HKCU\Software\示例", @"reg\0.reg", BackupEntryKind.RegistryKey),
        ]);

    [Fact]
    public void Save_then_ReadAll_round_trips()
    {
        var store = Create();
        var older = Manifest("a", _now.AddDays(-2), "旧软件");
        var newer = Manifest("b", _now.AddDays(-1));
        store.Save(older);
        store.Save(newer);

        var all = store.ReadAll();

        Assert.Equal(2, all.Count);
        Assert.Equal("b", all[0].Id);   // 时间倒序
        Assert.Equal("a", all[1].Id);
        Assert.Equal("旧软件", all[1].SoftwareName);
        Assert.Equal(older.Time, all[1].Time);
        Assert.Equal(older.Entries, all[1].Entries);
        Assert.Contains("示例", File.ReadAllText(Path.Combine(_root, "manifests.jsonl")));   // 中文原样
    }

    [Fact]
    public void Find_returns_matching_manifest_by_id()
    {
        var store = Create();
        store.Save(Manifest("a", _now));
        store.Save(Manifest("b", _now, "另一个"));

        Assert.Equal("另一个", store.Find("b")?.SoftwareName);
        Assert.Null(store.Find("zzz"));
    }

    [Fact]
    public void ReadAll_skips_corrupt_lines()
    {
        var store = Create();
        store.Save(Manifest("a", _now));
        File.AppendAllText(Path.Combine(_root, "manifests.jsonl"), "{这不是json\n");
        store.Save(Manifest("b", _now.AddMinutes(1)));

        Assert.Equal(["b", "a"], store.ReadAll().Select(m => m.Id).ToArray());
    }

    [Fact]
    public void PruneExpired_removes_old_backups_and_their_dirs()
    {
        var store = Create();
        var oldDir = store.NewBackupDirectory("old");
        var newDir = store.NewBackupDirectory("new");
        File.WriteAllText(Path.Combine(oldDir, "f.txt"), "x");
        store.Save(Manifest("old", _now.AddDays(-40)));
        store.Save(Manifest("new", _now));

        var removed = store.PruneExpired(TimeSpan.FromDays(30));

        Assert.Equal(1, removed);
        Assert.False(Directory.Exists(oldDir));
        Assert.True(Directory.Exists(newDir));
        Assert.Equal(["new"], store.ReadAll().Select(m => m.Id).ToArray());
    }

    [Fact]
    public void PruneExpired_keeps_manifest_line_when_dir_cannot_be_deleted()
    {
        var store = Create();
        var lockedDir = store.NewBackupDirectory("locked");
        var freeDir = store.NewBackupDirectory("free");
        store.Save(Manifest("locked", _now.AddDays(-40)));
        store.Save(Manifest("free", _now.AddDays(-41)));

        int removed;
        using (new FileStream(Path.Combine(lockedDir, "busy.txt"), FileMode.Create, FileAccess.Write, FileShare.None))
        {
            removed = store.PruneExpired(TimeSpan.FromDays(30));
        }

        Assert.Equal(1, removed);                       // 只数真正删掉的
        Assert.False(Directory.Exists(freeDir));
        Assert.True(Directory.Exists(lockedDir));
        Assert.Equal(["locked"], store.ReadAll().Select(m => m.Id).ToArray());   // 删失败的保留清单行，下次重试

        Assert.Equal(1, store.PruneExpired(TimeSpan.FromDays(30)));                // 释放后重试成功
        Assert.False(Directory.Exists(lockedDir));
        Assert.Empty(store.ReadAll());
    }

    [Fact]
    public void PruneExpired_with_unsafe_ids_does_not_touch_root_or_other_backups()
    {
        var store = Create();
        var keepDir = store.NewBackupDirectory("keep");
        var keepFile = Path.Combine(keepDir, "f.txt");
        File.WriteAllText(keepFile, "x");
        var outside = Path.Combine(Path.GetTempPath(), "uh-outside-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outside);
        try
        {
            store.Save(Manifest("keep", _now));
            store.Save(Manifest("", _now.AddDays(-40)));
            store.Save(Manifest(@"..\" + Path.GetFileName(outside), _now.AddDays(-40)));
            store.Save(Manifest(outside, _now.AddDays(-40)));
            File.AppendAllText(Path.Combine(_root, "manifests.jsonl"),
                "{\"Time\":\"2026-01-01T00:00:00+00:00\",\"SoftwareName\":\"缺Id\",\"Entries\":[]}\n");

            var ex = Record.Exception(() => store.PruneExpired(TimeSpan.FromDays(30)));

            Assert.Null(ex);
            Assert.True(File.Exists(keepFile));
            Assert.True(Directory.Exists(outside));
            Assert.Equal(["keep"], store.ReadAll().Select(m => m.Id).ToArray());
        }
        finally
        {
            Directory.Delete(outside, true);
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("..")]
    [InlineData(@"..\x")]
    [InlineData("a/b")]
    public void NewBackupDirectory_rejects_unsafe_ids(string id)
    {
        Assert.Throws<ArgumentException>(() => Create().NewBackupDirectory(id));
    }
}
