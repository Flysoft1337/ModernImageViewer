namespace ModernImageViewer.Application.Settings;

public interface IUserSettingsService
{
    string? Language { get; }

    void SaveLanguage(string language);
}
