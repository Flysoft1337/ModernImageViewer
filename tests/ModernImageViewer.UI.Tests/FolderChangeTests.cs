using System.IO;
using System.Windows.Threading;

using ModernImageViewer.Application.Browsing;
using ModernImageViewer.Application.Images;
using ModernImageViewer.Application.Settings;
using ModernImageViewer.Imaging;
using ModernImageViewer.UI.Localization;
using ModernImageViewer.UI.ViewModels;

namespace ModernImageViewer.UI.Tests;

public sealed class FolderChangeTests
{
    [Fact]
    public Task NativeEventsKeepCurrentImageAndFollowNameChangesUntilDirectorySwitch() => RunOnDispatcherAsync(async () =>
    {
        using TestFolders folders = new();
        Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
        using ImageOpenCoordinator coordinator = new(new NullPicker(), new TestDecoder());
        ImageBrowseSession session = new();
        using MainWindowViewModel model = new(new LocalizationService(new TestSettings()), coordinator, session);
        using FolderChangeMonitor monitor = new();
        model.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is null or "") { monitor.Update(model, dispatcher); }
        };

        Assert.False(await model.OpenFolderAsync(folders.First));
        Assert.True(model.CanRefreshFolder);
        string current = CreateFile(folders.First, "20.png");
        await WaitUntilAsync(() => model.CurrentFilePath == current);
        PixelBuffer? original = model.CurrentImage;
        Guid? identity = model.Presentation.Source?.Identity;

        string earlier = CreateFile(folders.First, "10.png");
        string later = CreateFile(folders.First, "30.png");
        await WaitUntilAsync(() => session.Items.SequenceEqual([earlier, current, later]));
        Assert.Same(original, model.CurrentImage);
        Assert.Equal(1, session.CurrentIndex);

        string renamed = Path.Combine(folders.First, "25.png");
        File.Move(current, renamed);
        await WaitUntilAsync(() => model.CurrentFilePath == renamed && !model.IsLoading);
        Assert.Equal([earlier, renamed, later], session.Items);
        Assert.Same(original, model.CurrentImage);
        Assert.Equal(identity, model.Presentation.Source?.Identity);
        Assert.Equal(1, session.CurrentIndex);

        File.Delete(renamed);
        await WaitUntilAsync(() => model.CurrentFilePath == later && !model.IsLoading);
        Assert.Equal([earlier, later], session.Items);
        File.Delete(later);
        await WaitUntilAsync(() => model.CurrentFilePath == earlier && !model.IsLoading);
        File.Delete(earlier);
        await WaitUntilAsync(() => session.Count == 0 && !model.HasImage && !model.IsLoading);
        Assert.Equal(folders.First, model.WatchedDirectory);
        Assert.True(model.CanRefreshFolder);

        string returning = CreateFile(folders.First, "return.png");
        await WaitUntilAsync(() => model.CurrentFilePath == returning && !model.IsLoading);
        PixelBuffer? beforeF5 = model.CurrentImage;
        await model.RefreshFolderAsync();
        Assert.NotSame(beforeF5, model.CurrentImage);

        string elsewhere = CreateFile(folders.Second, "other.png");
        CreateFile(folders.First, "queued.png");
        Assert.True(await model.OpenFolderAsync(folders.Second));
        string next = CreateFile(folders.Second, "next.png");
        await WaitUntilAsync(() => session.Items.SequenceEqual([next, elsewhere]));
        Assert.Equal(elsewhere, model.CurrentFilePath);
        Assert.Equal(folders.Second, model.WatchedDirectory);

