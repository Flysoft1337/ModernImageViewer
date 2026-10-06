using ModernImageViewer.Application.Settings;

namespace ModernImageViewer.UI.ViewModels;

public sealed partial class MainWindowViewModel
{
    private readonly IUserSettingsService? _settings;
    internal IUserSettingsService? Settings => _settings;

    public WindowPlacementData? WindowPlacement => _settings?.Current.WindowPlacement;

    public void SaveWindowPlacement(WindowPlacementData placement) => _settings?.SaveWindowPlacement(placement);

    public void FlushPreferences() => _settings?.Flush();

    private void InitializePreferences()
    {
        if (_settings is null) { return; }
        BrowsingPreferencesData preferences = (_settings.Current.Browsing ?? new()).Normalize();
        _showInformation = preferences.ShowInformation;
        _showFilmstrip = preferences.ShowFilmstrip;
        _slideshowSeconds = preferences.SlideshowSeconds;
        _browseSession.InitializeSortPreference(preferences.SortMode, preferences.SortDescending);
    }

    private void SaveBrowsingPreferences()
    {
        if (!_disposed)
        {
            _settings?.SaveBrowsingPreferences(new BrowsingPreferencesData(
                _showInformation, _showFilmstrip, SortMode, SortDescending, _slideshowSeconds));
        }
    }
}
