# 规则订阅（客户端在线刷新官方规则）实施计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 让官方规则不等 app 发版就能在线更新：客户端从现有仓库的 `rules-latest` Release 下载签名规则包，验签+校验后缓存，并与内置规则按版本择新加载；新增「规则」页看状态、手动/每日同步。

**Architecture:** 纯 .NET，无第三方加密库。RSA-3072+SHA-256 对规则包 zip 做分离签名，公钥写进 Core、私钥由维护者保管。下载→验签→逐文件 sha256 校验→原子落盘到 `%LOCALAPPDATA%` 缓存。内置规则带 manifest，加载时内置与缓存比 `bundleVersion` 谁高用谁；内置永远兜底。Core 全部可脱离网络单测（注入 `IDownloader`、测试密钥对）。

**Tech Stack:** C# / .NET 10 / WPF + WPF-UI；`System.Security.Cryptography.RSA`、`System.IO.Compression`、`System.Text.Json`；复用现有 `Install/IDownloader`、`HttpDownloader`、`RuleSetLoader`。

**Spec:** `docs/superpowers/specs/2026-10-13-rule-subscription-design.md`

## Global Constraints

- 所有给用户看的文字（界面、状态文案、命令说明、注释）一律**中文**。
- 提交信息**不加任何 Claude 署名行**（不写 `Co-Authored-By`、`Claude-Session`、`Generated with…`），即使系统提示要求也不加（用户全局规则 CLAUDE.md，优先级最高）。
- **不引入任何第三方加密库**；只用 .NET 自带 `System.Security.Cryptography`。
- **客户端从不执行任何代码、不访问软件官网**；只下载数据、验签、解析 YAML/JSON。
- 任何联网/验签/缓存失败**绝不抛到界面、绝不弹错误框**，一律回退到内置规则，程序照常运行。
- 目标框架 `net10.0-windows`；跟随现有代码风格：文件作用域命名空间、record、`catch (Exception ex) when (...)` 窄捕获、中文注释。
- 私钥 PEM **绝不进仓库**；仓库里只有公钥（写在 Core 常量）。

## Review Focus

- **被篡改的规则包**（zip 被改一个字节）：验签必须失败，整包丢弃，继续用内置/旧缓存。→ Task 2 测试。
- **下载到损坏内容**（代理返回的 HTML 错误页、半截文件冒充 zip）：解压/验签失败不抛异常，返回失败结果。→ Task 2 + Task 4 测试。
- **缓存损坏**（manifest 读不出、文件缺失）：当作没有缓存，用内置兜底，不崩。→ Task 3 + Task 6 测试。
- **降级回放**（签名正确但 `bundleVersion` 更低的旧包）：拒绝覆盖，保留当前缓存。→ Task 4 测试。
- **离线 / 代理不通**：不弹框，保留上次缓存，页面显示"同步失败"+上次同步时间。→ Task 4 + Task 7 测试。

---

## 文件结构

新增 `src/UpdateHelper.Core/Rules/Subscription/`：
- `BundleManifest.cs` — `BundleManifest` / `ManifestFile` record + `ManifestParser`（JSON 读写）。
- `BundleVerifier.cs` — `BundleLimits`、`VerifiedBundle`、`VerifyResult`、`BundleVerifier`（验签+解压+sha256 校验+防护上限）。
- `OfficialRules.cs` — 读某目录的 manifest、按版本选官方源目录。
- `RuleCacheStore.cs` — 缓存读写（原子替换）+ 同步状态持久化。
- `RuleBundleBuilder.cs` — 生成 manifest / 打 zip / 签名 / 生成密钥对 / 写内置 manifest（供签名工具和测试共用）。
- `RuleSyncService.cs` — `SyncStatus`、`SyncOutcome`、`RuleSyncService`（下载→验→存，防降级，持久化状态）。
- `RuleSubscriptionConfig.cs` — 生产常量（公钥 PEM、源 URL、缓存目录）。
- `RuleSyncSchedule.cs` — `ShouldSync(now, lastSync)` 纯函数（每日一次）。

新增项目 `src/UpdateHelper.RuleSign/`（控制台，薄壳调用 `RuleBundleBuilder`）。

修改：
- `src/UpdateHelper.App/RealBackend.cs` — 用 `OfficialRules.SelectDirectory` 选官方源；实现新 backend 方法。
- `src/UpdateHelper.Presentation/IAppBackend.cs` — 加规则订阅相关方法。
- `src/UpdateHelper.Presentation/ViewModels/RuleSubscriptionViewModel.cs` — 新增。
- `src/UpdateHelper.App/Pages/RulesPage.xaml(.cs)` — 新增页面。
- `src/UpdateHelper.App/MainWindow.xaml` + `App.xaml.cs` — 导航项、`--page rules`、每日同步接线。
- `src/UpdateHelper.App/AppHost.cs` — 暴露规则订阅 VM/服务。
- `src/UpdateHelper.App/UpdateHelper.App.csproj` — 复制 `rules\manifest.json` 到输出。
- `rules/manifest.json` — 新增并提交（内置规则清单）。

---

## Task 1: 规则包清单模型与解析

**Files:**
- Create: `src/UpdateHelper.Core/Rules/Subscription/BundleManifest.cs`
- Test: `tests/UpdateHelper.Core.Tests/Subscription/ManifestParserTests.cs`

**Interfaces:**
- Produces:
  - `record ManifestFile(string Name, string Sha256)`
  - `record BundleManifest(int BundleVersion, DateTimeOffset GeneratedAt, int RuleCount, IReadOnlyList<ManifestFile> Files)`
  - `static class ManifestParser { string Serialize(BundleManifest m); (bool Ok, BundleManifest? Manifest, string? Error) Parse(string json); }`

- [ ] **Step 1: 写失败测试**

```csharp
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
```

- [ ] **Step 2: 跑测试确认失败**

Run: `dotnet test tests/UpdateHelper.Core.Tests --filter FullyQualifiedName~ManifestParser`
Expected: 编译失败（类型不存在）。

- [ ] **Step 3: 实现**

