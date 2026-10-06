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
