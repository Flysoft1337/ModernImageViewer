using System.Globalization;
using System.IO;

using ModernImageViewer.Application.Browsing;
using ModernImageViewer.Application.Images;
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
        List<string?> notifiedPaths = [];
        viewModel.PropertyChanged += (_, _) =>
        {
            if (coordinator.State.Status == ImageOpenStatus.Loaded)
            {
                notifiedPaths.Add(session.CurrentPath);
            }
        };

        Assert.True(await viewModel.OpenPathAsync(directory.Second));

        Assert.NotEmpty(notifiedPaths);
        Assert.All(notifiedPaths, path => Assert.Equal(directory.Second, path));
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
        PixelBuffer? image = viewModel.CurrentImage;

        await ((AsyncRelayCommand)viewModel.OpenCommand).ExecuteAsync();

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
        Assert.Equal(directory.First, viewModel.CurrentFilePath);
        Assert.True(await viewModel.OpenLastAsync());
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
                using ImageOpenCoordinator coordinator = new(new FixedFilePicker(null), new SuccessfulDecoder());
                using Themes.ThemeService themes = new();
                MainWindowViewModel viewModel = new(new TestLocalization(), coordinator, new ImageBrowseSession());
                MainWindow window = new(viewModel, themes);
                window.Measure(new System.Windows.Size(1280, 820));
                window.Arrange(new System.Windows.Rect(0, 0, 1280, 820));
                Controls.ImageViewport viewport = (Controls.ImageViewport)window.FindName("Viewport");
                Assert.Empty(((System.Windows.Controls.Grid)viewport.FindName("Canvas")).Children.Cast<object>());
                foreach (Themes.AppTheme theme in Enum.GetValues<Themes.AppTheme>())
                {
                    themes.Apply(theme);
                    Assert.IsType<System.Windows.Media.SolidColorBrush>(window.FindResource("CanvasBrush"));
                    Assert.Equal(theme, themes.CurrentTheme);
                }
                window.Close();
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
