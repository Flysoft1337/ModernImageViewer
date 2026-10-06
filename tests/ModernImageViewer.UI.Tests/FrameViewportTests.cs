using System.Reflection;
using System.Windows;

using ModernImageViewer.Application.Images;
using ModernImageViewer.Imaging;
using ModernImageViewer.UI.Controls;
using ModernImageViewer.UI.Rendering;

using SkiaSharp;

using ImageSource = ModernImageViewer.Application.Images.ImageSource;

namespace ModernImageViewer.UI.Tests;

[Collection("Pixel lifetime")]
public sealed class FrameViewportTests
{
    public static IEnumerable<object[]> ModesAndDpi =>
        from dpi in new[] { 1, 1.25, 1.5, 2 }
        from mode in Enum.GetValues<ViewportMode>()
        select new object[] { dpi, mode };

    [Theory]
    [MemberData(nameof(ModesAndDpi))]
    public Task SameSourceAnimationKeepsModeZoomPanAndOrientation(double dpi, ViewportMode mode) => RunSta(() =>
    {
        using PixelBuffer first = Pixels(30);
        using PixelBuffer next = Pixels(180);
        using ImageViewport viewport = CreateViewport(dpi);
        ImageOpenState state = State(first, ImageSequenceKind.Animation);
        viewport.Presentation = state;
        Configure(viewport, mode);
        var before = Snapshot(viewport);
        SKBitmap old = Field<SKBitmap>(viewport, "_bitmap")!;
        int scales = 0;
        int details = 0;
        viewport.ScaleChanged += (_, _) => scales++;
        viewport.DetailRequested += (_, _) => details++;
        List<ImageOpenState> presented = [];
        viewport.FramePresented += (_, value) => presented.Add(value);
        viewport.Presentation = state with { Image = next, FrameIndex = 1 };
        Assert.Equal(before, Snapshot(viewport));
        Assert.Equal(0, scales);
        Assert.Equal(0, details);
        Assert.Empty(presented);
        Assert.Equal(IntPtr.Zero, old.Handle);
        AssertPaint(viewport, dpi, 180);
        Assert.Same(viewport.Presentation, Assert.Single(presented));
        Assert.Equal(1, presented[0].FrameIndex);
        Assert.Equal((byte)30, first.Pixels.Span[2]);
    });

    [Theory]
    [MemberData(nameof(ModesAndDpi))]
    public Task SameSizeTiffPageResetsViewportButSamePageRefinementPreservesIt(double dpi, ViewportMode mode) => RunSta(() =>
    {
        using PixelBuffer first = Pixels(30);
        using PixelBuffer refined = Pixels(90, new(80, 40));
        using PixelBuffer next = Pixels(180);
        using ImageViewport viewport = CreateViewport(dpi);
        ImageOpenState state = State(first, ImageSequenceKind.Pages);
        viewport.Presentation = state;
        Configure(viewport, mode);
        var before = Snapshot(viewport);
        viewport.Presentation = state with { Image = refined };
        Assert.Equal(before, Snapshot(viewport));
        AssertPaint(viewport, dpi, 90);
        viewport.Presentation = state with { Image = next, FrameIndex = 1 };
        ViewportTransform transform = Field<ViewportTransform>(viewport, "_transform")!;
        Assert.Equal(ViewportMode.Fit, transform.Mode);
        Assert.Equal(.5, transform.Scale, 8);
        Assert.Equal(0, transform.OffsetX, 8);
        Assert.Equal(50, transform.OffsetY, 8);
        Assert.True(viewport.Orientation.IsIdentity);
        AssertPaint(viewport, dpi, 180);
    });