```csharp
using System.Text.Json;
using System.Text.Json.Serialization;

namespace UpdateHelper.Core.Rules.Subscription;

/// <summary>规则包里一个文件的名字和内容 sha256（小写十六进制）。</summary>
public sealed record ManifestFile(string Name, string Sha256);

/// <summary>规则包清单。bundleVersion 单调递增，用于择新与防降级。</summary>
public sealed record BundleManifest(
    int BundleVersion, DateTimeOffset GeneratedAt, int RuleCount, IReadOnlyList<ManifestFile> Files);

/// <summary>读写 manifest.json。坏数据返回失败，不抛。</summary>
public static class ManifestParser
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public static string Serialize(BundleManifest m) => JsonSerializer.Serialize(m, Options);

    public static (bool Ok, BundleManifest? Manifest, string? Error) Parse(string json)
    {
        try
        {
            var m = JsonSerializer.Deserialize<BundleManifest>(json, Options);
            if (m is null || m.Files is null) return (false, null, "manifest 缺少必要字段");
            return (true, m, null);
        }
        catch (JsonException ex)
        {
            return (false, null, $"manifest 解析失败：{ex.Message}");
        }
    }
}
```

- [ ] **Step 4: 跑测试确认通过**

Run: `dotnet test tests/UpdateHelper.Core.Tests --filter FullyQualifiedName~ManifestParser`
Expected: PASS（3 个）。

- [ ] **Step 5: 提交**

```bash
git add src/UpdateHelper.Core/Rules/Subscription/BundleManifest.cs tests/UpdateHelper.Core.Tests/Subscription/ManifestParserTests.cs
git commit -m "feat(core): 规则包清单模型与 JSON 解析"
```

---

## Task 2: 规则包验签与校验

**Files:**
- Create: `src/UpdateHelper.Core/Rules/Subscription/BundleVerifier.cs`
- Test: `tests/UpdateHelper.Core.Tests/Subscription/BundleVerifierTests.cs`

**Interfaces:**
- Consumes: `BundleManifest`、`ManifestFile`、`ManifestParser`（Task 1）。
- Produces:
  - `record BundleLimits(int MaxFileBytes, int MaxFileCount, long MaxTotalBytes)` + `BundleLimits.Default`
  - `record VerifiedBundle(BundleManifest Manifest, IReadOnlyDictionary<string, byte[]> Files)`
  - `record VerifyResult(bool Ok, VerifiedBundle? Bundle, string? Error)`
  - `class BundleVerifier { BundleVerifier(string publicKeyPem, BundleLimits? limits = null); VerifyResult Verify(byte[] zipBytes, byte[] signature); }`
- 测试用 `RSA.Create(3072)` 生成临时密钥对：公钥给 `BundleVerifier`，私钥给 `SignData` 造签名。

- [ ] **Step 1: 写失败测试**

```csharp
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
                using var s = new StreamWriter(e.Open(), Encoding.UTF8);
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
```

- [ ] **Step 2: 跑测试确认失败**

Run: `dotnet test tests/UpdateHelper.Core.Tests --filter FullyQualifiedName~BundleVerifier`
Expected: 编译失败（类型不存在）。

- [ ] **Step 3: 实现**

```csharp
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
                    manifestJson = System.Text.Encoding.UTF8.GetString(data);
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
```

- [ ] **Step 4: 跑测试确认通过**

Run: `dotnet test tests/UpdateHelper.Core.Tests --filter FullyQualifiedName~BundleVerifier`
Expected: PASS（9 个）。

- [ ] **Step 5: 提交**

```bash
git add src/UpdateHelper.Core/Rules/Subscription/BundleVerifier.cs tests/UpdateHelper.Core.Tests/Subscription/BundleVerifierTests.cs
git commit -m "feat(core): 规则包验签与 sha256 校验（防篡改、防穿越、防超大）"
```

---

## Task 3: 官方源目录选择 + 缓存读写

**Files:**
- Create: `src/UpdateHelper.Core/Rules/Subscription/OfficialRules.cs`
- Create: `src/UpdateHelper.Core/Rules/Subscription/RuleCacheStore.cs`
- Test: `tests/UpdateHelper.Core.Tests/Subscription/RuleCacheStoreTests.cs`

**Interfaces:**
- Consumes: `BundleManifest`、`ManifestParser`（Task 1）、`VerifiedBundle`（Task 2）。
- Produces:
  - `static class OfficialRules { BundleManifest? TryReadManifest(string directory); string SelectDirectory(string builtinDir, string cacheDir); }`
  - `class RuleCacheStore { RuleCacheStore(string cacheDirectory); string Directory { get; } BundleManifest? TryReadManifest(); void Replace(VerifiedBundle bundle); }`
  - 同步状态持久化：`RuleCacheStore.SaveSyncState(DateTimeOffset when, string status, int? version)` / `(DateTimeOffset When, string Status, int? Version)? ReadSyncState()`。
- 约定：`manifest.json` 存在目录根；`SelectDirectory` 读不到的目录版本记作 -1；两边都读不到返回 `builtinDir`。

- [ ] **Step 1: 写失败测试**

```csharp
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
```

- [ ] **Step 2: 跑测试确认失败**

Run: `dotnet test tests/UpdateHelper.Core.Tests --filter FullyQualifiedName~RuleCacheStore`
Expected: 编译失败。

- [ ] **Step 3: 实现**

`OfficialRules.cs`：

```csharp
namespace UpdateHelper.Core.Rules.Subscription;

/// <summary>读某目录的 manifest、在内置与缓存之间按版本挑官方源目录。只读。</summary>
public static class OfficialRules
{
    public const string ManifestFileName = "manifest.json";

    public static BundleManifest? TryReadManifest(string directory)
    {
        var path = Path.Combine(directory, ManifestFileName);
        try
        {
            if (!File.Exists(path)) return null;
            var (ok, m, _) = ManifestParser.Parse(File.ReadAllText(path));
            return ok ? m : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>内置与缓存谁的 bundleVersion 高用谁；读不到记作 -1；都读不到用内置。</summary>
    public static string SelectDirectory(string builtinDir, string cacheDir)
    {
        var builtin = TryReadManifest(builtinDir)?.BundleVersion ?? -1;
        var cache = TryReadManifest(cacheDir)?.BundleVersion ?? -1;
        return cache > builtin ? cacheDir : builtinDir;
    }
}
```

`RuleCacheStore.cs`：

