using System.Reflection;
using System.Windows;
using System.Windows.Media;

using ModernImageViewer.Application.Images;
using ModernImageViewer.Imaging;
using ModernImageViewer.UI.Controls;

using SkiaSharp;
using SkiaSharp.Views.Desktop;

namespace ModernImageViewer.UI.Tests;

public sealed class EditViewportTests
{
    [Fact]
    public async Task CroppedRotatedAndResizedPreviewUsesOriginalRegionExactlyOnceWithoutCopying()
    {
        TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Thread thread = new(() =>
        {
            try
            {
                PixelSize source = new(4, 2);
                PixelRect crop = new(1, 0, 2, 1);
                using PixelBuffer preview = new(new(2, 1), 8, [0, 0, 255, 255, 0, 0, 255, 255], sourceSize: source);
                using PixelBuffer detail = new(crop.Size, 8, [128, 128, 0, 128, 128, 128, 0, 128], sourceSize: source);
                using ImageViewport viewport = new();
                viewport.Resources["CheckerDarkBrush"] = Brushes.Black;
                viewport.Resources["CheckerLightBrush"] = Brushes.Black;
                viewport.Presentation = new(ImageOpenStatus.Loaded, preview, "crop.png", IsPreview: true,
                    Region: new(detail, crop));
                viewport.Measure(new Size(24, 16));
                viewport.Arrange(new Rect(0, 0, 24, 16));
                SKBitmap bitmap = ReadField<SKBitmap>(viewport, "_bitmap");
                IntPtr address = bitmap.GetPixels();
                viewport.SetEditRecipe(ImageEditRecipe.Create(source).WithCrop(crop).RotateRight().FlipHorizontal().WithSize(new(12, 8)));
                Assert.Equal(crop, viewport.VisibleDetailRegion);
                Assert.Same(bitmap, ReadField<SKBitmap>(viewport, "_bitmap"));
                Assert.Equal(address, bitmap.GetPixels());
                SKImageInfo info = new(24, 16, SKColorType.Bgra8888, SKAlphaType.Premul);
                using SKSurface surface = SKSurface.Create(info);
                typeof(ImageViewport).GetMethod("OnPaintSurface", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(viewport, [null, new SKPaintSurfaceEventArgs(surface, info)]);
                using SKImage snapshot = surface.Snapshot();
                using SKBitmap rendered = SKBitmap.FromImage(snapshot);
                Assert.Equal(new SKColor(0, 128, 128), rendered.GetPixel(12, 8));
                Point canvas = viewport.ToCanvasPoint(2, .5);
                var original = viewport.ToSourcePoint(canvas);
                Assert.Equal(2, original.X, 8);
                Assert.Equal(.5, original.Y, 8);
                completion.SetResult();
            }
            catch (Exception exception) { completion.SetException(exception); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await completion.Task.WaitAsync(TestContext.Current.CancellationToken);
    }

    private static T ReadField<T>(ImageViewport viewport, string name) =>
        (T)typeof(ImageViewport).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(viewport)!;
}
