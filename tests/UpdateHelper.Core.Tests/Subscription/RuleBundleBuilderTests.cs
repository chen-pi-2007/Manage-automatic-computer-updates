using System.Security.Cryptography;
using UpdateHelper.Core.Rules.Subscription;

namespace UpdateHelper.Core.Tests.Subscription;

public sealed class RuleBundleBuilderTests
{
    private static string RulesDir()
    {
        var d = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "uh-rules-" + Guid.NewGuid().ToString("N"))).FullName;
        File.WriteAllText(Path.Combine(d, "a.yaml"), "id: a\n");
        File.WriteAllText(Path.Combine(d, "b.yaml"), "id: b\n");
        return d;
    }

    [Fact]
    public void Built_bundle_verifies_with_its_public_key()
    {
        var (priv, pub) = RuleBundleBuilder.GenerateKeyPair();
        var dir = RulesDir();
        var manifest = RuleBundleBuilder.BuildManifest(dir, version: 4);
        var zip = RuleBundleBuilder.BuildZip(dir, manifest);
        var sig = RuleBundleBuilder.Sign(zip, priv);

        var r = new BundleVerifier(pub).Verify(zip, sig);
        Assert.True(r.Ok, r.Error);
        Assert.Equal(4, r.Bundle!.Manifest.BundleVersion);
        Assert.Equal(2, r.Bundle.Manifest.RuleCount);
    }

    [Fact]
    public void Generated_keys_are_pem()
    {
        var (priv, pub) = RuleBundleBuilder.GenerateKeyPair();
        Assert.Contains("BEGIN PRIVATE KEY", priv);
        Assert.Contains("BEGIN PUBLIC KEY", pub);
    }

    [Fact]
    public void WriteBuiltinManifest_puts_manifest_in_dir()
    {
        var dir = RulesDir();
        RuleBundleBuilder.WriteBuiltinManifest(dir, 2);
        Assert.Equal(2, OfficialRules.TryReadManifest(dir)!.BundleVersion);
    }
}
