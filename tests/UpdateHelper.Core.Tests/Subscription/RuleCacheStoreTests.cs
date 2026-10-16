using System.Text;
using UpdateHelper.Core.Rules.Subscription;

namespace UpdateHelper.Core.Tests.Subscription;

public sealed class RuleCacheStoreTests
{
    private static string TempDir() => Directory.CreateDirectory(
        Path.Combine(Path.GetTempPath(), "uh-test-" + Guid.NewGuid().ToString("N"))).FullName;

    private static VerifiedBundle Bundle(int version, params (string Name, string Content)[] files)
    {
        var dict = files.ToDictionary(f => f.Name, f => Encoding.UTF8.GetBytes(f.Content), StringComparer.Ordinal);
        var manifest = new BundleManifest(version, DateTimeOffset.UnixEpoch, files.Length,
            files.Select(f => new ManifestFile(f.Name, "x")).ToList());
        return new VerifiedBundle(manifest, dict);
    }

    [Fact]
    public void Replace_then_read_manifest()
    {
        var dir = TempDir();
        var store = new RuleCacheStore(dir);
        Assert.Null(store.TryReadManifest());
        store.Replace(Bundle(7, ("a.yaml", "hi")));
        Assert.Equal(7, store.TryReadManifest()!.BundleVersion);
        Assert.Equal("hi", File.ReadAllText(Path.Combine(dir, "a.yaml")));
    }

    [Fact]
    public void Replace_removes_old_files()
    {
        var dir = TempDir();
        var store = new RuleCacheStore(dir);
        store.Replace(Bundle(1, ("old.yaml", "x")));
        store.Replace(Bundle(2, ("new.yaml", "y")));
        Assert.False(File.Exists(Path.Combine(dir, "old.yaml")));
        Assert.True(File.Exists(Path.Combine(dir, "new.yaml")));
    }

    [Fact]
    public void Corrupt_cache_manifest_reads_as_null()
    {
        var dir = TempDir();
        File.WriteAllText(Path.Combine(dir, "manifest.json"), "{ broken");
        Assert.Null(new RuleCacheStore(dir).TryReadManifest());
    }

    [Fact]
    public void Select_picks_higher_version()
    {
        var builtin = TempDir();
        var cache = TempDir();
        File.WriteAllText(Path.Combine(builtin, "manifest.json"),
            ManifestParser.Serialize(new BundleManifest(2, DateTimeOffset.UnixEpoch, 0, [])));
        File.WriteAllText(Path.Combine(cache, "manifest.json"),
            ManifestParser.Serialize(new BundleManifest(5, DateTimeOffset.UnixEpoch, 0, [])));
        Assert.Equal(cache, OfficialRules.SelectDirectory(builtin, cache));
    }

    [Fact]
    public void Select_prefers_builtin_when_newer_or_no_cache()
    {
        var builtin = TempDir();
        var cache = TempDir();
        File.WriteAllText(Path.Combine(builtin, "manifest.json"),
            ManifestParser.Serialize(new BundleManifest(9, DateTimeOffset.UnixEpoch, 0, [])));
        File.WriteAllText(Path.Combine(cache, "manifest.json"),
            ManifestParser.Serialize(new BundleManifest(5, DateTimeOffset.UnixEpoch, 0, [])));
        Assert.Equal(builtin, OfficialRules.SelectDirectory(builtin, cache));
        Assert.Equal(builtin, OfficialRules.SelectDirectory(builtin, TempDir())); // 缓存无 manifest
    }

    [Fact]
    public void Sync_state_roundtrips()
    {
        var store = new RuleCacheStore(TempDir());
        Assert.Null(store.ReadSyncState());
        var when = new DateTimeOffset(2026, 10, 13, 8, 0, 0, TimeSpan.Zero);
        store.SaveSyncState(when, "updated", 5);
        var s = store.ReadSyncState();
        Assert.Equal("updated", s!.Value.Status);
        Assert.Equal(5, s.Value.Version);
    }
}