        Assert.True(await model.OpenInputsAsync([elsewhere, next]));
        Assert.Null(model.WatchedDirectory);
        Assert.True(session.IsSelection);
        Assert.Equal([elsewhere, next], session.Items);
    });

    [Fact]
    public async Task DeferredBatchFollowsRenameChainAndKeepsTheConfiguredSort()
    {
        using TestFolders folders = new();
        string current = CreateFile(folders.First, "10.png");
        string other = CreateFile(folders.First, "20.png");
        ImageBrowseSession session = new();
        using ImageOpenCoordinator coordinator = new(new NullPicker(), new TestDecoder());
        using MainWindowViewModel model = new(new LocalizationService(new TestSettings()), coordinator, session);
        CancellationToken token = TestContext.Current.CancellationToken;
        Assert.True(await model.OpenFolderAsync(folders.First));
        Assert.True(await model.ChangeSortAsync(BrowseSortMode.Name, descending: true));
        PixelBuffer? original = model.CurrentImage;

        string temporary = Path.Combine(folders.First, "bridge.tmp");
        string renamed = Path.Combine(folders.First, "30.png");
        File.Move(current, temporary);
        File.Move(temporary, renamed);
        BrowseFileRename[] renames = [new(current, temporary), new(temporary, renamed)];
        Assert.False(await model.RefreshWatchedDirectoryAsync(folders.First, renames, () => false, token));
        Assert.Equal([other, current], session.Items);
        Assert.Equal(current, model.CurrentFilePath);

        Assert.True(await model.RefreshWatchedDirectoryAsync(folders.First, renames, () => true, token));
        Assert.Equal([renamed, other], session.Items);
        Assert.Equal(renamed, model.CurrentFilePath);
        Assert.Equal(0, session.CurrentIndex);
        Assert.Same(original, model.CurrentImage);
    }

    [Fact]
    public async Task ReadFailureAndStaleSnapshotKeepTheDisplayedImageAndExistingList()
    {
        using TestFolders folders = new();
        string current = CreateFile(folders.First, "current.png");
        string elsewhere = CreateFile(folders.Second, "other.png");
        bool unreadable = false;
        using ManualResetEventSlim scanning = new();
        using ManualResetEventSlim release = new();
        bool pause = false;
        CancellationToken token = TestContext.Current.CancellationToken;
        ImageBrowseSession session = new((directory, _, cancellationToken) =>
        {
            if (directory == folders.First)
            {
                if (unreadable) { throw new IOException("Directory unavailable."); }
                if (pause)
                {
                    scanning.Set();
                    release.Wait(cancellationToken);
                }
            }
            return Directory.GetFiles(directory!);
        });
        using ImageOpenCoordinator coordinator = new(new NullPicker(), new TestDecoder());
        using MainWindowViewModel model = new(new LocalizationService(new TestSettings()), coordinator, session);
        Assert.True(await model.OpenFolderAsync(folders.First));
        PixelBuffer? original = model.CurrentImage;
        unreadable = true;
        Assert.True(await model.RefreshWatchedDirectoryAsync(folders.First, [], () => true, token));
        Assert.Same(original, model.CurrentImage);
        Assert.Equal([current], session.Items);
        Assert.NotEmpty(model.StatusText);

        unreadable = false;
        Assert.True(await model.RefreshWatchedDirectoryAsync(folders.First, [], () => true, token));
        Assert.Empty(model.StatusText);
        pause = true;
        CreateFile(folders.First, "new.png");
        Task<bool> refreshing = model.RefreshWatchedDirectoryAsync(folders.First, [], () => true, token);
        try
        {
            Assert.True(scanning.Wait(TimeSpan.FromSeconds(5), token));
            Assert.True(await model.OpenFolderAsync(folders.Second));
        }
        finally
        {
            release.Set();
        }
        Assert.False(await refreshing);
        Assert.Equal(elsewhere, model.CurrentFilePath);
        Assert.Equal([elsewhere], session.Items);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(8));
        while (!condition()) { await Task.Delay(20, timeout.Token); }
    }

    private static string CreateFile(string directory, string name)
    {
        string path = Path.Combine(directory, name);
        File.WriteAllBytes(path, [1, 2, 3]);
        return path;
    }

    private static Task RunOnDispatcherAsync(Func<Task> action)
    {
        TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Thread thread = new(() =>
        {
            Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.BeginInvoke(new Action(async () =>
            {
                try { await action(); completion.SetResult(); }
                catch (Exception exception) { completion.SetException(exception); }
                finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Normal); }
            }));
            Dispatcher.Run();
        })
        { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }

    private sealed class TestFolders : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), $"viewer-folder-watch-{Guid.NewGuid():N}");
        public TestFolders()
        {
            Directory.CreateDirectory(First);
            Directory.CreateDirectory(Second);
        }
        public string First => Path.Combine(_root, "first");
        public string Second => Path.Combine(_root, "second");
        public void Dispose() => Directory.Delete(_root, recursive: true);
    }

    private sealed class TestSettings : IUserSettingsService
    {
        public string? Language => null;
        public string? Theme => null;
        public void SaveLanguage(string language) { }
        public void SaveTheme(string theme) { }
    }

    private sealed class NullPicker : IImageFilePicker
    {
        public Task<string?> PickImageAsync(CancellationToken cancellationToken) => Task.FromResult<string?>(null);
    }

    private sealed class TestDecoder : IImageDecoder
    {
        public Task<PixelBuffer> DecodeAsync(string path, CancellationToken cancellationToken)
        {
            if (!File.Exists(path)) { throw new FileNotFoundException(null, path); }
            return Task.FromResult(new PixelBuffer(new(1, 1), 4, new byte[4]));
        }
    }
}
