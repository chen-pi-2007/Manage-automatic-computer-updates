using UpdateHelper.Core.Uninstall;
using UpdateHelper.Presentation.ViewModels;

namespace UpdateHelper.Presentation.Tests;

public sealed class HistoryRestoreTests
{
    private static BackupManifest Manifest(string id, string name, int entries) =>
        new(id, new DateTimeOffset(2026, 10, 20, 9, 30, 0, TimeSpan.FromHours(8)), name,
            Enumerable.Range(0, entries).Select(i => new BackupEntry(LeftoverType.File, @"C:\f" + i, @"files\" + i, BackupEntryKind.FileOrDir)).ToList());

    [Fact]
    public void Backups_are_listed_with_readable_fields()
    {
        var backend = new FakeBackend();
        backend.Backups.Add(Manifest("abc", "某软件", 3));
        var vm = new HistoryViewModel(new AppState(backend), new FakeConfirmService());

        var row = Assert.Single(vm.Backups);
        Assert.Equal("abc", row.Id);
        Assert.Equal("某软件", row.SoftwareName);
        Assert.Equal("2026-10-20 09:30", row.Time);
        Assert.Equal(3, row.ItemCount);
    }

    [Fact]
    public async Task Restore_asks_confirmation_then_calls_backend_and_reports_count()
    {
        var backend = new FakeBackend { RestoreResult = 2 };
        backend.Backups.Add(Manifest("abc", "某软件", 3));
        var confirm = new FakeConfirmService(true);
        var vm = new HistoryViewModel(new AppState(backend), confirm);

        await vm.RestoreAsync(vm.Backups[0]);

        Assert.Equal(["abc"], backend.Restored);
        Assert.Single(confirm.ConfirmMessages);
        Assert.Contains("恢复了 2 项", Assert.Single(confirm.InfoMessages));
        Assert.Contains("共 3 项", confirm.InfoMessages[0]);
    }

    [Fact]
    public async Task Restore_declined_does_nothing()
    {
        var backend = new FakeBackend();
        backend.Backups.Add(Manifest("abc", "某软件", 1));
        var vm = new HistoryViewModel(new AppState(backend), new FakeConfirmService(false));
        await vm.RestoreAsync(vm.Backups[0]);
        Assert.Empty(backend.Restored);
    }

    [Fact]
    public async Task Restore_failure_is_reported_not_thrown()
    {
        var backend = new FakeBackend { RestoreError = new IOException("盘不可用") };
        backend.Backups.Add(Manifest("abc", "某软件", 1));
        var confirm = new FakeConfirmService(true);
        var vm = new HistoryViewModel(new AppState(backend), confirm);
        await vm.RestoreAsync(vm.Backups[0]);
        Assert.Contains("盘不可用", Assert.Single(confirm.InfoMessages));
    }
}
