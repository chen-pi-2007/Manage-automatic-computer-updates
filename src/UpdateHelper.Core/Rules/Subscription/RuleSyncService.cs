using UpdateHelper.Core.Install;

namespace UpdateHelper.Core.Rules.Subscription;

public enum SyncStatus { Updated, AlreadyLatest, VerificationFailed, NetworkFailed, DowngradeRejected, CacheWriteFailed }

/// <summary>一次同步的结果。</summary>
public sealed record SyncOutcome(SyncStatus Status, int? NewVersion, DateTimeOffset When, string? Detail);

/// <summary>下载官方签名规则包 → 验签 → 防降级 → 落盘缓存。任何失败都不抛，回退到现有缓存/内置。</summary>
public sealed class RuleSyncService(
    IDownloader downloader, BundleVerifier verifier, RuleCacheStore cache,
    string zipUrl, string sigUrl, Func<DateTimeOffset>? clock = null)
{
    private readonly Func<DateTimeOffset> _clock = clock ?? (() => DateTimeOffset.Now);

    public async Task<SyncOutcome> SyncAsync(CancellationToken ct = default)
    {
        var outcome = await RunAsync(ct);
        cache.SaveSyncState(outcome.When, outcome.Status.ToString(), outcome.NewVersion);
        return outcome;
    }

    private async Task<SyncOutcome> RunAsync(CancellationToken ct)
    {
        var temp = Path.Combine(Path.GetTempPath(), "uh-rulesync-" + Guid.NewGuid().ToString("N"));
        var zipPath = temp + ".zip";
        var sigPath = temp + ".sig";
        byte[] zipBytes, sigBytes;
        try
        {
            await downloader.DownloadAsync(zipUrl, zipPath, null, ct);
            await downloader.DownloadAsync(sigUrl, sigPath, null, ct);
            zipBytes = await File.ReadAllBytesAsync(zipPath, ct);
            sigBytes = await File.ReadAllBytesAsync(sigPath, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)   // 网络/IO 一律当"网络失败"，不抛
        {
            return new SyncOutcome(SyncStatus.NetworkFailed, null, _clock(), ex.Message);
        }
        finally
        {
            TryDelete(zipPath);
            TryDelete(sigPath);
        }

        var verify = verifier.Verify(zipBytes, sigBytes);
        if (!verify.Ok)
            return new SyncOutcome(SyncStatus.VerificationFailed, null, _clock(), verify.Error);

        var newVersion = verify.Bundle!.Manifest.BundleVersion;
        var current = cache.TryReadManifest()?.BundleVersion;
        if (current is { } cur)
        {
            if (newVersion == cur) return new SyncOutcome(SyncStatus.AlreadyLatest, newVersion, _clock(), null);
            if (newVersion < cur) return new SyncOutcome(SyncStatus.DowngradeRejected, newVersion, _clock(),
                $"下载到的版本 {newVersion} 比当前缓存 {cur} 旧，已忽略");
        }

        try { cache.Replace(verify.Bundle); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new SyncOutcome(SyncStatus.CacheWriteFailed, newVersion, _clock(), ex.Message);
        }
        return new SyncOutcome(SyncStatus.Updated, newVersion, _clock(), null);
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
