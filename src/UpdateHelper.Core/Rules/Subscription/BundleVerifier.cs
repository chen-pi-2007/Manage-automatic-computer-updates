using System.IO.Compression;
using System.Security.Cryptography;

namespace UpdateHelper.Core.Rules.Subscription;

/// <summary>解压防护上限，防异常/超大包。</summary>
public sealed record BundleLimits(int MaxFileBytes, int MaxFileCount, long MaxTotalBytes)
{
    public static BundleLimits Default { get; } = new(MaxFileBytes: 1 * 1024 * 1024, MaxFileCount: 500, MaxTotalBytes: 20L * 1024 * 1024);
}

/// <summary>验证通过的规则包：清单 + 每个文件的内容。</summary>
public sealed record VerifiedBundle(BundleManifest Manifest, IReadOnlyDictionary<string, byte[]> Files);

public sealed record VerifyResult(bool Ok, VerifiedBundle? Bundle, string? Error)
{
    public static VerifyResult Fail(string error) => new(false, null, error);
}

/// <summary>用内置公钥验规则包签名，再逐文件校验 sha256 与 manifest 一致。只读、不落盘、不抛。</summary>
public sealed class BundleVerifier(string publicKeyPem, BundleLimits? limits = null)
{
    private readonly BundleLimits _limits = limits ?? BundleLimits.Default;

    public VerifyResult Verify(byte[] zipBytes, byte[] signature)
    {
        // 1. 先验签（对整个 zip 字节）。不过就整包作废。
        try
        {
            using var rsa = RSA.Create();
            rsa.ImportFromPem(publicKeyPem);
            if (!rsa.VerifyData(zipBytes, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1))
                return VerifyResult.Fail("签名校验未通过");
        }
        catch (Exception ex) when (ex is CryptographicException or ArgumentException)
        {
            return VerifyResult.Fail($"签名校验出错：{ex.Message}");
        }

        // 2. 解压（带上限、拒绝路径穿越），读出 manifest 和各文件
        try
        {
            using var ms = new MemoryStream(zipBytes, writable: false);
            using var zip = new ZipArchive(ms, ZipArchiveMode.Read);

            if (zip.Entries.Count > _limits.MaxFileCount) return VerifyResult.Fail("规则包文件过多");

            var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            string? manifestJson = null;
            long total = 0;

            foreach (var entry in zip.Entries)
            {
                var name = entry.FullName;
                // 必须是扁平的简单文件名：没有目录分隔、没有 ..、不是绝对路径
                if (name.Contains('/') || name.Contains('\\') || name.Contains("..")
                    || Path.IsPathRooted(name) || string.IsNullOrWhiteSpace(name))
                    return VerifyResult.Fail($"规则包含非法条目：{name}");
                if (entry.Length > _limits.MaxFileBytes) return VerifyResult.Fail($"规则包文件过大：{name}");

                var data = ReadCapped(entry, _limits.MaxFileBytes);
                if (data is null) return VerifyResult.Fail($"规则包文件过大：{name}");
                total += data.Length;
                if (total > _limits.MaxTotalBytes) return VerifyResult.Fail("规则包解压后过大");

                if (string.Equals(name, "manifest.json", StringComparison.Ordinal))
                    // 去掉可能存在的 UTF-8 BOM，否则 JSON 解析会因开头的 U+FEFF 失败
                    manifestJson = System.Text.Encoding.UTF8.GetString(data).TrimStart('﻿');
                else
                    files[name] = data;
            }

            if (manifestJson is null) return VerifyResult.Fail("规则包缺少 manifest.json");
            var (ok, manifest, err) = ManifestParser.Parse(manifestJson);
            if (!ok) return VerifyResult.Fail(err!);

            // 3. manifest 与实际文件必须不多不少、sha256 一致
            if (manifest!.Files.Count != files.Count) return VerifyResult.Fail("规则包文件数与清单不符");
            foreach (var mf in manifest.Files)
            {
                if (!files.TryGetValue(mf.Name, out var data)) return VerifyResult.Fail($"清单文件缺失：{mf.Name}");
                var actual = Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
                if (!string.Equals(actual, mf.Sha256, StringComparison.OrdinalIgnoreCase))
                    return VerifyResult.Fail($"文件内容与清单不符：{mf.Name}");
            }

            return new VerifyResult(true, new VerifiedBundle(manifest, files), null);
        }
        catch (InvalidDataException ex)
        {
            return VerifyResult.Fail($"规则包不是有效的 zip：{ex.Message}");
        }
    }

    // 读 entry 内容，超过 cap 就返回 null（防谎报长度的 zip 炸弹）
    private static byte[]? ReadCapped(ZipArchiveEntry entry, int cap)
    {
        using var s = entry.Open();
        using var buf = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = s.Read(chunk, 0, chunk.Length)) > 0)
        {
            buf.Write(chunk, 0, read);
            if (buf.Length > cap) return null;
        }
        return buf.ToArray();
    }
}
