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
}
