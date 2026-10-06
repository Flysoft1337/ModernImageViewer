using System.IO;
using System.Text.Json;

using ModernImageViewer.Application.Browsing;
using ModernImageViewer.Application.Settings;
using ModernImageViewer.Platform.Settings;

namespace ModernImageViewer.UI.Tests;

public sealed class EditorPresetTests
{
    [Fact]
    public void Utf8BomKeepsOldPreferences()
    {
        using SettingsFile file = new();
        File.WriteAllText(file.Path, "{\"Language\":\"zh-CN\",\"Theme\":\"Light\"}", new System.Text.UTF8Encoding(true));
        using UserSettingsService settings = new(file.Path);
        Assert.Equal("zh-CN", settings.Language);
        Assert.Equal("Light", settings.Theme);
    }

    [Fact]
    public void OversizedSettingsFallBackWithoutChangingTheFile()
    {
        using SettingsFile file = new(new string(' ', (256 * 1024) + 1));
        using UserSettingsService settings = new(file.Path);
        Assert.Null(settings.Language);
        Assert.Null(settings.Current.EditorPresets);
        Assert.Equal((256 * 1024) + 1, new FileInfo(file.Path).Length);
    }

    [Theory]
    [InlineData("{\"Language\":\"zh-CN\",\"Theme\":\"Light\"}")]
    [InlineData("{\"Language\":\"zh-CN\",\"Theme\":\"Light\",\"EditorPresets\":null}")]
    [InlineData("{\"Language\":\"zh-CN\",\"Theme\":\"Light\",\"EditorPresets\":[]}")]
    [InlineData("{\"Language\":\"zh-CN\",\"Theme\":\"Light\",\"EditorPresets\":\"corrupt\"}")]
    public void OldOrMalformedPresetCollectionPreservesExistingPreferences(string json)
    {
        using SettingsFile file = new(json);
        using UserSettingsService settings = new(file.Path);
        Assert.Equal("zh-CN", settings.Language);
        Assert.Equal("Light", settings.Theme);
        Assert.Equal(new BrowsingPreferencesData(), settings.Current.Browsing);
        Assert.Null(settings.Current.EditorPresets);
    }

    [Fact]
    public void CorruptPresetEntriesDoNotDiscardValidEntriesOrOldSettings()
    {
        using SettingsFile file = new("""
            {"Language":"zh-CN","Theme":"Dark",
             "Browsing":{"SortMode":2,"SlideshowSeconds":10},
             "EditorPresets":[null,7,"bad",{},
               {"Name":null,"Width":100,"Height":100},
               {"Name":"type-error","Width":"large","Height":100},
               {"Name":"overflow","Width":2147483648,"Height":1},
               {"Name":"too-large","Width":32768,"Height":32768},
               {"Name":" Web ","Width":640,"Height":480,"CropRatio":"4:3","Format":2,"Quality":87,"WebpLossless":true,"PreserveCamera":true},
               {"Name":"web","Width":10,"Height":10},
               {"Name":"Defaults","Width":100,"Height":100,"CropRatio":"broken","Format":999,"Quality":0}]}
            """);
        using UserSettingsService settings = new(file.Path);
        Assert.Equal("zh-CN", settings.Language);
        Assert.Equal("Dark", settings.Theme);
        Assert.Equal(10, settings.Current.Browsing!.SlideshowSeconds);
        Assert.Equal(new[]
        {
            new EditorPresetData("Web", 640, 480, "4:3", 2, 87, true, true),
            new EditorPresetData("Defaults", 100, 100, Quality: 1),
        }, settings.Current.EditorPresets);
    }