    [Theory]
    [InlineData(ImageSequenceKind.Animation)]
    [InlineData(ImageSequenceKind.Pages)]
    public Task UnloadReloadAndReplacementReleaseBitmapPinsAndPaintOnlyCurrentFrame(ImageSequenceKind kind) => RunSta(() =>
    {
        int baseline = SharedPixelBitmap.ActivePinCount;
        using PixelBuffer first = Pixels(30);
        using PixelBuffer next = Pixels(180);
        using ImageViewport viewport = CreateViewport(1);
        ImageOpenState state = State(first, kind);
        viewport.Presentation = state;
        Configure(viewport, ViewportMode.Custom);
        var before = Snapshot(viewport);
        Assert.Equal(baseline + 1, SharedPixelBitmap.ActivePinCount);
        SKBitmap old = Field<SKBitmap>(viewport, "_bitmap")!;
        viewport.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
        Assert.Equal(IntPtr.Zero, old.Handle);
        Assert.Equal(baseline, SharedPixelBitmap.ActivePinCount);
        Assert.Null(Field<SKBitmap>(viewport, "_bitmap"));
        viewport.Presentation = state with { Image = next, FrameIndex = 1 };
        Assert.Equal(baseline, SharedPixelBitmap.ActivePinCount);
        viewport.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
        Assert.Equal(baseline + 1, SharedPixelBitmap.ActivePinCount);
        if (kind == ImageSequenceKind.Animation) { Assert.Equal(before, Snapshot(viewport)); }
        else
        {
            Assert.Equal(ViewportMode.Fit, Snapshot(viewport).Mode);
            Assert.True(viewport.Orientation.IsIdentity);
        }
        AssertPaint(viewport, 1, 180);
        SKBitmap reloaded = Field<SKBitmap>(viewport, "_bitmap")!;
        viewport.Dispose();
        viewport.Dispose();
        Assert.Equal(IntPtr.Zero, reloaded.Handle);
        Assert.Equal(baseline, SharedPixelBitmap.ActivePinCount);
        Assert.Null(viewport.Presentation);
        Assert.Equal((byte)30, first.Pixels.Span[2]);
        Assert.Equal((byte)180, next.Pixels.Span[2]);
    });

    private static void Configure(ImageViewport viewport, ViewportMode mode)
    {
        viewport.RotateRight();
        viewport.FlipHorizontal();
        if (mode == ViewportMode.Fit) { viewport.Fit(); }
        else
        {
            viewport.ActualSize();
            if (mode == ViewportMode.Custom)
            {
                viewport.ZoomIn();
                Field<ViewportTransform>(viewport, "_transform")!.Pan(17, -29);
            }
        }
        Assert.Equal(mode, Snapshot(viewport).Mode);
    }

    private static (double Scale, double X, double Y, ViewportMode Mode, ViewOrientation Orientation) Snapshot(ImageViewport viewport)
    {
        ViewportTransform transform = Field<ViewportTransform>(viewport, "_transform")!;
        return (transform.Scale, transform.OffsetX, transform.OffsetY, transform.Mode, viewport.Orientation);
    }

    private static ImageOpenState State(PixelBuffer pixels, ImageSequenceKind kind) => new(ImageOpenStatus.Loaded, pixels,
        kind == ImageSequenceKind.Pages ? "pages.tiff" : "animation.gif", IsPreview: true,
        Source: new ImageSource(Guid.NewGuid(), ImageSourceKind.File), Sequence: Sequence(kind));

    internal static ImageSequenceInfo Sequence(ImageSequenceKind kind, PixelSize? canvas = null) => new(kind,
        Enumerable.Range(0, 3).Select(index => new ImageFrameInfo(index, canvas ?? new(800, 400),
            new(0, 0, (canvas ?? new(800, 400)).Width, (canvas ?? new(800, 400)).Height),
            RawDurationMilliseconds: 1000, DurationMilliseconds: 1000)).ToArray(), TotalPlays: null);

    internal static PixelBuffer Pixels(byte red, PixelSize? size = null, PixelSize? source = null, ImageFileStamp? stamp = null)
    {
        PixelSize output = size ?? new(40, 20);
        byte[] bytes = new byte[checked(output.Width * output.Height * 4)];
        for (int offset = 0; offset < bytes.Length; offset += 4)
        {
            bytes[offset] = 30;
            bytes[offset + 1] = 80;
            bytes[offset + 2] = red;
            bytes[offset + 3] = 255;
        }
        return new(output, output.Width * 4, bytes, sourceSize: source ?? new(800, 400), sourceFileStamp: stamp);
    }

    internal static void AssertPaint(ImageViewport viewport, double dpi, byte red)
    {
        using SKBitmap bitmap = (SKBitmap)Existing("Paint", viewport, (int)(400 * dpi), (int)(300 * dpi))!;
        Point center = viewport.ToCanvasPoint(400, 200);
        Assert.Equal(new SKColor(red, 80, 30), bitmap.GetPixel((int)(center.X * dpi), (int)(center.Y * dpi)));
    }

    internal static ImageViewport CreateViewport(double dpi)
    {
        ImageViewport viewport = (ImageViewport)Existing("CreateViewport")!;
        Existing("SetDpi", viewport, dpi, null);
        Existing("Layout", viewport, 400d, 300d);
        return viewport;
    }

    internal static Task RunSta(Action action) => (Task)Existing("RunSta", action)!;
    private static object? Existing(string name, params object?[] args) =>
        typeof(ViewportBrowsingTests).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, args);
    internal static T? Field<T>(object target, string name) where T : class =>
        (T?)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target);
}
