using UpdateHelper.Core.Rules;

namespace UpdateHelper.Core.Tests;

public class GlobPatternTests
{
    [Theory]
    [InlineData("QQ", "QQ", true)]
    [InlineData("QQ", "qq", true)]                                   // 忽略大小写
    [InlineData("QQ", "QQ音乐", false)]                              // 必须整串匹配
    [InlineData("WPS Office*", "WPS Office (12.1.0.23125)", true)]
    [InlineData("*", "", true)]
    [InlineData("a?c", "abc", true)]
    [InlineData("a?c", "ac", false)]
    [InlineData("Google*Updater*", "GoogleUpdaterService156.0.8067.0", true)]
    // Review Focus 3：正则特殊字符按字面处理
    [InlineData("Microsoft Visual C++*", "Microsoft Visual C++ 2013 Redistributable (x64)", true)]
    [InlineData("Node.js", "NodeXjs", false)]
    [InlineData("App (x64)", "App (x64)", true)]
    [InlineData("[abc]", "a", false)]
    [InlineData("腾讯科技*", "腾讯科技(深圳)有限公司", true)]
    public void Matches(string pattern, string text, bool expected)
        => Assert.Equal(expected, GlobPattern.IsMatch(pattern, text));

    [Fact]
    public void Null_text_never_matches()
        => Assert.False(GlobPattern.IsMatch("*", null));
}
