using CommunityToolkit.Mvvm.ComponentModel;

namespace UpdateHelper.Presentation.Settings;

/// <summary>当前设置，首页和设置页共用。每次修改都规范化后保存；保存失败不抛异常，原因写进 SaveError。</summary>
public sealed class SettingsService : ObservableObject
{
    private readonly SettingsStore _store;
    private AppSettings _current;
    private string? _saveError;

    public SettingsService(SettingsStore store)
    {
        _store = store;
        _current = store.Load();
    }

    public AppSettings Current => _current;

    public string? SaveError
    {
        get => _saveError;
        private set => SetProperty(ref _saveError, value);
    }

    public void Update(Func<AppSettings, AppSettings> change)
    {
        var next = change(_current).Normalized();
        if (next == _current) return;
        SetProperty(ref _current, next, nameof(Current));
        try
        {
            _store.Save(next);
            SaveError = null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SaveError = $"设置没能保存：{ex.Message}";
        }
    }
}
