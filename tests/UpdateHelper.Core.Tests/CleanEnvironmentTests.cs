using UpdateHelper.AgentLauncher;

namespace UpdateHelper.Core.Tests;

public sealed class CleanEnvironmentTests
{
    private static Dictionary<string, string> Env(params (string Name, string Value)[] items) =>
        items.ToDictionary(i => i.Name, i => i.Value, StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void Keeps_allowed_variables_with_canonical_names()
    {
        var result = CleanEnvironment.Build(
            Env(("systemroot", @"C:\Windows"), ("USERPROFILE", @"C:\Users\a"), ("TEMP", @"C:\Users\a\AppData\Local\Temp")),
            machinePath: @"C:\Windows\system32;C:\Windows", systemRoot: @"C:\Windows");

        Assert.Equal(@"C:\Windows", result["SystemRoot"]);
        Assert.Equal(@"C:\Users\a", result["USERPROFILE"]);
        Assert.Equal(@"C:\Users\a\AppData\Local\Temp", result["TEMP"]);
        Assert.Contains("SystemRoot", result.Keys);   // 标准写法，不是 systemroot
    }

    [Fact]
    public void Drops_everything_not_on_the_allow_list()
    {
        var result = CleanEnvironment.Build(
            Env(("SystemRoot", @"C:\Windows"), ("DOTNET_SOMETHING", "x"), ("COMPLUS_SOMETHING", "x"),
                ("CORECLR_SOMETHING", "x"), ("MY_TOOL_HOME", "x"), ("PSModulePath", "x")),
            machinePath: @"C:\Windows\system32", systemRoot: @"C:\Windows");

        Assert.All(result.Keys, k => Assert.Contains(k, CleanEnvironment.AllowedNames));
        Assert.DoesNotContain(result.Keys, k => k.StartsWith("DOTNET_", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(result.Keys, k => k.StartsWith("COMPLUS_", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(result.Keys, k => k.StartsWith("CORECLR_", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain("MY_TOOL_HOME", result.Keys);
    }

    [Fact]
    public void Path_comes_only_from_machine_value()
    {
        var result = CleanEnvironment.Build(
            Env(("SystemRoot", @"C:\Windows"), ("Path", @"C:\Users\a\evil;C:\Windows\system32")),
            machinePath: @"C:\Windows\system32;C:\Windows", systemRoot: @"C:\Windows");

        Assert.Equal(@"C:\Windows\system32;C:\Windows", result["Path"]);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Missing_machine_path_falls_back_to_system_directories(string? machinePath)
    {
        var result = CleanEnvironment.Build(Env(("SystemRoot", @"C:\Windows")), machinePath, systemRoot: @"C:\Windows");
        Assert.Equal(@"C:\Windows\system32;C:\Windows;C:\Windows\System32\Wbem", result["Path"]);
    }

    [Fact]
    public void SystemRoot_is_always_present()
    {
        var result = CleanEnvironment.Build(Env(), machinePath: null, systemRoot: @"C:\Windows");
        Assert.Equal(@"C:\Windows", result["SystemRoot"]);
    }

    [Fact]
    public void Allow_list_has_no_runtime_or_loader_variables()
    {
        foreach (var name in CleanEnvironment.AllowedNames)
        {
            Assert.False(name.StartsWith("DOTNET", StringComparison.OrdinalIgnoreCase), name);
            Assert.False(name.StartsWith("COMPLUS", StringComparison.OrdinalIgnoreCase), name);
            Assert.False(name.StartsWith("CORECLR", StringComparison.OrdinalIgnoreCase), name);
            Assert.False(name.StartsWith("COR_", StringComparison.OrdinalIgnoreCase), name);
        }
    }
}