    [Fact]
    public void NamesDimensionsAndPixelBudgetRejectUnsafePresetsWithoutThrowing()
    {
        Assert.Null(new EditorPresetData(null!, 10, 10).Normalize());
        Assert.Null(new EditorPresetData("   ", 10, 10).Normalize());
        Assert.Null(new EditorPresetData(new string('a', 41), 10, 10).Normalize());
        Assert.Null(new EditorPresetData("line\nbreak", 10, 10).Normalize());
        Assert.Null(new EditorPresetData("zero", 0, 10).Normalize());
        Assert.Null(new EditorPresetData("negative", 10, -1).Normalize());
        Assert.Null(new EditorPresetData("dimension", 32769, 1).Normalize());
        Assert.Null(new EditorPresetData("overflow", int.MaxValue, int.MaxValue).Normalize());
        Assert.Null(new EditorPresetData("budget", 4096, 4097).Normalize());
        Assert.True(new EditorPresetData(new string('a', 40), 4096, 4096).IsValid);
        Assert.True(new EditorPresetData("wide", 32768, 512).IsValid);
        Assert.True(new EditorPresetData("small", 1, 1).IsValid);
    }

    [Fact]
    public void NormalizationIsBoundedDeduplicatedAndDetachedFromMutableInputs()
    {
        List<EditorPresetData> input = [null!, new(" first ", 200, 100), new("FIRST", 300, 200)];
        input.AddRange(Enumerable.Range(1, 30).Select(i => new EditorPresetData($"Preset {i}", 300, 200)));
        UserSettingsSnapshot snapshot = new UserSettingsSnapshot(EditorPresets: input).Normalize();
        IReadOnlyList<EditorPresetData> normalized = snapshot.EditorPresets!;
        Assert.Equal(16, normalized.Count);
        Assert.Equal(new EditorPresetData("first", 200, 100), normalized[0]);
        Assert.Equal("Preset 15", normalized[^1].Name);
        input.Clear();
        Assert.Equal(16, normalized.Count);
        Assert.Throws<NotSupportedException>(() => ((IList<EditorPresetData>)normalized).Clear());
        Assert.Null(new UserSettingsSnapshot(EditorPresets: []).Normalize().EditorPresets);
        Assert.Null(EditorPresetData.NormalizeList([new("invalid", 0, 1)]));
    }

    [Fact]
    public void ActualJsonRoundTripKeepsAllPresetOptionsAndOtherPendingSettings()
    {
        using SettingsFile file = new();
        EditorPresetData[] presets =
        [
            new("Free", 640, 480), new("Original", 800, 600, "Original", 1, 1, false, true),
            new("Square", 500, 500, "1:1", 2, 100, true, false),
            new("Photo", 600, 400, "3:2", 1, 95), new("Classic", 800, 600, "4:3"),
            new("Wide", 1600, 900, "16:9", 2, 82, false, true),
            new("Portrait", 900, 1600, "9:16", 2, 100, true, true),
        ];
        WindowPlacementData placement = new(40, 50, 1024, 768);
        BrowsingPreferencesData browsing = new(true, false, BrowseSortMode.Size, true, 10);
        using (UserSettingsService settings = new(file.Path))
        {
            settings.SaveLanguage("zh-CN");
            settings.SaveTheme("Light");
            settings.SaveWindowPlacement(placement);
            settings.SaveBrowsingPreferences(browsing);
            Assert.True(settings.SaveEditorPresets(presets));
            // Success means the shared file already exists, without Flush or Dispose.
            using JsonDocument json = JsonDocument.Parse(File.ReadAllText(file.Path));
            JsonElement stored = json.RootElement.GetProperty("EditorPresets");
            Assert.Equal(7, stored.GetArrayLength());
            Assert.False(stored[0].TryGetProperty("IsValid", out _));
            Assert.True(stored[2].GetProperty("WebpLossless").GetBoolean());
            Assert.True(stored[6].GetProperty("PreserveCamera").GetBoolean());
            Assert.Equal(2, stored[6].GetProperty("Format").GetInt32());
            settings.SaveTheme("Dark");
        }
        using UserSettingsService reopened = new(file.Path);
        Assert.Equal(presets, reopened.Current.EditorPresets);
        Assert.Equal("zh-CN", reopened.Language);
        Assert.Equal("Dark", reopened.Theme);
        Assert.Equal(placement, reopened.Current.WindowPlacement);
        Assert.Equal(browsing, reopened.Current.Browsing);
        Assert.Empty(Directory.GetFiles(file.DirectoryPath, "*.tmp"));
        Assert.Single(Directory.GetFiles(file.DirectoryPath));
    }

