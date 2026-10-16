using System.IO.Compression;
using System.Security.Cryptography;

namespace UpdateHelper.Core.Rules.Subscription;

/// <summary>打包与签名规则包（供签名工具和测试共用）。只处理本地文件，不联网。</summary>
public static class RuleBundleBuilder
{
    public static (string PrivatePem, string PublicPem) GenerateKeyPair()
    {
        using var rsa = RSA.Create(3072);
        return (rsa.ExportPkcs8PrivateKeyPem(), rsa.ExportSubjectPublicKeyInfoPem());
    }

    private static IEnumerable<string> YamlFiles(string rulesDir) =>
        Directory.EnumerateFiles(rulesDir, "*.yaml", SearchOption.TopDirectoryOnly)
            .Concat(Directory.EnumerateFiles(rulesDir, "*.yml", SearchOption.TopDirectoryOnly))
            .OrderBy(Path.GetFileName, StringComparer.Ordinal);

    public static BundleManifest BuildManifest(string rulesDir, int version)
    {
        var files = YamlFiles(rulesDir).Select(f =>
            new ManifestFile(Path.GetFileName(f),
                Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(f))).ToLowerInvariant())).ToList();
        return new BundleManifest(version, DateTimeOffset.UtcNow, files.Count, files);
    }

    public static byte[] BuildZip(string rulesDir, BundleManifest manifest)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            WriteEntry(zip, "manifest.json", System.Text.Encoding.UTF8.GetBytes(ManifestParser.Serialize(manifest)));
            foreach (var mf in manifest.Files)
                WriteEntry(zip, mf.Name, File.ReadAllBytes(Path.Combine(rulesDir, mf.Name)));
        }
        return ms.ToArray();
    }

    public static byte[] Sign(byte[] zipBytes, string privatePem)
    {
        using var rsa = RSA.Create();
        rsa.ImportFromPem(privatePem);
        return rsa.SignData(zipBytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
    }

    /// <summary>把内置规则的 manifest.json 写进 rulesDir（随 app 打包，供加载时比版本）。</summary>
    public static void WriteBuiltinManifest(string rulesDir, int version) =>
        File.WriteAllText(Path.Combine(rulesDir, OfficialRules.ManifestFileName),
            ManifestParser.Serialize(BuildManifest(rulesDir, version)));

    private static void WriteEntry(ZipArchive zip, string name, byte[] data)
    {
        using var s = zip.CreateEntry(name).Open();
        s.Write(data, 0, data.Length);
    }
}