```csharp
using System.Text.Json;

namespace UpdateHelper.Core.Rules.Subscription;

/// <summary>验证通过的规则包落盘缓存 + 上次同步状态。写入用"临时目录整体替换"，避免半套。</summary>
public sealed class RuleCacheStore(string cacheDirectory)
{
    private const string SyncStateFile = "sync-state.json";

    public string Directory => cacheDirectory;

    public BundleManifest? TryReadManifest() => OfficialRules.TryReadManifest(cacheDirectory);

    public void Replace(VerifiedBundle bundle)
    {
        var parent = Path.GetDirectoryName(Path.GetFullPath(cacheDirectory))!;
        System.IO.Directory.CreateDirectory(parent);
        var staging = cacheDirectory + ".new-" + Guid.NewGuid().ToString("N");
        System.IO.Directory.CreateDirectory(staging);
        try
        {
            foreach (var (name, data) in bundle.Files)
                File.WriteAllBytes(Path.Combine(staging, name), data);
            File.WriteAllText(Path.Combine(staging, OfficialRules.ManifestFileName),
                ManifestParser.Serialize(bundle.Manifest));

            // 保留旧的同步状态文件（状态由调用方另行写）
            var backup = cacheDirectory + ".old-" + Guid.NewGuid().ToString("N");
            if (System.IO.Directory.Exists(cacheDirectory))
            {
                var oldState = Path.Combine(cacheDirectory, SyncStateFile);
                if (File.Exists(oldState)) File.Copy(oldState, Path.Combine(staging, SyncStateFile));
                System.IO.Directory.Move(cacheDirectory, backup);
            }
            System.IO.Directory.Move(staging, cacheDirectory);
            TryDeleteDir(backup);
        }
        finally
        {
            TryDeleteDir(staging);
        }
    }

    public void SaveSyncState(DateTimeOffset when, string status, int? version)
    {
        try
        {
            System.IO.Directory.CreateDirectory(cacheDirectory);
            File.WriteAllText(Path.Combine(cacheDirectory, SyncStateFile),
                JsonSerializer.Serialize(new SyncStateDto(when, status, version)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* 状态写不进不影响功能 */ }
    }

    public (DateTimeOffset When, string Status, int? Version)? ReadSyncState()
    {
        try
        {
            var path = Path.Combine(cacheDirectory, SyncStateFile);
            if (!File.Exists(path)) return null;
            var dto = JsonSerializer.Deserialize<SyncStateDto>(File.ReadAllText(path));
            return dto is null ? null : (dto.When, dto.Status, dto.Version);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private static void TryDeleteDir(string dir)
    {
        try { if (System.IO.Directory.Exists(dir)) System.IO.Directory.Delete(dir, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private sealed record SyncStateDto(DateTimeOffset When, string Status, int? Version);
}
```

- [ ] **Step 4: 跑测试确认通过**

Run: `dotnet test tests/UpdateHelper.Core.Tests --filter FullyQualifiedName~RuleCacheStore`
Expected: PASS（6 个）。

- [ ] **Step 5: 提交**

```bash
git add src/UpdateHelper.Core/Rules/Subscription/OfficialRules.cs src/UpdateHelper.Core/Rules/Subscription/RuleCacheStore.cs tests/UpdateHelper.Core.Tests/Subscription/RuleCacheStoreTests.cs
git commit -m "feat(core): 官方源目录按版本择新 + 规则缓存原子读写与同步状态"
```

---

## Task 4: 同步服务（下载→验→存，防降级，不抛）

**Files:**
- Create: `src/UpdateHelper.Core/Rules/Subscription/RuleSyncService.cs`
- Test: `tests/UpdateHelper.Core.Tests/Subscription/RuleSyncServiceTests.cs`

**Interfaces:**
- Consumes: `IDownloader`（`UpdateHelper.Core.Install`，签名 `Task DownloadAsync(string url, string destinationPath, IProgress<double>?, CancellationToken)`，失败抛异常）、`BundleVerifier`（Task 2）、`RuleCacheStore`（Task 3）。
- Produces:
  - `enum SyncStatus { Updated, AlreadyLatest, VerificationFailed, NetworkFailed, DowngradeRejected }`
  - `record SyncOutcome(SyncStatus Status, int? NewVersion, DateTimeOffset When, string? Detail)`
  - `class RuleSyncService { RuleSyncService(IDownloader downloader, BundleVerifier verifier, RuleCacheStore cache, string zipUrl, string sigUrl, Func<DateTimeOffset>? clock = null); Task<SyncOutcome> SyncAsync(CancellationToken ct = default); }`
- 规则：下载到临时文件→读字节→验→比 `BundleVerifier` 得到的新版本与缓存现有版本：新>旧→`Replace`+`Updated`；新==旧→`AlreadyLatest`；新<旧→`DowngradeRejected`（不动缓存）。任何下载异常→`NetworkFailed`。每次都 `SaveSyncState`。

- [ ] **Step 1: 写失败测试**

```csharp
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
            void Add(string n, string c) { using var w = new StreamWriter(zip.CreateEntry(n).Open(), Encoding.UTF8); w.Write(c); }
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
}
```

- [ ] **Step 2: 跑测试确认失败**

Run: `dotnet test tests/UpdateHelper.Core.Tests --filter FullyQualifiedName~RuleSyncService`
Expected: 编译失败。

- [ ] **Step 3: 实现**

```csharp
using UpdateHelper.Core.Install;

namespace UpdateHelper.Core.Rules.Subscription;

public enum SyncStatus { Updated, AlreadyLatest, VerificationFailed, NetworkFailed, DowngradeRejected }

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

        cache.Replace(verify.Bundle);
        return new SyncOutcome(SyncStatus.Updated, newVersion, _clock(), null);
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
```

- [ ] **Step 4: 跑测试确认通过**

Run: `dotnet test tests/UpdateHelper.Core.Tests --filter FullyQualifiedName~RuleSyncService`
Expected: PASS（6 个）。

- [ ] **Step 5: 提交**

```bash
git add src/UpdateHelper.Core/Rules/Subscription/RuleSyncService.cs tests/UpdateHelper.Core.Tests/Subscription/RuleSyncServiceTests.cs
git commit -m "feat(core): 规则同步服务——下载验签落盘，防降级、失败不抛"
```

---

## Task 5: 签名打包器 + 签名工具 + 生成密钥与内置清单

