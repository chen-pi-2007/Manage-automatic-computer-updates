namespace UpdateHelper.Presentation.Tests;

public sealed class IconSourceTests
{
    [Theory]
    [InlineData(@"C:\Apps\QQ\QQ.exe,0", @"C:\Apps\QQ\QQ.exe", 0)]
    [InlineData(@"""C:\Program Files\Tencent\WeChat.exe"",2", @"C:\Program Files\Tencent\WeChat.exe", 2)]
    [InlineData(@"""C:\Program Files\Tencent\WeChat.exe""", @"C:\Program Files\Tencent\WeChat.exe", 0)]
    [InlineData(@"C:\x\setup.dll,-101", @"C:\x\setup.dll", -101)]
    [InlineData(@"C:\x\app.ico", @"C:\x\app.ico", 0)]
    [InlineData(@"  C:\x\app.exe  ", @"C:\x\app.exe", 0)]
    public void Parses_display_icon(string raw, string path, int index)
    {
        var icon = IconSource.Parse(raw);
        Assert.NotNull(icon);
        Assert.Equal(path, icon.Path);
        Assert.Equal(index, icon.Index);
    }

    [Fact]
    public void Expands_environment_variables()
    {
        var icon = IconSource.Parse(@"%SystemRoot%\system32\shell32.dll,4");
        Assert.NotNull(icon);
        Assert.Equal(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "system32", "shell32.dll"), icon.Path, ignoreCase: true);
        Assert.Equal(4, icon.Index);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(@"relative\app.exe")]          // 相对路径不知道相对谁
    [InlineData(@"C:\x\readme.txt")]           // 不是能带图标的文件
    [InlineData(@"C:\x\app.exe /uninstall")]   // 带参数的命令行，不是图标
    public void Rejects_unusable_values(string? raw)
    {
        Assert.Null(IconSource.Parse(raw));
    }
}
