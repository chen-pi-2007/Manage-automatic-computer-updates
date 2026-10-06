# 计划 5：补充库软件的安装器 + 数字签名校验 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 让 winget 里没有、但 YAML 规则里写了 `update.latest` 的软件（WPS、迅雷之类）也能被发现有更新、被下载、被**校验数字签名**、被静默安装；签名无效或签名公司不对的安装包一律不运行。

**Architecture:** 新增两条链路，都接到计划 3、4 已有的接口上：
- 发现：`RuleUpdateSource : IUpdateSource` 从规则里的 `update.latest` 产生更新候选；`UpdateService` 支持多个来源，winget 优先，同一个软件不重复
- 安装：`RuleInstaller : IPackageInstaller` 依次调用 `IDownloader`（下载）、`ISignatureVerifier`（签名校验）、`IProcessRunner`（运行安装包）；`InstallerRouter` 按包 id 前缀 `rule:` 在 winget 安装器和规则安装器之间分流。`UpdateExecutor`（计划 4）完全不用改

**Tech Stack:** C# / .NET 10、WinVerifyTrust（wintrust.dll）、HttpClient、xUnit。

**Spec:** `docs/superpowers/specs/2026-09-30-update-helper-design.md`（第 6 节"补充库的包核对 Authenticode 签名，签名者必须和规则中的 signer 一致"；第 8 节规则安全底线；第 16 节"签名通过的文件无论从哪里下载都是原版"）

**前置：** 计划 1～4 已合并（`main` 上的 `681b04c`）。

## 技术验证结论（2026-10-07 在开发者本机实验，代码已丢弃）

用 `WinVerifyTrust`（`WINTRUST_ACTION_GENERIC_VERIFY_V2`，不弹界面，不联网查吊销）和 `X509Certificate.CreateFromSignedFile` 测了四个文件：

| 文件 | WinVerifyTrust 结果 | CreateFromSignedFile 读到的签名者 |
|---|---|---|
| `C:\Program Files\dotnet\dotnet.exe` | `0`（有效） | `CN=.NET, O=Microsoft Corporation` |
| dotnet.exe 的副本，中间改了 1 个字节 | `0x80096010`（签名与内容不符） | **仍然是 `CN=.NET, O=Microsoft Corporation`** |
| 4 个字节的假 exe | `0x800B0003` | 抛 `CryptographicException` |
| `C:\Windows\System32\notepad.exe` | `0x800B0100`（文件内没有签名） | 抛 `CryptographicException` |

**结论：**
1. **`CreateFromSignedFile` 不做任何校验**，被篡改的文件照样读出"微软"。所以必须**先** WinVerifyTrust 返回 0，**再**比较签名者；只比签名者等于没校验。这是本计划最重要的一条
2. 签名者要同时比较证书的 CN 和 O：dotnet.exe 的 CN 是 `.NET`，O 才是 `Microsoft Corporation`
3. 系统文件（notepad.exe）是"目录签名"，文件里没有签名，单独校验会失败；软件安装包都是文件内签名，不受影响
4. `X509Certificate.CreateFromSignedFile` 在 .NET 10 上可能报过时警告 SYSLIB0057，用 `#pragma warning disable SYSLIB0057` 局部关掉（这里只用它读名字，可信与否由 WinVerifyTrust 决定）

## 关于沙盒（与计划 4 开头的安排有变化）

计划 4 原先安排"在 Windows 沙盒里用自制假安装包测试失败、签名不对等情况"。实验后发现这些情况都能在本机安全地测：
- 签名校验只读文件、不运行文件：用 dotnet.exe 和它被篡改的副本测
- 安装失败、需要重启：运行 Windows 自带的 `cmd.exe /c exit 1603`、`/c exit 3010` 模拟，不安装任何东西
- 端到端：用一条**故意写错签名公司**的测试规则，真实下载一个微软的安装包，验证它在签名核对这一步被拒绝、根本不会运行

所以本计划不需要沙盒。用户此前也提出过对沙盒性能的顾虑。

## Global Constraints

- 沿用前几个计划：Core 和测试 `net10.0-windows`；Winget 和 ScanCli `net10.0-windows10.0.26100.0` + `win-x64`；警告即错误；提交信息不带任何 AI 署名
- **没有通过签名校验的文件绝不运行**：WinVerifyTrust 必须返回 0，**并且**签名者（CN 或 O）与规则的 `signer` 相同（忽略大小写和首尾空白）
- **规则没写 `signer` 就不安装**（spec 第 8 节：签名是社区规则的安全底线）
- 校验失败的下载文件立即删除
- 只安装规则 `update.latest.version` 写的那个版本；和判断层评估的目标版本对不上就拒绝（与计划 4 的 winget 安装器一致）
- 安装程序一旦开始运行，**不因取消而中途结束它**（强行结束安装程序可能把软件装坏）；取消只在下载和校验阶段生效
- 签名校验不联网查吊销列表（离线可用、不拖慢速度）；这是已知的取舍，记入暂缓事项
- winget 能管的软件以 winget 为准：同一个软件两个来源都有更新时，只保留 winget 的
- 测试不读真实注册表；签名测试使用 `C:\Program Files\dotnet\dotnet.exe`（开发机必有）和临时目录里的副本；运行测试只用 `cmd.exe /c exit N`

## Review Focus

1. **被篡改的安装包**（签名者字段照样能读出"正确的公司"）→ 必须被拒绝，并且文件被删除
2. **签名公司对不上**（社区规则写错或被恶意修改了下载地址）→ 拒绝，提示里写出实际签名者和规则要求的签名者
3. **下载到一半失败或被取消** → 不留下半截文件，不运行任何东西
4. **安装程序要求管理员权限而用户在确认框里点了"否"**（退出码 1223 或 1602）→ 记为失败，提示"没有获得管理员授权"，不当作安装程序出错
5. **两个来源都报告同一个软件有更新**（winget 和规则）→ 只出现一条，用 winget 的

以上每条都在对应任务里有专门的测试（第 4 条用 `cmd /c exit 1223` 模拟）。

---

## 文件结构

```
src/UpdateHelper.Core/Security/
  SignatureVerifier.cs        ISignatureVerifier、SignatureCheck、AuthenticodeVerifier（WinVerifyTrust + 签名者比较）
src/UpdateHelper.Core/Updates/
  RuleUpdateSource.cs         从规则的 update.latest 产生更新候选
  UpdateService.cs            （修改）支持多个来源、去重、合并警告
src/UpdateHelper.Core/Install/
  Downloader.cs               IDownloader、HttpDownloader
  ProcessRunner.cs            IProcessRunner、ProcessRunner（运行安装包，返回退出码）
  RuleInstaller.cs            规则安装器：下载 → 校验 → 运行 → 解读退出码
  InstallerRouter.cs          按 "rule:" 前缀分流到规则安装器或 winget 安装器
src/UpdateHelper.ScanCli/Program.cs   （修改）--updates、--install 同时使用两个来源和分流安装器
tests/UpdateHelper.Core.Tests/
  AuthenticodeVerifierTests.cs
  RuleUpdateSourceTests.cs
  HttpDownloaderTests.cs
  ProcessRunnerTests.cs
  RuleInstallerTests.cs
  InstallerRouterTests.cs
```

## 任务清单

- Task 1：AuthenticodeVerifier（签名校验）
- Task 2：RuleUpdateSource + UpdateService 多来源
- Task 3：HttpDownloader（下载）
- Task 4：ProcessRunner（运行安装包）
- Task 5：RuleInstaller（下载 → 校验 → 运行）
- Task 6：InstallerRouter + ScanCli 接入，本机端到端验证"签名不对就不运行"

---

### Task 1：AuthenticodeVerifier（签名校验）

**Files:**
- Create: `src/UpdateHelper.Core/Security/SignatureVerifier.cs`
- Test: `tests/UpdateHelper.Core.Tests/AuthenticodeVerifierTests.cs`

