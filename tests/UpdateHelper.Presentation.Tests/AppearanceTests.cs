using UpdateHelper.Presentation.Settings;
using UpdateHelper.Presentation.ViewModels;

namespace UpdateHelper.Presentation.Tests;

public sealed class AppearanceTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("uh-look-").FullName;
    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    private string SettingsPath => Path.Combine(_dir, "settings.json");

    [Fact]
    public void Defaults_follow_system_with_mica()
    {
        var s = new AppSettings();
        Assert.Equal(ThemeChoice.System, s.Theme);
        Assert.Null(s.AccentColor);
        Assert.Equal(BackdropChoice.Mica, s.Backdrop);
        Assert.Equal(TextSize.Standard, s.TextSize);
        Assert.False(s.Compact);
    }

    [Fact]
    public void Normalized_resets_bad_appearance_values()
    {
        var s = new AppSettings
        {
            Theme = (ThemeChoice)9, Backdrop = (BackdropChoice)9, TextSize = (TextSize)9, AccentColor = "red; drop",
        }.Normalized();

        Assert.Equal(ThemeChoice.System, s.Theme);
        Assert.Equal(BackdropChoice.Mica, s.Backdrop);
        Assert.Equal(TextSize.Standard, s.TextSize);
        Assert.Null(s.AccentColor);
    }

    [Fact]
    public void Normalized_keeps_valid_accent_color()
    {
        Assert.Equal("#0F9D58", new AppSettings { AccentColor = "#0F9D58" }.Normalized().AccentColor);
    }

    [Fact]
    public void Appearance_survives_save_and_load()
    {
        var store = new SettingsStore(SettingsPath);
        store.Save(new AppSettings { Theme = ThemeChoice.Dark, AccentColor = "#E81123", Backdrop = BackdropChoice.Solid, TextSize = TextSize.Large, Compact = true });

        var loaded = new SettingsStore(SettingsPath).Load();
        Assert.Equal(ThemeChoice.Dark, loaded.Theme);
        Assert.Equal("#E81123", loaded.AccentColor);
        Assert.Equal(BackdropChoice.Solid, loaded.Backdrop);
        Assert.Equal(TextSize.Large, loaded.TextSize);
        Assert.True(loaded.Compact);
    }

    [Fact]
    public void Old_settings_file_without_appearance_gets_defaults()
    {
        File.WriteAllText(SettingsPath, """{"Mode":0,"ObservationDays":3,"CheckIntervalHours":24,"TrayEnabled":true}""");
        var loaded = new SettingsStore(SettingsPath).Load();
        Assert.Equal(ThemeChoice.System, loaded.Theme);
        Assert.Equal(BackdropChoice.Mica, loaded.Backdrop);
    }

    [Fact]
    public void Settings_page_edits_appearance()
    {
        var service = new SettingsService(new SettingsStore(SettingsPath));
        var vm = new SettingsViewModel(service);

        vm.ThemeIndex = (int)ThemeChoice.Light;
        vm.BackdropIndex = (int)BackdropChoice.Acrylic;
        vm.TextSizeIndex = (int)TextSize.Small;
        vm.Compact = true;

        Assert.Equal(ThemeChoice.Light, service.Current.Theme);
        Assert.Equal(BackdropChoice.Acrylic, service.Current.Backdrop);
        Assert.Equal(TextSize.Small, service.Current.TextSize);
        Assert.True(service.Current.Compact);
    }

    [Fact]
    public void Accent_index_zero_means_follow_system()
    {
        var service = new SettingsService(new SettingsStore(SettingsPath));
        var vm = new SettingsViewModel(service);
        Assert.Equal("跟随系统", vm.AccentOptions[0].Name);
        Assert.Equal(0, vm.AccentIndex);

        vm.AccentIndex = 2;
        Assert.Equal(vm.AccentOptions[2].Color, service.Current.AccentColor);
        Assert.Equal(2, vm.AccentIndex);

        vm.AccentIndex = 0;
        Assert.Null(service.Current.AccentColor);
    }

    [Fact]
    public void Unknown_accent_color_shows_as_follow_system()
    {
        var service = new SettingsService(new SettingsStore(SettingsPath));
        service.Update(s => s with { AccentColor = "#123456" });   // 合法但不在预设里（比如手改了文件）
        Assert.Equal(0, new SettingsViewModel(service).AccentIndex);
    }
}
