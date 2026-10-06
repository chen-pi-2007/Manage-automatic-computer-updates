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
