using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Channels;

using ModernImageViewer.Application.Browsing;
using ModernImageViewer.Application.Images;
using ModernImageViewer.Application.Integration;
using ModernImageViewer.Imaging;
using ModernImageViewer.UI.Commands;
using ModernImageViewer.UI.Localization;
using ModernImageViewer.UI.ViewModels;

namespace ModernImageViewer.UI.Tests;

public sealed class MultiInputTests
{
    [Fact]
    public async Task SelectionPreservesOrderSkipsBadInputsAndRefreshNeverExpandsDirectories()
    {
        using ImageFiles files = new();
        string first = files.Add("第一张 & (原图).png");
        string second = files.Add("另一目录/02.jpg");
        string corrupt = files.Add("corrupt.png");
        string missing = Path.Combine(files.Directory, "missing.jpg");
        OpenRequest request = OpenRequest.Create([first, first, "https://example.com/image.png"]);
        Assert.Equal([first], request.Paths);
        Assert.Equal(1, request.RejectedCount);
        Assert.Empty(OpenRequest.Create([]).Paths);
        Assert.Throws<ArgumentException>(() => OpenRequest.Create(Enumerable.Repeat(first, OpenRequest.MaximumPaths + 1)));

        ImageBrowseSession session = new();
        using ImageOpenCoordinator coordinator = new(new FixedPicker(second), new FileDecoder());
        using MainWindowViewModel viewModel = new(new TestLocalization(), coordinator, session);
        Assert.True(await viewModel.OpenInputsAsync([corrupt, second, first, second, missing, files.Directory]));
        await coordinator.WaitForIndexingAsync(TestContext.Current.CancellationToken);
        Assert.True(session.IsSelection);
        Assert.Equal([second, first], session.Items);
        Assert.Equal(second, viewModel.CurrentFilePath);
        Assert.Equal("Browsing_Selection", viewModel.DirectoryName);
        Assert.Equal("Input_PartiallySkipped", viewModel.StatusText);
        Assert.Equal([second, first], viewModel.BrowseItems.Select(item => item.FilePath));
        Assert.True(await viewModel.OpenLastAsync());
        await coordinator.WaitForIndexingAsync(TestContext.Current.CancellationToken);
        Assert.Equal(first, viewModel.CurrentFilePath);
        viewModel.IsSlideshowPlaying = true;
        Assert.True(await viewModel.AdvanceSlideshowAsync());
        Assert.Equal(second, viewModel.CurrentFilePath);

        files.Add("另一目录/99.jpg");
        File.Delete(first);
        await viewModel.RefreshFolderAsync();
        Assert.True(session.IsSelection);
        Assert.Equal([second], session.Items);
        Assert.False(viewModel.IsSlideshowPlaying);

        files.Add("另一目录/100.png");
        Assert.True(await viewModel.OpenInputAsync(second));
        await coordinator.WaitForIndexingAsync(TestContext.Current.CancellationToken);
        Assert.False(session.IsSelection);
        Assert.Equal(3, session.Count);
        Assert.True(await viewModel.OpenInputsAsync([second, files.Add("picked.png")]));
        await coordinator.WaitForIndexingAsync(TestContext.Current.CancellationToken);
        await ((AsyncRelayCommand)viewModel.OpenCommand).ExecuteAsync();
        await coordinator.WaitForIndexingAsync(TestContext.Current.CancellationToken);
        Assert.False(session.IsSelection);
    }

    [Fact]
    public async Task InvalidAndEntirelyCorruptInputsKeepOldImageAndSelection()
    {
        using ImageFiles files = new();
        string first = files.Add("01.png");
        string second = files.Add("02.jpg");
        string corrupt = files.Add("corrupt.png");
        ImageBrowseSession session = new();
        using ImageOpenCoordinator coordinator = new(new FixedPicker(null), new FileDecoder());
        using MainWindowViewModel viewModel = new(new TestLocalization(), coordinator, session);
        Assert.True(await viewModel.OpenInputsAsync([second, first]));
        await coordinator.WaitForIndexingAsync(TestContext.Current.CancellationToken);
        PixelBuffer? image = viewModel.CurrentImage;

        Assert.False(await viewModel.OpenInputsAsync(["https://example.com/image.png", Path.Combine(files.Directory, "missing.jpg")]));
        Assert.Equal("Input_NoSupportedFiles", viewModel.StatusText);
        Assert.False(await viewModel.OpenInputsAsync([corrupt, files.Add("other-corrupt.jpg")]));
        Assert.True(session.IsSelection);
        Assert.Equal([second, first], session.Items);
        Assert.Equal(second, session.CurrentPath);
        Assert.Same(image, viewModel.CurrentImage);
        Assert.Equal(4, image!.Pixels.Length);
        Assert.Equal(ImageOpenError.CorruptFile, coordinator.State.Error);
    }

    [Fact]
    public async Task LateSelectionCompletionCannotReplaceNewRequestOrItsBrowsing()
    {
        using ImageFiles files = new();
        string oldFirst = files.Add("old/01.png");
        string oldSecond = files.Add("old/02.jpg");
        string latest = files.Add("new/03.png");
        ImageBrowseSession session = new();
        ControlledDecoder decoder = new();
        using ImageOpenCoordinator coordinator = new(new FixedPicker(null), decoder);
        using MainWindowViewModel viewModel = new(new TestLocalization(), coordinator, session);
        Task<bool> older = viewModel.OpenInputsAsync([oldSecond, oldFirst]);
        TaskCompletionSource<PixelBuffer> oldDecode = await decoder.Requests.Reader.ReadAsync(TestContext.Current.CancellationToken);
        Task<bool> newer = viewModel.OpenInputsAsync([latest]);
        TaskCompletionSource<PixelBuffer> newDecode = await decoder.Requests.Reader.ReadAsync(TestContext.Current.CancellationToken);
        PixelBuffer latestImage = CreateImage();
        newDecode.SetResult(latestImage);
        Assert.True(await newer);
        await coordinator.WaitForIndexingAsync(TestContext.Current.CancellationToken);
        PixelBuffer stale = CreateImage();
        oldDecode.SetResult(stale);

        Assert.False(await older);
        Assert.Throws<ObjectDisposedException>(() => stale.Pixels);
        Assert.Same(latestImage, viewModel.CurrentImage);
        Assert.Equal(latest, session.CurrentPath);
        Assert.False(session.IsSelection);
        Assert.Equal([latest], session.Items);
    }

