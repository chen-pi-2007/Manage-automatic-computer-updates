using UpdateHelper.Core.Scanning;
using static UpdateHelper.Core.Tests.Fakes;

namespace UpdateHelper.Core.Tests;

public class SystemScannerTests
{
    private sealed class FakeUninstall(params UninstallEntry[] entries) : IUninstallSource
    {
        public IReadOnlyList<UninstallEntry> Read() => entries;
    }

    private sealed class FakeBackground(params BackgroundItem[] items) : IBackgroundSource
    {
        public IReadOnlyList<BackgroundItem> Read() => items;
    }

    private sealed class BrokenBackground : IBackgroundSource
    {
        public IReadOnlyList<BackgroundItem> Read() => throw new InvalidOperationException("COM 坏了");
    }

    private static BackgroundItem Svc(string name, string? exe)
        => new(BackgroundKind.Service, name, null, exe, exe, null);

    [Fact]
    public void Items_inside_windows_directory_are_dropped()
    {
        var scanner = new SystemScanner(
            new FakeUninstall(),
            [new FakeBackground(
                Svc("Winmgmt", @"C:\Windows\system32\svchost.exe"),
                Svc("ToDesk_Service", @"C:\Program Files\ToDesk\ToDesk_Service.exe"),
                Svc("NoPath", null))],
            windowsDirectory: @"C:\Windows");

        var report = scanner.Scan();

        Assert.Equal(new[] { "ToDesk_Service", "NoPath" }, report.Result.UnassignedBackground.Select(b => b.Name));
        Assert.Empty(report.Warnings);
    }

    [Fact]
    public void Broken_source_becomes_warning_and_others_still_scan()   // Review Focus 5
    {
        var scanner = new SystemScanner(
            new FakeUninstall(Entry("QQ", "Tencent", location: @"C:\Program Files\Tencent\QQNT")),
            [new BrokenBackground(), new FakeBackground(Svc("QQSvc", @"C:\Program Files\Tencent\QQNT\svc.exe"))],
            windowsDirectory: @"C:\Windows");

        var report = scanner.Scan();

        var warning = Assert.Single(report.Warnings);
        Assert.Contains("BrokenBackground", warning);
        Assert.Single(report.Result.Groups.Single(g => g.Name == "QQ").Background);
    }
}
