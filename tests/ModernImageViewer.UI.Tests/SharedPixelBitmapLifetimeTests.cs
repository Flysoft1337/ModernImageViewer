using ModernImageViewer.Imaging;
using ModernImageViewer.UI.Rendering;

using SkiaSharp;

namespace ModernImageViewer.UI.Tests;

[CollectionDefinition("Pixel lifetime", DisableParallelization = true)]
public sealed class PixelLifetimeGroup;

[Collection("Pixel lifetime")]
public sealed class SharedPixelBitmapLifetimeTests
{
    [Fact]
    public void BitmapPinsBorrowedPixelsUntilDisposeExactlyOnce()
    {
        int baseline = SharedPixelBitmap.ActivePinCount;
        using PixelBuffer pixels = new(new(1, 1), 4, [30, 80, 150, 255]);
        using SKBitmap bitmap = SharedPixelBitmap.Create(pixels);
        Assert.Equal(baseline + 1, SharedPixelBitmap.ActivePinCount);
        pixels.Dispose();
        Assert.Equal(new SKColor(150, 80, 30), bitmap.GetPixel(0, 0));
        bitmap.Dispose();
        Assert.Equal(baseline, SharedPixelBitmap.ActivePinCount);
        bitmap.Dispose();
        Assert.Equal(baseline, SharedPixelBitmap.ActivePinCount);
    }

    [Fact]
    public void LastSubsetReleaseUnpinsAfterParentAndSourceDispose()
    {
        int baseline = SharedPixelBitmap.ActivePinCount;
        using PixelBuffer pixels = new(new(2, 1), 8, [30, 80, 150, 255, 0, 0, 255, 255]);
        using SKBitmap parent = SharedPixelBitmap.Create(pixels);
        using SKBitmap first = new();
        using SKBitmap last = new();
        Assert.True(parent.ExtractSubset(first, new SKRectI(0, 0, 1, 1)));
        Assert.True(parent.ExtractSubset(last, new SKRectI(1, 0, 2, 1)));
        Assert.Equal(baseline + 1, SharedPixelBitmap.ActivePinCount);
        parent.Dispose();
        pixels.Dispose();
        Assert.Equal(baseline + 1, SharedPixelBitmap.ActivePinCount);
        Assert.Equal(new SKColor(150, 80, 30), first.GetPixel(0, 0));
        Assert.Equal(SKColors.Red, last.GetPixel(0, 0));
        first.Dispose();
        Assert.Equal(baseline + 1, SharedPixelBitmap.ActivePinCount);
        last.Dispose();
        Assert.Equal(baseline, SharedPixelBitmap.ActivePinCount);
    }

    [Fact]
    public void DisposedInputCannotAllocateAPin()
    {
        int baseline = SharedPixelBitmap.ActivePinCount;
        using PixelBuffer pixels = new(new(1, 1), 4, new byte[4]);
        pixels.Dispose();
        Assert.Throws<ObjectDisposedException>(() => SharedPixelBitmap.Create(pixels));
        Assert.Equal(baseline, SharedPixelBitmap.ActivePinCount);
    }
}