**Interfaces:**
- Consumes: `TempDir`（计划 2 测试辅助）
- Produces（命名空间 `UpdateHelper.Core.Security`）：
  - `sealed record SignatureCheck(bool Trusted, string? Signer, string Message)`——`Signer` 是证书的 O（没有 O 时为 CN）；`Message` 是给用户看的中文
  - `interface ISignatureVerifier { SignatureCheck Verify(string filePath, string expectedSigner); }`
  - `sealed class AuthenticodeVerifier : ISignatureVerifier`——**永不抛异常**

**判断顺序：**
1. `expectedSigner` 为空 → 不可信："规则没有写签名者，不能安装"
2. 文件不存在 → 不可信："找不到安装包文件"
3. WinVerifyTrust 结果不为 0 → 不可信，按结果码说明原因：
   - `0x800B0100`：安装包没有数字签名
   - `0x80096010`：签名与文件内容不符，文件可能被篡改过
   - `0x800B0109`：签名证书不受信任
   - 其他：签名校验失败（0x……）
4. 读签名证书的 CN 和 O；`expectedSigner` 与其中任何一个相同（忽略大小写、首尾空白）→ 可信："签名有效，签名者：{O 或 CN}"
5. 否则 → 不可信："签名者是“{实际}”，不是规则要求的“{要求}”"

- [ ] **Step 1: 写失败的测试** `tests/UpdateHelper.Core.Tests/AuthenticodeVerifierTests.cs`

```csharp
using UpdateHelper.Core.Security;

namespace UpdateHelper.Core.Tests;

/// <summary>用开发机上必有的 dotnet.exe（微软签名）及其副本测试签名校验。只读文件，不运行任何文件。</summary>
public class AuthenticodeVerifierTests
{
    private static readonly string SignedFile =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet", "dotnet.exe");

    private static readonly AuthenticodeVerifier Verifier = new();

    [Fact]
    public void Test_fixture_exists()
        => Assert.True(File.Exists(SignedFile), $"测试需要 {SignedFile}");

    [Theory]
    [InlineData("Microsoft Corporation")]     // 证书的 O
    [InlineData(".NET")]                      // 证书的 CN
    [InlineData("  microsoft corporation ")]  // 忽略大小写和首尾空白
    public void Genuine_file_with_matching_signer_is_trusted(string expected)
    {
        var check = Verifier.Verify(SignedFile, expected);
        Assert.True(check.Trusted, check.Message);
        Assert.Equal("Microsoft Corporation", check.Signer);
        Assert.Equal("签名有效，签名者：Microsoft Corporation", check.Message);
    }

    [Fact]
    public void Wrong_signer_is_rejected_and_names_both()   // Review Focus 2
    {
        var check = Verifier.Verify(SignedFile, "Zhuhai Kingsoft Office Software Co., Ltd");
        Assert.False(check.Trusted);
        Assert.Equal("签名者是“Microsoft Corporation”，不是规则要求的“Zhuhai Kingsoft Office Software Co., Ltd”", check.Message);
    }

    [Fact]
    public void Tampered_file_is_rejected_even_though_signer_still_reads_correctly()   // Review Focus 1
    {
        using var dir = new TempDir();
        var bytes = File.ReadAllBytes(SignedFile);
        bytes[bytes.Length / 2] ^= 0xFF;
        var tampered = Path.Combine(dir.Path, "tampered.exe");
        File.WriteAllBytes(tampered, bytes);

        var check = Verifier.Verify(tampered, "Microsoft Corporation");

        Assert.False(check.Trusted);
        Assert.Equal("签名与文件内容不符，文件可能被篡改过", check.Message);
    }

    [Fact]
    public void Unsigned_file_is_rejected()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "unsigned.exe");
        File.WriteAllBytes(path, [0x4D, 0x5A, 0, 0]);

        var check = Verifier.Verify(path, "Microsoft Corporation");

        Assert.False(check.Trusted);
        Assert.Null(check.Signer);
        Assert.StartsWith("签名校验失败", check.Message);
    }

    [Fact]
    public void Missing_file_is_rejected()
    {
        var check = Verifier.Verify(@"C:\no\such\setup.exe", "Microsoft Corporation");
        Assert.False(check.Trusted);
        Assert.Equal("找不到安装包文件", check.Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Empty_expected_signer_is_rejected(string expected)
    {
        var check = Verifier.Verify(SignedFile, expected);
        Assert.False(check.Trusted);
        Assert.Equal("规则没有写签名者，不能安装", check.Message);
    }
}
```

- [ ] **Step 2: 运行，确认失败**

Run: `dotnet test --filter AuthenticodeVerifierTests`
Expected: 编译失败，找不到 `UpdateHelper.Core.Security`

- [ ] **Step 3: 实现** `src/UpdateHelper.Core/Security/SignatureVerifier.cs`

```csharp
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace UpdateHelper.Core.Security;

/// <summary>签名校验结果。Signer 是证书的组织名（没有时为通用名）；Message 给用户看。</summary>
public sealed record SignatureCheck(bool Trusted, string? Signer, string Message);

public interface ISignatureVerifier
{
    SignatureCheck Verify(string filePath, string expectedSigner);
}

/// <summary>
/// Authenticode 签名校验。顺序很重要：先用 WinVerifyTrust 确认签名有效，再比较签名者。
/// 只比签名者等于没校验——被篡改的文件照样能读出原来的签名者（计划 5 技术验证）。
/// 不联网查吊销列表。永不抛异常。
/// </summary>
public sealed class AuthenticodeVerifier : ISignatureVerifier
{
    private const uint TrustNoSignature = 0x800B0100;
    private const uint TrustBadDigest = 0x80096010;
    private const uint TrustUntrustedRoot = 0x800B0109;

    public SignatureCheck Verify(string filePath, string expectedSigner)
    {
        if (string.IsNullOrWhiteSpace(expectedSigner))
            return new SignatureCheck(false, null, "规则没有写签名者，不能安装");
        if (!File.Exists(filePath))
            return new SignatureCheck(false, null, "找不到安装包文件");

        var code = WinVerifyTrustFile(filePath);
        if (code != 0)
        {
            var reason = code switch
            {
                TrustNoSignature => "安装包没有数字签名",
                TrustBadDigest => "签名与文件内容不符，文件可能被篡改过",
                TrustUntrustedRoot => "签名证书不受信任",
                _ => $"签名校验失败（0x{code:X8}）",
            };
            return new SignatureCheck(false, null, reason);
        }

        var (cn, o) = ReadSigner(filePath);
        var actual = o ?? cn;
        if (actual is null)
            return new SignatureCheck(false, null, "签名有效，但读不到签名者");

        var want = expectedSigner.Trim();
        var matches = string.Equals(want, o?.Trim(), StringComparison.OrdinalIgnoreCase)
                      || string.Equals(want, cn?.Trim(), StringComparison.OrdinalIgnoreCase);
        return matches
            ? new SignatureCheck(true, actual, $"签名有效，签名者：{actual}")
            : new SignatureCheck(false, actual, $"签名者是“{actual}”，不是规则要求的“{want}”");
    }

    /// <summary>读证书的通用名（CN）和组织名（O）。只用来读名字，可信与否已由 WinVerifyTrust 决定。</summary>
    private static (string? Cn, string? O) ReadSigner(string filePath)
    {
        try
        {
#pragma warning disable SYSLIB0057
            using var cert = new X509Certificate2(X509Certificate.CreateFromSignedFile(filePath));
#pragma warning restore SYSLIB0057
            var cn = cert.GetNameInfo(X509NameType.SimpleName, false);
            var o = cert.SubjectName.EnumerateRelativeDistinguishedNames()
                .Where(r => !r.HasMultipleElements && r.GetSingleElementType().Value == "2.5.4.10")   // O（组织名）的 OID
                .Select(r => r.GetSingleElementValue())
                .FirstOrDefault();
            return (string.IsNullOrWhiteSpace(cn) ? null : cn, string.IsNullOrWhiteSpace(o) ? null : o);
        }
        catch (CryptographicException)
        {
            return (null, null);
        }
    }

    // ———— WinVerifyTrust ————

    private static readonly Guid GenericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    private static uint WinVerifyTrustFile(string path)
    {
        var fileInfo = new WintrustFileInfo
        {
            cbStruct = (uint)Marshal.SizeOf<WintrustFileInfo>(),
            pcwszFilePath = path,
        };
        var pFile = Marshal.AllocHGlobal(Marshal.SizeOf<WintrustFileInfo>());
        try
        {
            Marshal.StructureToPtr(fileInfo, pFile, false);
            var data = new WintrustData
            {
                cbStruct = (uint)Marshal.SizeOf<WintrustData>(),
                dwUIChoice = 2,               // WTD_UI_NONE：不弹任何界面
                fdwRevocationChecks = 0,      // WTD_REVOKE_NONE：不联网查吊销
                dwUnionChoice = 1,            // WTD_CHOICE_FILE
                pFile = pFile,
                dwStateAction = 1,            // WTD_STATEACTION_VERIFY
                dwProvFlags = 0x80,           // WTD_REVOCATION_CHECK_NONE
            };
            var action = GenericVerifyV2;
            var result = (uint)WinVerifyTrust(-1, ref action, ref data);

            data.dwStateAction = 2;           // WTD_STATEACTION_CLOSE：释放校验状态
            WinVerifyTrust(-1, ref action, ref data);
            return result;
        }
        finally
        {
            Marshal.DestroyStructure<WintrustFileInfo>(pFile);
            Marshal.FreeHGlobal(pFile);
        }
    }

    [DllImport("wintrust.dll", CharSet = CharSet.Unicode)]
    private static extern int WinVerifyTrust(nint hwnd, ref Guid action, ref WintrustData data);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WintrustFileInfo
    {
        public uint cbStruct;
        public string pcwszFilePath;
        public nint hFile;
        public nint pgKnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WintrustData
    {
        public uint cbStruct;
        public nint pPolicyCallbackData;
        public nint pSIPClientData;
        public uint dwUIChoice;
        public uint fdwRevocationChecks;
        public uint dwUnionChoice;
        public nint pFile;
        public uint dwStateAction;
        public nint hWVTStateData;
        public nint pwszURLReference;
        public uint dwProvFlags;
        public uint dwUIContext;
        public nint pSignatureSettings;
    }
}
```

