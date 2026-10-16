namespace UpdateHelper.Presentation.Tests;

public sealed class AutoStartTests
{
    [Fact]
    public void Enabled_registration_quotes_path_and_adds_flags()
    {
        var cmd = AutoStartPolicy.DesiredCommand(true, @"C:\Program Files\UpdateHelper\UpdateHelper.exe");
        Assert.Equal("\"C:\\Program Files\\UpdateHelper\\UpdateHelper.exe\" --minimized --login", cmd);
    }

    [Fact]
    public void Disabled_means_remove()
    {
        Assert.Null(AutoStartPolicy.DesiredCommand(false, @"C:\x\UpdateHelper.exe"));
    }

    [Fact]
    public void Enabled_registration_points_to_current_exe()
    {
        // 程序被挪到别的文件夹后，开关仍是开的：登记用的是传入的当前路径，不是旧路径
        var moved = AutoStartPolicy.DesiredCommand(true, @"D:\NewFolder\UpdateHelper.exe");
        Assert.Contains(@"D:\NewFolder\UpdateHelper.exe", moved);
    }
}