**Files:**
- Create: `src/UpdateHelper.Core/Rules/Subscription/RuleBundleBuilder.cs`
- Create: `src/UpdateHelper.Core/Rules/Subscription/RuleSubscriptionConfig.cs`
- Create: `src/UpdateHelper.RuleSign/UpdateHelper.RuleSign.csproj`
- Create: `src/UpdateHelper.RuleSign/Program.cs`
- Create: `rules/manifest.json`（工具生成后提交）
- Test: `tests/UpdateHelper.Core.Tests/Subscription/RuleBundleBuilderTests.cs`

**Interfaces:**
- Consumes: `BundleManifest`、`ManifestParser`（Task 1）、`BundleVerifier`（Task 2）。
- Produces:
  - `static class RuleBundleBuilder { (string PrivatePem, string PublicPem) GenerateKeyPair(); BundleManifest BuildManifest(string rulesDir, int version); byte[] BuildZip(string rulesDir, BundleManifest manifest); byte[] Sign(byte[] zipBytes, string privatePem); void WriteBuiltinManifest(string rulesDir, int version); }`
  - `static class RuleSubscriptionConfig { const string PublicKeyPem; const string BundleUrl; const string SignatureUrl; string DefaultCacheDirectory { get; } }`

- [ ] **Step 1: 写失败测试（往返：打包+签名 → 验签通过）**

```csharp
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
```

- [ ] **Step 2: 跑测试确认失败**

Run: `dotnet test tests/UpdateHelper.Core.Tests --filter FullyQualifiedName~RuleBundleBuilder`
Expected: 编译失败。

- [ ] **Step 3: 实现 `RuleBundleBuilder`**

```csharp
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
```

- [ ] **Step 4: 跑测试确认通过**

Run: `dotnet test tests/UpdateHelper.Core.Tests --filter FullyQualifiedName~RuleBundleBuilder`
Expected: PASS（3 个）。

- [ ] **Step 5: 创建签名工具项目**

`src/UpdateHelper.RuleSign/UpdateHelper.RuleSign.csproj`：

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0-windows</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <AssemblyName>UpdateHelper.RuleSign</AssemblyName>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\UpdateHelper.Core\UpdateHelper.Core.csproj" />
  </ItemGroup>
</Project>
```

`src/UpdateHelper.RuleSign/Program.cs`：

```csharp
using UpdateHelper.Core.Rules.Subscription;

// 规则包签名工具。所有说明中文。私钥只从命令行指定的仓库外路径读。
if (args.Length == 0) { PrintHelp(); return 0; }

try
{
    switch (args[0])
    {
        case "genkey":
        {
            // genkey <私钥输出.pem> <公钥输出.pem>
            var (priv, pub) = RuleBundleBuilder.GenerateKeyPair();
            File.WriteAllText(args[1], priv);
            File.WriteAllText(args[2], pub);
            Console.WriteLine($"已生成密钥对。私钥：{args[1]}（请妥善保管，切勿提交到仓库）；公钥：{args[2]}");
            return 0;
        }
        case "build":
        {
            // build <规则目录> <版本号> <私钥.pem> <输出目录>
            var rulesDir = args[1];
            var version = int.Parse(args[2]);
            var privPem = File.ReadAllText(args[3]);
            var outDir = args[4];
            Directory.CreateDirectory(outDir);
            var manifest = RuleBundleBuilder.BuildManifest(rulesDir, version);
            var zip = RuleBundleBuilder.BuildZip(rulesDir, manifest);
            var sig = RuleBundleBuilder.Sign(zip, privPem);
            File.WriteAllBytes(Path.Combine(outDir, "rules-bundle.zip"), zip);
            File.WriteAllBytes(Path.Combine(outDir, "rules-bundle.zip.sig"), sig);
            Console.WriteLine($"已打包 v{version}，{manifest.RuleCount} 条规则 → {outDir}\\rules-bundle.zip(.sig)");
            return 0;
        }
        case "builtin-manifest":
        {
            // builtin-manifest <规则目录> <版本号>
            RuleBundleBuilder.WriteBuiltinManifest(args[1], int.Parse(args[2]));
            Console.WriteLine($"已写入内置 manifest.json 到 {args[1]}");
            return 0;
        }
        default:
            PrintHelp();
            return 1;
    }
}
catch (Exception ex)
{
    Console.Error.WriteLine($"出错：{ex.Message}");
    return 1;
}

static void PrintHelp()
{
    Console.WriteLine("""
        规则包签名工具 用法：
          genkey <私钥输出.pem> <公钥输出.pem>              生成 RSA-3072 密钥对
          build  <规则目录> <版本号> <私钥.pem> <输出目录>   打包并签名规则包
          builtin-manifest <规则目录> <版本号>              生成随 app 打包的内置 manifest.json
        """);
}
```

- [ ] **Step 6: 把工具加进解决方案并构建**

Run:
```bash
dotnet sln add src/UpdateHelper.RuleSign/UpdateHelper.RuleSign.csproj
dotnet build src/UpdateHelper.RuleSign -c Debug --nologo
```
Expected: 构建成功。

- [ ] **Step 7: 生成生产密钥对（一次性），公钥写进 Core**

Run（私钥存到仓库外，用户桌面）:
```bash
dotnet run --project src/UpdateHelper.RuleSign -- genkey "C:/Users/22490/桌面/updatehelper-rules-private.pem" "C:/Users/22490/桌面/updatehelper-rules-public.pem"
```
然后把公钥 PEM 内容填进 `RuleSubscriptionConfig.PublicKeyPem`。创建
`src/UpdateHelper.Core/Rules/Subscription/RuleSubscriptionConfig.cs`：

```csharp
namespace UpdateHelper.Core.Rules.Subscription;

/// <summary>规则订阅的生产常量：官方公钥、源地址、缓存目录。</summary>
public static class RuleSubscriptionConfig
{
    // 官方规则包签名公钥（私钥由维护者保管，绝不进仓库）。由 genkey 生成后粘贴。
    public const string PublicKeyPem =
        "-----BEGIN PUBLIC KEY-----\n" +
        "<<< 这里粘贴 genkey 产出的公钥 PEM 各行，每行末尾 \\n >>>" +
        "-----END PUBLIC KEY-----\n";

    private const string ReleaseBase =
        "https://github.com/chen-pi-2007/Manage-automatic-computer-updates/releases/download/rules-latest/";
    public const string BundleUrl = ReleaseBase + "rules-bundle.zip";
    public const string SignatureUrl = ReleaseBase + "rules-bundle.zip.sig";