注：`Unsigned_file_is_rejected` 只断言消息以"签名校验失败"开头——4 个字节的假文件在技术验证里返回的是 `0x800B0003`（无法识别的文件格式），落在"其他"分支。如果编译时 `#pragma warning disable SYSLIB0057` 本身报"无效的警告编号"，说明该 API 在当前 SDK 上没有过时，删掉这两行 pragma 即可。

- [ ] **Step 4: 运行，确认通过**

Run: `dotnet test --filter AuthenticodeVerifierTests`
Expected: 全部 PASS

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat(security): Authenticode 签名校验——先验签名有效再比签名者"
```

### Task 2：RuleUpdateSource + UpdateService 多来源

**Files:**
- Create: `src/UpdateHelper.Core/Updates/RuleUpdateSource.cs`
- Modify: `src/UpdateHelper.Core/Updates/UpdateService.cs`
- Test: `tests/UpdateHelper.Core.Tests/RuleUpdateSourceTests.cs`

**Interfaces:**
- Consumes: `IUpdateSource`、`UpdateCandidate`、`UpdateService.Check(IUpdateSource, …)`、`UpdateReport`（计划 3）；`ScanResult`、`SoftwareGroup`、`SoftwareGrouper`（计划 1）；`Rule`、`RuleSet`、`RuleMatch`、`UpdateRule`、`UpdateLatest`、`RiskLevel`、`RuleApplier`（计划 2）；`Fakes.Entry`
- Produces:
  - `const string RuleUpdateSource.PackagePrefix = "rule:"`
  - `sealed class RuleUpdateSource(ScanResult scan, RuleSet rules) : IUpdateSource`（`Name` 为 `"规则库"`）——对每个命中了规则（`RuleId` 非空）、规则写了 `update.latest`、且最新版本号与已装版本不同的软件组，产生候选：`PackageId = "rule:{规则 id}"`，`ProductCodes = [主条目 KeyName]`
  - `UpdateService.Check(IReadOnlyList<IUpdateSource> sources, ScanResult scan, RuleSet rules)`——按顺序问每个来源；某个来源出错只记警告不影响其他来源；后面来源的候选只要有一个 ProductCode 和前面已保留的候选相同就丢掉；多条警告用"；"连接
  - 原来的单来源重载保留，改为调用多来源重载

候选里新版本并不比已装版本新的情况（比如规则还没更新），交给 `UpdateJudge` 的第 0 条过滤，这里不重复判断。

- [ ] **Step 1: 写失败的测试** `tests/UpdateHelper.Core.Tests/RuleUpdateSourceTests.cs`

```csharp
using UpdateHelper.Core.Grouping;
using UpdateHelper.Core.Rules;
using UpdateHelper.Core.Updates;
using static UpdateHelper.Core.Tests.Fakes;

namespace UpdateHelper.Core.Tests;

public class RuleUpdateSourceTests
{
    private static Rule WpsRule(string? latest = "12.1.0.24000") => new(
        "kingsoft.wps", "WPS Office", new RuleMatch("WPS Office*", null), null,
        latest is null ? null : new UpdateRule(null, new UpdateLatest(latest, "https://example.com/wps.exe"),
            "Zhuhai Kingsoft Office Software Co., Ltd", "/S", RiskLevel.Normal),
        [], []);

    private static (ScanResult Scan, RuleSet Rules) Setup(Rule rule)
    {
        var scan = SoftwareGrouper.Group(
            [Entry("WPS Office (12.1.0.23125)", "Kingsoft Corp.", "12.1.0.23125", key: "Kingsoft Office")], []);
        var rules = new RuleSet([rule], [], []);
        RuleApplier.Apply(scan, rules);
        return (scan, rules);
    }

    [Fact]
    public void Rule_with_latest_produces_candidate()
    {
        var (scan, rules) = Setup(WpsRule());
        var c = Assert.Single(new RuleUpdateSource(scan, rules).GetAvailableUpdates());

        Assert.Equal("rule:kingsoft.wps", c.PackageId);
        Assert.Equal("WPS Office (12.1.0.23125)", c.Name);
        Assert.Equal("12.1.0.23125", c.InstalledVersion);
        Assert.Equal("12.1.0.24000", c.AvailableVersion);
        Assert.Equal(new[] { "Kingsoft Office" }, c.ProductCodes);
    }

    [Fact]
    public void Rule_without_update_section_produces_nothing()
    {
        var (scan, rules) = Setup(WpsRule(latest: null));
        Assert.Empty(new RuleUpdateSource(scan, rules).GetAvailableUpdates());
    }

    [Fact]
    public void Same_version_produces_nothing()
    {
        var (scan, rules) = Setup(WpsRule("12.1.0.23125"));
        Assert.Empty(new RuleUpdateSource(scan, rules).GetAvailableUpdates());
    }

    private sealed class FixedSource(string name, params UpdateCandidate[] candidates) : IUpdateSource
    {
        public string Name => name;
        public IReadOnlyList<UpdateCandidate> GetAvailableUpdates() => candidates;
    }

    private sealed class BrokenSource(string name) : IUpdateSource
    {
        public string Name => name;
        public IReadOnlyList<UpdateCandidate> GetAvailableUpdates() => throw new InvalidOperationException("连不上");
    }

    private static UpdateCandidate C(string id, string code) => new(id, "QQ", null, "1.0", "1.1", [code]);

