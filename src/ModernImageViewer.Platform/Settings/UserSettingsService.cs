using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

using ModernImageViewer.Application.Settings;

namespace ModernImageViewer.Platform.Settings;

public sealed class UserSettingsService : IUserSettingsService
{
    private readonly string _settingsPath;

    public UserSettingsService()
    {
        string settingsDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ModernImageViewer");
        _settingsPath = Path.Combine(settingsDirectory, "settings.json");
        UserSettingsData? settings = Load();
        Language = settings?.Language;
        Theme = settings?.Theme;
    }

    public string? Language { get; private set; }

    public string? Theme { get; private set; }

    public void SaveTheme(string theme)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(theme);
        Save(new UserSettingsData(Language, theme));
        Theme = theme;
    }

    public void SaveLanguage(string language)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(language);

        Save(new UserSettingsData(language, Theme));
        Language = language;
    }

    private void Save(UserSettingsData settings)
    {
        string? directory = Path.GetDirectoryName(_settingsPath);
        if (directory is not null)
        {
            Directory.CreateDirectory(directory);
        }

        string temporaryPath = _settingsPath + $".{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(settings, UserSettingsJsonContext.Default.UserSettingsData));
            File.Move(temporaryPath, _settingsPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private UserSettingsData? Load()
    {
        try
        {
            if (!File.Exists(_settingsPath))
            {
                return null;
            }

            UserSettingsData? settings = JsonSerializer.Deserialize(File.ReadAllText(_settingsPath), UserSettingsJsonContext.Default.UserSettingsData);
            return settings;
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

}

internal sealed record UserSettingsData(string? Language, string? Theme = null);

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(UserSettingsData))]
internal sealed partial class UserSettingsJsonContext : JsonSerializerContext;
