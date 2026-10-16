using UpdateHelper.Core.Rules.Subscription;

namespace UpdateHelper.Core.Tests.Subscription;

public sealed class ManifestParserTests
{
    [Fact]
    public void Roundtrips()
    {
        var m = new BundleManifest(3, new DateTimeOffset(2026, 10, 13, 0, 0, 0, TimeSpan.Zero), 2,
            [new ManifestFile("a.yaml", "aa"), new ManifestFile("b.yaml", "bb")]);
        var (ok, parsed, err) = ManifestParser.Parse(ManifestParser.Serialize(m));
        Assert.True(ok, err);
        Assert.Equal(3, parsed!.BundleVersion);
        Assert.Equal(2, parsed.Files.Count);
        Assert.Equal("b.yaml", parsed.Files[1].Name);
    }

    [Fact]
    public void Bad_json_fails_without_throwing()
    {
        var (ok, parsed, err) = ManifestParser.Parse("{ not json");
        Assert.False(ok);
        Assert.Null(parsed);
        Assert.NotNull(err);
    }

    [Fact]
    public void Missing_fields_fail()
    {
        var (ok, _, _) = ManifestParser.Parse("{\"bundleVersion\":1}"); // 缺 files
        Assert.False(ok);
    }
}