    [Fact]
    public void Earlier_source_wins_when_both_report_same_software()   // Review Focus 5
    {
        var scan = SoftwareGrouper.Group([Entry("QQ", "Tencent", "1.0", key: "QQ")], []);
        var report = UpdateService.Check(
            [new FixedSource("winget", C("Tencent.QQ.NT", "qq")), new FixedSource("规则库", C("rule:tencent.qq", "QQ"))],
            scan, RuleSet.Empty);

        Assert.Equal("Tencent.QQ.NT", Assert.Single(report.Updates).Candidate.PackageId);
    }

    [Fact]
    public void One_broken_source_does_not_hide_the_others()
    {
        var scan = SoftwareGrouper.Group([Entry("QQ", "Tencent", "1.0", key: "QQ")], []);
        var report = UpdateService.Check(
            [new BrokenSource("winget"), new FixedSource("规则库", C("rule:tencent.qq", "QQ"))], scan, RuleSet.Empty);

        Assert.Equal("rule:tencent.qq", Assert.Single(report.Updates).Candidate.PackageId);
        Assert.Equal("无法从 winget 获取更新信息：连不上", report.Warning);
    }

    [Fact]
    public void Warnings_from_several_sources_are_joined()
    {
        var scan = SoftwareGrouper.Group([], []);
        var report = UpdateService.Check([new BrokenSource("a"), new BrokenSource("b")], scan, RuleSet.Empty);
        Assert.Equal("无法从 a 获取更新信息：连不上；无法从 b 获取更新信息：连不上", report.Warning);
    }
}
```

- [ ] **Step 2: 运行，确认失败**

Run: `dotnet test --filter RuleUpdateSourceTests`
Expected: 编译失败，找不到 `RuleUpdateSource`

- [ ] **Step 3: 规则来源** `src/UpdateHelper.Core/Updates/RuleUpdateSource.cs`

```csharp
using UpdateHelper.Core.Grouping;
using UpdateHelper.Core.Rules;

namespace UpdateHelper.Core.Updates;

/// <summary>
/// 从 YAML 规则的 update.latest 产生更新候选（winget 里没有的软件靠它）。
/// scan 必须已经套用过 rules（RuleApplier.Apply），这样组上才有 RuleId。
/// </summary>
public sealed class RuleUpdateSource(ScanResult scan, RuleSet rules) : IUpdateSource
{
    public const string PackagePrefix = "rule:";

    public string Name => "规则库";

    public IReadOnlyList<UpdateCandidate> GetAvailableUpdates()
    {
        var byId = rules.Rules.ToDictionary(r => r.Id, StringComparer.Ordinal);
        var result = new List<UpdateCandidate>();
        foreach (var g in scan.Groups)
        {
            if (g.Primary is null || g.RuleId is null || g.Version is null) continue;
            if (!byId.TryGetValue(g.RuleId, out var rule) || rule.Update?.Latest is not { } latest) continue;
            if (string.Equals(latest.Version, g.Version, StringComparison.OrdinalIgnoreCase)) continue;

            result.Add(new UpdateCandidate(PackagePrefix + rule.Id, g.Name, g.Publisher, g.Version, latest.Version,
                [g.Primary.KeyName]));
        }
        return result;
    }
}
```

- [ ] **Step 4: 多来源** —— 把 `src/UpdateHelper.Core/Updates/UpdateService.cs` 里的 `UpdateService` 类整个替换为：

```csharp
/// <summary>向各来源要候选并分档。来源出任何问题都转成中文提示，不向外抛异常。</summary>
public static class UpdateService
{
    public static UpdateReport Check(IUpdateSource source, ScanResult scan, RuleSet rules)
        => Check([source], scan, rules);

    /// <summary>按顺序问每个来源（winget 放前面）；同一个软件只保留最先报告它的来源的候选。</summary>
    public static UpdateReport Check(IReadOnlyList<IUpdateSource> sources, ScanResult scan, RuleSet rules)
    {
        var candidates = new List<UpdateCandidate>();
        var seenCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var warnings = new List<string>();

        foreach (var source in sources)
        {
            IReadOnlyList<UpdateCandidate> found;
            try
            {
                found = source.GetAvailableUpdates();
            }
            catch (Exception ex)
            {
                warnings.Add($"无法从 {source.Name} 获取更新信息：{ex.Message}");
                continue;
            }

            foreach (var c in found)
            {
                if (c.ProductCodes.Any(seenCodes.Contains)) continue;   // 前面的来源已经报告过这个软件
                candidates.Add(c);
                seenCodes.UnionWith(c.ProductCodes);
            }
        }

        return new UpdateReport(UpdateJudge.Judge(candidates, scan.Groups, rules),
            warnings.Count == 0 ? null : string.Join("；", warnings));
    }
}
```

（`UpdateReport` 记录类型保持不变。）

- [ ] **Step 5: 运行，确认通过**

Run: `dotnet test`
Expected: 全部 PASS（计划 3 的 `Service_turns_source_failure_into_warning` 仍然通过）

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "feat(updates): 规则库作为第二个更新来源，多来源合并时 winget 优先"
```

### Task 3：HttpDownloader（下载）

**Files:**
- Create: `src/UpdateHelper.Core/Install/Downloader.cs`
- Test: `tests/UpdateHelper.Core.Tests/HttpDownloaderTests.cs`

**Interfaces:**
- Consumes: `TempDir`（计划 2 测试辅助）
- Produces:
  - `interface IDownloader { Task DownloadAsync(string url, string destinationPath, IProgress<double>? progress, CancellationToken cancellationToken); }`
  - `sealed class HttpDownloader(HttpClient? client = null) : IDownloader`——先写到 `{目标}.part`，完整下载后才改名为目标文件；**任何失败或取消都删掉 `.part`，不留半截文件**（Review Focus 3）；能拿到文件大小时报告 0～1 的进度

BITS 后台下载和限速属于计划 6（不打扰规则）；这里先用 HttpClient，接口不变，以后换实现即可。

- [ ] **Step 1: 写失败的测试** `tests/UpdateHelper.Core.Tests/HttpDownloaderTests.cs`

```csharp
using System.Net;
using System.Net.Sockets;
using UpdateHelper.Core.Install;

namespace UpdateHelper.Core.Tests;

/// <summary>在本机 localhost 起一个临时 HTTP 服务来测试下载，不访问外网。</summary>
public sealed class HttpDownloaderTests : IDisposable
{
    private readonly HttpListener _server = new();
    private readonly string _baseUrl;
    private readonly byte[] _payload = Enumerable.Range(0, 300_000).Select(i => (byte)i).ToArray();

    public HttpDownloaderTests()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();

        _baseUrl = $"http://localhost:{port}/";
        _server.Prefixes.Add(_baseUrl);   // localhost 前缀不需要管理员权限
        _server.Start();
        _ = Task.Run(Serve);
    }

    private async Task Serve()
    {
        while (_server.IsListening)
        {
            HttpListenerContext ctx;
            try { ctx = await _server.GetContextAsync(); }
            catch (Exception) { return; }   // 服务关闭

            if (ctx.Request.Url!.AbsolutePath == "/setup.exe")
            {
                ctx.Response.ContentLength64 = _payload.Length;
                await ctx.Response.OutputStream.WriteAsync(_payload);
            }
            else
            {
                ctx.Response.StatusCode = 404;
            }
            ctx.Response.Close();
        }
    }

    public void Dispose() => _server.Close();

    private sealed class SyncProgress(List<double> seen) : IProgress<double>
    {
        public void Report(double value) => seen.Add(value);
    }

    [Fact]
    public async Task Downloads_complete_file_and_reports_progress()
    {
        using var dir = new TempDir();
        var dest = Path.Combine(dir.Path, "downloads", "setup.exe");   // 目录不存在也能下载
        var seen = new List<double>();

        await new HttpDownloader().DownloadAsync(_baseUrl + "setup.exe", dest, new SyncProgress(seen), CancellationToken.None);

        Assert.Equal(_payload, File.ReadAllBytes(dest));
        Assert.Equal(1.0, seen[^1]);
        Assert.False(File.Exists(dest + ".part"));
    }

    [Fact]
    public async Task Http_error_leaves_no_files()   // Review Focus 3
    {
        using var dir = new TempDir();
        var dest = Path.Combine(dir.Path, "missing.exe");

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            new HttpDownloader().DownloadAsync(_baseUrl + "missing.exe", dest, null, CancellationToken.None));

        Assert.Empty(Directory.GetFiles(dir.Path));
    }

    [Fact]
    public async Task Cancelled_download_leaves_no_files()   // Review Focus 3
    {
        using var dir = new TempDir();
        var dest = Path.Combine(dir.Path, "setup.exe");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new HttpDownloader().DownloadAsync(_baseUrl + "setup.exe", dest, null, cts.Token));

        Assert.Empty(Directory.GetFiles(dir.Path));
    }

    [Fact]
    public async Task Existing_file_is_replaced()
    {
        using var dir = new TempDir();
        var dest = dir.Write("setup.exe", "旧内容");
        await new HttpDownloader().DownloadAsync(_baseUrl + "setup.exe", dest, null, CancellationToken.None);
        Assert.Equal(_payload, File.ReadAllBytes(dest));
    }
}
```

