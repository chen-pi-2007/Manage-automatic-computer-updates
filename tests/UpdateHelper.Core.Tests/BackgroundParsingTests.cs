using UpdateHelper.Core.Scanning;

namespace UpdateHelper.Core.Tests;

public class BackgroundParsingTests
{
    private static Func<string, object?> Values(Dictionary<string, object?> d)
        => name => d.TryGetValue(name, out var v) ? v : null;

    [Fact]
    public void Service_win32_service_is_parsed_with_start_mode()
    {
        var item = ServiceSource.ToItem("ToDesk_Service", Values(new()
        {
            ["Type"] = 16,
            ["Start"] = 2,
            ["DisplayName"] = "ToDesk Service",
            ["ImagePath"] = "\"C:\\Program Files\\ToDesk\\ToDesk_Service.exe\"",
        }));

        Assert.NotNull(item);
        Assert.Equal(BackgroundKind.Service, item!.Kind);
        Assert.Equal("ToDesk Service", item.DisplayName);
        Assert.Equal(@"C:\Program Files\ToDesk\ToDesk_Service.exe", item.ExecutablePath);
        Assert.Equal("自动启动", item.Detail);
    }

    [Theory]
    [InlineData(1)]   // 内核驱动
    [InlineData(2)]   // 文件系统驱动
    public void Service_drivers_are_skipped(int type)
        => Assert.Null(ServiceSource.ToItem("nvlddmkm", Values(new() { ["Type"] = type, ["ImagePath"] = "x.sys" })));

    [Fact]
    public void Service_without_image_path_is_skipped()
        => Assert.Null(ServiceSource.ToItem("ghost", Values(new() { ["Type"] = 16 })));

    [Theory]
    [InlineData(3, "手动启动")]
    [InlineData(4, "已禁用")]
    [InlineData(99, null)]
    public void Service_start_modes(int start, string? expected)
        => Assert.Equal(expected, ServiceSource.ToItem("s", Values(new()
            { ["Type"] = 32, ["Start"] = start, ["ImagePath"] = @"C:\a.exe" }))!.Detail);

    [Fact]
    public void RunKey_value_is_parsed()
    {
        var item = RunKeySource.ToItem("QQNT", "\"C:\\Program Files\\Tencent\\QQNT\\QQ.exe\" /background", @"HKCU\...\Run");
        Assert.Equal(BackgroundKind.RunKey, item!.Kind);
        Assert.Equal(@"C:\Program Files\Tencent\QQNT\QQ.exe", item.ExecutablePath);
        Assert.Equal(@"HKCU\...\Run", item.Detail);
    }

    [Fact]
    public void RunKey_non_string_value_is_skipped()   // Review Focus 1
        => Assert.Null(RunKeySource.ToItem("weird", new byte[] { 1 }, "HKLM"));

    private const string TaskXml = """
        <?xml version="1.0" encoding="UTF-16"?>
        <Task xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
          <Actions Context="Author">
            <Exec>
              <Command>"C:\Users\me\AppData\Local\Kingsoft\WPS Office\ksolaunch.exe"</Command>
              <Arguments>/update</Arguments>
            </Exec>
          </Actions>
        </Task>
        """;

    [Fact]
    public void Task_exec_action_is_parsed()
    {
        var item = ScheduledTaskSource.ParseTaskXml(@"\WpsUpdateTask_chen_pi", TaskXml);
        Assert.Equal(BackgroundKind.ScheduledTask, item!.Kind);
        Assert.Equal("WpsUpdateTask_chen_pi", item.Name);
        Assert.Equal(@"C:\Users\me\AppData\Local\Kingsoft\WPS Office\ksolaunch.exe", item.ExecutablePath);
        Assert.Contains("/update", item.Command);
    }

    [Fact]
    public void Task_without_exec_action_is_skipped()   // Review Focus 5（比如只有 COM 动作）
    {
        const string xml = """
            <Task xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <Actions><ComHandler><ClassId>{0000}</ClassId></ComHandler></Actions>
            </Task>
            """;
        Assert.Null(ScheduledTaskSource.ParseTaskXml(@"\x", xml));
    }

    [Fact]
    public void Task_with_broken_xml_is_skipped()   // Review Focus 5
        => Assert.Null(ScheduledTaskSource.ParseTaskXml(@"\x", "<Task><Actions>"));
}
