using System.Globalization;
using System.IO;
using System.Reflection;
using System.Resources;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

using ModernImageViewer.Application.Browsing;
using ModernImageViewer.Application.Images;
using ModernImageViewer.Application.Integration;
using ModernImageViewer.Application.Settings;
using ModernImageViewer.Imaging;
using ModernImageViewer.UI.Controls;
using ModernImageViewer.UI.Localization;
using ModernImageViewer.UI.Themes;
using ModernImageViewer.UI.ViewModels;

namespace ModernImageViewer.UI.Tests;

[Collection("Pixel lifetime")]
public sealed class FrameBrowsingUiTests
{
    [Fact]
    public async Task StaticImagesHaveNoControllerAndFrameCommandsDoNotDecode()
    {
        Decoder decoder = new((IImageFrameSession?)null);
        using ImageOpenCoordinator coordinator = new(new Picker(), decoder);
        using MainWindowViewModel model = new(new Localization(), coordinator, new());
        Assert.True(await model.OpenPathAsync("static.png"));
        await coordinator.WaitForIndexingAsync(TestContext.Current.CancellationToken);
        Assert.False(model.HasFrameSequence);
        Assert.False(model.IsAnimationPlaying);
        Assert.Equal(0, model.ActiveFrameControllers);
        model.ToggleAnimation();
        model.RestartAnimation();
        await model.PulseAnimationAsync();
        Assert.False(await model.SeekFrameAsync(0));
        Assert.False(await model.MoveFrameAsync(1));
        Assert.Equal(1, decoder.StaticCalls);
        Assert.Equal((byte)77, model.CurrentImage!.Pixels.Span[2]);
    }

    [Theory]
    [InlineData(ImageSequenceKind.Animation)]
    [InlineData(ImageSequenceKind.Pages)]
    public async Task RapidFrameNavigationUsesPendingIntentAndLeavesFolderUntouched(ImageSequenceKind kind)
    {
        Session frames = new(kind);
        ImageBrowseSession browse = new();
        using ImageOpenCoordinator coordinator = new(new Picker(), new Decoder(frames));
        using MainWindowViewModel model = new(new Localization(), coordinator, browse);
        string[] files = [Path.GetFullPath("frames.tiff"), Path.GetFullPath("next.png")];
        Assert.True(await coordinator.OpenCandidatesAsync(files, browse, selection: true, cancellationToken: TestContext.Current.CancellationToken));
        string position = model.PositionText;
        BrowseItem[] items = model.BrowseItems.ToArray();
        frames.HoldFrames = true;
        Task<bool> first = model.MoveFrameAsync(1);
        Task<bool> second = model.MoveFrameAsync(1);
        Assert.Equal<int>([0, 1, 2], frames.Requests.Select(request => request.Index));
        Assert.False(model.IsAnimationPlaying);
        PixelBuffer latest = frames.Requests[2].Complete();
        Assert.True(await second);
        PixelBuffer stale = frames.Requests[1].Complete();
        Assert.False(await first);
        Assert.Throws<ObjectDisposedException>(() => stale.Pixels);
        Assert.Equal(2, model.Presentation.FrameIndex);
        Assert.Same(latest, model.CurrentImage);
        Assert.Equal((byte)170, latest.Pixels.Span[2]);
        Assert.Equal(position, model.PositionText);
        Assert.Equal(items, model.BrowseItems);
        Assert.Equal(files, browse.Items);
        Assert.Equal(files[0], browse.CurrentPath);
        Assert.True(model.NextCommand.CanExecute(null));
        Assert.Equal(kind == ImageSequenceKind.Animation, model.CanMoveNextFrame);
        Assert.False(model.CanEditStatic);
    }

