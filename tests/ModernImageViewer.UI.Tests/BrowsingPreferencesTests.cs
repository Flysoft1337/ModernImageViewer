using System.IO;

using ModernImageViewer.Application.Browsing;
using ModernImageViewer.Application.Images;
using ModernImageViewer.Application.Settings;
using ModernImageViewer.Imaging;
using ModernImageViewer.Platform.Settings;
using ModernImageViewer.UI.Localization;
using ModernImageViewer.UI.ViewModels;

namespace ModernImageViewer.UI.Tests;

public sealed class BrowsingPreferencesTests
{
    [Fact]
    public void InitializationRestoresPreferencesWithoutDecodingOrStartingPlayback()
    {
        MemorySettings settings = new(new UserSettingsSnapshot(Browsing: new(true, false, BrowseSortMode.Size, true, 10)));
        CountingDecoder decoder = new();
        using ImageOpenCoordinator coordinator = new(new NullPicker(), decoder);
        ImageBrowseSession session = new();
        using MainWindowViewModel viewModel = new(new LocalizationService(settings), coordinator, session, settings: settings);

        Assert.True(viewModel.ShowInformation);
        Assert.False(viewModel.ShowFilmstrip);
        Assert.Equal(10, viewModel.SlideshowSeconds);
        Assert.Equal(BrowseSortMode.Size, viewModel.SortMode);
        Assert.True(viewModel.SortDescending);
        Assert.False(viewModel.IsSlideshowPlaying);
        Assert.False(viewModel.HasImage);
        Assert.Equal(0, decoder.Count);
        Assert.Equal(0, settings.SaveCount);
        Assert.Equal(0, session.Count);
    }

    [Fact]
    public async Task UpdatedPreferencesSurviveRestartAndExplicitSelectionKeepsItsOrder()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"ModernImageViewer-preferences-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            string first = Path.Combine(directory, "01.png");
            string second = Path.Combine(directory, "02.png");
            string settingsPath = Path.Combine(directory, "settings.json");
            File.WriteAllBytes(first, []);
            File.WriteAllBytes(second, []);
            using (UserSettingsService settings = new(settingsPath))
            using (ImageOpenCoordinator coordinator = new(new NullPicker(), new CountingDecoder()))
            using (MainWindowViewModel viewModel = new(new LocalizationService(settings), coordinator, new(), settings: settings))
            {
                Assert.True(await viewModel.OpenPathAsync(first));
                await coordinator.WaitForIndexingAsync(TestContext.Current.CancellationToken);
                Assert.True(await viewModel.ChangeSortAsync(BrowseSortMode.Name, true));
                viewModel.ShowInformation = true;
                viewModel.ShowFilmstrip = false;
                viewModel.SlideshowSeconds = 10;
                settings.SaveLanguage("zh-CN");
                settings.SaveTheme("Light");
            }

            using UserSettingsService reopened = new(settingsPath);
            using ImageOpenCoordinator reopenedCoordinator = new(new NullPicker(), new CountingDecoder());
            ImageBrowseSession reopenedSession = new();
            using MainWindowViewModel restored = new(new LocalizationService(reopened), reopenedCoordinator, reopenedSession, settings: reopened);
            Assert.Equal(new BrowsingPreferencesData(true, false, BrowseSortMode.Name, true, 10), reopened.Current.Browsing);
            Assert.Equal("zh-CN", reopened.Language);
            Assert.Equal("Light", reopened.Theme);
            Assert.True(await restored.OpenFolderAsync(directory));
            await reopenedCoordinator.WaitForIndexingAsync(TestContext.Current.CancellationToken);
            Assert.Equal(second, restored.CurrentFilePath);
            Assert.True(await restored.OpenInputsAsync([first, second]));
            Assert.True(reopenedSession.IsSelection);
            Assert.Equal(new[] { first, second }, reopenedSession.Items);
            Assert.False(await restored.ChangeSortAsync(BrowseSortMode.Size, false));
            Assert.True(restored.SortDescending);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private sealed class NullPicker : IImageFilePicker
    {
        public Task<string?> PickImageAsync(CancellationToken cancellationToken) => Task.FromResult<string?>(null);
    }

    private sealed class CountingDecoder : IImageDecoder
    {
        public int Count { get; private set; }
        public Task<PixelBuffer> DecodeAsync(string path, CancellationToken cancellationToken)
        {
            Count++;
            return Task.FromResult(new PixelBuffer(new PixelSize(1, 1), 4, new byte[4]));
        }
    }

    private sealed class MemorySettings(UserSettingsSnapshot current) : IUserSettingsService
    {
        public UserSettingsSnapshot Current { get; private set; } = current;
        public string? Language => Current.Language;
        public string? Theme => Current.Theme;
        public int SaveCount { get; private set; }
        public void SaveLanguage(string language) => Current = Current with { Language = language };
        public void SaveTheme(string theme) => Current = Current with { Theme = theme };
        public void SaveBrowsingPreferences(BrowsingPreferencesData preferences)
        {
            SaveCount++;
            Current = Current with { Browsing = preferences };
        }
    }
}
