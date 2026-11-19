using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

using ModernImageViewer.Application.Browsing;
using ModernImageViewer.Application.Images;
using ModernImageViewer.Codecs;
using ModernImageViewer.Imaging;
using ModernImageViewer.UI.Localization;
using ModernImageViewer.UI.Themes;
using ModernImageViewer.UI.ViewModels;

using SkiaSharp;

namespace ModernImageViewer.UI.Tests;

[Collection("Region decoder slot")]
public sealed class IconSizeBrowsingTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task SelectsNativeVariantForViewportAndRetainsManualChoiceThroughResizeAndRename()
    {
        using IconFile file = new();
        using ImageOpenCoordinator coordinator = new(new Picker(), new ImageDecoder());
        coordinator.SetPreviewTarget(new(20, 40));
        Assert.True(await coordinator.OpenAsync(file.Path, Token));
        Assert.Equal(ImageSequenceKind.IconSizes, coordinator.State.Sequence!.Kind);
        Assert.Collection(coordinator.State.Sequence.Frames.Select(frame => frame.CanvasSize.Width).Order(),
            width => Assert.Equal(16, width), width => Assert.Equal(32, width), width => Assert.Equal(256, width));
        VerifyPixels(coordinator, 32, 1);

        Assert.True(await coordinator.UpgradePreviewAsync(new(400, 400), cancellationToken: Token));
        VerifyPixels(coordinator, 256, 0);
        Assert.True(await coordinator.SelectIconSizeAsync(0, cancellationToken: Token));
        VerifyPixels(coordinator, 16, 2);
        Assert.False(coordinator.State.IsIconSizeAutomatic);
        Assert.False(await coordinator.UpgradePreviewAsync(new(1000, 1000), cancellationToken: Token));
        VerifyPixels(coordinator, 16, 2);

        await coordinator.PrepareCurrentFileOperationAsync();
        file.Rename();
        await coordinator.CompleteCurrentFileRenameAsync(file.Path);
        Assert.True(coordinator.HasFrameSession);
        Assert.False(coordinator.State.IsIconSizeAutomatic);
        VerifyPixels(coordinator, 16, 2);

        Assert.True(await coordinator.SelectIconSizeAsync(null, cancellationToken: Token));
        Assert.True(coordinator.State.IsIconSizeAutomatic);
        VerifyPixels(coordinator, 256, 0);
        Assert.True(await coordinator.UpgradePreviewAsync(new(16, 16), cancellationToken: Token));
        VerifyPixels(coordinator, 16, 2);
        await coordinator.CloseImageAsync();
    }

    [Fact]
    public async Task SingleVariantUsesStaticPathAndReleasesFile()
    {
        using IconFile file = new(single: true);
        using ImageOpenCoordinator coordinator = new(new Picker(), new ImageDecoder());
        Assert.True(await coordinator.OpenAsync(file.Path, Token));
        Assert.Null(coordinator.State.Sequence);
        VerifyPixels(coordinator, 16, 2);
        file.Rename();
    }

    internal static void VerifySizeMenu(ThemeService themes, LocalizationService localization)
    {
        using IconFile file = new();
        using ImageOpenCoordinator coordinator = new(new Picker(), new ImageDecoder());
        using MainWindowViewModel model = new(localization, coordinator, new ImageBrowseSession());
        MainWindow window = new(model, themes) { Width = 720, Height = 480 };
        SynchronizationContext? previousContext = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(window.Dispatcher));
        try
        {
            window.Show();
            Task<bool> open = coordinator.OpenAsync(file.Path);
            WaitFor(window, open);
            Assert.True(open.Result);
            Assert.True(coordinator.State.IsIconSizeAutomatic);
            Button button = (Button)window.FindName("IconSizeButton");
            Assert.Equal(Visibility.Visible, button.Visibility);
            Assert.Equal(Visibility.Collapsed, ((FrameworkElement)window.FindName("FrameControls")).Visibility);
            Assert.Equal(0, model.ActiveFrameControllers);
            string previousLanguage = localization.CurrentCulture.Name;
            try
            {
                foreach ((string language, string auto) in new[] { ("zh-CN", "自动"), ("en-US", "Automatic") })
                {
                    localization.SetCulture(language);
                    button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    ContextMenu menu = button.ContextMenu!;
                    Assert.True(menu.IsOpen);
                    menu.ApplyTemplate();
                    Assert.IsType<Border>(menu.Template.FindName("MenuSurface", menu));
                    MenuItem[] options = menu.Items.OfType<MenuItem>().ToArray();
                    Assert.Equal(new[] { auto, "16 × 16", "32 × 32", "256 × 256" }, options.Select(item => item.Header));
                    Assert.Same(options[0], Assert.Single(options, item => item.IsChecked));
                    options[1].RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                    menu.IsOpen = false;
                    WaitFor(window, () => model.IconSizeText == "16 × 16" && !coordinator.State.IsIconSizeAutomatic);
                    button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    menu = button.ContextMenu!;
                    options = menu.Items.OfType<MenuItem>().ToArray();
                    Assert.Same(options[1], Assert.Single(options, item => item.IsChecked));
                    options[0].RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                    menu.IsOpen = false;
                    WaitFor(window, () => model.IconSizeText == "256 × 256" && coordinator.State.IsIconSizeAutomatic);
                }
            }
            finally { localization.SetCulture(previousLanguage); }
        }
        finally
        {
            try { WaitFor(window, coordinator.CloseImageAsync()); }
            finally
            {
                window.Close();
                SynchronizationContext.SetSynchronizationContext(previousContext);
            }
        }
    }

    private static void WaitFor(MainWindow window, Task task)
    {
        WaitFor(window, () => task.IsCompleted);
        task.GetAwaiter().GetResult();
    }

    private static void WaitFor(MainWindow window, Func<bool> predicate)
    {
        bool reached = false;
        bool expired = false;
        DispatcherFrame frame = new();
        DispatcherTimer timer = new(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(5) };
        DispatcherTimer timeout = new() { Interval = TimeSpan.FromSeconds(3) };
        timer.Tick += (_, _) => { if (reached = predicate()) { frame.Continue = false; } };
        timeout.Tick += (_, _) => { expired = true; frame.Continue = false; };
        timer.Start();
        timeout.Start();
        Dispatcher.PushFrame(frame);
        timer.Stop();
        timeout.Stop();
        Assert.True(reached && !expired, $"Icon selection timed out in {window.Title}.");
    }

    private static void VerifyPixels(ImageOpenCoordinator coordinator, int size, int colorChannel)
    {
        PixelBuffer pixels = coordinator.State.Image!;
        Assert.Equal(new PixelSize(size, size), pixels.SourceSize);
        Assert.Equal(pixels.SourceSize, pixels.Size);
        Assert.Equal((byte)255, pixels.Pixels.Span[colorChannel]);
        Assert.Equal((byte)255, pixels.Pixels.Span[3]);
        Assert.Equal((byte)0, pixels.Pixels.Span[4 + 3]);
    }

    private sealed class Picker : IImageFilePicker
    {
        public Task<string?> PickImageAsync(CancellationToken cancellationToken) => Task.FromResult<string?>(null);
    }

    private sealed class IconFile : IDisposable
    {
        public IconFile(bool single = false)
        {
            (int Size, byte[] Data)[] images = single ? [(16, RedDib())]
                : [(16, RedDib()), (256, Png(256, SKColors.Blue)), (32, Png(32, SKColors.Lime))];
            using BinaryWriter writer = new(File.Open(Path, FileMode.CreateNew));
            writer.Write((ushort)0);
            writer.Write((ushort)1);
            writer.Write((ushort)images.Length);
            int offset = 6 + 16 * images.Length;
            foreach ((int size, byte[] data) in images)
            {
                writer.Write((byte)(size % 256));
                writer.Write((byte)(size % 256));
                writer.Write((ushort)0);
                writer.Write((ushort)1);
                writer.Write((ushort)32);
                writer.Write(data.Length);
                writer.Write(offset);
                offset += data.Length;
            }
            foreach (var image in images) { writer.Write(image.Data); }
        }

        public string Path { get; private set; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"{Guid.NewGuid():N}.ico");

        public void Rename()
        {
            string renamed = System.IO.Path.ChangeExtension(Path, ".renamed.ico");
            File.Move(Path, renamed);
            Path = renamed;
        }

        public void Dispose() => File.Delete(Path);

        private static byte[] RedDib()
        {
            // One opaque red pixel at the top left of an otherwise transparent bottom-up DIB.
            using MemoryStream stream = new();
            using BinaryWriter writer = new(stream);
            writer.Write(40);
            writer.Write(16);
            writer.Write(32);
            writer.Write((ushort)1);
            writer.Write((ushort)32);
            writer.Write(new byte[24]);
            byte[] pixels = new byte[16 * 16 * 4];
            pixels[15 * 16 * 4 + 2] = 255;
            pixels[15 * 16 * 4 + 3] = 255;
            writer.Write(pixels);
            writer.Write(new byte[16 * 4]);
            return stream.ToArray();
        }

        private static byte[] Png(int size, SKColor color)
        {
            using SKBitmap bitmap = new(size, size);
            bitmap.Erase(SKColors.Transparent);
            bitmap.SetPixel(0, 0, color);
            using SKData png = bitmap.Encode(SKEncodedImageFormat.Png, 100);
            return png.ToArray();
        }
    }
}
