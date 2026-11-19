using System.Globalization;
using System.IO;
using System.Windows.Input;

using ModernImageViewer.Application.Browsing;
using ModernImageViewer.Application.Images;
using ModernImageViewer.Application.Integration;
using ModernImageViewer.Codecs;
using ModernImageViewer.Imaging;
using ModernImageViewer.Platform.Integration;
using ModernImageViewer.UI.Localization;
using ModernImageViewer.UI.Tests.Fixtures;
using ModernImageViewer.UI.ViewModels;

namespace ModernImageViewer.UI.Tests;

[Collection("Region decoder slot")]
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
    public async Task RenameRespectsDirectorySortAndNeverOverwrites()
    {
        using Files files = new();
        ImageBrowseSession session = new();
        using ImageOpenCoordinator coordinator = new(new NullPicker(), new PreviewDecoder());
        using MainWindowViewModel model = new(new TestLocalization(), coordinator, session, fileOperations: new WindowsImageFileOperations());
        Assert.True(await model.OpenPathAsync(files.First));
        await coordinator.WaitForIndexingAsync(TestContext.Current.CancellationToken);
        PixelBuffer? image = model.CurrentImage;
        Assert.True(model.CanChangeFile);
        Assert.Equal("File_InvalidName", await model.RenameCurrentFileAsync(files.First, @"..\outside"));
        Assert.Equal("File_NameConflict", await model.RenameCurrentFileAsync(files.First, "2"));
        Assert.Equal([1], File.ReadAllBytes(files.First));
        Assert.Equal(new byte[10], File.ReadAllBytes(files.Second));
        Assert.Equal([files.First, files.Second, files.Third], session.Items);
        Assert.Same(image, model.CurrentImage);

        Assert.Null(await model.RenameCurrentFileAsync(files.First, "20"));
        string renamed = Path.Combine(files.Path, "20.png");
        Assert.False(File.Exists(files.First));
        Assert.Equal([1], File.ReadAllBytes(renamed));
        Assert.Equal([files.Second, files.Third, renamed], session.Items);
        Assert.Equal(renamed, model.CurrentFilePath);
        Assert.Equal("3 / 3", model.PositionText);
        Assert.Same(image, model.CurrentImage);
    }

    [Fact]
    public async Task SelectionKeepsItsOrderAfterRenameAndRecycleFindsReadableNeighbors()
    {
        using Files files = new();
        ImageBrowseSession session = new();
        PreviewDecoder decoder = new() { UnreadablePath = files.Third };
        using ImageOpenCoordinator coordinator = new(new NullPicker(), decoder);
        using MainWindowViewModel model = new(new TestLocalization(), coordinator, session, fileOperations: new TestFileOperations());
        Assert.True(await model.OpenInputsAsync([files.Second, files.First, files.Third]));
        string renamed = Path.Combine(files.Path, "Cover.png");
        Assert.Null(await model.RenameCurrentFileAsync(files.Second, "Cover"));
        string lowercase = Path.Combine(files.Path, "cover.png");
        Assert.Null(await model.RenameCurrentFileAsync(renamed, "cover"));
        Assert.True(session.IsSelection);
        Assert.Equal([lowercase, files.First, files.Third], session.Items);
        Assert.Equal(lowercase, model.CurrentFilePath);

        Assert.True(await model.OpenPathAsync(files.First));
        Assert.Null(await model.RecycleCurrentFileAsync(files.First));
        Assert.True(session.IsSelection);
        Assert.Equal([lowercase, files.Third], session.Items);
        Assert.Equal(lowercase, model.CurrentFilePath);
        Assert.True(File.Exists(files.First + ".recycled"));

        Assert.Null(await model.RecycleCurrentFileAsync(lowercase));
        Assert.False(model.HasImage);
        Assert.False(model.CanChangeFile);
        Assert.Empty(model.CurrentFilePath);
        Assert.Equal("File_RecycleNoReadableNeighbor", model.StatusText);

        decoder.UnreadablePath = null;
        Assert.True(await model.OpenPathAsync(files.Third));
        await coordinator.WaitForIndexingAsync(TestContext.Current.CancellationToken);
        Assert.Null(await model.RecycleCurrentFileAsync(files.Third));
        Assert.False(model.HasImage);
        Assert.Empty(model.BrowseItems);
        Assert.Equal("File_RecycleSucceeded", model.StatusText);
    }

    [Fact]
    public async Task FailedRecycleKeepsCurrentImageAndQueuesExternalOpen()
    {
        using Files files = new();
        ImageBrowseSession session = new();
        TaskCompletionSource<ImageFileOperationResult> response = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TestFileOperations operations = new() { Recycle = _ => response.Task };
        using ImageOpenCoordinator coordinator = new(new NullPicker(), new PreviewDecoder());
        using MainWindowViewModel model = new(new TestLocalization(), coordinator, session, fileOperations: operations);
        Assert.True(await model.OpenPathAsync(files.First));
        await coordinator.WaitForIndexingAsync(TestContext.Current.CancellationToken);
        PixelBuffer? image = model.CurrentImage;
        Task<string?> recycling = model.RecycleCurrentFileAsync(files.First);
        Assert.False(model.CanChangeFile);
        Assert.False(await model.OpenPathAsync(files.Second));
        response.SetResult(ImageFileOperationResult.Unavailable);
        Assert.Equal("File_RecycleFailed", await recycling);
        Assert.Same(image, model.CurrentImage);
        Assert.Equal([files.First, files.Second, files.Third], session.Items);

        response = new(TaskCreationOptions.RunContinuationsAsynchronously);
        recycling = model.RecycleCurrentFileAsync(files.First);
        Task<bool> opening = model.OpenInputAsync(files.Second);
        Assert.False(opening.IsCompleted);
        response.SetResult(ImageFileOperationResult.Unavailable);
        Assert.Equal("File_RecycleFailed", await recycling);
        Assert.True(await opening);
        await coordinator.WaitForIndexingAsync(TestContext.Current.CancellationToken);
        Assert.Equal(files.Second, model.CurrentFilePath);
        Assert.True(File.Exists(files.First));
    }

    [Theory]
    [InlineData(".gif")]
    [InlineData(".tiff")]
    public async Task FrameFileRenameReleasesItsHandleAndKeepsTheCurrentFrame(string extension)
    {
        using Files files = new();
        string path = Path.Combine(files.Path, "sequence" + extension);
        string occupied = Path.Combine(files.Path, "taken" + extension);
        string renamed = Path.Combine(files.Path, "renamed" + extension);
        byte[] bytes = extension == ".gif" ? AnimationCodecFixtures.Gif() : TiffFixtures.Multiple();
        File.WriteAllBytes(path, bytes);
        File.WriteAllBytes(occupied, []);
        using ImageOpenCoordinator coordinator = new(new NullPicker(), new ImageDecoder());
        using MainWindowViewModel model = new(new TestLocalization(), coordinator, new ImageBrowseSession(),
            fileOperations: new WindowsImageFileOperations());
        try
        {
            Assert.True(await model.OpenPathAsync(path));
            await coordinator.WaitForIndexingAsync(TestContext.Current.CancellationToken);
            Assert.True(await model.SeekFrameAsync(1));
            PixelBuffer? frame = model.CurrentImage;
            Assert.Equal("File_NameConflict", await model.RenameCurrentFileAsync(path, "taken"));
            Assert.Same(frame, model.CurrentImage);
            Assert.Equal(1, model.Presentation.FrameIndex);
            Assert.True(coordinator.HasFrameSession);
            Assert.Null(await model.RenameCurrentFileAsync(path, "renamed"));
            Assert.Same(frame, model.CurrentImage);
            Assert.Equal(1, model.Presentation.FrameIndex);
            Assert.Equal(renamed, model.CurrentFilePath);
            Assert.True(await model.SeekFrameAsync(0));
            Assert.Equal(bytes, File.ReadAllBytes(renamed));
            Assert.Empty(File.ReadAllBytes(occupied));
            Assert.False(File.Exists(path));
        }
        finally
        {
            await coordinator.CloseImageAsync();
        }
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
        public string? UnreadablePath { get; set; }
        public Task<PixelBuffer> DecodeAsync(string path, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Sorting must not refine the main image.");
        public Task<PixelBuffer> DecodePreviewAsync(string path, PixelSize maximumSize, CancellationToken cancellationToken)
        {
            if (path == UnreadablePath) { throw new ImageDecodeException(ImageOpenError.CorruptFile); }
            PreviewCount++;
            return Task.FromResult(new PixelBuffer(new PixelSize(1, 1), 4, new byte[4], sourceSize: SourceSize));
        }
        public Task<DecodedImageRegion> DecodeRegionAsync(string path, PixelRect region, PixelSize expectedSourceSize,
            long maximumDecodedBytes, CancellationToken cancellationToken) => Task.FromResult(new DecodedImageRegion(
                new PixelBuffer(region.Size, region.Width * 4, new byte[checked((int)region.Size.PixelCount * 4)], sourceSize: SourceSize), region));
    }

    private sealed class TestFileOperations : IImageFileOperations
    {
        public Func<string, Task<ImageFileOperationResult>>? Recycle { get; init; }
        public Task<ImageFileOperationResult> RenameAsync(string sourcePath, string destinationPath) =>
            new WindowsImageFileOperations().RenameAsync(sourcePath, destinationPath);

        public Task<ImageFileOperationResult> RecycleAsync(string path)
        {
            if (Recycle is not null) { return Recycle(path); }
            File.Move(path, path + ".recycled");
            return Task.FromResult(ImageFileOperationResult.Success);
        }
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
