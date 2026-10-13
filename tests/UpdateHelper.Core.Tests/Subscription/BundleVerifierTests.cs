using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using UpdateHelper.Core.Rules.Subscription;

namespace UpdateHelper.Core.Tests.Subscription;

public sealed class BundleVerifierTests
{
    private static string Sha256Hex(byte[] b) => Convert.ToHexString(SHA256.HashData(b)).ToLowerInvariant();

    // 造一个规则包 zip：给定若干 (名字, 内容)，自动生成匹配的 manifest.json
    private static byte[] BuildZip(IEnumerable<(string Name, string Content)> files, int version = 1,
        string? overrideManifestJson = null)
    {
        var list = files.ToList();
        var manifest = new BundleManifest(version, DateTimeOffset.UnixEpoch, list.Count,
            list.Select(f => new ManifestFile(f.Name, Sha256Hex(Encoding.UTF8.GetBytes(f.Content)))).ToList());
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            void Add(string name, string content)
            {
                var e = zip.CreateEntry(name);
                // 用无 BOM 的 UTF-8，保证写入 zip 的字节与上面算 sha256 用的字节一致
                using var s = new StreamWriter(e.Open(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                s.Write(content);
            }
            Add("manifest.json", overrideManifestJson ?? ManifestParser.Serialize(manifest));
            foreach (var f in list) Add(f.Name, f.Content);
        }
        return ms.ToArray();
    }

    private static (BundleVerifier verifier, Func<byte[], byte[]> sign) NewPair(BundleLimits? limits = null)
    {
        var rsa = RSA.Create(3072);
        var verifier = new BundleVerifier(rsa.ExportSubjectPublicKeyInfoPem(), limits);
        return (verifier, zip => rsa.SignData(zip, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
    }

    [Fact]
    public void Valid_bundle_passes()
    {
        var (v, sign) = NewPair();
        var zip = BuildZip([("a.yaml", "hello"), ("b.yaml", "world")], version: 5);
        var r = v.Verify(zip, sign(zip));
        Assert.True(r.Ok, r.Error);
        Assert.Equal(5, r.Bundle!.Manifest.BundleVersion);
        Assert.Equal("hello", Encoding.UTF8.GetString(r.Bundle.Files["a.yaml"]));
    }

    [Fact]
    public void Tampered_zip_fails_signature()
    {
        var (v, sign) = NewPair();
        var zip = BuildZip([("a.yaml", "hello")]);
        var sig = sign(zip);
        zip[zip.Length / 2] ^= 0xFF; // 改一个字节
        var r = v.Verify(zip, sig);
        Assert.False(r.Ok);
    }

    [Fact]
    public void Signature_from_other_key_fails()
    {
        var (v, _) = NewPair();
        var other = RSA.Create(3072);
        var zip = BuildZip([("a.yaml", "hello")]);
        var r = v.Verify(zip, other.SignData(zip, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
        Assert.False(r.Ok);
    }

    [Fact]
    public void Content_hash_mismatch_fails()
    {
        var (v, sign) = NewPair();
        // manifest 里写错 a.yaml 的 hash
        var bad = new BundleManifest(1, DateTimeOffset.UnixEpoch, 1, [new ManifestFile("a.yaml", "deadbeef")]);
        var zip = BuildZip([("a.yaml", "hello")], overrideManifestJson: ManifestParser.Serialize(bad));
        var r = v.Verify(zip, sign(zip));
        Assert.False(r.Ok);
    }

    [Fact]
    public void Extra_file_not_in_manifest_fails()
    {
        var (v, sign) = NewPair();
        var m = new BundleManifest(1, DateTimeOffset.UnixEpoch, 1,
            [new ManifestFile("a.yaml", Sha256Hex(Encoding.UTF8.GetBytes("hello")))]);
        // zip 里多一个 manifest 没登记的 b.yaml
        var zip = BuildZip([("a.yaml", "hello"), ("b.yaml", "x")], overrideManifestJson: ManifestParser.Serialize(m));
        var r = v.Verify(zip, sign(zip));
        Assert.False(r.Ok);
    }

    [Fact]
    public void Path_traversal_entry_rejected()
    {
        var (v, sign) = NewPair();
        var content = "x";
        var m = new BundleManifest(1, DateTimeOffset.UnixEpoch, 1,
            [new ManifestFile("../evil.yaml", Sha256Hex(Encoding.UTF8.GetBytes(content)))]);
        var zip = BuildZip([("../evil.yaml", content)], overrideManifestJson: ManifestParser.Serialize(m));
        var r = v.Verify(zip, sign(zip));
        Assert.False(r.Ok);
    }

    [Fact]
    public void Oversize_file_rejected()
    {
        var (v, sign) = NewPair(new BundleLimits(MaxFileBytes: 8, MaxFileCount: 100, MaxTotalBytes: 1000));
        var zip = BuildZip([("a.yaml", new string('x', 50))]);
        var r = v.Verify(zip, sign(zip));
        Assert.False(r.Ok);
    }

    [Fact]
    public void Garbage_not_a_zip_fails_without_throwing()
    {
        var (v, sign) = NewPair();
        var junk = Encoding.UTF8.GetBytes("<html>error</html>");
        var r = v.Verify(junk, sign(junk)); // 签名对的上，但不是 zip
        Assert.False(r.Ok);
    }
}