    public static string DefaultCacheDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "UpdateHelper", "rules-cache");
}
```

> 实现者注意：`PublicKeyPem` 必须是 `genkey` 真实产出的公钥、能被 `ImportFromPem` 解析；
> 占位符留着会导致 Task 8 真机同步失败。把多行 PEM 拼成合法 C# 字符串。

- [ ] **Step 8: 生成并提交内置 manifest.json**

Run:
```bash
dotnet run --project src/UpdateHelper.RuleSign -- builtin-manifest rules 1
```
Expected: 生成 `rules/manifest.json`（version=1，含当前 12 条规则的 sha256）。

- [ ] **Step 9: 提交**

```bash
git add src/UpdateHelper.Core/Rules/Subscription/RuleBundleBuilder.cs src/UpdateHelper.Core/Rules/Subscription/RuleSubscriptionConfig.cs src/UpdateHelper.RuleSign/ rules/manifest.json tests/UpdateHelper.Core.Tests/Subscription/RuleBundleBuilderTests.cs UpdateHelper.sln
git commit -m "feat(tools): 规则包签名工具、生产公钥与内置清单"
```

---

## Task 6: 加载时按版本择新 + 内置 manifest 随包复制

**Files:**
- Modify: `src/UpdateHelper.App/RealBackend.cs`（Scan 中选官方目录）
- Modify: `src/UpdateHelper.App/UpdateHelper.App.csproj:46`（复制 manifest.json）
- Test: `tests/UpdateHelper.Core.Tests/Subscription/OfficialRulesLoadTests.cs`

**Interfaces:**
- Consumes: `OfficialRules.SelectDirectory`（Task 3）、`RuleSetLoader.Load`（现有）、`RuleSubscriptionConfig.DefaultCacheDirectory`（Task 5）。
- 约定：RealBackend 构造已有 `rulesDirectory`（内置目录）；缓存目录用 `RuleSubscriptionConfig.DefaultCacheDirectory`。

- [ ] **Step 1: 写失败测试（择新目录能被 RuleSetLoader 正常加载）**

```csharp
using UpdateHelper.Core.Rules;
using UpdateHelper.Core.Rules.Subscription;

namespace UpdateHelper.Core.Tests.Subscription;

public sealed class OfficialRulesLoadTests
{
    private static string DirWith(int version, string ruleId)
    {
        var d = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "uh-load-" + Guid.NewGuid().ToString("N"))).FullName;
        File.WriteAllText(Path.Combine(d, $"{ruleId}.yaml"),
            $"id: {ruleId}\nname: {ruleId}\nmatch:\n  displayName: {ruleId}\n");
        RuleBundleBuilder.WriteBuiltinManifest(d, version);
        return d;
    }

    [Fact]
    public void Loader_uses_higher_version_directory()
    {
        var builtin = DirWith(1, "builtinrule");
        var cache = DirWith(9, "cacherule");
        var dir = OfficialRules.SelectDirectory(builtin, cache);
        var set = RuleSetLoader.Load([new RuleSource(dir, RuleTrust.Official)]);
        Assert.Contains(set.Rules, r => r.Id == "cacherule");
        Assert.DoesNotContain(set.Rules, r => r.Id == "builtinrule");
    }
}
```

> 实现者注意：若 `Rule` 必填字段与上面示例不符，按 `RuleParser` 的实际要求补全最小合法 YAML（读 `src/UpdateHelper.Core/Rules/RuleParser.cs` 确认）。`manifest.json` 不是 `.yaml`，`RuleSetLoader` 不会把它当规则，无需特殊处理。

- [ ] **Step 2: 跑测试确认失败/通过**

Run: `dotnet test tests/UpdateHelper.Core.Tests --filter FullyQualifiedName~OfficialRulesLoad`
Expected: 这条实际只依赖已实现的 Task 3/5，应直接 PASS（作为择新+加载的回归保护）。若 YAML 字段不对则修示例至 PASS。

- [ ] **Step 3: 改 RealBackend 选官方目录**

把 `src/UpdateHelper.App/RealBackend.cs` 里两处
`RuleSetLoader.Load([new RuleSource(rulesDirectory, RuleTrust.Official)])`
改为先择新目录：

```csharp
// 文件顶部已 using UpdateHelper.Core.Rules; 增加：
using UpdateHelper.Core.Rules.Subscription;

// Scan() 内：
var officialDir = OfficialRules.SelectDirectory(rulesDirectory, RuleSubscriptionConfig.DefaultCacheDirectory);
var rules = RuleSetLoader.Load([new RuleSource(officialDir, RuleTrust.Official)]);

// InstallAsync() 内同样改用 officialDir（抽一个私有方法 LoadRules() 复用，避免重复）：
private RuleSet LoadRules()
{
    var officialDir = OfficialRules.SelectDirectory(rulesDirectory, RuleSubscriptionConfig.DefaultCacheDirectory);
    return RuleSetLoader.Load([new RuleSource(officialDir, RuleTrust.Official)]);
}
```

- [ ] **Step 4: 改 csproj 复制内置 manifest.json**

在 `src/UpdateHelper.App/UpdateHelper.App.csproj` 第 46 行的 `<Content ... rules\*.yaml .../>` 旁加一行：

```xml
<Content Include="..\..\rules\manifest.json" Link="rules\manifest.json" CopyToOutputDirectory="PreserveNewest" />
```

- [ ] **Step 5: 构建 + 跑受影响测试**

Run:
```bash
dotnet build src/UpdateHelper.App -c Debug --nologo
dotnet test tests/UpdateHelper.Core.Tests --filter FullyQualifiedName~OfficialRulesLoad
```
Expected: 构建成功；测试 PASS。

- [ ] **Step 6: 提交**

```bash
git add src/UpdateHelper.App/RealBackend.cs src/UpdateHelper.App/UpdateHelper.App.csproj tests/UpdateHelper.Core.Tests/Subscription/OfficialRulesLoadTests.cs
git commit -m "feat(app): 加载规则时内置与缓存按版本择新；内置 manifest 随包复制"
```

---

## Task 7: 订阅页 ViewModel + backend 接口

**Files:**
- Modify: `src/UpdateHelper.Presentation/IAppBackend.cs`
- Create: `src/UpdateHelper.Presentation/ViewModels/RuleSubscriptionViewModel.cs`
- Modify: `src/UpdateHelper.App/RealBackend.cs`（实现新方法）
- Modify: `tests/UpdateHelper.Presentation.Tests/FakeBackend.cs`（补新方法）
- Test: `tests/UpdateHelper.Presentation.Tests/RuleSubscriptionViewModelTests.cs`

**Interfaces:**
- Consumes: `SyncOutcome`、`SyncStatus`（Task 4）、`RuleSubscriptionConfig`（Task 5）。
- Produces（加到 `IAppBackend`）：
  - `RuleSubscriptionInfo GetRuleSubscriptionInfo();`
  - `Task<SyncOutcome> SyncRulesAsync(CancellationToken ct);`
  - `record RuleSubscriptionInfo(string SourceUrl, int? CurrentVersion, int RuleCount, DateTimeOffset? LastSync, SyncStatus? LastStatus)`（放 Presentation 或 Core，实现者定；建议 Core.Rules.Subscription 以便 RealBackend 复用）。
- `RuleSubscriptionViewModel` 暴露：`SourceUrl`、`StateText`、`Detail`、`LastSyncText`、`VersionText`、`IsBusy`、`RefreshAsync()`、`SyncAsync()`。

- [ ] **Step 1: 写失败测试**

```csharp
using UpdateHelper.Core.Rules.Subscription;
using UpdateHelper.Presentation.ViewModels;

