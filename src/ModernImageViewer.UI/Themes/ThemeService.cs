using System.Windows;

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
    private readonly IUserSettingsService? _settings;

    public ThemeService(IUserSettingsService? settings = null)
    {
        _settings = settings;
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
    }

    public AppTheme CurrentTheme { get; private set; } = AppTheme.Dark;

    public void Initialize()
    {
        if (Enum.TryParse(_settings?.Theme, out AppTheme saved) && Enum.IsDefined(saved))
        {
            Apply(saved, save: false);
        }
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
        bool light = theme == AppTheme.Light || (theme == AppTheme.System && IsSystemLight());
        string file = light ? "Colors.Light.xaml" : "Colors.xaml";
        ResourceDictionary colors = new()
        {
            Source = new Uri($"pack://application:,,,/ModernImageViewer.UI;component/Themes/{file}", UriKind.Absolute),
        };
        var dictionaries = System.Windows.Application.Current.Resources.MergedDictionaries;
        for (int index = 0; index < dictionaries.Count; index++)
        {
            if (dictionaries[index].Source?.OriginalString.Contains("/Themes/Colors", StringComparison.Ordinal) == true)
            {
                dictionaries[index] = colors;
                return;
            }
        }

        dictionaries.Insert(0, colors);
    }

    public void Dispose() => SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;

    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (CurrentTheme == AppTheme.System && dispatcher is { HasShutdownStarted: false })
        {
            dispatcher.InvokeAsync(() => Apply(AppTheme.System, save: false));
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
