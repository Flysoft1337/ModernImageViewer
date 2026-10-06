using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

using ModernImageViewer.Application.Settings;

namespace ModernImageViewer.Platform.Settings;

public sealed class UserSettingsService : IUserSettingsService, IDisposable
{
    private const int SettingsByteLimit = 256 * 1024;
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

    public bool SaveEditorPresets(IReadOnlyList<EditorPresetData> presets)
    {
        ArgumentNullException.ThrowIfNull(presets);
        lock (_gate)
        {
            if (_disposed) { return false; }
            UserSettingsSnapshot next = _current with { EditorPresets = EditorPresetData.NormalizeList(presets) };
            try
            {
                // Preset commands need an immediate disk result; merge any pending browsing updates.
                Save(next);
                _current = next;
                _dirty = false;
                return true;
            }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
            catch (System.Security.SecurityException) { return false; }
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

            using FileStream input = new(_settingsPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (input.Length > SettingsByteLimit) { return null; }
            byte[] json = new byte[checked((int)input.Length)];
            input.ReadExactly(json);
            ReadOnlySpan<byte> payload = json;
            if (payload.StartsWith<byte>([0xef, 0xbb, 0xbf])) { payload = payload[3..]; }
            UserSettingsSnapshot? settings = JsonSerializer.Deserialize(payload, UserSettingsJsonContext.Default.UserSettingsSnapshot);
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

[JsonSourceGenerationOptions(WriteIndented = true, Converters = new[] { typeof(EditorPresetListJsonConverter) })]
[JsonSerializable(typeof(UserSettingsSnapshot))]
[JsonSerializable(typeof(EditorPresetData))]
internal sealed partial class UserSettingsJsonContext : JsonSerializerContext;

internal sealed class EditorPresetListJsonConverter : JsonConverter<IReadOnlyList<EditorPresetData>>
{
    public override IReadOnlyList<EditorPresetData>? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using JsonDocument document = JsonDocument.ParseValue(ref reader);
        if (document.RootElement.ValueKind != JsonValueKind.Array) { return null; }
        List<EditorPresetData> presets = new(EditorPresetData.MaximumCount);
        HashSet<string> names = new(StringComparer.OrdinalIgnoreCase);
        foreach (JsonElement item in document.RootElement.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) { continue; }
            try
            {
                if (item.Deserialize(UserSettingsJsonContext.Default.EditorPresetData)?.Normalize() is not { } preset
                    || !names.Add(preset.Name)) { continue; }
                presets.Add(preset);
                if (presets.Count == EditorPresetData.MaximumCount) { break; }
            }
            catch (JsonException) { }
        }
        return presets.Count == 0 ? null : presets.AsReadOnly();
    }

    public override void Write(Utf8JsonWriter writer, IReadOnlyList<EditorPresetData> value, JsonSerializerOptions options)
    {
        writer.WriteStartArray();
        foreach (EditorPresetData preset in value)
        {
            JsonSerializer.Serialize(writer, preset, UserSettingsJsonContext.Default.EditorPresetData);
        }
        writer.WriteEndArray();
    }
}