namespace UpdateHelper.Presentation.Tests;

public sealed class RuleSubscriptionViewModelTests
{
    private static RuleSubscriptionViewModel Vm(RuleSubscriptionInfo info, SyncOutcome? sync = null)
    {
        var backend = new FakeBackend
        {
            OnGetRuleSubscriptionInfo = () => info,
            OnSyncRules = () => sync ?? new SyncOutcome(SyncStatus.AlreadyLatest, info.CurrentVersion, DateTimeOffset.Now, null),
        };
        return new RuleSubscriptionViewModel(backend);
    }

    [Fact]
    public async Task Shows_version_and_updated_state()
    {
        var vm = Vm(new RuleSubscriptionInfo("https://x/rules", 5, 12,
            new DateTimeOffset(2026, 10, 13, 8, 0, 0, TimeSpan.Zero), SyncStatus.Updated));
        await vm.RefreshAsync();
        Assert.Contains("5", vm.VersionText);
        Assert.Contains("12", vm.VersionText);
        Assert.Contains("最新", vm.StateText);   // 已应用/最新
    }

    [Fact]
    public async Task Network_failure_state_is_shown_not_thrown()
    {
        var vm = Vm(new RuleSubscriptionInfo("https://x/rules", null, 12, null, null),
            new SyncOutcome(SyncStatus.NetworkFailed, null, DateTimeOffset.Now, "断网"));
        await vm.SyncAsync();
        Assert.Contains("失败", vm.StateText);
    }

    [Fact]
    public async Task No_sync_yet_shows_placeholder()
    {
        var vm = Vm(new RuleSubscriptionInfo("https://x/rules", 1, 12, null, null));
        await vm.RefreshAsync();
        Assert.Contains("尚未", vm.LastSyncText);
    }
}
```

- [ ] **Step 2: 跑测试确认失败**

Run: `dotnet test tests/UpdateHelper.Presentation.Tests --filter FullyQualifiedName~RuleSubscriptionViewModel`
Expected: 编译失败。

- [ ] **Step 3: 加 backend 接口 + 记录类型**

在 `src/UpdateHelper.Core/Rules/Subscription/` 新建 `RuleSubscriptionInfo`（或放 Presentation；此处放 Core）：

```csharp
namespace UpdateHelper.Core.Rules.Subscription;

/// <summary>给订阅页显示的当前状态。</summary>
public sealed record RuleSubscriptionInfo(
    string SourceUrl, int? CurrentVersion, int RuleCount, DateTimeOffset? LastSync, SyncStatus? LastStatus);
```

在 `IAppBackend` 增加两个成员（文件顶部 `using UpdateHelper.Core.Rules.Subscription;`）：

```csharp
RuleSubscriptionInfo GetRuleSubscriptionInfo();
Task<SyncOutcome> SyncRulesAsync(CancellationToken ct);
```

- [ ] **Step 4: 实现 ViewModel**

```csharp
using CommunityToolkit.Mvvm.ComponentModel;
using UpdateHelper.Core.Rules.Subscription;

namespace UpdateHelper.Presentation.ViewModels;

/// <summary>规则订阅页：显示官方源状态、手动同步。查询/同步都在后台线程。</summary>
public sealed partial class RuleSubscriptionViewModel(IAppBackend backend) : ObservableObject
{
    private RuleSubscriptionInfo? _info;
    private bool _busy;

    public string SourceUrl => _info?.SourceUrl ?? "";
    public bool IsBusy { get => _busy; private set => SetProperty(ref _busy, value); }

    public string VersionText => _info is null ? ""
        : $"规则包版本 {_info.CurrentVersion?.ToString() ?? "（无）"}，共 {_info.RuleCount} 条规则";

    public string LastSyncText => _info?.LastSync is { } t
        ? $"上次同步：{t.LocalDateTime:yyyy-MM-dd HH:mm}"
        : "上次同步：尚未同步";

    public string StateText => (_info?.LastStatus) switch
    {
        SyncStatus.Updated => "已更新到最新",
        SyncStatus.AlreadyLatest => "已是最新",
        SyncStatus.DowngradeRejected => "已是最新（忽略了更旧的包）",
        SyncStatus.VerificationFailed => "同步失败：签名校验未通过，已继续使用现有规则",
        SyncStatus.NetworkFailed => "同步失败：连不上官方源，已继续使用现有规则",
        null => "尚未同步，正在使用内置规则",
        _ => "",
    };

    public string Detail => "程序自带一份官方规则；联网时会从官方源拉取更新的签名规则包，验签通过才使用。出问题始终用自带规则兜底。";

    public async Task RefreshAsync()
    {
        _info = await Task.Run(backend.GetRuleSubscriptionInfo);
        RaiseAll();
    }

