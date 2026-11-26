using System.ComponentModel;
using System.Windows;
using System.Windows.Threading;

using Microsoft.Win32;

using ModernImageViewer.Application.Settings;

namespace ModernImageViewer.UI.Themes;

public enum AppTheme
{
    Dark,
    Light,
    System,
}

public sealed class ThemeService : IDisposable
{
    private const string HighContrastPalette = "Colors.HighContrast.xaml";
    private readonly IUserSettingsService? _settings;
    private readonly Func<bool> _readHighContrast;
    private bool _disposed;
    private int _paletteRefreshQueued;

    public ThemeService(IUserSettingsService? settings = null)
        : this(settings, () => SystemParameters.HighContrast)
    {
    }

    internal ThemeService(IUserSettingsService? settings, Func<bool> readHighContrast)
    {
        _settings = settings;
        _readHighContrast = readHighContrast;
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        SystemParameters.StaticPropertyChanged += OnSystemParametersChanged;
    }

    public AppTheme CurrentTheme { get; private set; } = AppTheme.Dark;

    public void Initialize()
    {
        if (Enum.TryParse(_settings?.Theme, out AppTheme saved) && Enum.IsDefined(saved))
        {
            CurrentTheme = saved;
        }
        RefreshPalette();
    }

    public void Apply(AppTheme theme, bool save = true)
    {
        CurrentTheme = theme;
        if (save && _settings is not null)
        {
            try { _settings.SaveTheme(theme.ToString()); }
            catch (System.IO.IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        RefreshPalette();
    }

    internal void RefreshPalette()
    {
        bool highContrast = _readHighContrast();
        bool light = CurrentTheme == AppTheme.Light || (!highContrast && CurrentTheme == AppTheme.System && IsSystemLight());
        string file = highContrast ? HighContrastPalette : light ? "Colors.Light.xaml" : "Colors.xaml";
        Uri source = new($"pack://application:,,,/ModernImageViewer.UI;component/Themes/{file}", UriKind.Absolute);
        var dictionaries = System.Windows.Application.Current.Resources.MergedDictionaries;
        for (int index = 0; index < dictionaries.Count; index++)
        {
            if (dictionaries[index].Source?.OriginalString.Contains("/Themes/Colors", StringComparison.Ordinal) == true)
            {
                // Reload the high-contrast dictionary when Windows changes its actual palette.
                if (highContrast || dictionaries[index].Source != source)
                {
                    dictionaries[index] = new ResourceDictionary { Source = source };
                }
                return;
            }
        }

        dictionaries.Insert(0, new ResourceDictionary { Source = source });
    }

    public void Dispose()
    {
        _disposed = true;
        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        SystemParameters.StaticPropertyChanged -= OnSystemParametersChanged;
    }

    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category is UserPreferenceCategory.Accessibility or UserPreferenceCategory.Color
            or UserPreferenceCategory.General or UserPreferenceCategory.VisualStyle)
        {
            QueuePaletteRefresh();
        }
    }

    private void OnSystemParametersChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SystemParameters.HighContrast))
        {
            QueuePaletteRefresh();
        }
    }

    private void QueuePaletteRefresh()
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is { HasShutdownStarted: false } && Interlocked.Exchange(ref _paletteRefreshQueued, 1) == 0)
        {
            dispatcher.InvokeAsync(() =>
            {
                Interlocked.Exchange(ref _paletteRefreshQueued, 0);
                if (!_disposed) { RefreshPalette(); }
            }, DispatcherPriority.Background);
        }
    }

    private static bool IsSystemLight()
    {
        try
        {
            return Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme", 1) is not 0;
        }
        catch (System.Security.SecurityException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }
}
