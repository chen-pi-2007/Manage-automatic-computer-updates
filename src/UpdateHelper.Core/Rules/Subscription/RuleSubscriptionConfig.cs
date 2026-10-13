namespace UpdateHelper.Core.Rules.Subscription;

/// <summary>规则订阅的生产常量：官方公钥、源地址、缓存目录。</summary>
public static class RuleSubscriptionConfig
{
    // 官方规则包签名公钥（私钥由维护者保管，绝不进仓库）。由 RuleSign genkey 生成。
    public const string PublicKeyPem =
        "-----BEGIN PUBLIC KEY-----\n" +
        "MIIBojANBgkqhkiG9w0BAQEFAAOCAY8AMIIBigKCAYEA16N9SYFHRJxZSHIwpkzS\n" +
        "yK609bvwjTs9mZa5w0wSajte5pm2UhTlFu1nWeO/k0xp7d+qUKrWUbUUVNplzl9J\n" +
        "IiT6rJqAlFfN7m1Zvd9fusyrmp3FlEjgiGxUl5H4i9SpNUfEWSMwVinn7tJYu2AG\n" +
        "X6BROiak/SBwALhUVFmsEiEFooqSyHwS7V+nLkTpUdrT8igGu8Nhd8ldf1F6vZS/\n" +
        "/FEyVjfKYdp0+Il1YnNxX6WaIPMgQesG9ldYKNEF6XJnyqeXjkoo5bDiXRIrfsA3\n" +
        "h/+Gw31Ns772GwsVQWaRQQqvGJJBM+4+yWLS0EAygWNInndYqlI4HGlbJ0UIt8fq\n" +
        "voqmhfaMxmswrGaTvxs97BRtiXQpcmHF+urZo8Y1RWN7Zm2UF1DQtPwwMZSs2AkZ\n" +
        "SNmFyw1zt1PPhQuUA4zNQ2VHTy+Bq/+rBKDbrma7T6IegKlYN7dL/7e2XyDEbE+X\n" +
        "AKdmD9ZZ/6TyYwIeCwYVbU3x+lzqyfAd7cmqnm19mnxJAgMBAAE=\n" +
        "-----END PUBLIC KEY-----\n";

    private const string ReleaseBase =
        "https://github.com/chen-pi-2007/Manage-automatic-computer-updates/releases/download/rules-latest/";
    public const string BundleUrl = ReleaseBase + "rules-bundle.zip";
    public const string SignatureUrl = ReleaseBase + "rules-bundle.zip.sig";

    public static string DefaultCacheDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "UpdateHelper", "rules-cache");
}
