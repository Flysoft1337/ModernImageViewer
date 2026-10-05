using System.IO;
using System.Text.Json;

using ModernImageViewer.Application.Browsing;
using ModernImageViewer.Application.Settings;
using ModernImageViewer.Platform.Settings;

namespace ModernImageViewer.UI.Tests;

public sealed class UserSettingsServiceTests
{
    [Theory]
    [InlineData("{\"Language\":\"zh-CN\"}", "zh-CN", null)]
    [InlineData("{\"Language\":\"en-US\",\"Theme\":\"Light\"}", "en-US", "Light")]
    public void OldSettingsKeepLanguageAndThemeWithBrowsingDefaults(string json, string language, string? theme)
    {
        using SettingsFile file = new(json);
        using UserSettingsService settings = new(file.Path);

        Assert.Equal(language, settings.Language);
        Assert.Equal(theme, settings.Theme);
        Assert.Equal(new BrowsingPreferencesData(), settings.Current.Browsing);
        Assert.Null(settings.Current.WindowPlacement);
    }

    [Fact]
    public void InvalidNumericFieldsFallBackWithoutDiscardingOldPreferences()
    {
        using SettingsFile file = new("""
            {"Language":"zh-CN","Theme":"Dark",
             "WindowPlacement":{"Left":10,"Top":10,"Width":-1,"Height":800,"DpiScale":0},
             "Browsing":{"ShowInformation":true,"ShowFilmstrip":false,"SortMode":999,"SlideshowSeconds":-5}}
            """);
        using UserSettingsService settings = new(file.Path);

        Assert.Equal("zh-CN", settings.Language);
        Assert.Equal("Dark", settings.Theme);
        Assert.Null(settings.Current.WindowPlacement);
        Assert.Equal(new BrowsingPreferencesData(true, false), settings.Current.Browsing);
    }

    [Fact]
    public void MergedUpdatesPreserveAllFieldsAndCloseFlushes()
    {
        using SettingsFile file = new();
        WindowPlacementData placement = new(-800, 80, 1024, 768, 1.5, true);
        BrowsingPreferencesData browsing = new(true, false, BrowseSortMode.Size, true, 10);
        using (UserSettingsService settings = new(file.Path))
        {
            settings.SaveLanguage("zh-CN");
            settings.SaveWindowPlacement(placement);
            settings.SaveBrowsingPreferences(browsing);
            settings.SaveTheme("Light");
            Assert.Equal(new UserSettingsSnapshot("zh-CN", "Light", placement, browsing), settings.Current);
        }

        using UserSettingsService reopened = new(file.Path);
        Assert.Equal(new UserSettingsSnapshot("zh-CN", "Light", placement, browsing), reopened.Current);
        Assert.Empty(Directory.GetFiles(file.DirectoryPath, "*.tmp"));
        using JsonDocument saved = JsonDocument.Parse(File.ReadAllText(file.Path));
        Assert.False(saved.RootElement.GetProperty("WindowPlacement").TryGetProperty("IsValid", out _));
    }

    [Fact]
    public void InvalidPlacementUpdateKeepsLastValidWindow()
    {
        using SettingsFile file = new();
        using UserSettingsService settings = new(file.Path);
        WindowPlacementData placement = new(0, 0, 1024, 768);
        settings.SaveWindowPlacement(placement);
        settings.SaveWindowPlacement(placement with { Left = double.NaN });
        settings.SaveWindowPlacement(placement with { DpiScale = double.PositiveInfinity });
        settings.Flush();

        using UserSettingsService reopened = new(file.Path);
        Assert.Equal(placement, reopened.Current.WindowPlacement);
    }

    [Fact]
    public void CorruptSettingsUseDefaultsAndCanBeReplaced()
    {
        using SettingsFile file = new("{unfinished");
        using UserSettingsService settings = new(file.Path);
        Assert.Equal(new UserSettingsSnapshot().Normalize(), settings.Current);
        settings.SaveLanguage("en-US");
        settings.Flush();

        using UserSettingsService reopened = new(file.Path);
        Assert.Equal("en-US", reopened.Language);
    }

    [Fact]
    public void FailedSaveDoesNotThrowOrLoseInMemoryPreferences()
    {
        using SettingsFile file = new();
        Directory.CreateDirectory(file.Path);
        using UserSettingsService settings = new(file.Path);
        settings.SaveTheme("Light");
        settings.Flush();

        Assert.Equal("Light", settings.Theme);
        Assert.Empty(Directory.GetFiles(file.DirectoryPath, "*.tmp"));
    }

    private sealed class SettingsFile : IDisposable
    {
        public SettingsFile(string? json = null)
        {
            Directory.CreateDirectory(DirectoryPath);
            if (json is not null) { File.WriteAllText(Path, json); }
        }

        public string DirectoryPath { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"ModernImageViewer-settings-{Guid.NewGuid():N}");
        public string Path => System.IO.Path.Combine(DirectoryPath, "settings.json");
        public void Dispose() => Directory.Delete(DirectoryPath, recursive: true);
    }
}
