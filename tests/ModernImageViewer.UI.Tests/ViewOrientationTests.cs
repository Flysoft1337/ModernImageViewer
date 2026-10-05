using System.Reflection;
using System.Windows;
using System.Windows.Media;

using ModernImageViewer.Application.Images;
using ModernImageViewer.Imaging;
using ModernImageViewer.UI.Controls;

using SkiaSharp;
using SkiaSharp.Views.Desktop;

namespace ModernImageViewer.UI.Tests;

public sealed class ViewOrientationTests
{
    [Fact]
    public async Task PreviewAndTransparentRegionUseOneMatrixWithoutReplacingSharedPixels()
    {
        await OnStaThread(() =>
        {
            PixelSize source = new(4, 2);
            using PixelBuffer preview = new(new PixelSize(2, 1), 8,
                [0, 0, 255, 255, 0, 0, 255, 255], sourceSize: source);
            using PixelBuffer detail = new(new PixelSize(2, 1), 8,
                [128, 128, 0, 128, 128, 128, 0, 128], sourceSize: source);
            using ImageViewport viewport = new();
            viewport.Resources["CheckerDarkBrush"] = Brushes.Black;
            viewport.Resources["CheckerLightBrush"] = Brushes.Black;
            viewport.Presentation = new ImageOpenState(ImageOpenStatus.Loaded, preview, "first.png", IsPreview: true,
                Region: new DecodedImageRegion(detail, new PixelRect(0, 0, 2, 1)));
            SKBitmap previewBitmap = ReadField<SKBitmap>(viewport, "_bitmap");
            SKBitmap regionBitmap = ReadField<SKBitmap>(viewport, "_regionBitmap");
            IntPtr pixels = previewBitmap.GetPixels();
            IntPtr regionPixels = regionBitmap.GetPixels();
            for (int turns = 0; turns < 4; turns++)
            {
                for (int horizontal = 0; horizontal < 2; horizontal++)
                {
                    for (int vertical = 0; vertical < 2; vertical++)
                    {
                        viewport.ResetOrientation();
                        for (int turn = 0; turn < turns; turn++) { viewport.RotateRight(); }
                        if (horizontal != 0) { viewport.FlipHorizontal(); }
                        if (vertical != 0) { viewport.FlipVertical(); }
                        PixelSize display = viewport.Orientation.GetDisplaySize(source);
                        viewport.Measure(new Size(display.Width * 10, display.Height * 10));
                        viewport.Arrange(new Rect(0, 0, display.Width * 10, display.Height * 10));
                        viewport.Fit();
                        SKImageInfo info = new(display.Width * 10, display.Height * 10, SKColorType.Bgra8888, SKAlphaType.Premul);
                        using SKSurface surface = SKSurface.Create(info);
                        typeof(ImageViewport).GetMethod("OnPaintSurface", BindingFlags.Instance | BindingFlags.NonPublic)!
                            .Invoke(viewport, [null, new SKPaintSurfaceEventArgs(surface, info)]);
                        using SKImage snapshot = surface.Snapshot();
                        using SKBitmap rendered = SKBitmap.FromImage(snapshot);
                        var detailed = viewport.Orientation.ToDisplayPoint(source, .5, .5);
                        SKColor actual = rendered.GetPixel((int)(detailed.X * 10), (int)(detailed.Y * 10));
                        Assert.Equal(new SKColor(0, 128, 128), actual);
                        var plain = viewport.Orientation.ToDisplayPoint(source, 3.5, 1.5);
                        Assert.Equal(SKColors.Red, rendered.GetPixel((int)(plain.X * 10), (int)(plain.Y * 10)));
                        Assert.Same(previewBitmap, ReadField<SKBitmap>(viewport, "_bitmap"));
                        Assert.Same(regionBitmap, ReadField<SKBitmap>(viewport, "_regionBitmap"));
                        Assert.Equal(pixels, previewBitmap.GetPixels());
                        Assert.Equal(regionPixels, regionBitmap.GetPixels());
                    }
                }
            }
        });
    }

    [Fact]
    public async Task RefinementKeepsOrientationAndViewportWhileNewPathResetsBoth()
    {
        await OnStaThread(() =>
        {
            PixelSize source = new(400, 200);
            using PixelBuffer preview = new(new PixelSize(2, 1), 8, new byte[8], sourceSize: source);
            using PixelBuffer refined = new(new PixelSize(4, 2), 16, new byte[32], sourceSize: source);
            using ImageViewport viewport = new();
            viewport.Measure(new Size(300, 200));
            viewport.Arrange(new Rect(0, 0, 300, 200));
            viewport.Presentation = new ImageOpenState(ImageOpenStatus.Loaded, preview, "first.png", IsPreview: true);
            viewport.RotateRight();
            viewport.FlipHorizontal();
            viewport.ZoomIn();
            ViewOrientation orientation = viewport.Orientation;
            ViewportTransform transform = ReadField<ViewportTransform>(viewport, "_transform");
            var position = (transform.Scale, transform.OffsetX, transform.OffsetY, transform.Mode);
            viewport.Presentation = new ImageOpenState(ImageOpenStatus.Loaded, refined, "FIRST.png");
            Assert.Equal(orientation, viewport.Orientation);
            Assert.Equal(position, (transform.Scale, transform.OffsetX, transform.OffsetY, transform.Mode));
            viewport.Presentation = new ImageOpenState(ImageOpenStatus.Loaded, preview, "second.png", IsPreview: true);
            Assert.True(viewport.Orientation.IsIdentity);
            Assert.Equal(ViewportMode.Fit, transform.Mode);
            Assert.Equal(.75, transform.Scale);
            Assert.Equal(0, transform.OffsetX);
            Assert.Equal(25, transform.OffsetY);
        });
    }

    private static T ReadField<T>(ImageViewport viewport, string name) =>
        (T)typeof(ImageViewport).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(viewport)!;

    private static Task OnStaThread(Action action)
    {
        TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Thread thread = new(() =>
        {
            try { action(); completion.SetResult(); }
            catch (Exception exception) { completion.SetException(exception); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }
}
