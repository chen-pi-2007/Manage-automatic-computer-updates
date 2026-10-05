namespace UpdateHelper.Core.Tests;

public class SmokeTests
{
    [Fact]
    public void Core_assembly_loads()
    {
        Assert.Equal("UpdateHelper.Core", typeof(UpdateHelper.Core.CoreInfo).Assembly.GetName().Name);
    }
}
