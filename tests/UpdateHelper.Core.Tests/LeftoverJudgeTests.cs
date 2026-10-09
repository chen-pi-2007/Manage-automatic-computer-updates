using UpdateHelper.Core.Uninstall;

namespace UpdateHelper.Core.Tests;

public sealed class LeftoverJudgeTests
{
    private static LeftoverItem Classify(bool owned = false, bool personal = false, bool shared = false,
        bool isInstallLoc = false, bool sharedOthers = false, bool nameOnly = false,
        LeftoverType type = LeftoverType.DataDir) =>
        LeftoverJudge.Classify(type, @"C:\x", 100, owned, personal, shared, isInstallLoc, sharedOthers, nameOnly);

    [Fact]
    public void Install_location_is_owned_and_checked()
    {
        var item = Classify(isInstallLoc: true);
        Assert.Equal(LeftoverCategory.Owned, item.Category);
        Assert.True(item.DefaultChecked);
        Assert.Null(item.Note);
    }

    [Fact]
    public void Rule_owned_is_checked()
    {
        Assert.True(Classify(owned: true).DefaultChecked);
    }

    [Fact]
    public void Possible_is_unchecked_with_note()
    {
        var item = Classify(nameOnly: true);
        Assert.Equal(LeftoverCategory.Possible, item.Category);
        Assert.False(item.DefaultChecked);
        Assert.Contains("推测", item.Note);
    }

    [Fact]
    public void Shared_is_unchecked_with_warning()
    {
        var item = Classify(sharedOthers: true, isInstallLoc: true);   // 即使看着像自己的，共用也不默认删
        Assert.Equal(LeftoverCategory.Shared, item.Category);
        Assert.False(item.DefaultChecked);
        Assert.Contains("其他", item.Note);
    }

    [Fact]
    public void Personal_beats_owned()
    {
        // 规则同时标 owned 和 personal，或个人数据恰好在安装目录内：个人数据优先，保护它
        var item = Classify(owned: true, personal: true, isInstallLoc: true);
        Assert.Equal(LeftoverCategory.Personal, item.Category);
        Assert.False(item.DefaultChecked);
        Assert.Contains("个人数据", item.Note);
    }

    [Fact]
    public void Personal_beats_shared()
    {
        var item = Classify(personal: true, sharedOthers: true);
        Assert.Equal(LeftoverCategory.Personal, item.Category);
    }

    [Theory]
    [InlineData(LeftoverType.Service)]
    [InlineData(LeftoverType.ScheduledTask)]
    [InlineData(LeftoverType.StartupEntry)]
    public void Background_items_are_owned(LeftoverType type)
    {
        var item = Classify(type: type);
        Assert.Equal(LeftoverCategory.Owned, item.Category);
        Assert.True(item.DefaultChecked);
    }

    [Fact]
    public void Carries_through_path_type_size()
    {
        var item = LeftoverJudge.Classify(LeftoverType.File, @"C:\a\b.log", 2048,
            false, false, false, false, false, true);
        Assert.Equal(LeftoverType.File, item.Type);
        Assert.Equal(@"C:\a\b.log", item.Path);
        Assert.Equal(2048, item.SizeBytes);
    }
}
