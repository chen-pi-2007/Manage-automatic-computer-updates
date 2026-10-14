using UpdateHelper.Core.Rules;
using UpdateHelper.Core.Rules.ThirdParty;

namespace UpdateHelper.Core.Tests.ThirdParty;

public class RuleSourceStoreTests
{
    private static RuleSourceStore New(TempDir dir) =>
        new(System.IO.Path.Combine(dir.Path, "rule-sources.json"), System.IO.Path.Combine(dir.Path, "cache"));

    [Fact]
    public void Source_id_is_64_hex_lowercase_and_stable()
    {
        var a = SourceId.FromUrl("https://x/rules.yaml");
        var b = SourceId.FromUrl("https://x/rules.yaml");
        Assert.Equal(a, b);
        Assert.Equal(64, a.Length);
        Assert.Matches("^[0-9a-f]{64}$", a);
    }

    [Fact]
    public void Missing_or_corrupt_config_falls_back_to_default()   // Review Focus 3
    {
        using var dir = new TempDir();
        var store = New(dir);
        var missing = store.Read();
        Assert.True(missing.ThirdPartyEnabled);
        Assert.Empty(missing.Sources);

        dir.Write("rule-sources.json", "{ 这不是合法 json");
        var corrupt = store.Read();
        Assert.True(corrupt.ThirdPartyEnabled);
        Assert.Empty(corrupt.Sources);
    }

    [Fact]
    public void Write_then_read_roundtrips()
    {
        using var dir = new TempDir();
        var store = New(dir);
        var config = new ThirdPartyConfig(false, [
            new ThirdPartySource(SourceId.FromUrl("https://x/a.yaml"), "https://x/a.yaml", "备注A", true, false),
            new ThirdPartySource(SourceId.FromUrl("https://x/b.yaml"), "https://x/b.yaml", "", false, true)]);
        store.Write(config);

        var read = store.Read();
        Assert.False(read.ThirdPartyEnabled);
        Assert.Equal(2, read.Sources.Count);
        Assert.Equal("备注A", read.Sources[0].Note);
        Assert.True(read.Sources[1].AllowOverrideOfficial);
        Assert.False(read.Sources[1].Enabled);
    }

    [Fact]
    public void Clean_orphan_caches_removes_dirs_not_in_config()
    {
        using var dir = new TempDir();
        var store = New(dir);
        var keep = SourceId.FromUrl("https://x/keep.yaml");
        var orphan = SourceId.FromUrl("https://x/gone.yaml");
        System.IO.Directory.CreateDirectory(store.CacheDirFor(keep));
        System.IO.Directory.CreateDirectory(store.CacheDirFor(orphan));

        store.CleanOrphanCaches(new ThirdPartyConfig(true, [
            new ThirdPartySource(keep, "https://x/keep.yaml", "", true, false)]));

        Assert.True(System.IO.Directory.Exists(store.CacheDirFor(keep)));
        Assert.False(System.IO.Directory.Exists(store.CacheDirFor(orphan)));
    }

    [Fact]
    public void Build_rule_sources_includes_only_enabled_and_maps_fields()
    {
        using var dir = new TempDir();
        var store = New(dir);
        var id = SourceId.FromUrl("https://x/a.yaml");
        var config = new ThirdPartyConfig(true, [
            new ThirdPartySource(id, "https://x/a.yaml", "社区源", true, true),
            new ThirdPartySource(SourceId.FromUrl("https://x/b.yaml"), "https://x/b.yaml", "", false, false)]);

        var built = store.BuildRuleSources(config);
        var s = Assert.Single(built);
        Assert.Equal(store.CacheDirFor(id), s.Directory);
        Assert.Equal(RuleTrust.ThirdParty, s.Trust);
        Assert.True(s.AllowOverrideOfficial);
        Assert.Equal("https://x/a.yaml", s.SourceUrl);
        Assert.Equal("社区源", s.SourceName);
    }

    [Fact]
    public void Build_rule_sources_empty_when_global_disabled()
    {
        using var dir = new TempDir();
        var store = New(dir);
        var config = new ThirdPartyConfig(false, [
            new ThirdPartySource(SourceId.FromUrl("https://x/a.yaml"), "https://x/a.yaml", "", true, false)]);
        Assert.Empty(store.BuildRuleSources(config));
    }
}
