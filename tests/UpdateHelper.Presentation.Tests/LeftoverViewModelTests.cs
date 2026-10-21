using UpdateHelper.Core.Uninstall;
using UpdateHelper.Presentation.ViewModels;

namespace UpdateHelper.Presentation.Tests;

public sealed class LeftoverViewModelTests
{
    private static LeftoverItem Item(LeftoverType type, LeftoverCategory cat, bool chk, long? size = 1024, string path = @"C:\x") =>
        new(type, path, cat, size, chk, cat == LeftoverCategory.Personal ? "个人数据" : null);

    [Fact]
    public void Rows_show_category_and_size_and_checkbox()
    {
        var vm = new LeftoverViewModel([
            Item(LeftoverType.InstallDir, LeftoverCategory.Owned, true, 5_000_000),
            Item(LeftoverType.DataDir, LeftoverCategory.Personal, false, 2048),
        ]);

        Assert.Equal(2, vm.Rows.Count);
        var owned = vm.Rows[0];
        Assert.Equal("确定属于", owned.CategoryName);
        Assert.True(owned.Checked);
        Assert.Contains("MB", owned.SizeText);
        var personal = vm.Rows[1];
        Assert.False(personal.Checked);
        Assert.Equal("个人数据", personal.CategoryName);
    }

    [Fact]
    public void Summary_counts_default_checked()
    {
        var vm = new LeftoverViewModel([
            Item(LeftoverType.InstallDir, LeftoverCategory.Owned, true),
            Item(LeftoverType.DataDir, LeftoverCategory.Shared, false),
            Item(LeftoverType.RegistryKey, LeftoverCategory.Owned, true, size: null),
        ]);
        Assert.Contains("共 3 项", vm.Summary);
        Assert.Contains("2 项", vm.Summary);   // 默认勾选 2
    }

    [Fact]
    public void Unknown_size_shows_dash()
    {
        var vm = new LeftoverViewModel([Item(LeftoverType.RegistryKey, LeftoverCategory.Owned, true, size: null)]);
        Assert.Equal("—", vm.Rows[0].SizeText);
    }

    [Fact]
    public void Empty_has_friendly_message()
    {
        var vm = new LeftoverViewModel([]);
        Assert.Empty(vm.Rows);
        Assert.Contains("没有找到", vm.Summary);
    }

    [Fact]
    public void Warning_maps_from_category()
    {
        var vm = new LeftoverViewModel([
            Item(LeftoverType.DataDir, LeftoverCategory.Personal, false),
            Item(LeftoverType.DataDir, LeftoverCategory.Shared, false),
            Item(LeftoverType.InstallDir, LeftoverCategory.Owned, true),
        ]);
        Assert.Equal("personal", vm.Rows[0].Warning);
        Assert.Equal("shared", vm.Rows[1].Warning);
        Assert.Null(vm.Rows[2].Warning);
    }

    [Fact]
    public void Has_disclaimer()
    {
        Assert.Contains("做不到 100%", new LeftoverViewModel([]).DisclaimerText);
    }

    [Fact]
    public void Leftover_rows_toggle_checked_and_collect_selection()
    {
        var a = Item(LeftoverType.InstallDir, LeftoverCategory.Owned, true, path: @"C:");
        var b = Item(LeftoverType.DataDir, LeftoverCategory.Personal, false, path: @"C:");
        var c = Item(LeftoverType.RegistryKey, LeftoverCategory.Possible, false, path: @"HKCU\c");
        var vm = new LeftoverViewModel([a, b, c]);

        Assert.Equal([a], vm.SelectedItems);   // 默认只有预选项

        vm.Rows[1].Checked = true;
        vm.Rows[0].Checked = false;
        vm.Rows[2].Checked = true;

        Assert.Equal([b, c], vm.SelectedItems);
        Assert.Equal(2, vm.SelectedCount);
    }

    [Fact]
    public void Uninstall_note_no_longer_says_coming_soon()
    {
        Assert.DoesNotContain("将在后台助手完成后提供", new LeftoverViewModel([]).DisclaimerText);
        Assert.Contains("做不到 100%", new LeftoverViewModel([]).DisclaimerText);
    }

    [Fact]
    public async Task UninstallAsync_passes_exactly_the_checked_items_to_backend()
    {
        var a = Item(LeftoverType.InstallDir, LeftoverCategory.Owned, true, path: @"C:");
        var b = Item(LeftoverType.DataDir, LeftoverCategory.Personal, false, path: @"C:");
        var backend = new FakeBackend();
        var vm = new LeftoverViewModel([a, b]);
        vm.Rows[1].Checked = true;
        vm.Rows[0].Checked = false;

        var group = new UpdateHelper.Core.Grouping.SoftwareGroup { Name = "X" };
        var outcome = await vm.UninstallAsync(backend, group, CancellationToken.None);

        Assert.Equal([b], backend.LastUninstallSelected);
        Assert.Same(group, backend.LastUninstallGroup);
        Assert.Equal("fake-outcome", outcome.Message);
    }

    [Fact]
    public async Task UninstallAsync_with_nothing_checked_does_not_call_backend()
    {
        var backend = new FakeBackend();
        var vm = new LeftoverViewModel([Item(LeftoverType.InstallDir, LeftoverCategory.Owned, false)]);
        var outcome = await vm.UninstallAsync(backend, new UpdateHelper.Core.Grouping.SoftwareGroup { Name = "X" }, CancellationToken.None);
        Assert.Equal(0, backend.UninstallCalls);
        Assert.Contains("没有勾选", outcome.Message);
    }
}