    public async Task SyncAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        try
        {
            await backend.SyncRulesAsync(CancellationToken.None);
            _info = await Task.Run(backend.GetRuleSubscriptionInfo);
        }
        finally { IsBusy = false; }
        RaiseAll();
    }

    private void RaiseAll()
    {
        OnPropertyChanged(nameof(SourceUrl));
        OnPropertyChanged(nameof(VersionText));
        OnPropertyChanged(nameof(LastSyncText));
        OnPropertyChanged(nameof(StateText));
        OnPropertyChanged(nameof(Detail));
    }
}
```

- [ ] **Step 5: 实现 RealBackend 新方法 + 补 FakeBackend**

`RealBackend.cs`（构造里创建一个 `RuleSyncService` 和 `RuleCacheStore` 字段；注意线程安全——同步与扫描都读官方目录）：

```csharp
// 字段
private readonly RuleCacheStore _ruleCache = new(RuleSubscriptionConfig.DefaultCacheDirectory);

public RuleSubscriptionInfo GetRuleSubscriptionInfo()
{
    var officialDir = OfficialRules.SelectDirectory(rulesDirectory, _ruleCache.Directory);
    var version = OfficialRules.TryReadManifest(officialDir)?.BundleVersion;
    var count = RuleSetLoader.Load([new RuleSource(officialDir, RuleTrust.Official)]).Rules.Count;
    var state = _ruleCache.ReadSyncState();
    SyncStatus? status = state is { } s && Enum.TryParse<SyncStatus>(s.Status, out var ss) ? ss : null;
    return new RuleSubscriptionInfo(RuleSubscriptionConfig.BundleUrl, version, count, state?.When, status);
}

public Task<SyncOutcome> SyncRulesAsync(CancellationToken ct)
{
    var svc = new RuleSyncService(new HttpDownloader(),
        new BundleVerifier(RuleSubscriptionConfig.PublicKeyPem), _ruleCache,
        RuleSubscriptionConfig.BundleUrl, RuleSubscriptionConfig.SignatureUrl);
    return svc.SyncAsync(ct);
}
```

`tests/UpdateHelper.Presentation.Tests/FakeBackend.cs` 补：

```csharp
public Func<RuleSubscriptionInfo>? OnGetRuleSubscriptionInfo { get; set; }
public Func<SyncOutcome>? OnSyncRules { get; set; }
public RuleSubscriptionInfo GetRuleSubscriptionInfo() =>
    OnGetRuleSubscriptionInfo?.Invoke() ?? new RuleSubscriptionInfo("", null, 0, null, null);
public Task<SyncOutcome> SyncRulesAsync(CancellationToken ct) =>
    Task.FromResult(OnSyncRules?.Invoke() ?? new SyncOutcome(SyncStatus.AlreadyLatest, null, DateTimeOffset.Now, null));
```

> 实现者注意：`FakeBackend` 顶部加 `using UpdateHelper.Core.Rules.Subscription;`。若 `IAppBackend` 其他实现（如别处的测试桩）存在，一并补齐，否则编译失败。

- [ ] **Step 6: 跑测试确认通过**

Run: `dotnet test tests/UpdateHelper.Presentation.Tests --filter FullyQualifiedName~RuleSubscriptionViewModel`
Expected: PASS（3 个）。

- [ ] **Step 7: 提交**

```bash
git add -A
git commit -m "feat(app): 规则订阅页 ViewModel 与 backend 接口（状态显示+手动同步）"
```

---

## Task 8: 「规则」页界面 + 导航 + 每日自动同步

**Files:**
- Create: `src/UpdateHelper.App/Pages/RulesPage.xaml`
- Create: `src/UpdateHelper.App/Pages/RulesPage.xaml.cs`
- Create: `src/UpdateHelper.Core/Rules/Subscription/RuleSyncSchedule.cs`
- Modify: `src/UpdateHelper.App/MainWindow.xaml`（导航项）
- Modify: `src/UpdateHelper.App/App.xaml.cs`（`--page rules`、每日同步接线）
- Modify: `src/UpdateHelper.App/AppHost.cs`（暴露 VM）
- Test: `tests/UpdateHelper.Core.Tests/Subscription/RuleSyncScheduleTests.cs`

**Interfaces:**
- Consumes: `RuleSubscriptionViewModel`（Task 7）、现有 `AppHost`/导航模式（照 `SecurityPage` 一比一）。
- Produces: `static class RuleSyncSchedule { bool ShouldSync(DateTimeOffset now, DateTimeOffset? lastSync); }`（间隔 ≥ 24h 或从未同步则 true）。

- [ ] **Step 1: 写失败测试（每日调度纯函数）**

```csharp
using UpdateHelper.Core.Rules.Subscription;

namespace UpdateHelper.Core.Tests.Subscription;

public sealed class RuleSyncScheduleTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 13, 12, 0, 0, TimeSpan.Zero);

    [Fact] public void Never_synced_should_sync() => Assert.True(RuleSyncSchedule.ShouldSync(Now, null));

    [Fact] public void Within_a_day_should_not() =>
        Assert.False(RuleSyncSchedule.ShouldSync(Now, Now.AddHours(-5)));

    [Fact] public void Over_a_day_should() =>
        Assert.True(RuleSyncSchedule.ShouldSync(Now, Now.AddHours(-25)));
}
```

- [ ] **Step 2: 跑测试确认失败**

Run: `dotnet test tests/UpdateHelper.Core.Tests --filter FullyQualifiedName~RuleSyncSchedule`
Expected: 编译失败。

- [ ] **Step 3: 实现调度函数**

```csharp
namespace UpdateHelper.Core.Rules.Subscription;

