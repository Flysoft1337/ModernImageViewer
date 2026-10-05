using System.Collections.Concurrent;
using System.Globalization;
using System.IO;

using ModernImageViewer.Application.Browsing;
using ModernImageViewer.Application.Images;
using ModernImageViewer.Application.Integration;
using ModernImageViewer.Imaging;
using ModernImageViewer.UI.Commands;
using ModernImageViewer.UI.Localization;
using ModernImageViewer.UI.ViewModels;

namespace ModernImageViewer.UI.Tests;

public sealed class MainWindowViewModelTests
{
    private static readonly string[] ThemeResourceFiles = ["Colors.xaml", "Controls.xaml", "Icons.xaml"];
    [Fact]
    public async Task PickerOpenEstablishesBrowsingAndNavigatesBothDirections()
    {
        using ImageDirectory directory = new();
        using ImageOpenCoordinator coordinator = new(new FixedFilePicker(directory.Second), new SuccessfulDecoder());
        ImageBrowseSession session = new();
        MainWindowViewModel viewModel = new(new TestLocalization(), coordinator, session);

        await ((AsyncRelayCommand)viewModel.OpenCommand).ExecuteAsync();
        await coordinator.WaitForIndexingAsync(TestContext.Current.CancellationToken);

        Assert.Equal(directory.Second, session.CurrentPath);
        Assert.Equal(3, session.Count);
        Assert.True(viewModel.PreviousCommand.CanExecute(null));
        Assert.True(viewModel.NextCommand.CanExecute(null));
        Assert.Equal("2 / 3", viewModel.PositionText);

        await viewModel.NextCommand.ExecuteAsync();
        Assert.Equal(directory.Third, coordinator.State.FilePath);
        Assert.Equal("3 / 3", viewModel.PositionText);
        Assert.False(viewModel.NextCommand.CanExecute(null));

        await viewModel.PreviousCommand.ExecuteAsync();
        Assert.Equal(directory.Second, coordinator.State.FilePath);
        await viewModel.PreviousCommand.ExecuteAsync();
        Assert.Equal(directory.First, coordinator.State.FilePath);
        Assert.False(viewModel.PreviousCommand.CanExecute(null));
    }