    [Fact]
    public async Task ClipboardFilesReuseSelectionAndFailuresPreserveCurrentImage()
    {
        using ImageFiles files = new();
        string first = files.Add("第一张.png");
        string second = files.Add("另一目录/02.jpg");
        string corrupt = files.Add("corrupt.png");
        ClipboardFiles clipboard = new();
        ImageBrowseSession session = new();
        using ImageOpenCoordinator coordinator = new(new FixedPicker(null), new FileDecoder());
        using MainWindowViewModel model = new(new TestLocalization(), coordinator, session, clipboardFiles: clipboard);
        clipboard.Paths = [corrupt, second, first, second, files.Directory, files.Add("unsupported.txt")];
        await model.PasteFilesCommand.ExecuteAsync();
        await coordinator.WaitForIndexingAsync(TestContext.Current.CancellationToken);
        Assert.Equal([second, first], session.Items);
        Assert.True(session.IsSelection);
        Assert.Equal(second, model.CurrentFilePath);
        Assert.Equal("Input_PartiallySkipped", model.StatusText);
        PixelBuffer image = model.CurrentImage!;

        clipboard.Paths = [];
        await model.PasteFilesCommand.ExecuteAsync();
        Assert.Equal("Clipboard_NoFiles", model.StatusText);
        clipboard.Error = Marshal.GetExceptionForHR(unchecked((int)0x800401D0));
        await model.PasteFilesCommand.ExecuteAsync();
        Assert.Equal("Error_ClipboardBusy", model.StatusText);
        clipboard.Error = new ArgumentException("Too many files");
        await model.PasteFilesCommand.ExecuteAsync();
        Assert.Equal("Input_TooManyFiles", model.StatusText);
        clipboard.Error = null;
        clipboard.Paths = [files.Add("unsupported.txt"), Path.Combine(files.Directory, "missing.png")];
        await model.PasteFilesCommand.ExecuteAsync();
        Assert.Equal("Input_NoSupportedFiles", model.StatusText);
        clipboard.Paths = [corrupt];
        await model.PasteFilesCommand.ExecuteAsync();
        Assert.Equal(ImageOpenError.CorruptFile, coordinator.State.Error);
        Assert.Same(image, model.CurrentImage);
        Assert.Equal([second, first], session.Items);

        clipboard.Paths = [first];
        await model.PasteFilesCommand.ExecuteAsync(); // Retrying a previously failed paste succeeds.
        await coordinator.WaitForIndexingAsync(TestContext.Current.CancellationToken);
        Assert.Equal(first, model.CurrentFilePath);
        Assert.False(session.IsSelection);
        Assert.False(model.IsSlideshowPlaying);
    }

    private sealed class ClipboardFiles : IClipboardFileService
    {
        public IReadOnlyList<string> Paths { get; set; } = [];
        public Exception? Error { get; set; }
        public IReadOnlyList<string> ReadFiles() => Error is null ? Paths : throw Error;
    }

    private static PixelBuffer CreateImage() => new(new PixelSize(1, 1), 4, new byte[4]);

    private sealed class FixedPicker(string? path) : IImageFilePicker
    {
        public Task<string?> PickImageAsync(CancellationToken cancellationToken) => Task.FromResult(path);
    }

    private sealed class FileDecoder : IImageDecoder
    {
        public Task<PixelBuffer> DecodeAsync(string path, CancellationToken cancellationToken) =>
            Path.GetFileName(path).Contains("corrupt", StringComparison.Ordinal)
                ? Task.FromException<PixelBuffer>(new ImageDecodeException(ImageOpenError.CorruptFile))
                : File.Exists(path) ? Task.FromResult(CreateImage()) : Task.FromException<PixelBuffer>(new FileNotFoundException());
    }

    private sealed class ControlledDecoder : IImageDecoder
    {
        public Channel<TaskCompletionSource<PixelBuffer>> Requests { get; } = Channel.CreateUnbounded<TaskCompletionSource<PixelBuffer>>();

        public Task<PixelBuffer> DecodeAsync(string path, CancellationToken cancellationToken)
        {
            TaskCompletionSource<PixelBuffer> request = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Requests.Writer.TryWrite(request);
            return request.Task;
        }
    }

    private sealed class TestLocalization : ILocalizationService
    {
        public event EventHandler? CultureChanged { add { } remove { } }
        public CultureInfo CurrentCulture => CultureInfo.GetCultureInfo("en-US");
        public IReadOnlyList<SupportedLanguage> SupportedLanguages { get; } = [new("en-US", "English")];
        public void Initialize() { }
        public void SetCulture(string cultureName) { }
        public string GetString(string name) => name;
    }

    private sealed class ImageFiles : IDisposable
    {
        public string Directory { get; } = Path.Combine(Path.GetTempPath(), $"ModernImageViewer-selection-{Guid.NewGuid():N}");

        public string Add(string name)
        {
            string path = Path.GetFullPath(Path.Combine(Directory, name));
            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, []);
            return path;
        }

        public void Dispose() => System.IO.Directory.Delete(Directory, recursive: true);
    }
}