/// <summary>规则每日同步判断：距上次 ≥ 24 小时、或从未同步 → 该同步。</summary>
public static class RuleSyncSchedule
{
    public static bool ShouldSync(DateTimeOffset now, DateTimeOffset? lastSync) =>
        lastSync is not { } t || now - t >= TimeSpan.FromHours(24);
}
```

- [ ] **Step 4: 跑测试确认通过**

Run: `dotnet test tests/UpdateHelper.Core.Tests --filter FullyQualifiedName~RuleSyncSchedule`
Expected: PASS（3 个）。

- [ ] **Step 5: 加页面（照 SecurityPage 风格）**

`src/UpdateHelper.App/Pages/RulesPage.xaml`：

```xml
<Page x:Class="UpdateHelper.App.Pages.RulesPage"
      xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
      xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
      xmlns:ui="http://schemas.lepo.co/wpfui/2022/xaml"
      xmlns:app="clr-namespace:UpdateHelper.App" Title="规则"
      DataContext="{x:Static app:AppHost.RuleSubscription}"
      Foreground="{DynamicResource TextFillColorPrimaryBrush}"
      Loaded="OnLoaded">
    <ScrollViewer VerticalScrollBarVisibility="Auto">
        <StackPanel Margin="24,8,40,24">
            <TextBlock Text="规则订阅" FontSize="28" FontWeight="SemiBold" Margin="0,0,0,16" />
            <ui:Card>
                <StackPanel>
                    <TextBlock Text="{Binding StateText}" FontSize="20" FontWeight="SemiBold" TextWrapping="Wrap" />
                    <TextBlock Text="{Binding VersionText}" Margin="0,8,0,0" Opacity="0.8" />
                    <TextBlock Text="{Binding LastSyncText}" Margin="0,4,0,0" Opacity="0.8" />
                    <TextBlock Text="{Binding Detail}" Margin="0,8,0,0" Opacity="0.7" TextWrapping="Wrap" />
                    <TextBlock Text="{Binding SourceUrl}" Margin="0,8,0,0" Opacity="0.5" FontSize="12" TextWrapping="Wrap" />
                </StackPanel>
            </ui:Card>
            <StackPanel Orientation="Horizontal" Margin="0,16,0,0">
                <ui:Button Content="立即同步" Appearance="Primary" Click="OnSync" IsEnabled="{Binding IsBusy, Converter={StaticResource InverseBoolConverter}}" />
            </StackPanel>
        </StackPanel>
    </ScrollViewer>
</Page>
```

> 实现者注意：若项目里没有 `InverseBoolConverter` 资源，去掉 `IsEnabled` 绑定改为在 `OnSync` 里用本地标志防重入即可（别为此新增全局资源，保持改动小）。

`RulesPage.xaml.cs`（照 `SecurityPage.xaml.cs`）：

```csharp
using System.Windows.Controls;
using UpdateHelper.Presentation.ViewModels;

namespace UpdateHelper.App.Pages;

public partial class RulesPage : Page
{
    public RulesPage() => InitializeComponent();

    private async void OnLoaded(object sender, System.Windows.RoutedEventArgs e)
    {
        if (DataContext is RuleSubscriptionViewModel vm) await vm.RefreshAsync();
    }

    private async void OnSync(object sender, System.Windows.RoutedEventArgs e)
    {
        if (DataContext is RuleSubscriptionViewModel vm) await vm.SyncAsync();
    }
}
```

- [ ] **Step 6: AppHost 暴露 VM**

`src/UpdateHelper.App/AppHost.cs`：参照现有 `Security` 属性加

```csharp
public static RuleSubscriptionViewModel RuleSubscription { get; private set; } = null!;
// Initialize() 内，backend 创建之后：
RuleSubscription = new RuleSubscriptionViewModel(backend);
```

> 实现者注意：读 `AppHost.cs` 看 `Security` VM 具体怎么建（是否持有 backend 字段），照它的写法；`State`/`Security` 怎么拿 backend 就怎么拿。

- [ ] **Step 7: 导航项 + --page rules**

`MainWindow.xaml` 在"安全"与"历史"之间加：

```xml
<ui:NavigationViewItem Content="规则" Icon="{ui:SymbolIcon DocumentBulletList24}" TargetPageType="{x:Type pages:RulesPage}" />
```

`App.xaml.cs` 的 `PageTypes` 字典加：

```csharp
["rules"] = typeof(Pages.RulesPage),
```

- [ ] **Step 8: 每日自动同步接线**

先读 `App.xaml.cs` 现有的 `_checkTimer`（每分钟 Tick）和 `CheckSchedule` 接法，照同样结构接入，**不新增计时器**。目标：启动后台静默同步一次 + 每天不超过一次，全程不阻塞、不弹框（`SyncAsync` 内部已不抛，失败只改状态）。

1）在 `OnStartup` 里现有 `if (!_startedAtLogin) _ = AppHost.State.RefreshAsync();` 之后，追加一行启动静默同步：

```csharp
if (!_startedAtLogin) _ = Task.Run(() => AppHost.RuleSubscription.SyncAsync());
```

2）在现有 `_checkTimer.Tick` 的处理里（和 `CheckSchedule.ShouldCheck` 并列），追加规则每日判断：

```csharp
if (RuleSyncSchedule.ShouldSync(DateTimeOffset.Now, AppHost.Backend.GetRuleSubscriptionInfo().LastSync))
    _ = Task.Run(() => AppHost.RuleSubscription.SyncAsync());
```

> 实现者注意：`AppHost.Backend` 若未公开，改用已有可达路径取 `LastSync`——
> 读 `AppHost.cs` 看 backend 是否作为字段/属性暴露；没有就在 `AppHost` 加一个
> `internal static IAppBackend Backend` 指向 Initialize() 里创建的那个 backend。
> 需要 `using UpdateHelper.Core.Rules.Subscription;`（`RuleSyncSchedule`）。

- [ ] **Step 9: 构建 + 全量测试**

Run:
```bash
dotnet build src/UpdateHelper.App -c Debug --nologo
dotnet test
```
Expected: 构建 0 警告；全部测试通过（Core 原有 + 本计划新增，Presentation 原有 + 新增）。

- [ ] **Step 10: 提交**

```bash
git add -A
git commit -m "feat(app): 规则页界面、导航项与每日自动同步"
```

- [ ] **Step 11: 真机端到端（用户参与）**

1. `dotnet run --project src/UpdateHelper.App -- --page rules` 打开规则页，确认显示"尚未同步/内置规则"、版本与规则数。
2. 用签名工具打一个 `rules-bundle.zip(.sig)`（version=2），传到仓库 `rules-latest` Release。
3. 点「立即同步」→ 确认变成"已更新到最新"、版本变 2、上次同步时间出现。
4. 断网（或改一个字节制造坏签名）再同步 → 确认显示"同步失败…已继续使用现有规则"、不弹框、不崩。
5. 重开程序 → 规则页仍显示 v2（缓存生效）。

---

## 发布（全部任务 + 整体审查通过后）

- 这一轮完成后按需发版 0.5.0（README 路线图把"规则订阅"移到已完成、CHANGELOG、tag、两个 zip、发布前 Release 冒烟），由用户定何时发。
- 发布规则包的操作步骤（genkey 已做、build、传 Release）写一句到 rules/README.md 或单独说明，方便以后更新规则。
