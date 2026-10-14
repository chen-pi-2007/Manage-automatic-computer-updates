using System.Text;
using UpdateHelper.Core.Install;
using UpdateHelper.Core.Rules.ThirdParty;

namespace UpdateHelper.Core.Tests.ThirdParty;

public class ThirdPartySyncServiceTests
{
    private sealed class FakeDownloader(Dictionary<string, string?> map) : IDownloader
    {
        // map: url → 内容；值为 null 表示该 url 抛异常（模拟断网）
        public Task DownloadAsync(string url, string dest, IProgress<double>? p, CancellationToken ct)
        {
            if (!map.TryGetValue(url, out var content) || content is null) throw new HttpRequestException("模拟失败");
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(dest))!);
            File.WriteAllText(dest, content, new UTF8Encoding(false));
            return Task.CompletedTask;
        }
    }

    private const string RuleYaml = "id: soft.x\nname: X\nmatch:\n  displayName: \"X\"\n";

    private static (ThirdPartySyncService svc, RuleSourceStore store) New(Dictionary<string, string?> map, TempDir dir)
    {
        var store = new RuleSourceStore(Path.Combine(dir.Path, "c.json"), Path.Combine(dir.Path, "cache"));
        var svc = new ThirdPartySyncService(new FakeDownloader(map), store,
            () => new DateTimeOffset(2026, 10, 14, 0, 0, 0, TimeSpan.Zero));
        return (svc, store);
    }

    private static ThirdPartySource Src(string url, bool enabled = true) =>
        new(SourceId.FromUrl(url), url, "", enabled, false);

    [Fact]
    public async Task Downloads_yaml_and_records_rule_count()
    {
        using var dir = new TempDir();
        var url = "https://x/a.yaml";
        var (svc, store) = New(new() { [url] = RuleYaml }, dir);
        var src = Src(url);

        await svc.SyncAsync([src]);

        Assert.Equal(RuleYaml, File.ReadAllText(Path.Combine(store.CacheDirFor(src.Id), "rules.yaml")));
        var state = ThirdPartySyncService.ReadState(store, src.Id);
        Assert.Equal(ThirdPartySyncStatus.Updated, state!.Status);
        Assert.Equal(1, state.RuleCount);
    }

    [Fact]
    public async Task One_source_failure_does_not_affect_others_and_keeps_last_cache()   // Review Focus 4
    {
        using var dir = new TempDir();
        var good = "https://x/good.yaml";
        var bad = "https://x/bad.yaml";
        var (svc, store) = New(new() { [good] = RuleYaml, [bad] = null }, dir);
        var goodSrc = Src(good);
        var badSrc = Src(bad);

        // 先给 bad 一份"上次成功"的缓存
        Directory.CreateDirectory(store.CacheDirFor(badSrc.Id));
        File.WriteAllText(Path.Combine(store.CacheDirFor(badSrc.Id), "rules.yaml"), "旧缓存");

        await svc.SyncAsync([goodSrc, badSrc]);

        // good 正常
        Assert.Equal(ThirdPartySyncStatus.Updated, ThirdPartySyncService.ReadState(store, goodSrc.Id)!.Status);
        // bad 失败但旧缓存保留
        Assert.Equal(ThirdPartySyncStatus.NetworkFailed, ThirdPartySyncService.ReadState(store, badSrc.Id)!.Status);
        Assert.Equal("旧缓存", File.ReadAllText(Path.Combine(store.CacheDirFor(badSrc.Id), "rules.yaml")));
    }

    [Fact]
    public async Task Empty_download_is_marked_empty_and_does_not_overwrite()
    {
        using var dir = new TempDir();
        var url = "https://x/e.yaml";
        var (svc, store) = New(new() { [url] = "   " }, dir);
        var src = Src(url);

        await svc.SyncAsync([src]);

        Assert.Equal(ThirdPartySyncStatus.Empty, ThirdPartySyncService.ReadState(store, src.Id)!.Status);
        Assert.False(File.Exists(Path.Combine(store.CacheDirFor(src.Id), "rules.yaml")));
    }

    // 模拟 HttpClient 超时：抛 TaskCanceledException，但调用方的 ct 并未取消
    private sealed class TimeoutDownloader(string timeoutUrl, IDownloader inner) : IDownloader
    {
        public Task DownloadAsync(string url, string dest, IProgress<double>? p, CancellationToken ct)
        {
            if (url == timeoutUrl) throw new TaskCanceledException();
            return inner.DownloadAsync(url, dest, p, ct);
        }
    }

    [Fact]
    public async Task Timeout_without_cancellation_is_isolated_not_rethrown()
    {
        using var dir = new TempDir();
        var slow = "https://x/slow.yaml";
        var good = "https://x/good.yaml";
        var store = new RuleSourceStore(Path.Combine(dir.Path, "c.json"), Path.Combine(dir.Path, "cache"));
        var inner = new FakeDownloader(new() { [good] = RuleYaml });
        var svc = new ThirdPartySyncService(new TimeoutDownloader(slow, inner), store,
            () => new DateTimeOffset(2026, 10, 14, 0, 0, 0, TimeSpan.Zero));
        var slowSrc = Src(slow);
        var goodSrc = Src(good);

        // ① 超时不应以异常形式穿出 SyncAsync
        await svc.SyncAsync([slowSrc, goodSrc], CancellationToken.None);

        // ② 超时源记为 NetworkFailed
        Assert.Equal(ThirdPartySyncStatus.NetworkFailed, ThirdPartySyncService.ReadState(store, slowSrc.Id)!.Status);
        // ③ 正常源仍被同步成功，证明循环未被中断
        Assert.Equal(ThirdPartySyncStatus.Updated, ThirdPartySyncService.ReadState(store, goodSrc.Id)!.Status);
    }
}
