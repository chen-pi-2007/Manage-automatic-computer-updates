namespace UpdateHelper.Core.Install;

/// <summary>把 GitHub 原始地址映射成国内可达的镜像地址（jsDelivr / ghfast）。
/// 让连不上 raw.githubusercontent / GitHub release 的网络也能下到同一个文件。</summary>
public static class GitHubMirror
{
    /// <summary>返回 url 对应的镜像候选（按优先级）；非可镜像的地址返回空。</summary>
    public static IReadOnlyList<string> MirrorsFor(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || u.Scheme != Uri.UriSchemeHttps)
            return [];

        // raw 文件：https://raw.githubusercontent.com/OWNER/REPO/REF/PATH...
        if (u.Host.Equals("raw.githubusercontent.com", StringComparison.OrdinalIgnoreCase))
        {
            var seg = u.AbsolutePath.TrimStart('/').Split('/', 4);
            if (seg.Length == 4 && seg.All(s => s.Length > 0))
            {
                var (owner, repo, @ref, path) = (seg[0], seg[1], seg[2], seg[3]);
                return
                [
                    $"https://cdn.jsdelivr.net/gh/{owner}/{repo}@{@ref}/{path}",
                    $"https://gcore.jsdelivr.net/gh/{owner}/{repo}@{@ref}/{path}",
                    $"https://ghfast.top/https://raw.githubusercontent.com{u.AbsolutePath}",
                ];
            }
            return [];
        }

        // release 资产：https://github.com/OWNER/REPO/releases/download/TAG/ASSET
        // jsDelivr 不服务 release 资产，只能走 ghfast 这类透明代理。
        if (u.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
            && u.AbsolutePath.Contains("/releases/download/", StringComparison.OrdinalIgnoreCase))
        {
            return [$"https://ghfast.top/https://github.com{u.AbsolutePath}"];
        }

        return [];
    }
}

/// <summary>下载兜底：先直连原始 url，失败就按镜像依次重试，首个成功即用；全失败则抛最后一个异常。
/// 每个地址各有独立的尝试超时（默认 8 秒），这样直连"黑洞挂起"时也能及时让位给镜像，不会被卡住。
/// 用户真正的取消（ct）立即向上抛，不当作下载失败去试镜像。</summary>
public sealed class MirrorFallbackDownloader(IDownloader inner, TimeSpan? perAttemptTimeout = null) : IDownloader
{
    private readonly IDownloader _inner = inner;
    private readonly TimeSpan _attemptTimeout =
        perAttemptTimeout is { } t && t > TimeSpan.Zero ? t : TimeSpan.FromSeconds(8);

    public async Task DownloadAsync(string url, string destinationPath, IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        var candidates = new List<string> { url };
        candidates.AddRange(GitHubMirror.MirrorsFor(url));

        Exception? last = null;
        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            attemptCts.CancelAfter(_attemptTimeout);
            try
            {
                await _inner.DownloadAsync(candidate, destinationPath, progress, attemptCts.Token);
                return;   // 成功
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;    // 用户真正取消：不吞、不试镜像
            }
            catch (Exception ex)
            {
                last = ex;   // 这个地址不行（失败或本次尝试超时），换下一个
            }
        }
        // 直连和所有镜像都失败：统一抛清楚的网络异常（last 一定是真失败，真取消在上面已抛出）
        throw new HttpRequestException($"直连和镜像都连不上：{url}", last);
    }
}