    [Fact]
    public async Task DirectOpenUpdatesBrowsingBeforeImageNotification()
    {
        using ImageDirectory directory = new();
        using ImageOpenCoordinator coordinator = new(new FixedFilePicker(null), new SuccessfulDecoder());
        ImageBrowseSession session = new();
        MainWindowViewModel viewModel = new(new TestLocalization(), coordinator, session);
        ConcurrentQueue<string?> notifiedPaths = new();
        viewModel.PropertyChanged += (_, _) =>
        {
            if (coordinator.State.Status == ImageOpenStatus.Loaded)
            {
                notifiedPaths.Enqueue(session.CurrentPath);
            }
        };

        Assert.True(await viewModel.OpenPathAsync(directory.Second));
        await coordinator.WaitForIndexingAsync(TestContext.Current.CancellationToken);

        Assert.NotEmpty(notifiedPaths);
        Assert.All(notifiedPaths.ToArray(), path => Assert.Equal(directory.Second, path));
        Assert.Equal("2 / 3", viewModel.PositionText);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancelledOrFailedPickerLeavesExistingBrowsingUnchanged(bool fails)
    {
        using ImageDirectory directory = new();
        string missingPath = Path.Combine(directory.Path, "missing.png");
        using ImageOpenCoordinator coordinator = new(new FixedFilePicker(fails ? missingPath : null), new SuccessfulDecoder());
        ImageBrowseSession session = new();
        MainWindowViewModel viewModel = new(new TestLocalization(), coordinator, session);
        Assert.True(await viewModel.OpenPathAsync(directory.Second));
        await coordinator.WaitForIndexingAsync(TestContext.Current.CancellationToken);
        PixelBuffer? image = viewModel.CurrentImage;

        await ((AsyncRelayCommand)viewModel.OpenCommand).ExecuteAsync();
        await coordinator.WaitForIndexingAsync(TestContext.Current.CancellationToken);

        Assert.Equal(directory.Second, session.CurrentPath);
        Assert.Equal("2 / 3", viewModel.PositionText);
        Assert.Same(image, viewModel.CurrentImage);
        Assert.True(viewModel.CanMovePrevious);
        Assert.True(viewModel.CanMoveNext);
        Assert.Equal(fails ? ImageOpenStatus.Error : ImageOpenStatus.Loaded, coordinator.State.Status);
    }

    [Fact]
    public async Task FolderOpenNaturallySortsAndSlideshowLoopsWithoutLosingImageOnEmptyFolder()
    {
        using ImageDirectory directory = new();
        File.WriteAllBytes(System.IO.Path.Combine(directory.Path, "2.png"), []);
        string last = System.IO.Path.Combine(directory.Path, "10.png");
        File.WriteAllBytes(last, []);
        using ImageOpenCoordinator coordinator = new(new FixedFilePicker(null), new SuccessfulDecoder());
        using MainWindowViewModel viewModel = new(new TestLocalization(), coordinator, new ImageBrowseSession());
        Assert.True(await viewModel.OpenFolderAsync(directory.Path));
        await coordinator.WaitForIndexingAsync(TestContext.Current.CancellationToken);
        Assert.Equal(directory.First, viewModel.CurrentFilePath);
        Assert.True(await viewModel.OpenLastAsync());
        await coordinator.WaitForIndexingAsync(TestContext.Current.CancellationToken);
        Assert.Equal(last, viewModel.CurrentFilePath);
        viewModel.IsSlideshowPlaying = true;
        Assert.True(await viewModel.AdvanceSlideshowAsync());
        Assert.Equal(directory.First, viewModel.CurrentFilePath);

        string empty = System.IO.Path.Combine(directory.Path, "empty");
        Directory.CreateDirectory(empty);
        PixelBuffer? image = viewModel.CurrentImage;
        Assert.False(await viewModel.OpenFolderAsync(empty));
        Assert.Same(image, viewModel.CurrentImage);
        Assert.False(viewModel.IsSlideshowPlaying);
    }

    [Fact]
    public void WindowResourcesLoadAndThemesSwitch()
    {
        Exception? failure = null;
        Thread thread = new(() =>
        {
            System.Windows.Application app = new() { ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown };
            try
            {
                foreach (string file in ThemeResourceFiles)
                {
                    app.Resources.MergedDictionaries.Add(new System.Windows.ResourceDictionary
                    {
                        Source = new Uri($"pack://application:,,,/ModernImageViewer.UI;component/Themes/{file}"),
                    });
                }
                app.Resources.Add("BooleanToVisibilityConverter", new System.Windows.Controls.BooleanToVisibilityConverter());
                app.Resources.Add("InverseBooleanToVisibilityConverter", new Converters.InverseBooleanToVisibilityConverter());
                PreviewDecoder decoder = new();
                using ImageOpenCoordinator coordinator = new(new FixedFilePicker(null), decoder);
                using Themes.ThemeService themes = new();
                LocalizationService localization = new(new MemorySettings());
                using MainWindowViewModel viewModel = new(localization, coordinator, new ImageBrowseSession());
                MainWindow window = new(viewModel, themes);
                FileAssociationWindow associations = new(new NoopFileAssociations(), new TestLocalization());
                associations.Measure(new System.Windows.Size(620, 650));
                associations.Arrange(new System.Windows.Rect(0, 0, 620, 650));
                window.Measure(new System.Windows.Size(1280, 820));
                window.Arrange(new System.Windows.Rect(0, 0, 1280, 820));
                Controls.ImageViewport viewport = (Controls.ImageViewport)window.FindName("Viewport");
                Assert.Empty(((System.Windows.Controls.Grid)viewport.FindName("Canvas")).Children.Cast<object>());
                window.Show();
                DrainBindings(window);
                foreach (Themes.AppTheme theme in Enum.GetValues<Themes.AppTheme>())
                {
                    themes.Apply(theme);
                    Assert.IsType<System.Windows.Media.SolidColorBrush>(window.FindResource("CanvasBrush"));
                    Assert.IsType<System.Windows.Media.SolidColorBrush>(associations.FindResource("SurfaceBrush"));
                    Assert.Equal(theme, themes.CurrentTheme);
                }
                Assert.True(coordinator.OpenAsync("preview.png").GetAwaiter().GetResult());
                DrainBindings(window);
                Assert.Equal(new PixelSize(4000, 3000), viewModel.CurrentImage!.SourceSize);
                Assert.True(viewModel.ShowPreviewStatus);
                double scale = 0;
                viewport.ScaleChanged += (_, value) => scale = value;
                viewport.ActualSize();
                Assert.Equal(1, scale);
                viewport.ZoomIn();
                Assert.Equal(1.15, scale, precision: 6);
                Assert.True(coordinator.RefineAsync().GetAwaiter().GetResult());
                DrainBindings(window);
                Assert.Equal(1.15, scale, precision: 6);
                Assert.False(viewModel.ShowPreviewStatus);
                Assert.True(coordinator.OpenAsync("next.png").GetAwaiter().GetResult());
                DrainBindings(window);
                Assert.NotEqual(1.15, scale);
                CapturePreviewScreenshots(window, themes, localization);
                decoder.SourceSize = new PixelSize(8000, 6000);
                Assert.True(coordinator.OpenAsync("large.png").GetAwaiter().GetResult());
                DrainBindings(window);
                viewport.ActualSize();
                DrainBindings(window);
                PixelRect region = Assert.IsType<PixelRect>(viewport.VisibleDetailRegion);
                Assert.InRange(region.Width, 1, 2048);
                Assert.InRange(region.Height, 1, 2048);
                Assert.True(coordinator.RequestRegionAsync(region).GetAwaiter().GetResult());
                DrainBindings(window);
                Assert.Equal(1, scale);
                Assert.True(viewModel.Presentation.IsPreview);
                Assert.NotNull(viewModel.Presentation.Region);
                VerifyCompactAndImmersiveLayouts(window, viewport, viewModel, themes, localization);
                VerifyShortcutHelp(window, themes, localization);
                window.Close();
                associations.Close();
            }
            catch (Exception exception)
            {
                failure = exception;
            }
            finally
            {
                app.Shutdown();
            }
        });
        thread.IsBackground = true;
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(20)), "Window loading timed out.");
        if (failure is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }

