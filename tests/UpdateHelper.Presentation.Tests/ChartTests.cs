using UpdateHelper.Core.Updates;
using UpdateHelper.Presentation.Settings;
using UpdateHelper.Presentation.ViewModels;
using static UpdateHelper.Presentation.Tests.FakeBackend;

namespace UpdateHelper.Presentation.Tests;

public sealed class ChartTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("uh-chart-").FullName;
    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    [Fact]
    public void Donut_slices_share_the_circle_by_count()
    {
        var slices = Charts.Donut([("A", 1, "#111111"), ("B", 3, "#222222")]);

        Assert.Equal(2, slices.Count);
        Assert.Equal(0, slices[0].StartAngle);
        Assert.Equal(90, slices[0].SweepAngle, 6);
        Assert.Equal(90, slices[1].StartAngle, 6);
        Assert.Equal(270, slices[1].SweepAngle, 6);
    }

    [Fact]
    public void Donut_skips_zero_counts_and_handles_empty()
    {
        var slices = Charts.Donut([("A", 0, "#111111"), ("B", 2, "#222222")]);
        Assert.Single(slices);
        Assert.Equal(360, slices[0].SweepAngle, 6);

        Assert.Empty(Charts.Donut([("A", 0, "#111111")]));
    }

    [Fact]
    public void Bars_are_relative_to_the_largest()
    {
        var bars = Charts.Bars([("普通软件", 80), ("开发工具", 20), ("游戏", 0)]);

        Assert.Equal(["普通软件", "开发工具"], bars.Select(b => b.Label));   // 0 个的不显示
        Assert.Equal(100, bars[0].Percent, 6);
        Assert.Equal(25, bars[1].Percent, 6);
    }

    [Fact]
    public async Task Home_charts_updates_by_tier_and_software_by_category()
    {
        var backend = new FakeBackend
        {
            Snapshot = MakeSnapshot([Entry("QQ"), Entry("微信"), Entry("Visual Studio Code"), Entry("CUBLAS", hidden: true)]),
            Report = new UpdateReport([
                Update("QQ", UpdateTier.Low), Update("A", UpdateTier.Low), Update("B", UpdateTier.Careful),
                Update("CS2", UpdateTier.Ignored)], null),
        };
        var state = new AppState(backend);
        var home = new HomeViewModel(state, new SettingsService(new SettingsStore(Path.Combine(_dir, "s.json"))));
        Assert.Empty(home.TierSlices);

        await state.RefreshAsync();

        Assert.Equal(["低风险", "需确认"], home.TierSlices.Select(s => s.Label));   // 0 个的"不自动"和"不管"档都不画
        Assert.Equal([2, 1], home.TierSlices.Select(s => s.Count));
        Assert.Equal(3, home.TierTotal);
        Assert.NotEmpty(home.CategoryBars);
        Assert.Equal(3, home.CategoryBars.Sum(b => b.Count));   // 组件合集不算
        Assert.True(home.CategoryBars.Zip(home.CategoryBars.Skip(1)).All(p => p.First.Count >= p.Second.Count));   // 从多到少
    }
}
