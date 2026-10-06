using System.Globalization;
using System.Reflection;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

using ModernImageViewer.Application.Browsing;
using ModernImageViewer.Application.Images;
using ModernImageViewer.Imaging;
using ModernImageViewer.UI.Controls;
using ModernImageViewer.UI.Localization;
using ModernImageViewer.UI.Rendering;
using ModernImageViewer.UI.ViewModels;

using SkiaSharp;
using SkiaSharp.Views.Desktop;

using ImageSource = ModernImageViewer.Application.Images.ImageSource;

namespace ModernImageViewer.UI.Tests;

[Collection("Pixel lifetime")]
public sealed class ViewportBrowsingTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(1.25)]
    [InlineData(1.5)]
    [InlineData(2)]
    public Task DpiFitActualAndPaintUseDipPhysicalAndSourcePixels(double dpi) => RunSta(() =>
    {
        using PixelBuffer pixels = CreatePixels(new(40, 20), new(800, 400));
        using ImageViewport viewport = CreateViewport();
        SetDpi(viewport, dpi);
        double density = 0;
        viewport.ScaleChanged += (_, value) => density = value;
        PixelSize? notified = null;
        viewport.PreviewTargetChanged += (_, value) => notified = value;
        viewport.Presentation = State(pixels);
        Layout(viewport, 400, 300);
        viewport.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
        viewport.Fit();
        ViewportTransform transform = Field<ViewportTransform>(viewport, "_transform")!;
        Assert.Equal(.5, transform.Scale, 8);
        Assert.Equal(.5 * dpi, density, 8);
        Assert.Equal(new PixelSize((int)(400 * dpi), (int)(300 * dpi)), viewport.CurrentPreviewTarget);
        Assert.Equal(viewport.CurrentPreviewTarget, notified);
        using SKBitmap rendered = Paint(viewport, (int)(400 * dpi), (int)(300 * dpi));
        Assert.Equal(new SKColor(150, 80, 30), rendered.GetPixel((int)(200 * dpi), (int)(150 * dpi)));
        Assert.Equal(SKColors.Black, rendered.GetPixel((int)(200 * dpi), (int)(20 * dpi)));
        viewport.ActualSize();
        Assert.Equal(1, density, 8);
        Assert.Equal(1 / dpi, transform.Scale, 8);
        Point canvas = viewport.ToCanvasPoint(271, 137);
        var source = viewport.ToSourcePoint(canvas);
        Assert.Equal(271, source.X, 8);
        Assert.Equal(137, source.Y, 8);
        viewport.ZoomIn();
        Assert.Equal(1.15, density, 8);
        viewport.RotateRight();
        Assert.Equal(1.15, density, 8);
    });

    [Theory]
    [InlineData(1)]
    [InlineData(1.25)]
    [InlineData(1.5)]
    [InlineData(2)]
    public Task DpiChangeRecalculatesActualSizeAndTargetWithoutChangingSource(double dpi) => RunSta(() =>
    {
        using PixelBuffer pixels = CreatePixels(new(40, 20), new(800, 400));
        using ImageViewport viewport = CreateViewport();
        viewport.Presentation = State(pixels);
        Layout(viewport, 400, 300);
        viewport.ActualSize();
        PixelSize? target = null;
        viewport.PreviewTargetChanged += (_, value) => target = value;
        viewport.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
        SetDpi(viewport, dpi);
        Assert.Equal(1 / dpi, Field<ViewportTransform>(viewport, "_transform")!.Scale, 8);
        Assert.Equal(new PixelSize((int)(400 * dpi), (int)(300 * dpi)), viewport.CurrentPreviewTarget);
        Assert.Equal(viewport.CurrentPreviewTarget, target);
        Assert.Same(pixels, viewport.Image);
    });

    [Theory]
    [InlineData(ImageSourceKind.File, 1)]
    [InlineData(ImageSourceKind.File, 1.25)]
    [InlineData(ImageSourceKind.File, 1.5)]
    [InlineData(ImageSourceKind.File, 2)]
    [InlineData(ImageSourceKind.Memory, 1)]
    [InlineData(ImageSourceKind.Memory, 1.25)]
    [InlineData(ImageSourceKind.Memory, 1.5)]
    [InlineData(ImageSourceKind.Memory, 2)]
    public Task RefinementPreservesViewportAndNewIdentityResetsEvenWithSamePathAndPixels(ImageSourceKind kind, double dpi) => RunSta(() =>
    {
        using PixelBuffer preview = CreatePixels(new(20, 10), new(800, 400));
        using PixelBuffer detail = CreatePixels(new(80, 40), new(800, 400));
        using ImageViewport viewport = CreateViewport();
        SetDpi(viewport, dpi);
        ImageSource source = new(Guid.NewGuid(), kind);
        ImageOpenState state = State(preview) with { Source = source };
        viewport.Presentation = state;
        Layout(viewport, 400, 300);
        viewport.ActualSize();
        viewport.ZoomIn();
        viewport.RotateRight();
        ViewportTransform transform = Field<ViewportTransform>(viewport, "_transform")!;
        transform.Pan(17, -29);
        var before = (transform.Scale, transform.OffsetX, transform.OffsetY, transform.Mode);
        ViewOrientation orientation = viewport.Orientation;
        SKBitmap old = Field<SKBitmap>(viewport, "_bitmap")!;

        viewport.Presentation = state with { Image = detail };
        Assert.Equal(before, (transform.Scale, transform.OffsetX, transform.OffsetY, transform.Mode));
        Assert.Equal(orientation, viewport.Orientation);
        Assert.Equal(IntPtr.Zero, old.Handle);
        Assert.NotEqual(IntPtr.Zero, Field<SKBitmap>(viewport, "_bitmap")!.Handle);

        viewport.Presentation = viewport.Presentation with { Source = new ImageSource(Guid.NewGuid(), kind) };
        Assert.Equal(default, viewport.Orientation);
        Assert.Equal(ViewportMode.Fit, transform.Mode);
        Assert.Equal(.5, transform.Scale, 8);
        Assert.Equal(0, transform.OffsetX, 8);
        Assert.Equal(50, transform.OffsetY, 8);
    });

    [Fact]
    public Task LegacyPathFallbackPreservesRefinementButDifferentFileResets() => RunSta(() =>
    {
        using PixelBuffer preview = CreatePixels(new(20, 10), new(800, 400));
        using PixelBuffer detail = CreatePixels(new(80, 40), new(800, 400));
        using ImageViewport viewport = CreateViewport();
        viewport.Presentation = State(preview) with { Source = null };
        Layout(viewport, 400, 300);
        viewport.ActualSize();
        viewport.RotateRight();
        viewport.Presentation = viewport.Presentation with { Image = detail, FilePath = "IMAGE.PNG" };
        Assert.Equal(ViewportMode.ActualSize, Field<ViewportTransform>(viewport, "_transform")!.Mode);
        Assert.Equal(1, viewport.Orientation.QuarterTurns);
        viewport.Presentation = viewport.Presentation with { FilePath = "another.png" };
        Assert.Equal(ViewportMode.Fit, Field<ViewportTransform>(viewport, "_transform")!.Mode);
        Assert.Equal(default, viewport.Orientation);
    });

    [Fact]
    public Task FitDoesNotRequestFullOrRegionDetailButActualAndCustomDo() => RunSta(() =>
    {
        using PixelBuffer tiny = CreatePixels(new(20, 10), new(10000, 10000));
        using ImageViewport viewport = CreateViewport();
        int details = 0;
        int regions = 0;
        viewport.DetailRequested += (_, _) => details++;
        viewport.RegionDetailRequested += (_, _) => regions++;
        viewport.Presentation = State(tiny);
        Layout(viewport, 400, 300);
        viewport.Fit();
        Invoke(viewport, "OnRegionTimer", null, EventArgs.Empty);
        Assert.Equal(0, details);
        Assert.Equal(0, regions);
        Assert.False(Field<DispatcherTimer>(viewport, "_regionTimer")!.IsEnabled);
        viewport.ActualSize();
        Invoke(viewport, "OnRegionTimer", null, EventArgs.Empty);
        Assert.Equal(1, details);
        Assert.Equal(1, regions);
        viewport.Fit();
        viewport.ZoomIn();
        Assert.True(Field<DispatcherTimer>(viewport, "_regionTimer")!.IsEnabled);
        Assert.Equal(2, details);
        viewport.Presentation = new(ImageOpenStatus.Empty);
        Assert.False(Field<DispatcherTimer>(viewport, "_regionTimer")!.IsEnabled);
        Assert.Null(Field<SKBitmap>(viewport, "_bitmap"));
    });

    [Fact]
    public Task FramePresentedOnlyFollowsPaintAndMatchesPixelsAndRegion() => RunSta(() =>
    {
        using PixelBuffer preview = CreatePixels(new(20, 10), new(800, 400));
        using PixelBuffer other = CreatePixels(new(20, 10), new(800, 400));
        using PixelBuffer regionPixels = CreatePixels(new(8, 8), new(800, 400));
        DecodedImageRegion region = new(regionPixels, new(0, 0, 8, 8));
        using ImageViewport viewport = CreateViewport();
        List<ImageOpenState> frames = [];
        viewport.FramePresented += (_, state) => frames.Add(state);
        viewport.Presentation = State(preview);
        Layout(viewport, 400, 300);
        Assert.Empty(frames);
        using (Paint(viewport, 400, 300)) { }
        Assert.Same(viewport.Presentation, Assert.Single(frames));
        viewport.Presentation = viewport.Presentation with { Region = region };
        Assert.Single(frames);
        using (Paint(viewport, 400, 300)) { }
        Assert.Same(region, frames[1].Region);
        Assert.Same(preview, frames[1].Image);
        viewport.Image = other;
        using (Paint(viewport, 400, 300)) { }
        Assert.Equal(2, frames.Count);
        viewport.Presentation = State(preview);
        preview.Dispose();
        using (Paint(viewport, 400, 300)) { }
        Assert.Equal(2, frames.Count);
    });

    [Fact]
    public Task UnloadAndDisposeReleaseSkiaResourcesAndDoNotDisposeBorrowedPixels() => RunSta(() =>
    {
        using PixelBuffer preview = CreatePixels(new(20, 10), new(800, 400));
        using PixelBuffer next = CreatePixels(new(20, 10), new(800, 400));
        using PixelBuffer regionPixels = CreatePixels(new(8, 8), new(800, 400));
        DecodedImageRegion region = new(regionPixels, new(0, 0, 8, 8));
        using ImageViewport viewport = CreateViewport();
        viewport.Presentation = State(preview) with { Region = region };
        Layout(viewport, 400, 300);
        viewport.ActualSize();
        viewport.ZoomIn();
        using (Paint(viewport, 400, 300)) { }
        ViewportTransform transform = Field<ViewportTransform>(viewport, "_transform")!;
        double scale = transform.Scale;
        SKBitmap bitmap = Field<SKBitmap>(viewport, "_bitmap")!;
        SKBitmap regionBitmap = Field<SKBitmap>(viewport, "_regionBitmap")!;
        SKBitmap checker = Field<SKBitmap>(viewport, "_checkerTile")!;
        viewport.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
        Assert.Equal(IntPtr.Zero, bitmap.Handle);
        Assert.Equal(IntPtr.Zero, regionBitmap.Handle);
        Assert.Equal(IntPtr.Zero, checker.Handle);
        AssertReleased(viewport);
        Assert.NotEmpty(preview.Pixels.ToArray());
        Assert.NotEmpty(regionPixels.Pixels.ToArray());
        viewport.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
        Assert.Equal(scale, transform.Scale);
        Assert.NotNull(Field<SKBitmap>(viewport, "_bitmap"));
        Assert.NotNull(Field<SKBitmap>(viewport, "_regionBitmap"));
        viewport.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
        viewport.Presentation = State(next);
        AssertReleased(viewport);
        viewport.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
        Assert.Equal(ViewportMode.Fit, transform.Mode);
        Assert.Equal(.5, transform.Scale, 8);
        viewport.Dispose();
        viewport.Dispose();
        Assert.Null(viewport.Image);
        Assert.Null(viewport.Presentation);
        AssertReleased(viewport);
        viewport.Presentation = State(preview) with { Region = region };
        viewport.Image = next;
        viewport.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
        AssertReleased(viewport);
        Assert.NotEmpty(next.Pixels.ToArray());
    });

    [Fact]
    public Task RegionReplacementAndFullUpgradeReleasePreviousViews() => RunSta(() =>
    {
        using PixelBuffer preview = CreatePixels(new(20, 10), new(800, 400));
        using PixelBuffer full = CreatePixels(new(800, 400), new(800, 400));
        using PixelBuffer firstPixels = CreatePixels(new(8, 8), new(800, 400));
        using PixelBuffer secondPixels = CreatePixels(new(8, 8), new(800, 400));
        using ImageViewport viewport = CreateViewport();
        viewport.Presentation = State(preview) with { Region = new(firstPixels, new(0, 0, 8, 8)) };
        Layout(viewport, 400, 300);
        viewport.ActualSize();
        SKBitmap first = Field<SKBitmap>(viewport, "_regionBitmap")!;
        viewport.Presentation = viewport.Presentation with { Region = new(secondPixels, new(12, 12, 8, 8)) };
        Assert.Equal(IntPtr.Zero, first.Handle);
        SKBitmap second = Field<SKBitmap>(viewport, "_regionBitmap")!;
        viewport.Presentation = viewport.Presentation with { Image = full, Region = null, IsPreview = false };
        Assert.Equal(IntPtr.Zero, second.Handle);
        Assert.Null(Field<SKBitmap>(viewport, "_regionBitmap"));
        Assert.Equal(ViewportMode.ActualSize, Field<ViewportTransform>(viewport, "_transform")!.Mode);
        Assert.NotEmpty(firstPixels.Pixels.ToArray());
        Assert.NotEmpty(secondPixels.Pixels.ToArray());
    });

    [Fact]
    public Task TargetNotificationsRespondToLayoutButNotZoom() => RunSta(() =>
    {
        using PixelBuffer pixels = CreatePixels(new(20, 10), new(800, 400));
        using ImageViewport viewport = CreateViewport();
        int targets = 0;
        viewport.PreviewTargetChanged += (_, _) => targets++;
        viewport.Presentation = State(pixels);
        Layout(viewport, 400, 300);
        viewport.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
        int before = targets;
        viewport.ActualSize();
        viewport.ZoomIn();
        viewport.RotateRight();
        Assert.Equal(before, targets);
        Layout(viewport, 600, 400);
        Assert.True(targets > before);
        Assert.Equal(new PixelSize(600, 400), viewport.CurrentPreviewTarget);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task StateThenOwnerDisposeReplacesRegionAndImageWithoutUsingDisposedWrappers(bool replaceImage) => RunSta(() =>
    {
        int baseline = SharedPixelBitmap.ActivePinCount;
        using PixelBuffer firstImage = CreatePixels(new(20, 10), new(800, 400));
        using PixelBuffer nextImage = CreatePixels(new(20, 10), new(800, 400));
        using PixelBuffer firstPixels = CreatePixels(new(8, 8), new(800, 400));
        byte[] green = new byte[8 * 8 * 4];
        for (int index = 0; index < green.Length; index += 4)
        {
            green[index + 1] = 255;
            green[index + 3] = 255;
        }
        using PixelBuffer nextPixels = new(new(8, 8), 32, green, sourceSize: new(800, 400));
        DecodedImageRegion firstRegion = new(firstPixels, new(200, 100, 8, 8));
        DecodedImageRegion nextRegion = new(nextPixels, firstRegion.Bounds);
        using ImageViewport viewport = CreateViewport();
        ImageOpenState state = State(firstImage) with { Region = firstRegion };
        viewport.Presentation = state;
        Layout(viewport, 400, 300);
        viewport.Fit();
        SKBitmap oldImageView = Field<SKBitmap>(viewport, "_bitmap")!;
        SKBitmap oldRegionView = Field<SKBitmap>(viewport, "_regionBitmap")!;
        PixelBuffer currentImage = replaceImage ? nextImage : firstImage;
        ImageOpenState current = state with
        {
            Image = currentImage,
            Region = nextRegion,
            Source = replaceImage ? new ImageSource(Guid.NewGuid(), ImageSourceKind.File) : state.Source
        };

        // The coordinator publishes first, then releases its previous owned buffers.
        viewport.Presentation = current;
        firstRegion.Dispose();
        if (replaceImage) { firstImage.Dispose(); }
        Assert.Equal(IntPtr.Zero, oldRegionView.Handle);
        if (replaceImage) { Assert.Equal(IntPtr.Zero, oldImageView.Handle); }
        else { Assert.Same(oldImageView, Field<SKBitmap>(viewport, "_bitmap")); }
        Assert.Same(nextRegion, Field<DecodedImageRegion>(viewport, "_displayedRegion"));
        Assert.Equal(baseline + 2, SharedPixelBitmap.ActivePinCount);
        ImageOpenState? presented = null;
        viewport.FramePresented += (_, frame) => presented = frame;
        using (SKBitmap rendered = Paint(viewport, 400, 300))
        {
            Assert.Equal(SKColors.Lime, rendered.GetPixel(102, 102));
        }
        Assert.Same(current, presented);

        viewport.Presentation = current with { Region = null };
        nextRegion.Dispose();
        Assert.Equal(baseline + 1, SharedPixelBitmap.ActivePinCount);
        using (SKBitmap rendered = Paint(viewport, 400, 300))
        {
            Assert.Equal(new SKColor(150, 80, 30), rendered.GetPixel(102, 102));
        }
        viewport.Dispose();
        Assert.Equal(baseline, SharedPixelBitmap.ActivePinCount);
    });

    [Fact]
    public Task HundredsOfAlternatingImagesAndRegionsKeepPinsBoundedAndReturnToBaseline() => RunSta(() =>
    {
        int baseline = SharedPixelBitmap.ActivePinCount;
        using ImageViewport viewport = CreateViewport();
        Layout(viewport, 400, 300);
        SKBitmap? previous = null;
        SKBitmap? previousRegion = null;
        for (int index = 0; index < 400; index++)
        {
            PixelSize source = index % 2 == 0 ? new(800, 400) : new(10000, 8000);
            using PixelBuffer pixels = CreatePixels(index % 2 == 0 ? new(80, 40) : new(20, 16), source);
            using PixelBuffer region = CreatePixels(new(8, 8), source);
            viewport.Presentation = State(pixels) with { Region = new(region, new(0, 0, 8, 8)) };
            Assert.Equal(baseline + 2, SharedPixelBitmap.ActivePinCount);
            if (previous is not null) { Assert.Equal(IntPtr.Zero, previous.Handle); }
            if (previousRegion is not null) { Assert.Equal(IntPtr.Zero, previousRegion.Handle); }
            previous = Field<SKBitmap>(viewport, "_bitmap");
            previousRegion = Field<SKBitmap>(viewport, "_regionBitmap");
            if (index % 25 == 0)
            {
                viewport.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
                Assert.Equal(baseline, SharedPixelBitmap.ActivePinCount);
                viewport.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
                Assert.Equal(baseline + 2, SharedPixelBitmap.ActivePinCount);
                previous = Field<SKBitmap>(viewport, "_bitmap");
                previousRegion = Field<SKBitmap>(viewport, "_regionBitmap");
            }
        }
        viewport.Dispose();
        Assert.Equal(baseline, SharedPixelBitmap.ActivePinCount);
        AssertReleased(viewport);
    });

    private static ImageViewport CreateViewport()
    {
        ImageViewport viewport = new() { CanvasBackground = Brushes.Black };
        viewport.Resources["CheckerDarkBrush"] = Brushes.Black;
        viewport.Resources["CheckerLightBrush"] = Brushes.Black;
        SetDpi(viewport, 1);
        return viewport;
    }

    [Theory]
    [InlineData(true, 1)]
    [InlineData(true, 1.25)]
    [InlineData(true, 1.5)]
    [InlineData(true, 2)]
    [InlineData(false, 1)]
    [InlineData(false, 1.25)]
    [InlineData(false, 1.5)]
    [InlineData(false, 2)]
    public Task QueuedAutomaticDetailRechecksDemandAndRearmsAfterFitOrZoomOut(bool returnToFit, double dpi) => RunSta(() =>
    {
        AutomaticDetailDecoder decoder = new();
        using ImageOpenCoordinator coordinator = new(new AutomaticDetailPicker(), decoder);
        using MainWindowViewModel model = new(new AutomaticDetailLocalization(), coordinator, new ImageBrowseSession());
        using ImageViewport viewport = CreateViewport();
        SetDpi(viewport, dpi);
        coordinator.PropertyChanged += (_, _) => viewport.Presentation = coordinator.State;
        Assert.True(coordinator.OpenAsync("automatic-detail.png").GetAwaiter().GetResult());
        Layout(viewport, 400, 300);
        int requests = 0;
        viewport.DetailRequested += (_, _) =>
        {
            requests++;
            MainWindow.QueueAutomaticDetail(viewport, model, () => true);
        };
        viewport.ActualSize();
        Assert.True(viewport.IsAutomaticDetailRequired);
        Assert.Equal(1, requests);
        Assert.Equal(0, decoder.DetailCalls);
        if (returnToFit) { viewport.Fit(); }
        else
        {
            int steps = 0;
            while (viewport.IsAutomaticDetailRequired)
            {
                viewport.ZoomOut();
                Assert.True(++steps < 100);
            }
        }
        Assert.False(viewport.IsAutomaticDetailRequired);
        DrainAutomaticDetailQueue(viewport);
        Assert.Equal(0, decoder.DetailCalls);

        viewport.ActualSize();
        Assert.True(viewport.IsAutomaticDetailRequired);
        Assert.Equal(2, requests);
        Assert.Equal(0, decoder.DetailCalls);
        DrainAutomaticDetailQueue(viewport);
        Assert.Equal(1, decoder.DetailCalls);
        Assert.False(model.Presentation.IsPreview);
    });

    [Fact]
    public Task ManualDetailInFitIsNotRestrictedByAutomaticDemand() => RunSta(() =>
    {
        AutomaticDetailDecoder decoder = new();
        using ImageOpenCoordinator coordinator = new(new AutomaticDetailPicker(), decoder);
        using MainWindowViewModel model = new(new AutomaticDetailLocalization(), coordinator, new ImageBrowseSession());
        using ImageViewport viewport = CreateViewport();
        coordinator.PropertyChanged += (_, _) => viewport.Presentation = coordinator.State;
        Assert.True(coordinator.OpenAsync("manual-detail.png").GetAwaiter().GetResult());
        Layout(viewport, 400, 300);
        viewport.Fit();
        Assert.False(viewport.IsAutomaticDetailRequired);
        model.RefineImageAsync().GetAwaiter().GetResult();
        Assert.Equal(1, decoder.DetailCalls);
        Assert.False(model.Presentation.IsPreview);
    });

    private static void DrainAutomaticDetailQueue(ImageViewport viewport) =>
        viewport.Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);

    private sealed class AutomaticDetailPicker : IImageFilePicker
    {
        public Task<string?> PickImageAsync(CancellationToken cancellationToken) => Task.FromResult<string?>(null);
    }

    private sealed class AutomaticDetailDecoder : IImageDecoder, IPreviewImageDecoder
    {
        public int DetailCalls { get; private set; }
        public Task<PixelBuffer> DecodeAsync(string path, CancellationToken cancellationToken) =>
            DecodePreviewAsync(path, new(200, 100), cancellationToken);
        public Task<PixelBuffer> DecodePreviewAsync(string path, PixelSize maximumSize, CancellationToken cancellationToken) =>
            Task.FromResult(CreatePixels(new(200, 100), new(800, 400)));
        public Task<PixelBuffer> DecodeDetailAsync(string path, long maximumDecodedBytes, CancellationToken cancellationToken)
        {
            DetailCalls++;
            return Task.FromResult(CreatePixels(new(800, 400), new(800, 400)));
        }
    }

    private sealed class AutomaticDetailLocalization : ILocalizationService
    {
        public event EventHandler? CultureChanged { add { } remove { } }
        public CultureInfo CurrentCulture => CultureInfo.GetCultureInfo("en-US");
        public IReadOnlyList<SupportedLanguage> SupportedLanguages { get; } = [new("en-US", "English")];
        public string GetString(string name) => name;
        public void Initialize() { }
        public void SetCulture(string cultureName) { }
    }

    [Fact]
    public Task PreviewTargetAndPaintApplyDpiOnBothAxes() => RunSta(() =>
    {
        using PixelBuffer pixels = CreatePixels(new(40, 20), new(800, 400));
        using ImageViewport viewport = CreateViewport();
        SetDpi(viewport, 1.25, 2);
        viewport.Presentation = State(pixels);
        Layout(viewport, 400, 300);
        viewport.Fit();
        Assert.Equal(new PixelSize(500, 600), viewport.CurrentPreviewTarget);
        using SKBitmap rendered = Paint(viewport, 500, 600);
        Assert.Equal(new SKColor(150, 80, 30), rendered.GetPixel(250, 420));
        Assert.Equal(SKColors.Black, rendered.GetPixel(250, 60));
    });

    private static void SetDpi(ImageViewport viewport, double scale, double? scaleY = null)
    {
        // SetRootDpi changes only a root visual. The disconnected STA canvas needs its own DPI,
        // then the callback models the monitor transition normally delivered by WPF's HWND.
        System.Windows.Controls.Grid canvas = (System.Windows.Controls.Grid)viewport.Content;
        DpiScale previous = VisualTreeHelper.GetDpi(canvas);
        viewport.Content = null;
        viewport.UpdateLayout();
        DpiScale current = new(scale, scaleY ?? scale);
        VisualTreeHelper.SetRootDpi(canvas, current);
        VisualTreeHelper.SetRootDpi(viewport, current);
        viewport.Content = canvas;
        Invoke(viewport, "OnDpiChanged", previous, current);
        DpiScale actual = VisualTreeHelper.GetDpi(canvas);
        Assert.Equal(current.DpiScaleX, actual.DpiScaleX);
        Assert.Equal(current.DpiScaleY, actual.DpiScaleY);
    }

    private static PixelBuffer CreatePixels(PixelSize size, PixelSize source)
    {
        byte[] bytes = new byte[checked(size.Width * size.Height * 4)];
        for (int index = 0; index < bytes.Length; index += 4)
        {
            bytes[index] = 30;
            bytes[index + 1] = 80;
            bytes[index + 2] = 150;
            bytes[index + 3] = 255;
        }
        return new PixelBuffer(size, size.Width * 4, bytes, sourceSize: source);
    }

    private static ImageOpenState State(PixelBuffer pixels) => new(ImageOpenStatus.Loaded, pixels, "image.png",
        IsPreview: pixels.Size != pixels.SourceSize, Source: new ImageSource(Guid.NewGuid(), ImageSourceKind.File));

    private static void Layout(ImageViewport viewport, double width, double height)
    {
        viewport.Measure(new Size(width, height));
        viewport.Arrange(new Rect(0, 0, width, height));
        viewport.UpdateLayout();
    }

    private static SKBitmap Paint(ImageViewport viewport, int width, int height)
    {
        SKImageInfo info = new(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
        using SKSurface surface = SKSurface.Create(info);
        Invoke(viewport, "OnPaintSurface", null, new SKPaintSurfaceEventArgs(surface, info));
        using SKImage snapshot = surface.Snapshot();
        return SKBitmap.FromImage(snapshot);
    }

    private static void AssertReleased(ImageViewport viewport)
    {
        Assert.Null(Field<SKBitmap>(viewport, "_bitmap"));
        Assert.Null(Field<SKBitmap>(viewport, "_regionBitmap"));
        Assert.Null(Field<SKBitmap>(viewport, "_checkerTile"));
        Assert.Null(Field<SkiaSharp.Views.WPF.SKElement>(viewport, "_surface"));
        Assert.False(Field<DispatcherTimer>(viewport, "_regionTimer")!.IsEnabled);
    }

    private static T? Field<T>(ImageViewport viewport, string name) where T : class =>
        (T?)typeof(ImageViewport).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(viewport);

    private static void Invoke(ImageViewport viewport, string name, params object?[] args) =>
        typeof(ImageViewport).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(viewport, args);

    private static async Task RunSta(Action action)
    {
        TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Thread thread = new(() =>
        {
            try { action(); completion.SetResult(); }
            catch (Exception exception) { completion.SetException(exception); }
            finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await completion.Task.WaitAsync(TestContext.Current.CancellationToken);
    }
}