- [ ] **Step 2: 运行，确认失败**

Run: `dotnet test --filter HttpDownloaderTests`
Expected: 编译失败，找不到 `HttpDownloader`

- [ ] **Step 3: 实现** `src/UpdateHelper.Core/Install/Downloader.cs`

```csharp
namespace UpdateHelper.Core.Install;

public interface IDownloader
{
    /// <summary>把 url 下载到 destinationPath。失败或取消时抛异常，并且不留下任何文件。</summary>
    Task DownloadAsync(string url, string destinationPath, IProgress<double>? progress, CancellationToken cancellationToken);
}

/// <summary>用 HttpClient 下载：先写 .part，完整后再改名，避免留下半截文件被当成完整安装包。</summary>
public sealed class HttpDownloader(HttpClient? client = null) : IDownloader
{
    private static readonly HttpClient SharedClient = new() { Timeout = TimeSpan.FromMinutes(30) };
    private readonly HttpClient _client = client ?? SharedClient;

    public async Task DownloadAsync(string url, string destinationPath, IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(destinationPath))!);
        var partPath = destinationPath + ".part";
        try
        {
            using var response = await _client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength;

            await using (var input = await response.Content.ReadAsStreamAsync(cancellationToken))
            await using (var output = File.Create(partPath))
            {
                var buffer = new byte[81920];
                long done = 0;
                int read;
                while ((read = await input.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    done += read;
                    if (total is > 0) progress?.Report((double)done / total.Value);
                }
            }

            File.Move(partPath, destinationPath, overwrite: true);
            progress?.Report(1.0);
        }
        catch
        {
            TryDelete(partPath);
            throw;
        }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
```

- [ ] **Step 4: 运行，确认通过**

Run: `dotnet test --filter HttpDownloaderTests`
Expected: 全部 PASS

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat(install): HTTP 下载器，失败或取消不留半截文件"
```

### Task 4：ProcessRunner（运行安装包）

**Files:**
- Create: `src/UpdateHelper.Core/Install/ProcessRunner.cs`
- Test: `tests/UpdateHelper.Core.Tests/ProcessRunnerTests.cs`

**Interfaces:**
- Produces:
  - `interface IProcessRunner { Task<int> RunAsync(string fileName, string arguments, CancellationToken cancellationToken); }`——返回退出码
  - `sealed class ProcessRunner : IProcessRunner`——用 `UseShellExecute = true` 启动（安装包要求管理员权限时，系统会弹出"用户账户控制"确认框），窗口隐藏；**取消只在启动前生效，启动后一定等它自己结束**（Global Constraints）

- [ ] **Step 1: 写失败的测试** `tests/UpdateHelper.Core.Tests/ProcessRunnerTests.cs`

```csharp
using UpdateHelper.Core.Install;

namespace UpdateHelper.Core.Tests;

/// <summary>只运行 Windows 自带的 cmd.exe /c exit N，不安装任何东西。</summary>
public class ProcessRunnerTests
{
    private static readonly string Cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");

    [Theory]
    [InlineData(0)]
    [InlineData(1603)]   // MSI 常见的"安装出错"
    [InlineData(3010)]   // 需要重启
    public async Task Returns_exit_code(int code)
        => Assert.Equal(code, await new ProcessRunner().RunAsync(Cmd, $"/c exit {code}", CancellationToken.None));

    [Fact]
    public async Task Cancelled_before_start_does_not_start()
    {
        using var dir = new TempDir();
        var marker = Path.Combine(dir.Path, "ran.txt");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new ProcessRunner().RunAsync(Cmd, $"/c echo x > \"{marker}\"", cts.Token));

        Assert.False(File.Exists(marker));
    }

    [Fact]
    public async Task Cancellation_after_start_waits_for_the_process()
    {
        using var dir = new TempDir();
        var marker = Path.Combine(dir.Path, "done.txt");
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        // 大约 2 秒后写文件再退出；200 毫秒时取消也必须等它跑完
        var code = await new ProcessRunner().RunAsync(Cmd, $"/c ping -n 3 127.0.0.1 >nul & echo x > \"{marker}\"", cts.Token);

        Assert.Equal(0, code);
        Assert.True(File.Exists(marker));
    }
}
```

- [ ] **Step 2: 运行，确认失败**

Run: `dotnet test --filter ProcessRunnerTests`
Expected: 编译失败，找不到 `ProcessRunner`

- [ ] **Step 3: 实现** `src/UpdateHelper.Core/Install/ProcessRunner.cs`

```csharp
using System.Diagnostics;

namespace UpdateHelper.Core.Install;

public interface IProcessRunner
{
    /// <summary>运行程序并返回退出码。取消只在启动前生效。</summary>
    Task<int> RunAsync(string fileName, string arguments, CancellationToken cancellationToken);
}

/// <summary>
/// 运行安装包。UseShellExecute=true：安装包要求管理员权限时由系统弹出"用户账户控制"确认框。
/// 安装程序启动后不会因为取消而被结束——强行结束安装程序可能把软件装坏。
/// </summary>
public sealed class ProcessRunner : IProcessRunner
{
    public async Task<int> RunAsync(string fileName, string arguments, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        using var process = Process.Start(new ProcessStartInfo(fileName, arguments)
        {
            UseShellExecute = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        }) ?? throw new InvalidOperationException($"无法启动 {Path.GetFileName(fileName)}");

        await process.WaitForExitAsync(CancellationToken.None);
        return process.ExitCode;
    }
}
```

注：用户在"用户账户控制"框里点"否"时，`Process.Start` 会抛 `Win32Exception`（错误码 1223）。`RuleInstaller`（Task 5）负责把它转成"没有获得管理员授权"。

- [ ] **Step 4: 运行，确认通过**

Run: `dotnet test --filter ProcessRunnerTests`
Expected: 全部 PASS

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat(install): 运行安装包，启动后不因取消而中途结束"
```

### Task 5：RuleInstaller（下载 → 校验 → 运行）

**Files:**
- Create: `src/UpdateHelper.Core/Install/RuleInstaller.cs`
- Test: `tests/UpdateHelper.Core.Tests/RuleInstallerTests.cs`

**Interfaces:**
- Consumes: `IPackageInstaller`、`InstallerReport`、`InstallScopeHint`（计划 4）；`IDownloader`（Task 3）；`IProcessRunner`（Task 4）；`ISignatureVerifier`、`SignatureCheck`、`AuthenticodeVerifier`（Task 1）；`RuleUpdateSource.PackagePrefix`（Task 2）；`Rule`、`RuleSet`、`UpdateRule`、`UpdateLatest`（计划 2）；`TempDir`
- Produces: `sealed class RuleInstaller(RuleSet rules, IDownloader downloader, ISignatureVerifier verifier, IProcessRunner runner, string downloadDirectory) : IPackageInstaller`（`Name` 为 `"规则库"`），`static string DefaultDownloadDirectory`（`%LOCALAPPDATA%\UpdateHelper\downloads`）

