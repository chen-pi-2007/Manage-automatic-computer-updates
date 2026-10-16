using UpdateHelper.Core.Install;

namespace UpdateHelper.Core.Tests;

public class GitHubMirrorTests
{
    [Fact]
    public void Raw_file_maps_to_jsdelivr_gcore_and_ghfast()
    {
        var m = GitHubMirror.MirrorsFor(
            "https://raw.githubusercontent.com/chen-pi-2007/Manage-automatic-computer-updates/main/rules/tencent.qq.yaml");
        Assert.Equal(3, m.Count);
        Assert.Equal("https://cdn.jsdelivr.net/gh/chen-pi-2007/Manage-automatic-computer-updates@main/rules/tencent.qq.yaml", m[0]);
        Assert.Equal("https://gcore.jsdelivr.net/gh/chen-pi-2007/Manage-automatic-computer-updates@main/rules/tencent.qq.yaml", m[1]);
        Assert.Equal("https://ghfast.top/https://raw.githubusercontent.com/chen-pi-2007/Manage-automatic-computer-updates/main/rules/tencent.qq.yaml", m[2]);
    }

    [Fact]
    public void Raw_file_with_subdirs_keeps_full_path()
    {
        var m = GitHubMirror.MirrorsFor("https://raw.githubusercontent.com/o/r/v1.2/a/b/c.yaml");
        Assert.Equal("https://cdn.jsdelivr.net/gh/o/r@v1.2/a/b/c.yaml", m[0]);
    }

    [Fact]
    public void Release_asset_maps_to_ghfast_only()
    {
        var m = GitHubMirror.MirrorsFor(
            "https://github.com/chen-pi-2007/Manage-automatic-computer-updates/releases/download/rules-latest/rules-bundle.zip");
        var one = Assert.Single(m);
        Assert.Equal("https://ghfast.top/https://github.com/chen-pi-2007/Manage-automatic-computer-updates/releases/download/rules-latest/rules-bundle.zip", one);
    }

    [Theory]
    [InlineData("https://example.com/x.yaml")]                 // 非 GitHub
    [InlineData("http://raw.githubusercontent.com/o/r/m/x")]   // 非 https
    [InlineData("https://github.com/o/r/blob/main/x.yaml")]    // github 但不是 release 资产
    [InlineData("https://raw.githubusercontent.com/o/r")]      // 段数不足
    [InlineData("not a url")]
    public void Non_mirrorable_urls_return_empty(string url) =>
        Assert.Empty(GitHubMirror.MirrorsFor(url));
}

public class MirrorFallbackDownloaderTests
{
    // 可编排的假下载器：只有 okUrls 里的地址成功（写入内容），其余抛异常
    private sealed class ScriptedDownloader(HashSet<string> okUrls) : IDownloader
    {
        public List<string> Tried { get; } = [];
        public Task DownloadAsync(string url, string dest, IProgress<double>? p, CancellationToken ct)
        {
            Tried.Add(url);
            if (!okUrls.Contains(url)) throw new HttpRequestException("模拟失败: " + url);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(dest))!);
            File.WriteAllText(dest, "ok:" + url);
            return Task.CompletedTask;
        }
    }

    private const string Raw = "https://raw.githubusercontent.com/o/r/main/rules/x.yaml";

    [Fact]
    public async Task Uses_direct_when_it_works_and_does_not_touch_mirrors()
    {
        using var dir = new TempDir();
        var inner = new ScriptedDownloader([Raw]);
        var dl = new MirrorFallbackDownloader(inner);
        var dest = Path.Combine(dir.Path, "x.yaml");

        await dl.DownloadAsync(Raw, dest, null, default);

        Assert.Equal([Raw], inner.Tried);                       // 只试了直连
        Assert.Equal("ok:" + Raw, File.ReadAllText(dest));
    }

    [Fact]
    public async Task Falls_back_to_first_working_mirror_when_direct_fails()
    {
        using var dir = new TempDir();
        var jsdelivr = GitHubMirror.MirrorsFor(Raw)[0];
        var inner = new ScriptedDownloader([jsdelivr]);         // 直连坏、jsDelivr 好
        var dl = new MirrorFallbackDownloader(inner);
        var dest = Path.Combine(dir.Path, "x.yaml");

        await dl.DownloadAsync(Raw, dest, null, default);

        Assert.Equal([Raw, jsdelivr], inner.Tried);             // 直连→jsDelivr 后停
        Assert.Equal("ok:" + jsdelivr, File.ReadAllText(dest));
    }

    [Fact]
    public async Task Throws_when_direct_and_all_mirrors_fail()
    {
        using var dir = new TempDir();
        var inner = new ScriptedDownloader([]);                 // 全坏
        var dl = new MirrorFallbackDownloader(inner);

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            dl.DownloadAsync(Raw, Path.Combine(dir.Path, "x.yaml"), null, default));
        Assert.Equal(4, inner.Tried.Count);                     // 直连 + 3 个镜像都试过
    }

    [Fact]
    public async Task Non_github_url_has_no_fallback()
    {
        using var dir = new TempDir();
        var other = "https://example.com/x.yaml";
        var inner = new ScriptedDownloader([]);
        var dl = new MirrorFallbackDownloader(inner);

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            dl.DownloadAsync(other, Path.Combine(dir.Path, "x.yaml"), null, default));
        Assert.Equal([other], inner.Tried);                     // 只试直连，无镜像
    }

    // 对 hangUrl 真正挂起（尊重取消令牌），其余地址立即成功
    private sealed class HangingInner(string hangUrl, HashSet<string> okUrls) : IDownloader
    {
        public List<string> Tried { get; } = [];
        public async Task DownloadAsync(string url, string dest, IProgress<double>? p, CancellationToken ct)
        {
            Tried.Add(url);
            if (url == hangUrl) { await Task.Delay(TimeSpan.FromSeconds(30), ct); return; }
            if (!okUrls.Contains(url)) throw new HttpRequestException("模拟失败: " + url);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(dest))!);
            File.WriteAllText(dest, "ok:" + url);
        }
    }

    [Fact]
    public async Task Hanging_direct_times_out_per_attempt_and_lets_mirror_win()
    {
        using var dir = new TempDir();
        var jsdelivr = GitHubMirror.MirrorsFor(Raw)[0];
        var inner = new HangingInner(Raw, [jsdelivr]);            // 直连挂起、jsDelivr 好
        var dl = new MirrorFallbackDownloader(inner, TimeSpan.FromMilliseconds(150));  // 每地址 150ms
        var dest = Path.Combine(dir.Path, "x.yaml");

        var sw = System.Diagnostics.Stopwatch.StartNew();
        await dl.DownloadAsync(Raw, dest, null, default);
        sw.Stop();

        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(3), $"耗时 {sw.Elapsed} 不该卡在挂起的直连上");
        Assert.Equal("ok:" + jsdelivr, File.ReadAllText(dest));   // 用上了镜像
        Assert.Equal([Raw, jsdelivr], inner.Tried);              // 直连超时后才轮到镜像
    }

    [Fact]
    public async Task Real_cancellation_is_rethrown_not_retried()
    {
        using var dir = new TempDir();
        var inner = new ScriptedDownloader([]);
        var dl = new MirrorFallbackDownloader(inner);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            dl.DownloadAsync(Raw, Path.Combine(dir.Path, "x.yaml"), null, cts.Token));
        Assert.Empty(inner.Tried);                              // 取消：一个都没试
    }
}