    private static void DrainBindings(MainWindow window)
    {
        // This STA test deliberately has no Application.Run loop. Transfer deferred bindings
        // before observing the viewport, as the normal window dispatcher does in the app.
        window.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
        window.UpdateLayout();
    }

    private static void VerifyCompactAndImmersiveLayouts(MainWindow window, Controls.ImageViewport viewport,
        MainWindowViewModel viewModel, Themes.ThemeService themes, LocalizationService localization)
    {
        window.Width = 720;
        window.Height = 480;
        DrainBindings(window);
        Assert.True(window.IsCompactLayout);
        double width = viewport.ActualWidth;
        viewModel.ShowInformation = true;
        DrainBindings(window);
        Assert.Equal(width, viewport.ActualWidth);
        System.Windows.FrameworkElement toolbar = (System.Windows.FrameworkElement)window.FindName("Toolbar");
        Assert.InRange(toolbar.ActualWidth, 1, window.ActualWidth);
        CapturePreviewScreenshots(window, themes, localization, "compact");
        typeof(MainWindow).GetMethod("ToggleFullScreen", System.Reflection.BindingFlags.Instance
            | System.Reflection.BindingFlags.NonPublic)!.Invoke(window, null);
        DrainBindings(window);
        Assert.True(viewModel.IsFullScreen);
        Assert.Equal(new System.Windows.Thickness(0), viewport.Margin);
        Assert.Equal(3, System.Windows.Controls.Grid.GetRowSpan((System.Windows.UIElement)window.FindName("CanvasLayout")));
        System.Reflection.MethodInfo setChrome = typeof(MainWindow).GetMethod("SetChromeVisible",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        setChrome.Invoke(window, [false]);
        DrainBindings(window);
        foreach (string name in new[] { "Toolbar", "InformationPanel", "Filmstrip", "StatusBar", "PreviewStatusOverlay", "LoadingOverlay", "MessageOverlay" })
        {
            Assert.Equal(System.Windows.Visibility.Collapsed, ((System.Windows.UIElement)window.FindName(name)).Visibility);
        }
        Assert.True(window.ForceCursor);
        CapturePreviewScreenshots(window, themes, localization, "fullscreen-hidden", hideChrome: true);
        setChrome.Invoke(window, [true]);
        typeof(MainWindow).GetMethod("ToggleFullScreen", System.Reflection.BindingFlags.Instance
            | System.Reflection.BindingFlags.NonPublic)!.Invoke(window, null);
        DrainBindings(window);
        Assert.False(viewModel.IsFullScreen);
        Assert.True(window.IsChromeVisible);
        Assert.True(viewModel.ShowInformation);
        Assert.Equal(720, window.Width);
        Assert.Equal(480, window.Height);
    }

    private static void CapturePreviewScreenshots(MainWindow window, Themes.ThemeService themes, LocalizationService localization,
        string prefix = "preview", bool hideChrome = false)
    {
        string? output = Environment.GetEnvironmentVariable("MIV_UI_SCREENSHOT_DIRECTORY");
        if (string.IsNullOrEmpty(output))
        {
            return;
        }
        Directory.CreateDirectory(output);
        foreach ((Themes.AppTheme theme, string language) in new[]
            { (Themes.AppTheme.Dark, "zh-CN"), (Themes.AppTheme.Light, "en-US") })
        {
            themes.Apply(theme);
            localization.SetCulture(language);
            if (hideChrome)
            {
                typeof(MainWindow).GetMethod("SetChromeVisible", System.Reflection.BindingFlags.Instance
                    | System.Reflection.BindingFlags.NonPublic)!.Invoke(window, [false]);
            }
            DrainBindings(window);
            SaveScreenshot(window, Path.Combine(output, $"{prefix}-{theme}-{language}.png"));
        }
    }

    private static void VerifyShortcutHelp(MainWindow owner, Themes.ThemeService themes, LocalizationService localization)
    {
        foreach ((Themes.AppTheme theme, string language) in new[]
            { (Themes.AppTheme.Dark, "zh-CN"), (Themes.AppTheme.Light, "en-US") })
        {
            themes.Apply(theme);
            localization.SetCulture(language);
            using ShortcutHelpWindow help = new(localization) { Owner = owner, Width = 400, Height = 640 };
            help.Show();
            help.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
            help.UpdateLayout();
            ShortcutHelpViewModel model = Assert.IsType<ShortcutHelpViewModel>(help.DataContext);
            Assert.Equal(6, model.Groups.Count);
            Assert.Equal(18, model.Groups.Sum(group => group.Rows.Count));
            Assert.DoesNotContain(model.Groups.SelectMany(group => group.Rows), row => row.Description.StartsWith("Shortcut_", StringComparison.Ordinal));
            Assert.Equal(localization.GetString("Shortcut_Title"), help.Title);
            string? output = Environment.GetEnvironmentVariable("MIV_UI_SCREENSHOT_DIRECTORY");
            if (!string.IsNullOrEmpty(output))
            {
                Directory.CreateDirectory(output);
                SaveScreenshot(help, Path.Combine(output, $"help-{theme}-{language}.png"));
            }
            // Culture changes rebuild the same catalog without creating another window.
            localization.SetCulture(language == "zh-CN" ? "en-US" : "zh-CN");
            help.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
            Assert.Equal(localization.GetString("Shortcut_Title"), help.Title);
            help.Close();
        }
    }

    private static void SaveScreenshot(System.Windows.Window window, string path)
    {
        System.Windows.Media.Imaging.RenderTargetBitmap bitmap = new((int)Math.Ceiling(window.ActualWidth),
            (int)Math.Ceiling(window.ActualHeight), 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
        bitmap.Render(window);
        System.Windows.Media.Imaging.PngBitmapEncoder encoder = new();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        using FileStream file = File.Create(path);
        encoder.Save(file);
    }

    private sealed class PreviewDecoder : IPreviewImageDecoder, IRegionImageDecoder
    {
        public PixelSize SourceSize { get; set; } = new(4000, 3000);
        public Task<PixelBuffer> DecodeAsync(string path, CancellationToken cancellationToken) =>
            Task.FromResult(CreateImage(SourceSize));

        public Task<PixelBuffer> DecodePreviewAsync(string path, PixelSize maximumSize, CancellationToken cancellationToken) =>
            Task.FromResult(CreateImage(new PixelSize(2000, 1500)));

        public Task<DecodedImageRegion> DecodeRegionAsync(string path, PixelRect region, PixelSize expectedSourceSize,
            long maximumDecodedBytes, CancellationToken cancellationToken) => Task.FromResult(new DecodedImageRegion(CreateImage(region.Size), region));

        private PixelBuffer CreateImage(PixelSize size)
        {
            byte[] pixels = new byte[size.Width * size.Height * 4];
            for (int y = 0; y < size.Height; y++)
            {
                for (int x = 0; x < size.Width; x++)
                {
                    int index = ((y * size.Width) + x) * 4;
                    pixels[index] = (byte)(100 + (100 * x / size.Width));
                    pixels[index + 1] = (byte)(60 + (120 * y / size.Height));
                    pixels[index + 2] = 40;
                    pixels[index + 3] = 255;
                }
            }
            return new PixelBuffer(size, size.Width * 4, pixels, sourceSize: SourceSize);
        }
    }

    private sealed class MemorySettings : ModernImageViewer.Application.Settings.IUserSettingsService
    {
        public string? Language => "en-US";
        public string? Theme => "Dark";
        public void SaveLanguage(string language) { }
        public void SaveTheme(string theme) { }
    }

    private sealed class NoopFileAssociations : IFileAssociationService
    {
        public FileAssociationStatus ReadStatus() => new(false, false, null, false);
        public void Register() { }
        public void Unregister() { }
        public void OpenDefaultAppsSettings() { }
    }

    private sealed class FixedFilePicker(string? path) : IImageFilePicker
    {
        public Task<string?> PickImageAsync(CancellationToken cancellationToken) => Task.FromResult(path);
    }

    private sealed class SuccessfulDecoder : IImageDecoder
    {
        public Task<PixelBuffer> DecodeAsync(string path, CancellationToken cancellationToken) =>
            File.Exists(path)
                ? Task.FromResult(new PixelBuffer(new PixelSize(1, 1), 4, new byte[4]))
                : Task.FromException<PixelBuffer>(new FileNotFoundException());
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

    private sealed class ImageDirectory : IDisposable
    {
        public ImageDirectory()
        {
            Directory.CreateDirectory(Path);
            File.WriteAllBytes(First, []);
            File.WriteAllBytes(Second, []);
            File.WriteAllBytes(Third, []);
            File.WriteAllText(System.IO.Path.Combine(Path, "ignored.txt"), string.Empty);
        }

        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"ModernImageViewer-{Guid.NewGuid():N}");

        public string First => System.IO.Path.Combine(Path, "01.png");
        public string Second => System.IO.Path.Combine(Path, "02.jpg");
        public string Third => System.IO.Path.Combine(Path, "03.PNG");

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