**流程（先命中先返回；除取消外都以 `InstallerReport` 返回，不抛异常）：**

| # | 情况 | 结果 |
|---|---|---|
| 1 | 包 id 不以 `rule:` 开头 / 找不到规则 / 规则没写 `update.latest` | 失败，说明原因 |
| 2 | 规则里的最新版本 ≠ 目标版本 | 失败："可用版本已经变了（规则里现在是 X，检查时是 Y），请重新检查更新"（不下载） |
| 3 | 规则没写 `signer` 或 `silentArgs` | 失败："规则没有写签名者，不能安装" / "规则没有写静默安装参数，不能自动安装"（不下载） |
| 4 | 下载被取消 | 抛 `OperationCanceledException`（由 `UpdateExecutor` 记为"已取消"） |
| 5 | 下载出错 | 失败："下载失败：{原因}" |
| 6 | 签名校验不通过 | **删除下载的文件**，失败："安装包没有通过签名校验：{原因}。文件已删除，没有运行" |
| 7 | 用户在"用户账户控制"框里点了"否"（启动时 `Win32Exception` 1223，或退出码 1223 / 1602） | 失败："没有获得管理员授权（在确认框里点了“否”）" |
| 8 | 退出码 0 | 成功 |
| 9 | 退出码 3010 / 1641 | 成功，需要重启 |
| 10 | 其他退出码 | 失败："安装程序返回错误（退出码 N）"，`InstallerErrorCode = N` |

- 下载文件名：`{规则 id}-{版本}.exe`；下载地址的路径以 `.msi` 结尾时用 `.msi`，并通过 `msiexec.exe /i "{文件}" {静默参数}` 运行
- 版本号里不能用作文件名的字符替换为 `_`
- 安装成功后保留下载的安装包（spec 第 6 节：留作以后回退用；缓存上限和清理放到版本回退一起做）
- 进度：下载占 0～0.5，运行结束报告 1.0
- `scope` 参数在这里不起作用：安装范围由安装包本身和规则里的静默参数决定

- [ ] **Step 1: 写失败的测试** `tests/UpdateHelper.Core.Tests/RuleInstallerTests.cs`

```csharp
using System.ComponentModel;
using UpdateHelper.Core.Install;
using UpdateHelper.Core.Rules;
using UpdateHelper.Core.Security;

namespace UpdateHelper.Core.Tests;

public sealed class RuleInstallerTests : IDisposable
{
    private readonly TempDir _dir = new();
    public void Dispose() => _dir.Dispose();

    private static readonly string SignedFile =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet", "dotnet.exe");

    // —— 假实现 ——
    private sealed class FakeDownloader(Func<string, byte[]>? content = null, Exception? error = null) : IDownloader
    {
        public List<string> Urls { get; } = [];
        public Task DownloadAsync(string url, string destinationPath, IProgress<double>? progress, CancellationToken ct)
        {
            Urls.Add(url);
            ct.ThrowIfCancellationRequested();
            if (error is not null) throw error;
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            File.WriteAllBytes(destinationPath, (content ?? (_ => [1, 2, 3]))(url));
            return Task.CompletedTask;
        }
    }

    private sealed class FakeVerifier(bool trusted) : ISignatureVerifier
    {
        public List<(string Path, string Signer)> Calls { get; } = [];
        public SignatureCheck Verify(string filePath, string expectedSigner)
        {
            Calls.Add((filePath, expectedSigner));
            return trusted ? new SignatureCheck(true, expectedSigner, "签名有效") : new SignatureCheck(false, "Evil Corp", "签名者不对");
        }
    }

    private sealed class FakeRunner(Func<int>? behave = null) : IProcessRunner
    {
        public List<(string File, string Args)> Calls { get; } = [];
        public Task<int> RunAsync(string fileName, string arguments, CancellationToken ct)
        {
            Calls.Add((fileName, arguments));
            return Task.FromResult((behave ?? (() => 0))());
        }
    }

    private static RuleSet Rules(string version = "12.1.0.24000", string url = "https://example.com/wps_setup.exe",
        string? signer = "Zhuhai Kingsoft Office Software Co., Ltd", string? silentArgs = "/S")
        => new([new Rule("kingsoft.wps", "WPS Office", new RuleMatch("WPS Office*", null), null,
            new UpdateRule(null, new UpdateLatest(version, url), signer, silentArgs, RiskLevel.Normal), [], [])], [], []);

    private RuleInstaller Make(RuleSet rules, FakeDownloader downloader, ISignatureVerifier verifier, FakeRunner runner)
        => new(rules, downloader, verifier, runner, _dir.Path);

    private static Task<InstallerReport> Upgrade(RuleInstaller installer, string id = "rule:kingsoft.wps",
        string target = "12.1.0.24000", CancellationToken ct = default)
        => installer.UpgradeAsync(id, target, InstallScopeHint.Machine, null, ct);

    private string Expected(string ext = ".exe") => Path.Combine(_dir.Path, "kingsoft.wps-12.1.0.24000" + ext);

    [Fact]
    public async Task Happy_path_downloads_verifies_and_runs_silently()
    {
        var downloader = new FakeDownloader();
        var verifier = new FakeVerifier(true);
        var runner = new FakeRunner();

        var report = await Upgrade(Make(Rules(), downloader, verifier, runner));

        Assert.Equal(new InstallerReport(true, false, null, null), report);
        Assert.Equal(new[] { "https://example.com/wps_setup.exe" }, downloader.Urls);
        Assert.Equal((Expected(), "Zhuhai Kingsoft Office Software Co., Ltd"), Assert.Single(verifier.Calls));
        Assert.Equal((Expected(), "/S"), Assert.Single(runner.Calls));
        Assert.True(File.Exists(Expected()));   // 成功后保留，留作以后回退
    }

    [Fact]
    public async Task Msi_is_run_through_msiexec()
    {
        var runner = new FakeRunner();
        await Upgrade(Make(Rules(url: "https://example.com/setup.msi", silentArgs: "/qn /norestart"),
            new FakeDownloader(), new FakeVerifier(true), runner));

        var call = Assert.Single(runner.Calls);
        Assert.Equal("msiexec.exe", call.File);
        Assert.Equal($"/i \"{Expected(".msi")}\" /qn /norestart", call.Args);
    }

    [Fact]
    public async Task Failed_signature_deletes_file_and_never_runs()   // Review Focus 2
    {
        var runner = new FakeRunner();
        var report = await Upgrade(Make(Rules(), new FakeDownloader(), new FakeVerifier(false), runner));

        Assert.False(report.Success);
        Assert.Equal("安装包没有通过签名校验：签名者不对。文件已删除，没有运行", report.ErrorMessage);
        Assert.Empty(runner.Calls);
        Assert.False(File.Exists(Expected()));
    }

    [Fact]
    public async Task Tampered_installer_is_caught_by_the_real_verifier()   // Review Focus 1（真实签名校验）
    {
        var tampered = File.ReadAllBytes(SignedFile);
        tampered[tampered.Length / 2] ^= 0xFF;
        var runner = new FakeRunner();

        var report = await Upgrade(Make(Rules(signer: "Microsoft Corporation"),
            new FakeDownloader(_ => tampered), new AuthenticodeVerifier(), runner));

        Assert.False(report.Success);
        Assert.Contains("文件可能被篡改过", report.ErrorMessage);
        Assert.Empty(runner.Calls);
        Assert.False(File.Exists(Expected()));
    }

    [Fact]
    public async Task Genuine_installer_passes_the_real_verifier()
    {
        var runner = new FakeRunner();
        var report = await Upgrade(Make(Rules(signer: "Microsoft Corporation"),
            new FakeDownloader(_ => File.ReadAllBytes(SignedFile)), new AuthenticodeVerifier(), runner));

        Assert.True(report.Success);
        Assert.Single(runner.Calls);
    }

    [Fact]
    public async Task Version_mismatch_is_refused_before_downloading()
    {
        var downloader = new FakeDownloader();
        var report = await Upgrade(Make(Rules(version: "12.1.0.25000"), downloader, new FakeVerifier(true), new FakeRunner()));

        Assert.False(report.Success);
        Assert.Equal("可用版本已经变了（规则里现在是 12.1.0.25000，检查时是 12.1.0.24000），请重新检查更新", report.ErrorMessage);
        Assert.Empty(downloader.Urls);
    }

    [Theory]
    [InlineData(null, "/S", "规则没有写签名者，不能安装")]
    [InlineData("Kingsoft", null, "规则没有写静默安装参数，不能自动安装")]
    public async Task Incomplete_rule_is_refused_before_downloading(string? signer, string? silentArgs, string message)
    {
        var downloader = new FakeDownloader();
        var report = await Upgrade(Make(Rules(signer: signer, silentArgs: silentArgs), downloader, new FakeVerifier(true), new FakeRunner()));

        Assert.Equal(message, report.ErrorMessage);
        Assert.Empty(downloader.Urls);
    }

    [Theory]
    [InlineData("Tencent.QQ.NT", "不是规则库的包：Tencent.QQ.NT")]
    [InlineData("rule:no.such", "找不到规则：no.such")]
    public async Task Unknown_package_is_refused(string id, string message)
        => Assert.Equal(message, (await Upgrade(Make(Rules(), new FakeDownloader(), new FakeVerifier(true), new FakeRunner()), id)).ErrorMessage);

    [Fact]
    public async Task Download_error_is_a_failure_and_nothing_runs()
    {
        var runner = new FakeRunner();
        var report = await Upgrade(Make(Rules(), new FakeDownloader(error: new HttpRequestException("404 Not Found")),
            new FakeVerifier(true), runner));

        Assert.Equal("下载失败：404 Not Found", report.ErrorMessage);
        Assert.Empty(runner.Calls);
    }

    [Fact]
    public async Task Cancelled_download_propagates()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Upgrade(Make(Rules(), new FakeDownloader(), new FakeVerifier(true), new FakeRunner()), ct: cts.Token));
    }

    [Theory]
    [InlineData(3010, true, true, null)]
    [InlineData(1641, true, true, null)]
    [InlineData(1602, false, false, "没有获得管理员授权（在确认框里点了“否”）")]
    [InlineData(1223, false, false, "没有获得管理员授权（在确认框里点了“否”）")]   // Review Focus 4
    [InlineData(1603, false, false, "安装程序返回错误（退出码 1603）")]
    public async Task Exit_codes_are_interpreted(int code, bool success, bool reboot, string? message)
    {
        var report = await Upgrade(Make(Rules(), new FakeDownloader(), new FakeVerifier(true), new FakeRunner(() => code)));
        Assert.Equal(success, report.Success);
        Assert.Equal(reboot, report.RebootRequired);
        Assert.Equal(message, report.ErrorMessage);
    }

    [Fact]
    public async Task Declined_elevation_at_start_is_reported()   // Review Focus 4
    {
        var report = await Upgrade(Make(Rules(), new FakeDownloader(), new FakeVerifier(true),
            new FakeRunner(() => throw new Win32Exception(1223))));
        Assert.Equal("没有获得管理员授权（在确认框里点了“否”）", report.ErrorMessage);
    }
}
```

