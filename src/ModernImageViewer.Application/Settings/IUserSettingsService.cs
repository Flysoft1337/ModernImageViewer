namespace ModernImageViewer.Application.Settings;

public interface IUserSettingsService
{
    string? Language { get; }
    string? Theme { get; }
    void SaveLanguage(string language);
    void SaveTheme(string theme);
}
