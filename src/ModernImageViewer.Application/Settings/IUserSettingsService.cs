namespace ModernImageViewer.Application.Settings;

public interface IUserSettingsService
{
    string? Language { get; }
    string? Theme { get; }
    UserSettingsSnapshot Current => new UserSettingsSnapshot(Language, Theme).Normalize();
    void SaveLanguage(string language);
    void SaveTheme(string theme);
    void SaveBrowsingPreferences(BrowsingPreferencesData preferences) { }
    void SaveWindowPlacement(WindowPlacementData placement) { }
    void Flush() { }
}
