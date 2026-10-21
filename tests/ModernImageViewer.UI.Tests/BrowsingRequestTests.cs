using System.Globalization;
using System.IO;

using ModernImageViewer.Application.Browsing;
using ModernImageViewer.Application.Images;
using ModernImageViewer.Imaging;
using ModernImageViewer.UI.Localization;
using ModernImageViewer.UI.ViewModels;

namespace ModernImageViewer.UI.Tests;

public sealed class BrowsingRequestTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FolderReadFailureKeepsPictureAndListUntilRetrySucceeds(bool accessDenied)
    {
        using Files files = new();
        Exception? failure = null;
        ImageBrowseSession session = new((_, _, _) => failure is null ? files.Paths : throw failure);
        DelayedDecoder decoder = new();
        using ImageOpenCoordinator coordinator = new(new NullPicker(), decoder);
        using MainWindowViewModel model = new(new Localization(), coordinator, session);
        string directory = Path.GetDirectoryName(files.Paths[0])!;
        Assert.True(await model.OpenFolderAsync(directory));
        PixelBuffer image = model.CurrentImage!;
        IReadOnlyList<string> items = session.Items;
        model.IsSlideshowPlaying = true;
        failure = accessDenied ? new UnauthorizedAccessException() : new IOException();

        Assert.False(await model.OpenFolderAsync(directory));

        Assert.Same(image, model.CurrentImage);
        Assert.Same(items, session.Items);
        Assert.Equal(files.Paths[0], model.CurrentFilePath);
        Assert.Equal(accessDenied ? "Error_FolderAccessDenied" : "Error_FolderReadFailed", model.StatusText);
        Assert.True(model.HasStatusMessage);
        Assert.False(model.IsLoading);
        Assert.False(model.IsSlideshowPlaying);
        Assert.True(model.NextCommand.CanExecute(null));
        failure = null;
        Task<bool> retry = model.OpenFolderAsync(directory);
        await WaitUntil(() => decoder.Pending.Count == 1);
        decoder.Pending[0].Completion.SetResult(Pixels());
        Assert.True(await retry);
        Assert.False(model.HasStatusMessage);
        Assert.Throws<ObjectDisposedException>(() => image.Pixels);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RefreshReadFailureKeepsPictureAndListAndCanRecover(bool accessDenied)
    {
        using Files files = new();
        Exception? failure = null;
        ImageBrowseSession session = new((_, _, _) => failure is null ? files.Paths : throw failure);
        DelayedDecoder decoder = new();
        using ImageOpenCoordinator coordinator = new(new NullPicker(), decoder);
        using MainWindowViewModel model = new(new Localization(), coordinator, session);
        Assert.True(await model.OpenFolderAsync(Path.GetDirectoryName(files.Paths[0])!));
        PixelBuffer image = model.CurrentImage!;
        IReadOnlyList<string> items = session.Items;
        model.IsSlideshowPlaying = true;
        failure = accessDenied ? new UnauthorizedAccessException() : new IOException();

        await model.RefreshFolderAsync();

        Assert.Same(image, model.CurrentImage);
        Assert.Same(items, session.Items);
        Assert.Equal(0, session.CurrentIndex);
        Assert.False(session.IsIndexing);
        Assert.False(model.IsLoading);
        Assert.False(model.IsSlideshowPlaying);
        Assert.Equal(accessDenied ? "Error_FolderAccessDenied" : "Error_FolderReadFailed", model.StatusText);
        failure = null;
        Task retry = model.RefreshFolderAsync();
        await WaitUntil(() => decoder.Pending.Count == 1);
        decoder.Pending[0].Completion.SetResult(Pixels());
        await retry;
        Assert.False(model.HasStatusMessage);
        Assert.Equal(files.Paths, session.Items);
        Assert.True(model.NextCommand.CanExecute(null));
        Assert.Throws<ObjectDisposedException>(() => image.Pixels);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OlderFolderOrRefreshErrorCannotReplaceNewerPictureOrMessage(bool refresh)
    {
        using Files files = new();
        using ManualResetEventSlim entered = new();
        using ManualResetEventSlim release = new();
        int failOnce = 0;
        ImageBrowseSession session = new((_, _, _) =>
        {
            if (Interlocked.Exchange(ref failOnce, 0) == 1)
            {
                entered.Set();
                // An already executing directory read may return after cancellation.
                release.Wait(TestContext.Current.CancellationToken);
                throw new IOException();
            }
            return files.Paths;
        });
        DelayedDecoder decoder = new();
        using ImageOpenCoordinator coordinator = new(new NullPicker(), decoder);
        using MainWindowViewModel model = new(new Localization(), coordinator, session);
        string directory = Path.GetDirectoryName(files.Paths[0])!;
        Assert.True(await model.OpenFolderAsync(directory));
        failOnce = 1;
        Task older = refresh ? model.RefreshFolderAsync() : model.OpenFolderAsync(directory);
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
            Task<bool> newer = model.OpenPathAsync(files.Paths[2]);
            await WaitUntil(() => decoder.Pending.Count == 1);
            PixelBuffer latest = Pixels();
            decoder.Pending[0].Completion.SetResult(latest);
            Assert.True(await newer);
            model.ShowMessage("Clipboard_NoFiles");
            release.Set();
            await older;
            Assert.Same(latest, model.CurrentImage);
            Assert.Equal(files.Paths[2], model.CurrentFilePath);
            Assert.Equal("Clipboard_NoFiles", model.StatusText);
            Assert.False(model.IsLoading);
        }
        finally { release.Set(); await older; }
    }

    [Fact]
    public async Task MissingExplicitFolderReportsReadFailureAndCanBeOpenedAfterCreation()
    {
        using Files files = new();
        DelayedDecoder decoder = new();
        ImageBrowseSession session = new();
        using ImageOpenCoordinator coordinator = new(new NullPicker(), decoder);
        using MainWindowViewModel model = new(new Localization(), coordinator, session);
        Assert.True(await model.OpenPathAsync(files.Paths[0]));
        await coordinator.WaitForIndexingAsync(TestContext.Current.CancellationToken);
        PixelBuffer image = model.CurrentImage!;
        string missing = Path.Combine(Path.GetDirectoryName(files.Paths[0])!, "later");
        Assert.False(await model.OpenFolderAsync(missing));
        Assert.Equal("Error_FolderReadFailed", model.StatusText);
        Assert.Same(image, model.CurrentImage);
        Directory.CreateDirectory(missing);
        string path = Path.Combine(missing, "one.png");
        File.WriteAllBytes(path, []);
        Task<bool> retry = model.OpenFolderAsync(missing);
        await WaitUntil(() => decoder.Pending.Count == 1);
        decoder.Pending[0].Completion.SetResult(Pixels());
        Assert.True(await retry);
        Assert.Equal(path, model.CurrentFilePath);
        Assert.Equal(1, session.Count);
        Assert.False(model.HasStatusMessage);
    }

    [Fact]
    public async Task RepeatedNextAccumulatesLatestIntentWhileFirstDecodeStillRuns()
    {
        using Files files = new();
        DelayedDecoder decoder = new();
        ImageBrowseSession session = new();
        using ImageOpenCoordinator coordinator = new(new NullPicker(), decoder);
        using MainWindowViewModel model = new(new Localization(), coordinator, session);
        Assert.True(await model.OpenPathAsync(files.Paths[0]));
        await coordinator.WaitForIndexingAsync(TestContext.Current.CancellationToken);

        Task second = model.NextCommand.ExecuteAsync();
        Assert.Equal(files.Paths[1], decoder.Pending[0].Path);
        Assert.True(model.NextCommand.CanExecute(null));
        Task third = model.NextCommand.ExecuteAsync();
        Assert.Equal(files.Paths[2], decoder.Pending[1].Path);
        Task fourth = model.NextCommand.ExecuteAsync();
        Assert.Equal(files.Paths[3], decoder.Pending[2].Path);
        PixelBuffer newest = Pixels();
        decoder.Pending[2].Completion.SetResult(newest);
        await fourth;
        for (int index = 1; index >= 0; index--)
        {
            PixelBuffer stale = Pixels();
            Assert.True(decoder.Pending[index].Token.IsCancellationRequested);
            decoder.Pending[index].Completion.SetResult(stale);
            await (index == 0 ? second : third);
            Assert.Throws<ObjectDisposedException>(() => stale.Pixels);
        }
        Assert.Equal(files.Paths[3], coordinator.State.FilePath);
        Assert.Equal(3, session.CurrentIndex);
        Assert.Same(newest, model.CurrentImage);
    }

    [Fact]
    public async Task DirectionReversalUsesPendingPositionAndCancelsLaterForwardRequest()
    {
        using Files files = new();
        DelayedDecoder decoder = new();
        ImageBrowseSession session = new();
        using ImageOpenCoordinator coordinator = new(new NullPicker(), decoder);
        using MainWindowViewModel model = new(new Localization(), coordinator, session);
        Assert.True(await model.OpenPathAsync(files.Paths[0]));
        await coordinator.WaitForIndexingAsync(TestContext.Current.CancellationToken);
        Task forward = model.NextCommand.ExecuteAsync();
        Task back = model.PreviousCommand.ExecuteAsync();
        Assert.Equal(files.Paths[0], decoder.Pending[1].Path);
        decoder.Pending[1].Completion.SetResult(Pixels());
        await back;
        PixelBuffer stale = Pixels();
        decoder.Pending[0].Completion.SetResult(stale);
        await forward;
        Assert.Equal(files.Paths[0], model.CurrentFilePath);
        Assert.Throws<ObjectDisposedException>(() => stale.Pixels);
    }

    [Fact]
    public async Task RefreshInvalidatesCurrentDecodeBeforeDirectoryEnumerationFinishes()
    {
        using Files files = new();
        using ManualResetEventSlim refreshEntered = new();
        using ManualResetEventSlim releaseRefresh = new();
        int enumerations = 0;
        ImageBrowseSession session = new((_, _, token) =>
        {
            if (Interlocked.Increment(ref enumerations) > 1)
            {
                refreshEntered.Set();
                releaseRefresh.Wait(token);
            }
            return files.Paths;
        });
        DelayedDecoder decoder = new();
        using ImageOpenCoordinator coordinator = new(new NullPicker(), decoder);
        using MainWindowViewModel model = new(new Localization(), coordinator, session);
        Assert.True(await model.OpenPathAsync(files.Paths[0]));
        await coordinator.WaitForIndexingAsync(TestContext.Current.CancellationToken);
        PixelBuffer previous = model.CurrentImage!;
        Task next = model.NextCommand.ExecuteAsync();
        Task refresh = model.RefreshFolderAsync();
        Assert.True(refreshEntered.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        PixelBuffer stale = Pixels();
        decoder.Pending[0].Completion.SetResult(stale);
        await next;
        Assert.Same(previous, model.CurrentImage);
        Assert.Throws<ObjectDisposedException>(() => stale.Pixels);
        releaseRefresh.Set();
        await WaitUntil(() => decoder.Pending.Count == 2);
        Assert.Equal(files.Paths[0], decoder.Pending[1].Path);
        decoder.Pending[1].Completion.SetResult(Pixels());
        await refresh;
        Assert.Equal(files.Paths[0], model.CurrentFilePath);
    }

    [Theory]
    [InlineData(-1, false)]
    [InlineData(-1, true)]
    [InlineData(1, false)]
    [InlineData(1, true)]
    public async Task FailedOrDeletedFileKeepsDisplayAndAllowsNextNavigation(int direction, bool deleted)
    {
        using Files files = new();
        DelayedDecoder decoder = new();
        ImageBrowseSession session = new();
        using ImageOpenCoordinator coordinator = new(new NullPicker(), decoder);
        using MainWindowViewModel model = new(new Localization(), coordinator, session);
        Assert.True(await model.OpenPathAsync(files.Paths[2]));
        await coordinator.WaitForIndexingAsync(TestContext.Current.CancellationToken);
        PixelBuffer current = model.CurrentImage!;
        var command = direction > 0 ? model.NextCommand : model.PreviousCommand;
        Task next = command.ExecuteAsync();
        Assert.Equal(files.Paths[2 + direction], decoder.Pending[0].Path);
        if (deleted) { File.Delete(files.Paths[2 + direction]); }
        decoder.Pending[0].Completion.SetException(deleted ? new FileNotFoundException()
            : new ImageDecodeException(ImageOpenError.CorruptFile));
        await next;
        Assert.Same(current, model.CurrentImage);
        Assert.Equal(files.Paths[2], model.CurrentFilePath);
        Assert.Equal(2, session.CurrentIndex);
        Assert.Equal(deleted ? ImageOpenError.FileNotFound : ImageOpenError.CorruptFile, model.Presentation.Error);
        Assert.True(command.CanExecute(null));
        Task continued = command.ExecuteAsync();
        try { Assert.Equal(files.Paths[2 + 2 * direction], decoder.Pending[1].Path); }
        finally { decoder.Pending[1].Completion.TrySetResult(Pixels()); await continued; }
        Assert.Equal(files.Paths[2 + 2 * direction], model.CurrentFilePath);
        Assert.Equal(ImageOpenStatus.Loaded, model.Presentation.Status);
        Assert.Throws<ObjectDisposedException>(() => current.Pixels);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RefreshOrSortAfterFailureReturnsNavigationToDisplayedImage(bool refresh)
    {
        using Files files = new();
        DelayedDecoder decoder = new();
        ImageBrowseSession session = new();
        using ImageOpenCoordinator coordinator = new(new NullPicker(), decoder);
        using MainWindowViewModel model = new(new Localization(), coordinator, session);
        Assert.True(await model.OpenPathAsync(files.Paths[2]));
        await coordinator.WaitForIndexingAsync(TestContext.Current.CancellationToken);
        Task failed = model.NextCommand.ExecuteAsync();
        File.Delete(files.Paths[3]);
        decoder.Pending[0].Completion.SetException(new FileNotFoundException());
        await failed;

        if (refresh)
        {
            Task reloading = model.RefreshFolderAsync();
            await WaitUntil(() => decoder.Pending.Count == 2);
            decoder.Pending[1].Completion.SetResult(Pixels());
            await reloading;
            Assert.DoesNotContain(files.Paths[3], session.Items);
        }
        else
        {
            Assert.True(await model.ChangeSortAsync(BrowseSortMode.Name, true));
        }
        Task next = model.NextCommand.ExecuteAsync();
        var pending = decoder.Pending[^1];
        try { Assert.Equal(files.Paths[refresh ? 4 : 1], pending.Path); }
        finally { pending.Completion.TrySetResult(Pixels()); await next; }
        Assert.Equal(files.Paths[refresh ? 4 : 1], model.CurrentFilePath);
    }

    private static async Task WaitUntil(Func<bool> completed)
    {
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        while (!completed()) { await Task.Delay(10, timeout.Token); }
    }
    private static PixelBuffer Pixels() => new(new(1, 1), 4, new byte[4]);
    private sealed class NullPicker : IImageFilePicker
    {
        public Task<string?> PickImageAsync(CancellationToken cancellationToken) => Task.FromResult<string?>(null);
    }
    private sealed class DelayedDecoder : IImageDecoder
    {
        private int _count;
        public List<(string Path, CancellationToken Token, TaskCompletionSource<PixelBuffer> Completion)> Pending { get; } = [];
        public Task<PixelBuffer> DecodeAsync(string path, CancellationToken cancellationToken)
        {
            if (_count++ == 0) { return Task.FromResult(Pixels()); }
            TaskCompletionSource<PixelBuffer> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Pending.Add((path, cancellationToken, completion));
            return completion.Task;
        }
    }
    private sealed class Localization : ILocalizationService
    {
        public event EventHandler? CultureChanged { add { } remove { } }
        public CultureInfo CurrentCulture => CultureInfo.GetCultureInfo("en-US");
        public IReadOnlyList<SupportedLanguage> SupportedLanguages { get; } = [new("en-US", "English")];
        public string GetString(string name) => name;
        public void Initialize() { }
        public void SetCulture(string cultureName) { }
    }
    private sealed class Files : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "miv-browse-tests-" + Guid.NewGuid().ToString("N"));
        public string[] Paths { get; }
        public Files()
        {
            Directory.CreateDirectory(_directory);
            Paths = Enumerable.Range(0, 5).Select(index => Path.Combine(_directory, $"image-{index}.png")).ToArray();
            foreach (string path in Paths) { File.WriteAllBytes(path, [1]); }
        }
        public void Dispose() => Directory.Delete(_directory, true);
    }
}