- [ ] **Step 2: 运行，确认失败**

Run: `dotnet test --filter RuleInstallerTests`
Expected: 编译失败，找不到 `RuleInstaller`

- [ ] **Step 3: 实现** `src/UpdateHelper.Core/Install/RuleInstaller.cs`

```csharp
using System.ComponentModel;
using UpdateHelper.Core.Rules;
using UpdateHelper.Core.Security;
using UpdateHelper.Core.Updates;

namespace UpdateHelper.Core.Install;

/// <summary>
/// 安装规则库（YAML 规则 update.latest）里的软件：下载 → 校验签名 → 静默运行安装包。
/// 签名校验不通过的文件当场删除、绝不运行。流程见计划 5 Task 5 的表格。
/// </summary>
public sealed class RuleInstaller(
    RuleSet rules,
    IDownloader downloader,
    ISignatureVerifier verifier,
    IProcessRunner runner,
    string downloadDirectory) : IPackageInstaller
{
    public static string DefaultDownloadDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UpdateHelper", "downloads");

    private const string NoElevation = "没有获得管理员授权（在确认框里点了“否”）";

    public string Name => "规则库";

    public async Task<InstallerReport> UpgradeAsync(string packageId, string targetVersion, InstallScopeHint scope,
        IProgress<double>? progress, CancellationToken cancellationToken)
    {
        // 1～3：规则检查，全部在下载之前
        if (!packageId.StartsWith(RuleUpdateSource.PackagePrefix, StringComparison.Ordinal))
            return Fail($"不是规则库的包：{packageId}");
        var ruleId = packageId[RuleUpdateSource.PackagePrefix.Length..];
        var rule = rules.Rules.FirstOrDefault(r => r.Id == ruleId);
        if (rule is null) return Fail($"找不到规则：{ruleId}");
        if (rule.Update?.Latest is not { } latest) return Fail("规则没有写最新版本");
        if (!string.Equals(latest.Version, targetVersion, StringComparison.OrdinalIgnoreCase))
            return Fail($"可用版本已经变了（规则里现在是 {latest.Version}，检查时是 {targetVersion}），请重新检查更新");
        if (string.IsNullOrWhiteSpace(rule.Update.Signer)) return Fail("规则没有写签名者，不能安装");
        if (string.IsNullOrWhiteSpace(rule.Update.SilentArgs)) return Fail("规则没有写静默安装参数，不能自动安装");

        // 4～5：下载（取消向外抛，交给 UpdateExecutor 记为"已取消"）
        var isMsi = new Uri(latest.Url).AbsolutePath.EndsWith(".msi", StringComparison.OrdinalIgnoreCase);
        var file = Path.Combine(downloadDirectory, $"{ruleId}-{SafeName(latest.Version)}{(isMsi ? ".msi" : ".exe")}");
        var downloadProgress = progress is null ? null : new ScaledProgress(progress, 0.5);
        try
        {
            await downloader.DownloadAsync(latest.Url, file, downloadProgress, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Fail($"下载失败：{ex.Message}");
        }

        // 6：签名校验不通过 → 删除，绝不运行
        var check = verifier.Verify(file, rule.Update.Signer);
        if (!check.Trusted)
        {
            TryDelete(file);
            return Fail($"安装包没有通过签名校验：{check.Message}。文件已删除，没有运行");
        }

        cancellationToken.ThrowIfCancellationRequested();   // 运行前最后一次响应取消

        // 7～10：运行安装包（启动后不再响应取消）
        int exitCode;
        try
        {
            exitCode = isMsi
                ? await runner.RunAsync("msiexec.exe", $"/i \"{file}\" {rule.Update.SilentArgs}", CancellationToken.None)
                : await runner.RunAsync(file, rule.Update.SilentArgs, CancellationToken.None);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return Fail(NoElevation);
        }
        progress?.Report(1.0);

        return exitCode switch
        {
            0 => new InstallerReport(true, false, null, null),
            3010 or 1641 => new InstallerReport(true, true, null, null),
            1223 or 1602 => Fail(NoElevation),
            _ => new InstallerReport(false, false, $"安装程序返回错误（退出码 {exitCode}）", (uint)exitCode),
        };
    }

    private static InstallerReport Fail(string message) => new(false, false, message, null);

    private static string SafeName(string version) =>
        string.Concat(version.Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch));

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    /// <summary>把下载进度（0～1）压缩到总进度的前一部分。</summary>
    private sealed class ScaledProgress(IProgress<double> inner, double share) : IProgress<double>
    {
        public void Report(double value) => inner.Report(value * share);
    }
}
```

- [ ] **Step 4: 运行，确认通过**