    [Fact]
    public async Task F5InvalidatesSessionAndPlaybackBeforeFolderRefreshAndDropsLateFrame()
    {
        using ManualResetEventSlim entered = new();
        using ManualResetEventSlim release = new();
        int enumerations = 0;
        string[] paths = [Path.GetFullPath("frames.gif"), Path.GetFullPath("next.png")];
        ImageBrowseSession browse = new((_, _, token) =>
        {
            if (Interlocked.Increment(ref enumerations) > 1)
            {
                entered.Set();
                release.Wait(token);
            }
            return paths;
        });
        Session old = new(ImageSequenceKind.Animation);
        Session replacement = new(ImageSequenceKind.Animation);
        using ImageOpenCoordinator coordinator = new(new Picker(), new Decoder(old, replacement));
        using MainWindowViewModel model = new(new Localization(), coordinator, browse);
        Assert.True(await model.OpenPathAsync(paths[0]));
        await coordinator.WaitForIndexingAsync(TestContext.Current.CancellationToken);
        PixelBuffer current = model.CurrentImage!;
        old.HoldFrames = true;
        Task<bool> frame = model.SeekFrameAsync(1);
        Task refresh = model.RefreshFolderAsync();
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
            Assert.True(old.Disposed);
            Assert.True(old.Requests[1].Token.IsCancellationRequested);
            Assert.False(coordinator.HasFrameSession);
            Assert.Equal(0, model.ActiveFrameControllers);
            Assert.False(model.IsAnimationPlaying);
            PixelBuffer stale = old.Requests[1].Complete();
            Assert.False(await frame);
            Assert.Throws<ObjectDisposedException>(() => stale.Pixels);
            Assert.Same(current, model.CurrentImage);
            Assert.Equal(0, model.Presentation.FrameIndex);
        }
        finally { release.Set(); }
        await refresh.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Throws<ObjectDisposedException>(() => current.Pixels);
        Assert.True(coordinator.HasFrameSession);
        Assert.Equal(1, model.ActiveFrameControllers);
        Assert.Equal(paths[0], model.CurrentFilePath);
        Assert.Equal((byte)30, model.CurrentImage!.Pixels.Span[2]);
    }

    [Fact]
    public async Task NewAnimationDuringInitialDecodeStartsOnlyNewPlaybackController()
    {
        Session first = new(ImageSequenceKind.Animation) { HoldFrames = true };
        Session next = new(ImageSequenceKind.Animation);
        using ImageOpenCoordinator coordinator = new(new Picker(), new Decoder(first, next));
        using MainWindowViewModel model = new(new Localization(), coordinator, new());
        Task<bool> oldOpen = model.OpenPathAsync("first.gif");
        Assert.Equal(0, model.ActiveFrameControllers);
        Assert.True(await model.OpenPathAsync("next.gif"));
        Assert.Equal(1, model.ActiveFrameControllers);
        FramePlaybackController controller = FrameViewportTests.Field<FramePlaybackController>(model, "_framePlayback")!;
        PixelBuffer late = first.Requests[0].Complete();
        Assert.False(await oldOpen);
        Assert.True(first.Disposed);
        Assert.Throws<ObjectDisposedException>(() => late.Pixels);
        Assert.Same(controller, FrameViewportTests.Field<FramePlaybackController>(model, "_framePlayback"));
        Assert.Equal(Path.GetFullPath("next.gif"), model.CurrentFilePath);
        Assert.Equal((byte)30, model.CurrentImage!.Pixels.Span[2]);
        model.Dispose();
        Assert.Equal(0, model.ActiveFrameControllers);
        Assert.False(model.IsAnimationPlaying);
    }

    [Fact]
    public async Task FrameFileIndexCompletionUpdatesFolderItemsCommandsAndStatus()
    {
        using ManualResetEventSlim entered = new();
        using ManualResetEventSlim release = new();
        string[] paths = [Path.GetFullPath("frames.gif"), Path.GetFullPath("next.png")];
        ImageBrowseSession browse = new((_, _, token) =>
        {
            entered.Set();
            release.Wait(token);
            return paths;
        });
        using ImageOpenCoordinator coordinator = new(new Picker(), new Decoder(new Session(ImageSequenceKind.Animation)));
        using MainWindowViewModel model = new(new Localization(), coordinator, browse);
        Assert.True(await model.OpenPathAsync(paths[0]));
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
            Assert.True(coordinator.IsIndexing);
            Assert.False(model.NextCommand.CanExecute(null));
            Assert.Single(model.BrowseItems);
            List<string?> changes = [];
            model.PropertyChanged += (_, args) => changes.Add(args.PropertyName);
            release.Set();
            await coordinator.WaitForIndexingAsync(TestContext.Current.CancellationToken);
            Assert.False(coordinator.IsIndexing);
            Assert.Equal(paths, model.BrowseItems.Select(item => item.FilePath));
            Assert.True(model.NextCommand.CanExecute(null));
            Assert.True(model.CanPlaySlideshow);
            Assert.Equal("1 / 2", model.PositionText);
            Assert.Equal(string.Empty, model.StatusText);
            Assert.Contains(changes, property => string.IsNullOrEmpty(property) || property == nameof(MainWindowViewModel.BrowseItems));
            Assert.Contains(changes, property => string.IsNullOrEmpty(property) || property == nameof(MainWindowViewModel.CanMoveNext));
            Assert.Contains(changes, property => string.IsNullOrEmpty(property) || property == nameof(MainWindowViewModel.StatusText));
            Assert.Equal((byte)30, model.CurrentImage!.Pixels.Span[2]);
        }
        finally { release.Set(); }
    }

    [Fact]
    public async Task PlaybackPulseHasControlledClockSkipsFramesAndSuspensionDoesNotDecode()
    {
        Session frames = new(ImageSequenceKind.Animation);
        using ImageOpenCoordinator coordinator = new(new Picker(), new Decoder(frames));
        using MainWindowViewModel model = new(new Localization(), coordinator, new());
        Assert.True(await model.OpenPathAsync("frames.gif"));
        long now = 0;
        FramePlaybackController clocked = new(frames.Info, () => now);
        typeof(MainWindowViewModel).GetField("_framePlayback", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(model, clocked);
        clocked.Play();
        now = 2500;
        await model.PulseAnimationAsync();
        Assert.Equal<int>([0, 2], frames.Requests.Select(request => request.Index));
        Assert.Equal(2, model.Presentation.FrameIndex);
        Assert.Equal((byte)170, model.CurrentImage!.Pixels.Span[2]);
        model.SetFramePresentationAvailable(false);
        now = 50_000;
        await model.PulseAnimationAsync();
        Assert.Equal(2, frames.Requests.Count);
        model.SetFramePresentationAvailable(true);
        await model.PulseAnimationAsync();
        Assert.Equal(2, frames.Requests.Count);
        model.ToggleAnimation();
        Assert.False(model.IsAnimationPlaying);
        now += 4000;
        await model.PulseAnimationAsync();
        Assert.Equal(2, frames.Requests.Count);
    }

    [Fact]
    public async Task PauseDisplaysCurrentTimelineFrameAndRestartDisplaysZeroWhileSuspended()
    {
        Session frames = new(ImageSequenceKind.Animation);
        using ImageOpenCoordinator coordinator = new(new Picker(), new Decoder(frames));
        using MainWindowViewModel model = new(new Localization(), coordinator, new());
        Assert.True(await model.OpenPathAsync("frames.gif"));
        long now = 0;
        FramePlaybackController clocked = new(frames.Info, () => now);
        typeof(MainWindowViewModel).GetField("_framePlayback", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(model, clocked);
        clocked.Play();
        now = 2500;
        model.ToggleAnimation();
        Assert.False(model.IsAnimationPlaying);
        Assert.Equal(2, model.Presentation.FrameIndex);
        Assert.Equal((byte)170, model.CurrentImage!.Pixels.Span[2]);
        model.SetFramePresentationAvailable(false);
        model.RestartAnimation();
        Assert.Equal(0, model.Presentation.FrameIndex);
        Assert.Equal((byte)30, model.CurrentImage!.Pixels.Span[2]);
        Assert.True(clocked.IsSuspended);
        int reads = frames.Requests.Count;
        now += 10_000;
        await model.PulseAnimationAsync();
        Assert.Equal(reads, frames.Requests.Count);
    }

    [Fact]
    public async Task BudgetLimitedSequenceFallbackShowsPixelsButCannotEnterStaticEditing()
    {
        Decoder decoder = new((IImageFrameSession?)null) { LimitSessionOpen = true };
        using ImageOpenCoordinator coordinator = new(new Picker(), decoder);
        using MainWindowViewModel model = new(new Localization(), coordinator, new());
        Assert.True(await model.OpenPathAsync("limited.gif"));
        await coordinator.WaitForIndexingAsync(TestContext.Current.CancellationToken);
        Assert.True(model.HasImage);
        Assert.True(model.Presentation.IsSequenceUnavailable);
        Assert.Equal(ImageOpenError.ImageTooLarge, model.Presentation.RefinementError);
        Assert.False(coordinator.HasFrameSession);
        Assert.False(model.HasFrameSequence);
        Assert.False(model.CanEditStatic);
        Assert.Equal(0, model.ActiveFrameControllers);
        Assert.Equal((byte)77, model.CurrentImage!.Pixels.Span[2]);
        Assert.True(await model.OpenPathAsync("static.png"));
        await coordinator.WaitForIndexingAsync(TestContext.Current.CancellationToken);
        Assert.False(model.Presentation.IsSequenceUnavailable);
        Assert.True(model.CanEditStatic);
        Assert.Equal((byte)77, model.CurrentImage!.Pixels.Span[2]);
    }

    [Fact]
    public async Task VmPulseWaitsOutPreviewUpgradeThenShowsSharpNextFrame()
    {
        Session frames = new(ImageSequenceKind.Animation);
        using ImageOpenCoordinator coordinator = new(new Picker(), new Decoder(frames));
        coordinator.SetPreviewTarget(new(80, 40));
        using MainWindowViewModel model = new(new Localization(), coordinator, new());
        Assert.True(await model.OpenPathAsync("frames.gif"));
        await coordinator.WaitForIndexingAsync(TestContext.Current.CancellationToken);
        long now = 0;
        FramePlaybackController playback = new(frames.Info, () => now);
        typeof(MainWindowViewModel).GetField("_framePlayback", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(model, playback);
        playback.Play();
        frames.HoldFrames = true;
        Task<bool> upgrade = model.UpgradePreviewAsync(new(400, 200), default);
        now = 2500;
        await model.PulseAnimationAsync();
        Assert.Equal(2, frames.Requests.Count);
        Assert.False(frames.Requests[1].Token.IsCancellationRequested);
        frames.Requests[1].Complete();
        Assert.True(await upgrade);
        Assert.Equal(new PixelSize(400, 200), model.CurrentImage!.Size);
        frames.HoldFrames = false;
        await model.PulseAnimationAsync();
        Assert.Equal(2, model.Presentation.FrameIndex);
        Assert.Equal(new PixelSize(400, 200), model.CurrentImage!.Size);
        Assert.Equal((byte)170, model.CurrentImage.Pixels.Span[2]);
        Assert.True(model.IsAnimationPlaying);
    }

    // Called by the existing window smoke test on its sole Application dispatcher.
    internal static void VerifyCompactWindowScreenshotsAndTimers()
    {
        Assert.NotNull(System.Windows.Application.Current);
        Assert.True(System.Windows.Application.Current.Dispatcher.CheckAccess());
        MainWindow? window = null;
        try
        {
            Settings settings = new();
            LocalizationService localization = new(settings);
            Session animation = new(ImageSequenceKind.Animation);
            Session pages = new(ImageSequenceKind.Pages);
            Session closing = new(ImageSequenceKind.Animation);
            using ImageOpenCoordinator coordinator = new(new Picker(), new Decoder(null, animation, pages, null, closing));
            ImageBrowseSession browse = new();
            using MainWindowViewModel model = new(localization, coordinator, browse, imageClipboard: new Clipboard());
            using ThemeService themes = new();
            window = new(model, themes) { Width = 720, Height = 480 };
            window.Show();
            window.Activate();
            Drain(window);
            OpenSelection(coordinator, browse, "static.png");
            Drain(window);
            Assert.Null(FrameViewportTests.Field<DispatcherTimer>(window, "_frameTimer"));
            Assert.Equal(0, model.ActiveFrameControllers);
            OpenSelection(coordinator, browse, "frames.gif");
            Drain(window);
            Assert.True(window.IsActive);
            DispatcherTimer frameTimer = FrameViewportTests.Field<DispatcherTimer>(window, "_frameTimer")!;
            Assert.NotNull(frameTimer);
            Assert.True(frameTimer.IsEnabled);
            VerifyContinuousFramesKeepPreviewTimer(window, model, animation);
            VerifyFrameIndexEditing(window, model, animation, frameTimer);
            ((ImageViewport)window.FindName("Viewport")).Focus();
            KeyEventArgs f6 = new(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window), 0, Key.F6)
            {
                RoutedEvent = Keyboard.PreviewKeyDownEvent
            };
            Invoke(window, "OnPreviewKeyDown", window, f6);
            Assert.True(f6.Handled);
            DispatcherTimer slideshow = FrameViewportTests.Field<DispatcherTimer>(window, "_slideshowTimer")!;
            Assert.True(slideshow.IsEnabled);
            object operation = TimerOperation(slideshow);
            Assert.True(model.SeekFrameAsync(1).GetAwaiter().GetResult());
            Drain(window);
            model.UpdateScale(1.5);
            model.SetFramePresentationAvailable(false);
            Assert.Same(operation, TimerOperation(slideshow));
            Assert.True(slideshow.IsEnabled);
            Assert.False(frameTimer.IsEnabled);
            Assert.False(model.IsAnimationPlaying); // Seeking pauses playback.
            model.IsSlideshowPlaying = false;
            ((ImageViewport)window.FindName("Viewport")).Fit();
            VerifyInformationLayout(window, model);
            Capture(window, themes, localization, model, "animation", 100);
            DispatcherTimer previewTimer = FrameViewportTests.Field<DispatcherTimer>(window, "_previewTimer")!;
            Invoke(window, "QueuePreviewUpgrade");
            object oldPreviewOperation = TimerOperation(previewTimer);
            OpenSelection(coordinator, browse, "pages.tiff");
            Assert.NotSame(oldPreviewOperation, TimerOperation(previewTimer));
            Drain(window);
            Assert.Equal(0, model.ActiveFrameControllers);
            Assert.True(model.SeekFrameAsync(2).GetAwaiter().GetResult());
            Drain(window);
            VerifyInformationLayout(window, model);
            Capture(window, themes, localization, model, "pages", 170);
            OpenSelection(coordinator, browse, "static.png");
            Drain(window);
            Assert.Null(FrameViewportTests.Field<DispatcherTimer>(window, "_frameTimer"));
            Assert.False(frameTimer.IsEnabled);
            Assert.Equal(Visibility.Collapsed, ((FrameworkElement)window.FindName("FrameControls")).Visibility);
            OpenSelection(coordinator, browse, "closing.gif");
            Drain(window);
            closing.HoldFrames = true;
            Task<bool> pending = model.SeekFrameAsync(1);
            window.Close();
            Assert.False(slideshow.IsEnabled);
            Assert.True(closing.Disposed);
            Assert.True(closing.Requests[1].Token.IsCancellationRequested);
            PixelBuffer late = closing.Requests[1].Complete();
            Assert.False(pending.GetAwaiter().GetResult());
            Assert.Throws<ObjectDisposedException>(() => late.Pixels);
            Assert.Null(FrameViewportTests.Field<DispatcherTimer>(window, "_frameTimer"));
            Assert.Equal(0, model.ActiveFrameControllers);
        }
        finally { window?.Close(); }
    }

    private static void VerifyContinuousFramesKeepPreviewTimer(MainWindow window, MainWindowViewModel model, Session frames)
    {
        DispatcherTimer timer = FrameViewportTests.Field<DispatcherTimer>(window, "_previewTimer")!;
        TimeSpan interval = timer.Interval;
        timer.Interval = TimeSpan.FromMinutes(1);
        Invoke(window, "QueuePreviewUpgrade");
        object pending = TimerOperation(timer);
        Assert.NotNull(pending);
        long now = 0;
        FramePlaybackController playback = new(frames.Info, () => now);
        typeof(MainWindowViewModel).GetField("_framePlayback", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(model, playback);
        playback.Play();
        try
        {
            for (int tick = 1; tick <= 6; tick++)
            {
                now = tick * 1000;
                model.PulseAnimationAsync().GetAwaiter().GetResult();
                Drain(window);
                Assert.Equal(tick % 3, model.Presentation.FrameIndex);
                Assert.Equal((byte)(30 + tick % 3 * 70), model.CurrentImage!.Pixels.Span[2]);
                Assert.True(timer.IsEnabled);
                Assert.Same(pending, TimerOperation(timer));
            }
            window.Width = 760;
            Drain(window);
            Assert.True(timer.IsEnabled);
            Assert.NotSame(pending, TimerOperation(timer));
        }
        finally
        {
            window.Width = 720;
            timer.Interval = interval;
            Drain(window);
        }
    }

    private static void VerifyFrameIndexEditing(MainWindow window, MainWindowViewModel model, Session frames, DispatcherTimer timer)
    {
        TextBox input = (TextBox)window.FindName("FrameIndexInput");
        ImageViewport viewport = (ImageViewport)window.FindName("Viewport");
        long now = 0;
        FramePlaybackController playback = new(frames.Info, () => now);
        typeof(MainWindowViewModel).GetField("_framePlayback", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(model, playback);
        playback.Play();
        Assert.True(input.Focus());
        Drain(window);
        Assert.Same(input, Keyboard.FocusedElement);
        Assert.False(model.IsAnimationPlaying);
        Assert.False(timer.IsEnabled);
        Assert.Equal(input.Text, input.SelectedText);
        input.SetCurrentValue(TextBox.TextProperty, "2");
        int reads = frames.Requests.Count;
        now = 2500;
        model.PulseAnimationAsync().GetAwaiter().GetResult();
        model.UpdateScale(1.5);
        Drain(window);
        Assert.Equal("2", input.Text);
        Assert.Equal(reads, frames.Requests.Count);
        Assert.Equal(0, model.Presentation.FrameIndex);
        PressEnter(window, input);
        Drain(window);
        Assert.Equal(1, model.Presentation.FrameIndex);
        Assert.Equal((byte)100, model.CurrentImage!.Pixels.Span[2]);
        Assert.Equal(model.FrameIndexText, input.Text);
        Assert.Same(viewport, Keyboard.FocusedElement);
        foreach (bool enter in new[] { true, false })
            foreach (string invalid in new[] { "", "abc", "0", "-1", "4", "99999" })
            {
                Assert.True(input.Focus());
                Drain(window);
                Assert.Equal(input.Text, input.SelectedText);
                PixelBuffer current = model.CurrentImage!;
                reads = frames.Requests.Count;
                input.SetCurrentValue(TextBox.TextProperty, invalid);
                Assert.NotNull(input.GetBindingExpression(TextBox.TextProperty));
                if (enter) { PressEnter(window, input); }
                else { Assert.True(viewport.Focus()); }
                Drain(window);
                Assert.Equal(reads, frames.Requests.Count);
                Assert.Same(current, model.CurrentImage);
                Assert.Equal(1, model.Presentation.FrameIndex);
                Assert.Equal(model.FrameIndexText, input.Text);
                Assert.Same(viewport, Keyboard.FocusedElement);
                Assert.Equal((byte)100, current.Pixels.Span[2]);
            }
    }

    private static void PressEnter(MainWindow window, TextBox input)
    {
        KeyEventArgs key = new(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window), 0, Key.Enter)
        {
            RoutedEvent = Keyboard.PreviewKeyDownEvent
        };
        input.RaiseEvent(key);
        Assert.True(key.Handled);
    }

    private static void VerifyInformationLayout(MainWindow window, MainWindowViewModel model)
    {
        model.ShowInformation = true;
        Drain(window);
        AssertInformationUnobstructed(window);
        Invoke(window, "ToggleFullScreen");
        try
        {
            Invoke(window, "SetChromeVisible", true);
            Drain(window);
            Assert.True(model.IsFullScreen);
            Assert.True(model.IsFilmstripVisible);
            AssertInformationUnobstructed(window);
        }
        finally { Invoke(window, "ToggleFullScreen"); Drain(window); }
        Assert.Equal(720, window.ActualWidth);
        Assert.Equal(480, window.ActualHeight);
    }

    private static void AssertInformationUnobstructed(MainWindow window)
    {
        FrameworkElement information = (FrameworkElement)window.FindName("InformationPanel");
        Assert.Equal(Visibility.Visible, information.Visibility);
        Rect panel = Bounds(information, window);
        Assert.True(panel.Width > 0 && panel.Height > 0);
        foreach (string name in new[] { "Toolbar", "FrameControls", "Filmstrip" })
        {
            FrameworkElement element = (FrameworkElement)window.FindName(name);
            Assert.Equal(Visibility.Visible, element.Visibility);
            Assert.False(panel.IntersectsWith(Bounds(element, window)), $"InformationPanel overlaps {name}.");
        }
    }

    private static Rect Bounds(FrameworkElement element, MainWindow window) =>
        element.TransformToAncestor(window).TransformBounds(new Rect(element.RenderSize));

    private static void Capture(MainWindow window, ThemeService themes, LocalizationService localization,
        MainWindowViewModel model, string kind, byte red)
    {
        string? output = Environment.GetEnvironmentVariable("MIV_UI_SCREENSHOT_DIRECTORY");
        if (!string.IsNullOrWhiteSpace(output)) { Directory.CreateDirectory(output); }
        ImageViewport viewport = (ImageViewport)window.FindName("Viewport");
        foreach (AppTheme theme in new[] { AppTheme.Dark, AppTheme.Light })
            foreach (string language in new[] { "zh-CN", "en-US" })
            {
                themes.Apply(theme);
                localization.SetCulture(language);
                Drain(window);
                Assert.True(window.IsCompactLayout);
                Assert.Equal(720, window.ActualWidth);
                Assert.Equal(480, window.ActualHeight);
                FrameworkElement controls = (FrameworkElement)window.FindName("FrameControls");
                Assert.Equal(Visibility.Visible, controls.Visibility);
                Rect bounds = controls.TransformToAncestor(window).TransformBounds(new Rect(controls.RenderSize));
                Assert.InRange(bounds.Left, 0, window.ActualWidth - bounds.Width);
                Assert.InRange(bounds.Top, 0, window.ActualHeight - bounds.Height);
                TextBox input = (TextBox)window.FindName("FrameIndexInput");
                Assert.True(input.Focus());
                Drain(window);
                Assert.False(model.IsAnimationPlaying);
                Assert.Equal(((SolidColorBrush)window.FindResource("SurfaceHoverBrush")).Color, ((SolidColorBrush)input.Background).Color);
                Assert.Equal(((SolidColorBrush)window.FindResource("PrimaryTextBrush")).Color, ((SolidColorBrush)input.Foreground).Color);
                Border inputBorder = (Border)input.Template.FindName("InputBorder", input);
                Assert.Equal(((SolidColorBrush)window.FindResource("AccentBrush")).Color, ((SolidColorBrush)inputBorder.BorderBrush).Color);
                AssertInformationUnobstructed(window);
                Assert.Equal(model.FrameIndexText, input.Text);
                Assert.Equal(localization.GetString(kind == "animation" ? "Frames_Frame" : "Frames_Page"), model.FramePositionLabel);
                Assert.Equal(red, model.CurrentImage!.Pixels.Span[2]);
                if (!string.IsNullOrWhiteSpace(output))
                {
                    string path = Path.Combine(output, $"frames-{kind}-720x480-{theme}-{language}.png");
                    typeof(MainWindowViewModelTests).GetMethod("SaveScreenshot", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [window, path]);
                    VerifyScreenshot(path, viewport, window, red);
                }
            }
    }

    private static void VerifyScreenshot(string path, ImageViewport viewport, MainWindow window, byte red)
    {
        using FileStream stream = File.OpenRead(path);
        BitmapSource bitmap = BitmapDecoder.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames[0];
        Assert.Equal(720, bitmap.PixelWidth);
        Assert.Equal(480, bitmap.PixelHeight);
        Point center = viewport.TranslatePoint(new Point(viewport.ActualWidth / 2, viewport.ActualHeight / 2), window);
        byte[] pixel = new byte[4];
        bitmap.CopyPixels(new Int32Rect((int)center.X, (int)center.Y, 1, 1), pixel, 4, 0);
        Assert.Equal(new byte[] { 30, 80, red, 255 }, pixel);
    }

    private static object TimerOperation(DispatcherTimer timer) =>
        typeof(DispatcherTimer).GetField("_operation", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(timer)!;
    private static void OpenSelection(ImageOpenCoordinator coordinator, ImageBrowseSession browse, string name)
    {
        string[] files = [Path.GetFullPath(name), Path.GetFullPath("next.png")];
        Assert.True(coordinator.OpenCandidatesAsync(files, browse, selection: true,
            cancellationToken: TestContext.Current.CancellationToken).GetAwaiter().GetResult());
        Assert.Equal(files, browse.Items);
    }
    private static void Drain(MainWindow window) =>
        typeof(MainWindowViewModelTests).GetMethod("DrainBindings", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [window]);
    private static void Invoke(MainWindow window, string name, params object?[] args) =>
        typeof(MainWindow).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)!.Invoke(window, args);

    private sealed class Picker : IImageFilePicker
    {
        public Task<string?> PickImageAsync(CancellationToken cancellationToken) => Task.FromResult<string?>(null);
    }

    private sealed class Decoder(params IImageFrameSession?[] sessions) : IImageDecoder, IImageFrameDecoder
    {
        private readonly Queue<IImageFrameSession?> _sessions = new(sessions);
        public int StaticCalls { get; private set; }
        public bool LimitSessionOpen { get; set; }
        public Task<IImageFrameSession?> TryOpenFrameSessionAsync(string path, CancellationToken cancellationToken)
        {
            if (LimitSessionOpen)
            {
                LimitSessionOpen = false;
                return Task.FromException<IImageFrameSession?>(new ImageSizeLimitExceededException());
            }
            return Task.FromResult(_sessions.Dequeue());
        }
        public Task<PixelBuffer> DecodeAsync(string path, CancellationToken cancellationToken)
        {
            StaticCalls++;
            return Task.FromResult(FrameViewportTests.Pixels(77));
        }
    }

    private sealed class Session(ImageSequenceKind kind) : IImageFrameSession
    {
        private byte[]? _retained = new byte[800 * 400 * 4];
        public ImageSequenceInfo Info { get; } = FrameViewportTests.Sequence(kind);
        public ImageFileStamp FileStamp { get; } = new(123, DateTime.UnixEpoch);
        public long RetainedPixelBytes => _retained?.LongLength ?? 0;
        public bool HoldFrames { get; set; }
        public bool Disposed { get; private set; }
        public List<Request> Requests { get; } = [];
        public Task<PixelBuffer> DecodeFrameAsync(int index, PixelSize maximumSize, long maximumDecodedBytes, CancellationToken cancellationToken)
        {
            Request request = new(index, maximumSize, maximumDecodedBytes, FileStamp, cancellationToken);
            Requests.Add(request);
            if (!HoldFrames) { request.Complete(); }
            return request.Completion.Task;
        }
        public void Dispose() { Disposed = true; _retained = null; }
    }

    private sealed class Request(int index, PixelSize target, long budget, ImageFileStamp stamp, CancellationToken token)
    {
        public int Index => index;
        public CancellationToken Token => token;
        public TaskCompletionSource<PixelBuffer> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public PixelBuffer Complete()
        {
            PixelSize source = new(800, 400);
            PixelBuffer pixels = FrameViewportTests.Pixels((byte)(30 + index * 70), ImageFrameLimits.Fit(source, target, budget), source, stamp);
            Assert.InRange(pixels.Pixels.Length, 1, budget);
            Completion.SetResult(pixels);
            return pixels;
        }
    }

    private sealed class Localization : ILocalizationService
    {
        private readonly ResourceManager _resources = new("ModernImageViewer.UI.Resources.Strings", typeof(MainWindowViewModel).Assembly);
        public event EventHandler? CultureChanged { add { } remove { } }
        public CultureInfo CurrentCulture => CultureInfo.GetCultureInfo("en-US");
        public IReadOnlyList<SupportedLanguage> SupportedLanguages { get; } = [new("en-US", "English")];
        public string GetString(string name) => _resources.GetString(name, CurrentCulture) ?? name;
        public void Initialize() { }
        public void SetCulture(string cultureName) { }
    }

    private sealed class Settings : IUserSettingsService
    {
        public string? Language { get; private set; }
        public string? Theme { get; private set; }
        public void SaveLanguage(string language) => Language = language;
        public void SaveTheme(string theme) => Theme = theme;
    }

    private sealed class Clipboard : IImageClipboardService
    {
        public ImageClipboardInput ReadInput() => new([]);
        public Task WriteAsync(ImageClipboardPixels image, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
