using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using UpdateHelper.Core.Install;
using UpdateHelper.Core.Rules.Subscription;

namespace UpdateHelper.Core.Tests.Subscription;

public sealed class RuleSyncServiceTests
{
    private static string Sha256Hex(byte[] b) => Convert.ToHexString(SHA256.HashData(b)).ToLowerInvariant();

    private static byte[] BuildZip(int version, params (string Name, string Content)[] files)
    {
        var manifest = new BundleManifest(version, DateTimeOffset.UnixEpoch, files.Length,
            files.Select(f => new ManifestFile(f.Name, Sha256Hex(Encoding.UTF8.GetBytes(f.Content)))).ToList());
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, true))
        {
            void Add(string n, string c) { using var w = new StreamWriter(zip.CreateEntry(n).Open(), new UTF8Encoding(false)); w.Write(c); }
            Add("manifest.json", ManifestParser.Serialize(manifest));
            foreach (var f in files) Add(f.Name, f.Content);
        }
        return ms.ToArray();
    }

    // 假下载器：按 url 给预置字节；或抛异常模拟断网
    private sealed class FakeDownloader(Dictionary<string, byte[]> map, bool throwNetwork = false) : IDownloader
    {
        public Task DownloadAsync(string url, string dest, IProgress<double>? p, CancellationToken ct)
        {
            if (throwNetwork) throw new HttpRequestException("模拟断网");
            if (!map.TryGetValue(url, out var bytes)) throw new HttpRequestException("404");
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(dest))!);
            File.WriteAllBytes(dest, bytes);
            return Task.CompletedTask;
        }
    }

    private const string ZipUrl = "https://x/rules-bundle.zip";
    private const string SigUrl = "https://x/rules-bundle.zip.sig";

    private static (RuleSyncService svc, RuleCacheStore cache) New(byte[]? zip, byte[]? sig, string pubPem, bool net = false)
    {
        var map = new Dictionary<string, byte[]>();
        if (zip is not null) map[ZipUrl] = zip;
        if (sig is not null) map[SigUrl] = sig;
        var cacheDir = Path.Combine(Path.GetTempPath(), "uh-sync-" + Guid.NewGuid().ToString("N"));
        var cache = new RuleCacheStore(cacheDir);
        var svc = new RuleSyncService(new FakeDownloader(map, net), new BundleVerifier(pubPem), cache, ZipUrl, SigUrl,
            () => new DateTimeOffset(2026, 10, 13, 0, 0, 0, TimeSpan.Zero));
        return (svc, cache);
    }

    [Fact]
    public async Task First_sync_stores_and_reports_updated()
    {
        var rsa = RSA.Create(3072);
        var zip = BuildZip(3, ("a.yaml", "hi"));
        var sig = rsa.SignData(zip, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var (svc, cache) = New(zip, sig, rsa.ExportSubjectPublicKeyInfoPem());
        var r = await svc.SyncAsync();
        Assert.Equal(SyncStatus.Updated, r.Status);
        Assert.Equal(3, r.NewVersion);
        Assert.Equal(3, cache.TryReadManifest()!.BundleVersion);
    }

    [Fact]
    public async Task Same_version_reports_already_latest()
    {
        var rsa = RSA.Create(3072);
        var zip = BuildZip(3, ("a.yaml", "hi"));
        var sig = rsa.SignData(zip, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var (svc, _) = New(zip, sig, rsa.ExportSubjectPublicKeyInfoPem());
        await svc.SyncAsync();
        var r = await svc.SyncAsync(); // 第二次同样版本
        Assert.Equal(SyncStatus.AlreadyLatest, r.Status);
    }

    [Fact]
    public async Task Older_version_is_rejected()
    {
        var rsa = RSA.Create(3072);
        var newZip = BuildZip(5, ("a.yaml", "hi"));
        var (svc, cache) = New(newZip, rsa.SignData(newZip, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1),
            rsa.ExportSubjectPublicKeyInfoPem());
        await svc.SyncAsync(); // 先装 v5
        // 换成 v2 的旧包
        var oldZip = BuildZip(2, ("a.yaml", "old"));
        var svc2 = new RuleSyncService(
            new FakeDownloader(new() { [ZipUrl] = oldZip, [SigUrl] = rsa.SignData(oldZip, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1) }),
            new BundleVerifier(rsa.ExportSubjectPublicKeyInfoPem()), cache, ZipUrl, SigUrl,
            () => DateTimeOffset.UnixEpoch);
        var r = await svc2.SyncAsync();
        Assert.Equal(SyncStatus.DowngradeRejected, r.Status);
        Assert.Equal(5, cache.TryReadManifest()!.BundleVersion); // 仍是 v5
    }

    [Fact]
    public async Task Bad_signature_reports_verification_failed_and_keeps_cache()
    {
        var rsa = RSA.Create(3072);
        var zip = BuildZip(3, ("a.yaml", "hi"));
        var other = RSA.Create(3072);
        var (svc, cache) = New(zip, other.SignData(zip, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1),
            rsa.ExportSubjectPublicKeyInfoPem());
        var r = await svc.SyncAsync();
        Assert.Equal(SyncStatus.VerificationFailed, r.Status);
        Assert.Null(cache.TryReadManifest());
    }

    [Fact]
    public async Task Network_failure_does_not_throw()
    {
        var rsa = RSA.Create(3072);
        var (svc, _) = New(null, null, rsa.ExportSubjectPublicKeyInfoPem(), net: true);
        var r = await svc.SyncAsync();
        Assert.Equal(SyncStatus.NetworkFailed, r.Status);
    }

    [Fact]
    public async Task Each_attempt_saves_sync_state()
    {
        var rsa = RSA.Create(3072);
        var zip = BuildZip(3, ("a.yaml", "hi"));
        var (svc, cache) = New(zip, rsa.SignData(zip, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1),
            rsa.ExportSubjectPublicKeyInfoPem());
        await svc.SyncAsync();
        Assert.NotNull(cache.ReadSyncState());
    }

    [Fact]
    public async Task Cache_write_failure_does_not_throw()
    {
        var rsa = RSA.Create(3072);
        var zip = BuildZip(3, ("a.yaml", "hi"));
        var sig = rsa.SignData(zip, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        // 缓存目录放在一个普通文件"下面"，使 Replace 内部建目录必然抛 IOException
        var blocker = Path.Combine(Path.GetTempPath(), "uh-blocker-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(blocker, "x");
        try
        {
            var cache = new RuleCacheStore(Path.Combine(blocker, "sub", "cache"));
            var map = new Dictionary<string, byte[]> { [ZipUrl] = zip, [SigUrl] = sig };
            var svc = new RuleSyncService(new FakeDownloader(map), new BundleVerifier(rsa.ExportSubjectPublicKeyInfoPem()),
                cache, ZipUrl, SigUrl, () => DateTimeOffset.UnixEpoch);
            var r = await svc.SyncAsync();
            Assert.Equal(SyncStatus.CacheWriteFailed, r.Status);
        }
        finally { File.Delete(blocker); }
    }
}