Run: `dotnet test --filter RuleInstallerTests`
Expected: 全部 PASS

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat(install): 规则库安装器——下载、签名校验、静默运行，签名不过就删除"
```

### Task 6：InstallerRouter + ScanCli 接入，本机端到端验证"签名不对就不运行"

**Files:**
- Create: `src/UpdateHelper.Core/Install/InstallerRouter.cs`
- Test: `tests/UpdateHelper.Core.Tests/InstallerRouterTests.cs`
- Modify: `src/UpdateHelper.ScanCli/Program.cs`

**Interfaces:**
- Consumes: `IPackageInstaller`（计划 4）；`RuleUpdateSource`（Task 2）；`RuleInstaller`（Task 5）；`HttpDownloader`、`ProcessRunner`、`AuthenticodeVerifier`（Task 1、3、4）；`WingetUpdateSource`、`WingetInstaller`（计划 3、4）；`UpdateService.Check(IReadOnlyList<IUpdateSource>, …)`（Task 2）
- Produces: `sealed class InstallerRouter(IPackageInstaller winget, IPackageInstaller rules) : IPackageInstaller`——包 id 以 `rule:` 开头交给规则安装器，否则交给 winget

- [ ] **Step 1: 写失败的测试** `tests/UpdateHelper.Core.Tests/InstallerRouterTests.cs`

```csharp
using UpdateHelper.Core.Install;

namespace UpdateHelper.Core.Tests;

public class InstallerRouterTests
{
    private sealed class Recorder(string name) : IPackageInstaller
    {
        public List<string> Ids { get; } = [];
        public string Name => name;
        public Task<InstallerReport> UpgradeAsync(string packageId, string targetVersion, InstallScopeHint scope,
            IProgress<double>? progress, CancellationToken cancellationToken)
        {
            Ids.Add(packageId);
            return Task.FromResult(new InstallerReport(true, false, null, null));
        }
    }

    [Fact]
    public async Task Routes_by_prefix()
    {
        var winget = new Recorder("winget");
        var rules = new Recorder("规则库");
        var router = new InstallerRouter(winget, rules);

        await router.UpgradeAsync("Tencent.QQ.NT", "1", InstallScopeHint.Machine, null, CancellationToken.None);
        await router.UpgradeAsync("rule:kingsoft.wps", "1", InstallScopeHint.Machine, null, CancellationToken.None);

        Assert.Equal(new[] { "Tencent.QQ.NT" }, winget.Ids);
        Assert.Equal(new[] { "rule:kingsoft.wps" }, rules.Ids);
    }
}
```

Run: `dotnet test --filter InstallerRouterTests`
Expected: 编译失败，找不到 `InstallerRouter`

- [ ] **Step 2: 实现** `src/UpdateHelper.Core/Install/InstallerRouter.cs`

```csharp
using UpdateHelper.Core.Updates;

namespace UpdateHelper.Core.Install;

/// <summary>按包 id 分流：rule: 开头的交给规则库安装器，其余交给 winget。</summary>
public sealed class InstallerRouter(IPackageInstaller winget, IPackageInstaller rules) : IPackageInstaller
{
    public string Name => $"{winget.Name} + {rules.Name}";

    public Task<InstallerReport> UpgradeAsync(string packageId, string targetVersion, InstallScopeHint scope,
        IProgress<double>? progress, CancellationToken cancellationToken)
        => (packageId.StartsWith(RuleUpdateSource.PackagePrefix, StringComparison.Ordinal) ? rules : winget)
            .UpgradeAsync(packageId, targetVersion, scope, progress, cancellationToken);
}
```

Run: `dotnet test --filter InstallerRouterTests`
Expected: PASS

- [ ] **Step 3: ScanCli 同时使用两个来源和分流安装器**——在 `src/UpdateHelper.ScanCli/Program.cs` 中：

把 `--updates` 块里的

```csharp
    var updates = UpdateService.Check(new WingetUpdateSource(), r, rules);
```

改成

```csharp
    var updates = UpdateService.Check(UpdateSources(), r, rules);
```

把 `--install` 块里的

```csharp
    var check = UpdateService.Check(new WingetUpdateSource(), r, rules);
```

改成

```csharp
    var check = UpdateService.Check(UpdateSources(), r, rules);
```

把 `--install` 块里的

```csharp
    var executor = new UpdateExecutor(new WingetInstaller(), new RegistryVersionProbe(), new RunningProcessProbe(),
        new JsonLinesUpdateHistory(JsonLinesUpdateHistory.DefaultPath));
```

改成

```csharp
    var installer = new InstallerRouter(new WingetInstaller(),
        new RuleInstaller(rules, new HttpDownloader(), new AuthenticodeVerifier(), new ProcessRunner(),
            RuleInstaller.DefaultDownloadDirectory));
    var executor = new UpdateExecutor(installer, new RegistryVersionProbe(), new RunningProcessProbe(),
        new JsonLinesUpdateHistory(JsonLinesUpdateHistory.DefaultPath));
```

在 `--install` 块的 `if (installId is not null)` 之前加一个本地函数（winget 放前面，同一个软件以 winget 为准）：

```csharp
IReadOnlyList<IUpdateSource> UpdateSources() => [new WingetUpdateSource(), new RuleUpdateSource(r, rules)];
```

using 区加：

```csharp
using UpdateHelper.Core.Security;
```

Run: `dotnet build`
Expected: 0 警告 0 错误

Run: `dotnet run --project src/UpdateHelper.ScanCli -- --updates`
Expected: 和改动前完全一样（官方规则都没写 `update.latest`，规则库来源不产生候选）

- [ ] **Step 4: 本机端到端——签名不对就不运行**

做一个**临时**规则目录（不提交）：复制仓库的 `rules/`，再加一条故意写错签名公司的测试规则。它声称 EV 录屏（winget 里没有，本机装的是 5.2.3）有新版 9.9.9，下载地址却是微软的 VC++ 运行库安装包（约 25 MB，带微软签名）：

```bash
T=$(mktemp -d) && cp rules/*.yaml "$T"/ && cat > "$T/test.evscreen.yaml" <<'EOF'
id: test.evscreen
name: 签名校验演示（EV录屏）
match:
  displayName: "EV录屏"
update:
  latest:
    version: "9.9.9"
    url: https://aka.ms/vs/17/release/vc_redist.x64.exe
  installer:
    signer: "湖南一唯信息科技有限公司"
    silentArgs: "/S"
EOF
echo "$T"
```

Run: `dotnet run --project src/UpdateHelper.ScanCli -- --rules "$T" --updates`
Expected: 出现 `EV录屏  5.2.3 → 9.9.9  (rule:test.evscreen)`，档位【需确认】（大版本变化 5 → 9）

Run: `dotnet run --project src/UpdateHelper.ScanCli -- --rules "$T" --install rule:test.evscreen --yes`
Expected:
- 下载进度走到 50%
- `结果：Failed——安装包没有通过签名校验：签名者是“Microsoft Corporation”，不是规则要求的“湖南一唯信息科技有限公司”。文件已删除，没有运行`
- `%LOCALAPPDATA%\UpdateHelper\downloads\` 里**没有** `test.evscreen-9.9.9.exe`
- 本机没有弹出任何安装界面或"用户账户控制"确认框（安装包从未运行）

如果 EV 录屏正好开着，结果会是"正在运行，请先关闭"——这时关掉它再试一次。

最后删除临时规则目录：`rm -r "$T"`。更新历史里会留下这一条 Failed 记录，这是预期的（它确实是一次被拒绝的安装）。

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat: 安装分流到 winget 或规则库；ScanCli 同时查询两个来源"
```

---

## 完成标准

- `dotnet build` 0 警告 0 错误；`dotnet test` 全部通过
- 本机端到端：错误签名的安装包被下载、被拒绝、被删除，从未运行
- 源代码中运行安装包的调用只有 `RuleInstaller` 一处，且位于签名校验通过之后（`grep -n "runner.RunAsync" src -r --include=*.cs`）
- `--updates` 的结果与改动前一致
