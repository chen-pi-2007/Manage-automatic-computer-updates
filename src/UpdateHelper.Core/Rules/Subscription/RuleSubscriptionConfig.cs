namespace UpdateHelper.Core.Rules.Subscription;

/// <summary>规则订阅的生产常量：官方公钥、源地址、缓存目录。</summary>
public static class RuleSubscriptionConfig
{
    // 官方规则包签名公钥（私钥由维护者保管，绝不进仓库）。由 genkey 生成后粘贴。
    // 注意：下面仍是占位符，填入真实公钥前，真机同步会失败。
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