    [Fact]
    public void ActualJsonInputBoundsCountAndNormalizesOutOfRangeValues()
    {
        using SettingsFile file = new(JsonSerializer.Serialize(new
        {
            Language = "en-US",
            EditorPresets = Enumerable.Range(0, 40).Select(i => new EditorPresetData($"Preset {i}", 64, 64,
                CropRatio: i == 0 ? null! : "Free", Format: -1, Quality: 999)),
        }));
        using UserSettingsService settings = new(file.Path);
        Assert.Equal("en-US", settings.Language);
        Assert.Equal(16, settings.Current.EditorPresets!.Count);
        Assert.All(settings.Current.EditorPresets, preset =>
        {
            Assert.Equal("Free", preset.CropRatio);
            Assert.Equal(0, preset.Format);
            Assert.Equal(100, preset.Quality);
            Assert.True(preset.IsValid);
        });
    }

    [Fact]
    public void DeletingLastPresetPersistsEmptyCollectionAndRetainsOldPreferences()
    {
        using SettingsFile file = new("""{"Language":"zh-CN","Theme":"Dark"}""");
        using (UserSettingsService settings = new(file.Path))
        {
            Assert.True(settings.SaveEditorPresets([new("temporary", 100, 100)]));
            Assert.True(settings.SaveEditorPresets([]));
            Assert.Null(settings.Current.EditorPresets);
        }
        using UserSettingsService reopened = new(file.Path);
        Assert.Null(reopened.Current.EditorPresets);
        Assert.Equal("zh-CN", reopened.Language);
        Assert.Equal("Dark", reopened.Theme);
    }

    [Fact]
    public void FailedPresetSaveReportsFailureAndRetainsLastAcceptedState()
    {
        using SettingsFile file = new();
        using UserSettingsService settings = new(file.Path);
        Assert.True(settings.SaveEditorPresets([new("Original", 100, 100)]));
        IReadOnlyList<EditorPresetData> original = settings.Current.EditorPresets!;
        File.Delete(file.Path);
        Directory.CreateDirectory(file.Path);
        settings.SaveTheme("Light");
        Assert.False(settings.SaveEditorPresets([new("Rejected", 200, 200)]));
        Assert.Same(original, settings.Current.EditorPresets);
        Assert.Equal("Light", settings.Theme);
        Assert.Empty(Directory.GetFiles(file.DirectoryPath, "*.tmp"));
        Directory.Delete(file.Path);
        settings.Flush();
        using UserSettingsService reopened = new(file.Path);
        Assert.Equal(original, reopened.Current.EditorPresets);
        Assert.Equal("Light", reopened.Theme);
    }

    [Fact]
    public void DisposedServiceRejectsPresetCommands()
    {
        using SettingsFile file = new();
        UserSettingsService settings = new(file.Path);
        settings.Dispose();
        Assert.False(settings.SaveEditorPresets([new("late", 100, 100)]));
        Assert.False(File.Exists(file.Path));
    }

    private sealed class SettingsFile : IDisposable
    {
        public SettingsFile(string? json = null)
        {
            Directory.CreateDirectory(DirectoryPath);
            if (json is not null) { File.WriteAllText(Path, json); }
        }

        public string DirectoryPath { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"ModernImageViewer-presets-{Guid.NewGuid():N}");
        public string Path => System.IO.Path.Combine(DirectoryPath, "settings.json");
        public void Dispose() => Directory.Delete(DirectoryPath, recursive: true);
    }
}
