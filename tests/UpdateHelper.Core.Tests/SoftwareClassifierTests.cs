using UpdateHelper.Core.Grouping;
using static UpdateHelper.Core.Tests.Fakes;

namespace UpdateHelper.Core.Tests;

public class SoftwareClassifierTests
{
    [Theory]
    [InlineData("Microsoft Visual C++ 2013 Redistributable (x64) - 12.0.40664")]
    [InlineData("Microsoft Visual C++ v14 Redistributable (x86) - 14.50.35719")]
    [InlineData("Microsoft .NET Runtime - 8.0.21 (x64)")]
    [InlineData("Microsoft Windows Desktop Runtime - 8.0.14 (x64)")]
    [InlineData("Java 8 Update 331 (64-bit)")]
    [InlineData("NVIDIA PhysX 系统软件 9.23.1019")]
    public void Runtimes(string name) => Assert.Equal(SoftwareCategory.Runtime, SoftwareClassifier.Classify(Entry(name)));

    [Theory]
    [InlineData("NVIDIA 图形驱动程序 610.62")]
    [InlineData("Realtek Audio Driver")]
    [InlineData("Intel(R) Chipset Device Software")]
    [InlineData("Intel(R) Management Engine Components")]
    public void Drivers(string name) => Assert.Equal(SoftwareCategory.Driver, SoftwareClassifier.Classify(Entry(name)));

    [Fact]
    public void Steam_games_by_key_name()
        => Assert.Equal(SoftwareCategory.Game,
            SoftwareClassifier.Classify(Entry("Counter-Strike 2", "Valve", key: "Steam App 730")));

    [Theory]
    [InlineData(@"D:\SteamLibrary\steamapps\common\Terraria")]
    [InlineData(@"D:\Epic Games\HogwartsLegacy")]
    [InlineData(@"D:\WeGameApps\三角洲行动")]
    public void Games_by_install_location(string location)
        => Assert.Equal(SoftwareCategory.Game, SoftwareClassifier.Classify(Entry("某游戏", location: location)));

    [Theory]   // 真实情况：Epic 启动器的安装位置就是游戏库的根目录 D:\Epic Games\
    [InlineData(@"D:\Epic Games\")]
    [InlineData(@"D:\Epic Games")]
    [InlineData(@"D:\SteamLibrary\steamapps\common\")]
    public void Game_library_root_itself_is_not_a_game(string location)
        => Assert.Equal(SoftwareCategory.Application,
            SoftwareClassifier.Classify(Entry("Epic Games Launcher", location: location)));

    [Fact]
    public void Epic_launcher_itself_is_not_a_game()
        => Assert.Equal(SoftwareCategory.Application,
            SoftwareClassifier.Classify(Entry("Epic Games Launcher", location: @"D:\Epic Games\Launcher\")));

    [Theory]
    [InlineData("Git", "The Git Development Community")]
    [InlineData("CMake", "Kitware")]
    [InlineData("Node.js", "Node.js Foundation")]
    [InlineData("Python 3.10.2 (64-bit)", "Python Software Foundation")]
    [InlineData("IntelliJ IDEA 2025.1.3", "JetBrains s.r.o.")]
    [InlineData("Docker Desktop", "Docker Inc.")]
    [InlineData("Microsoft Visual Studio Code (User)", "Microsoft Corporation")]
    [InlineData("NVIDIA CUDA Toolkit 13.0", "NVIDIA Corporation")]
    [InlineData("Oracle VM VirtualBox 7.0.12", "Oracle and/or its affiliates")]
    public void DevTools(string name, string publisher)
        => Assert.Equal(SoftwareCategory.DevTool, SoftwareClassifier.Classify(Entry(name, publisher)));

    [Theory]
    [InlineData("微信", "腾讯科技(深圳)有限公司")]
    [InlineData("WPS Office (12.1.0.23125)", "Kingsoft Corp.")]
    [InlineData("网易云音乐", "网易公司")]
    [InlineData("Digital Photo Viewer", "Some Co")]   // 名字里含 "git" 但不是 Git
    public void Everything_else_is_an_application(string name, string publisher)
        => Assert.Equal(SoftwareCategory.Application, SoftwareClassifier.Classify(Entry(name, publisher)));
}
