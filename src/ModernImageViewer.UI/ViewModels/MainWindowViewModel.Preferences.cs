using ModernImageViewer.Application.Settings;

namespace ModernImageViewer.UI.ViewModels;

public sealed partial class MainWindowViewModel
{
    private readonly IUserSettingsService? _settings;
    private CanvasBackgroundMode _canvasBackground;
    internal IUserSettingsService? Settings => _settings;

    public WindowPlacementData? WindowPlacement => _settings?.Current.WindowPlacement;

    public void SaveWindowPlacement(WindowPlacementData placement) => _settings?.SaveWindowPlacement(placement);

    public void FlushPreferences() => _settings?.Flush();

    public string CanvasBackgroundLabel => Text("Canvas_Background");
    public string CheckerboardBackgroundLabel => Text("Canvas_Checkerboard");
    public string WhiteBackgroundLabel => Text("Canvas_White");
    public string BlackBackgroundLabel => Text("Canvas_Black");

    public CanvasBackgroundMode CanvasBackground
    {
        get => _canvasBackground;
        set
        {
            if (_canvasBackground == value) { return; }
            _canvasBackground = value;
            OnPropertyChanged();
            SaveBrowsingPreferences();
        }
    }

    private void InitializePreferences()
    {
        if (_settings is null) { return; }
        BrowsingPreferencesData preferences = (_settings.Current.Browsing ?? new()).Normalize();
        _showInformation = preferences.ShowInformation;
        _showFilmstrip = preferences.ShowFilmstrip;
        _slideshowSeconds = preferences.SlideshowSeconds;
        _canvasBackground = preferences.CanvasBackground;
        _browseSession.InitializeSortPreference(preferences.SortMode, preferences.SortDescending);
    }

    private void SaveBrowsingPreferences()
    {
        if (!_disposed)
        {
            _settings?.SaveBrowsingPreferences(new BrowsingPreferencesData(
                _showInformation, _showFilmstrip, SortMode, SortDescending, _slideshowSeconds, _canvasBackground));
        }
    }
}
