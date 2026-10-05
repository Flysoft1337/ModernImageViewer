using System.Globalization;
using System.IO;
using System.Windows.Input;

using ModernImageViewer.Application.Browsing;
using ModernImageViewer.Application.Images;
using ModernImageViewer.Application.Integration;
using ModernImageViewer.Imaging;
using ModernImageViewer.UI.Localization;
using ModernImageViewer.UI.ViewModels;

namespace ModernImageViewer.UI.Tests;

public sealed class BrowsingToolsTests
{
    [Fact]
    public async Task SortingPreservesMainAndRegionPixelsWithoutDecodingAgain()
    {
        using Files files = new();
        PreviewDecoder decoder = new();
        ImageBrowseSession session = new();
        using ImageOpenCoordinator coordinator = new(new NullPicker(), decoder);
        using MainWindowViewModel model = new(new TestLocalization(), coordinator, session);
        Assert.True(await model.OpenPathAsync(files.First));
        await coordinator.WaitForIndexingAsync(TestContext.Current.CancellationToken);
        Assert.True(await coordinator.RequestRegionAsync(new PixelRect(12, 14, 3, 4), TestContext.Current.CancellationToken));
        PixelBuffer image = coordinator.State.Image!;
        DecodedImageRegion region = coordinator.State.Region!;
        int decodes = decoder.PreviewCount;
        Assert.True(await model.ChangeSortAsync(BrowseSortMode.Size, true));
        Assert.Equal([files.Second, files.Third, files.First], session.Items);
        Assert.Equal(files.First, model.CurrentFilePath);
        Assert.Equal("3 / 3", model.PositionText);
        Assert.Same(image, model.CurrentImage);
        Assert.Same(region, model.Presentation.Region);
        Assert.Equal(4, image.Pixels.Length);
        Assert.Equal(48, region.Image.Pixels.Length);
        Assert.Equal(decodes, decoder.PreviewCount);
        Assert.True(model.IsSortBySize);
        Assert.True(model.SortDescending);
    }

    [Fact]
    public async Task MissingRevealKeepsImageAndLateRevealCannotMessageNewImage()
    {
        using Files files = new();
        RevealService reveal = new();
        using ImageOpenCoordinator coordinator = new(new NullPicker(), new PreviewDecoder());
        using MainWindowViewModel model = new(new TestLocalization(), coordinator, new ImageBrowseSession(), () => reveal);
        Assert.True(await model.OpenPathAsync(files.First));
        await coordinator.WaitForIndexingAsync(TestContext.Current.CancellationToken);
        PixelBuffer image = model.CurrentImage!;
        Task revealing = model.RevealCurrentFileAsync();
        Assert.False(model.CanReveal);
        Assert.Equal(files.First, reveal.Path);
        reveal.Completion.SetResult(FileRevealResult.MissingFile);
        await revealing;
        Assert.Same(image, model.CurrentImage);
        Assert.Equal("File_RevealMissing", model.StatusText);

        reveal.Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        revealing = model.RevealCurrentFileAsync();
        Assert.True(await model.OpenPathAsync(files.Second));
        Assert.True(reveal.Token.IsCancellationRequested);
        reveal.Completion.SetResult(FileRevealResult.Success);
        await revealing;
        await coordinator.WaitForIndexingAsync(TestContext.Current.CancellationToken);
        Assert.Equal(files.Second, model.CurrentFilePath);
        Assert.Equal(string.Empty, model.StatusText);
        Assert.True(model.CanReveal);
    }

    [Fact]
    public void HelpCatalogMatchesEveryUniqueGestureAndContainsDeliveredActionsOnly()
    {
        HashSet<ViewerShortcutGesture> gestures = [];
        IReadOnlyList<ShortcutHelpGroup> groups = ShortcutCatalog.CreateHelpGroups(new TestLocalization());
        Assert.Equal(6, groups.Count);
        Assert.Equal(Enum.GetValues<ViewerAction>().Length, groups.Sum(group => group.Rows.Count));
        foreach (ViewerShortcutDefinition definition in ShortcutCatalog.Definitions)
        {
            foreach (ViewerShortcutGesture gesture in definition.Gestures)
            {
                Assert.True(gestures.Add(gesture));
                Assert.Equal(definition.Action, ShortcutCatalog.Match(gesture.Key, gesture.Modifiers));
            }
        }
        Assert.Equal(ViewerAction.RevealInExplorer, ShortcutCatalog.Match(Key.E, ModifierKeys.Control | ModifierKeys.Shift));
        Assert.Equal(ViewerAction.ShowShortcutHelp, ShortcutCatalog.Match(Key.F1, ModifierKeys.None));
        Assert.Equal(ViewerAction.PasteFiles, ShortcutCatalog.Match(Key.V, ModifierKeys.Control));
        Assert.Null(ShortcutCatalog.Match(Key.Left, ModifierKeys.Control));
    }

    private sealed class Files : IDisposable
    {
        public Files()
        {
            Directory.CreateDirectory(Path);
            File.WriteAllBytes(First, [1]);
            File.WriteAllBytes(Second, new byte[10]);
            File.WriteAllBytes(Third, new byte[3]);
        }
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"viewer-tools-{Guid.NewGuid():N}");
        public string First => System.IO.Path.Combine(Path, "1.png");
        public string Second => System.IO.Path.Combine(Path, "2.png");
        public string Third => System.IO.Path.Combine(Path, "10.png");
        public void Dispose() => Directory.Delete(Path, true);
    }

    private sealed class NullPicker : IImageFilePicker
    {
        public Task<string?> PickImageAsync(CancellationToken cancellationToken) => Task.FromResult<string?>(null);
    }

    private sealed class PreviewDecoder : IPreviewImageDecoder, IRegionImageDecoder
    {
        private static readonly PixelSize SourceSize = new(8000, 6000);
        public int PreviewCount { get; private set; }
        public Task<PixelBuffer> DecodeAsync(string path, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Sorting must not refine the main image.");
        public Task<PixelBuffer> DecodePreviewAsync(string path, PixelSize maximumSize, CancellationToken cancellationToken)
        {
            PreviewCount++;
            return Task.FromResult(new PixelBuffer(new PixelSize(1, 1), 4, new byte[4], sourceSize: SourceSize));
        }
        public Task<DecodedImageRegion> DecodeRegionAsync(string path, PixelRect region, PixelSize expectedSourceSize,
            long maximumDecodedBytes, CancellationToken cancellationToken) => Task.FromResult(new DecodedImageRegion(
                new PixelBuffer(region.Size, region.Width * 4, new byte[checked((int)region.Size.PixelCount * 4)], sourceSize: SourceSize), region));
    }

    private sealed class RevealService : IFileRevealService
    {
        public TaskCompletionSource<FileRevealResult> Completion { get; set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string? Path { get; private set; }
        public CancellationToken Token { get; private set; }
        public Task<FileRevealResult> RevealAsync(string path, CancellationToken cancellationToken = default)
        {
            Path = path;
            Token = cancellationToken;
            return Completion.Task;
        }
    }

    private sealed class TestLocalization : ILocalizationService
    {
        public event EventHandler? CultureChanged { add { } remove { } }
        public CultureInfo CurrentCulture => CultureInfo.GetCultureInfo("en-US");
        public IReadOnlyList<SupportedLanguage> SupportedLanguages { get; } = [new("en-US", "English")];
        public void Initialize() { }
        public void SetCulture(string cultureName) { }
        public string GetString(string name) => name == "Status_PositionFormat" ? "{0} / {1}" : name;
    }
}
