using System.IO;
using System.Text.Json;

using ModernImageViewer.Application.Settings;

namespace ModernImageViewer.Platform.Settings;

public sealed class UserSettingsService : IUserSettingsService
{
    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };
    private readonly string _settingsPath;

    public UserSettingsService()
    {
        string settingsDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ModernImageViewer");
        _settingsPath = Path.Combine(settingsDirectory, "settings.json");
        Language = LoadLanguage();
    }

    public string? Language { get; private set; }

    public void SaveLanguage(string language)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(language);

        string? directory = Path.GetDirectoryName(_settingsPath);
        if (directory is not null)
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(
            _settingsPath,
            JsonSerializer.Serialize(new UserSettings(language), SerializerOptions));
        Language = language;
    }

    private string? LoadLanguage()
    {
        try
        {
            if (!File.Exists(_settingsPath))
            {
                return null;
            }

            UserSettings? settings = JsonSerializer.Deserialize<UserSettings>(File.ReadAllText(_settingsPath));
            return settings?.Language;
        }
        catch (JsonException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private sealed record UserSettings(string Language);
}
