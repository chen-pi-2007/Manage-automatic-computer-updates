using UpdateHelper.Core.Install;

namespace UpdateHelper.Core.Tests;

/// <summary>
/// 审查 #1：规则里的 MSI 静默参数不能让 msiexec 加载没经过签名校验的文件（TRANSFORMS、PATCH 等），
/// 也不能换成别的动作（卸载、打补丁、写日志）。只允许白名单里的开关和普通属性。
/// </summary>
public class MsiArgumentsTests
{
    [Theory]
    [InlineData("/qn")]
    [InlineData("/qn /norestart")]
    [InlineData("/quiet /norestart")]
    [InlineData("/passive")]
    [InlineData("/qb- /norestart")]
    [InlineData("/qn ALLUSERS=1")]
    [InlineData("/qn INSTALLDIR=\"C:\\Program Files\\My App\" ADDLOCAL=ALL")]
    public void Safe_arguments_are_allowed(string args)
        => Assert.Null(MsiArguments.FindUnsafe(args));

    [Theory]
    [InlineData("/qn TRANSFORMS=evil.mst", "TRANSFORMS=evil.mst")]
    [InlineData("/qn transforms=evil.mst", "transforms=evil.mst")]                    // 不区分大小写
    [InlineData("/qn TRANSFORMS=\\\\host@SSL\\x\\evil.mst", "TRANSFORMS=\\\\host@SSL\\x\\evil.mst")]
    [InlineData("/qn PATCH=c:\\x.msp", "PATCH=c:\\x.msp")]
    [InlineData("/qn MSIPATCHREMOVE=x", "MSIPATCHREMOVE=x")]
    [InlineData("/p evil.msp", "/p")]                                                // 打补丁
    [InlineData("/qn /x {GUID}", "/x")]                                              // 卸载
    [InlineData("/qn /update x.msp", "/update")]
    [InlineData("/qn /l*v c:\\log.txt", "/l*v")]                                     // 写日志（可写任意路径）
    [InlineData("/qn /i other.msi", "/i")]                                           // 换成安装别的包
    [InlineData("/qn bareword", "bareword")]
    [InlineData("/qn lower=1", "lower=1")]                                           // 私有属性（小写开头）
    public void Unsafe_arguments_are_reported(string args, string offending)
        => Assert.Equal(offending, MsiArguments.FindUnsafe(args));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Empty_arguments_are_reported(string args)
        => Assert.Equal("（空）", MsiArguments.FindUnsafe(args));
}
