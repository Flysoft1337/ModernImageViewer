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

    [Fact]
    public async Task FailedOrDeletedFileKeepsDisplayAndAllowsNextNavigation()
    {
        using Files files = new();
        DelayedDecoder decoder = new();
        ImageBrowseSession session = new();
        using ImageOpenCoordinator coordinator = new(new NullPicker(), decoder);
        using MainWindowViewModel model = new(new Localization(), coordinator, session);
        Assert.True(await model.OpenPathAsync(files.Paths[0]));
        await coordinator.WaitForIndexingAsync(TestContext.Current.CancellationToken);
        PixelBuffer current = model.CurrentImage!;
        Task next = model.NextCommand.ExecuteAsync();
        File.Delete(files.Paths[1]);
        decoder.Pending[0].Completion.SetException(new FileNotFoundException());
        await next;
        Assert.Same(current, model.CurrentImage);
        Assert.Equal(ImageOpenError.FileNotFound, model.Presentation.Error);
        Assert.True(model.NextCommand.CanExecute(null));
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
