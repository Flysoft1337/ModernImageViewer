using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

using ModernImageViewer.Application.Settings;

namespace ModernImageViewer.Platform.Settings;

public sealed class UserSettingsService : IUserSettingsService, IDisposable
{
    private readonly string _settingsPath;
    private readonly object _gate = new();
    private UserSettingsSnapshot _current;
    private Timer? _saveTimer;
    private bool _dirty;
    private bool _disposed;

    public UserSettingsService() : this(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ModernImageViewer", "settings.json"))
    { }

    public UserSettingsService(string settingsPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(settingsPath);
        _settingsPath = Path.GetFullPath(settingsPath);
        _current = (Load() ?? new()).Normalize();
    }

    public UserSettingsSnapshot Current { get { lock (_gate) { return _current; } } }
    public string? Language => Current.Language;
    public string? Theme => Current.Theme;

    public void SaveLanguage(string language)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(language);
        Update(current => current with { Language = language });
    }

    public void SaveTheme(string theme)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(theme);
        Update(current => current with { Theme = theme });
    }

    public void SaveBrowsingPreferences(BrowsingPreferencesData preferences)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        Update(current => current with { Browsing = preferences.Normalize() });
    }

    public void SaveWindowPlacement(WindowPlacementData placement)
    {
        ArgumentNullException.ThrowIfNull(placement);
        if (placement.IsValid)
        {
            Update(current => current with { WindowPlacement = placement });
        }
    }

    private void Update(Func<UserSettingsSnapshot, UserSettingsSnapshot> update)
    {
        lock (_gate)
        {
            if (_disposed) { return; }
            UserSettingsSnapshot next = update(_current);
            if (next == _current) { return; }
            _current = next;
            _dirty = true;
            _saveTimer ??= new Timer(_ => Flush(), null, Timeout.Infinite, Timeout.Infinite);
            _saveTimer.Change(350, Timeout.Infinite);
        }
    }

    public void Flush()
    {
        lock (_gate)
        {
            if (!_dirty) { return; }
            try
            {
                Save(_current);
                _dirty = false;
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            catch (System.Security.SecurityException) { }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) { return; }
            _disposed = true;
            _saveTimer?.Dispose();
            _saveTimer = null;
            Flush();
        }
        GC.SuppressFinalize(this);
    }

    private void Save(UserSettingsSnapshot settings)
    {
        string? directory = Path.GetDirectoryName(_settingsPath);
        if (directory is not null)
        {
            Directory.CreateDirectory(directory);
        }

        string temporaryPath = _settingsPath + $".{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(settings, UserSettingsJsonContext.Default.UserSettingsSnapshot));
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

    private UserSettingsSnapshot? Load()
    {
        try
        {
            if (!File.Exists(_settingsPath))
            {
                return null;
            }

            UserSettingsSnapshot? settings = JsonSerializer.Deserialize(File.ReadAllText(_settingsPath), UserSettingsJsonContext.Default.UserSettingsSnapshot);
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
        catch (System.Security.SecurityException)
        {
            return null;
        }
    }

}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(UserSettingsSnapshot))]
internal sealed partial class UserSettingsJsonContext : JsonSerializerContext;
