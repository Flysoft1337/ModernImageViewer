using ModernImageViewer.Imaging;
using ModernImageViewer.Rendering.Editing;

using SkiaSharp;

namespace ModernImageViewer.UI.Tests;

public sealed class ImageEditEffectsTests
{
    [Fact]
    public void NeutralPaintHasNoFiltersAndLeavesInputPixelsUntouched()
    {
        using SKPaint neutral = ImageEditEffects.CreatePaint(new());
        Assert.Null(neutral.ColorFilter);
        Assert.Null(neutral.ImageFilter);
        using SKBitmap source = new(new SKImageInfo(3, 1, SKColorType.Bgra8888, SKAlphaType.Premul));
        source.SetPixel(0, 0, new(64, 128, 192));
        source.SetPixel(1, 0, new(64, 128, 192, 128));
        source.SetPixel(2, 0, SKColors.Transparent);
        byte[] before = source.Bytes;
        using SKBitmap output = Render(source, new());
        Assert.Equal(before, output.Bytes);
        Assert.Equal(before, source.Bytes);
    }

    [Fact]
    public void ColorControlsProduceRealPixelChangesAndKeepAlpha()
    {
        SKColor gray = new(64, 64, 64, 128);
        SKColor exposed = RenderPixel(gray, new() { Exposure = 1 });
        Assert.InRange(exposed.Red, (byte)126, (byte)130);
        SKColor brighter = RenderPixel(gray, new() { Brightness = 25 });
        Assert.InRange(brighter.Red, (byte)126, (byte)130);
        SKColor contrast = RenderPixel(gray, new() { Contrast = 100 });
        Assert.InRange(contrast.Red, (byte)0, (byte)2);
        SKColor gamma = RenderPixel(gray, new() { Gamma = 2 });
        Assert.InRange(gamma.Red, (byte)126, (byte)130);
        SKColor desaturated = RenderPixel(new(192, 64, 32, 128), new() { Saturation = -100 });
        Assert.InRange(Math.Abs(desaturated.Red - desaturated.Green), 0, 2);
        Assert.InRange(Math.Abs(desaturated.Red - desaturated.Blue), 0, 2);
        SKColor warm = RenderPixel(new(128, 128, 128, 128), new() { Temperature = 100 });
        Assert.True(warm.Red > warm.Green && warm.Green > warm.Blue);
        foreach (SKColor pixel in new[] { exposed, brighter, contrast, gamma, desaturated, warm })
        {
            Assert.Equal((byte)128, pixel.Alpha);
        }
        SKColor combined = RenderPixel(gray, new() { Exposure = 1, Gamma = 2 });
        Assert.True(combined.Red > exposed.Red && combined.Red > gamma.Red);
        Assert.Equal((byte)128, combined.Alpha);
        using SKBitmap transparentSource = new(new SKImageInfo(1, 1, SKColorType.Bgra8888, SKAlphaType.Premul));
        transparentSource.SetPixel(0, 0, SKColors.Transparent);
        using SKBitmap transparentOutput = Render(transparentSource, new() { Brightness = 100, Gamma = 5 });
        Assert.Equal((byte)0, transparentOutput.GetPixel(0, 0).Alpha);
        Assert.Equal(new byte[4], transparentOutput.Bytes);
    }

    [Fact]
    public void SpatialFiltersWorkAcrossComposedTileBoundariesAndLeaveSourceUntouched()
    {
        using SKBitmap source = new(new SKImageInfo(25, 25, SKColorType.Bgra8888, SKAlphaType.Premul));
        source.Erase(new SKColor(100, 100, 100, 128));
        source.SetPixel(12, 12, new(160, 160, 160, 128));
        byte[] before = source.Bytes;
        using SKBitmap sharpen = Render(source, new() { Sharpen = 100 });
        Assert.True(sharpen.GetPixel(12, 12).Red > source.GetPixel(12, 12).Red);
        Assert.Equal((byte)128, sharpen.GetPixel(12, 12).Alpha);
        using SKBitmap blur = Render(source, new() { Blur = 2 });
        Assert.True(blur.GetPixel(12, 12).Red < source.GetPixel(12, 12).Red);
        Assert.True(blur.GetPixel(13, 12).Red > source.GetPixel(13, 12).Red);
        using SKBitmap tiled = Render(source, new() { Blur = 2, Sharpen = 30 }, split: true);
        using SKBitmap whole = Render(source, new() { Blur = 2, Sharpen = 30 });
        Assert.Equal(whole.Bytes, tiled.Bytes);
        Assert.Equal(before, source.Bytes);
    }

    [Fact]
    public void PreviewPixelScaleReducesBlurRadius()
    {
        using SKBitmap source = new(new SKImageInfo(25, 25, SKColorType.Bgra8888, SKAlphaType.Premul));
        source.Erase(SKColors.Black);
        source.SetPixel(12, 12, SKColors.White);
        using SKBitmap full = Render(source, new(Blur: 2));
        using SKBitmap preview = Render(source, new(Blur: 2), pixelScale: .5);
        Assert.True(preview.GetPixel(12, 12).Red > full.GetPixel(12, 12).Red);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void InvalidPixelScaleIsRejected(double scale) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => ImageEditEffects.CreatePaint(new(), scale));

    private static SKColor RenderPixel(SKColor color, ImageEditAdjustments adjustments)
    {
        using SKBitmap source = new(new SKImageInfo(1, 1, SKColorType.Bgra8888, SKAlphaType.Premul));
        source.SetPixel(0, 0, color);
        using SKBitmap output = Render(source, adjustments);
        return output.GetPixel(0, 0);
    }

    private static SKBitmap Render(SKBitmap source, ImageEditAdjustments adjustments, bool split = false, double pixelScale = 1)
    {
        SKBitmap output = new(source.Info);
        using SKCanvas canvas = new(output);
        canvas.Clear(SKColors.Transparent);
        using SKPaint paint = ImageEditEffects.CreatePaint(adjustments, pixelScale);
        canvas.SaveLayer(paint);
        if (split)
        {
            for (int x = 0; x < source.Width; x += 12)
            {
                canvas.Save();
                canvas.ClipRect(new(x, 0, Math.Min(source.Width, x + 12), source.Height));
                canvas.DrawBitmap(source, 0, 0, new SKSamplingOptions(SKFilterMode.Nearest));
                canvas.Restore();
            }
        }
        else { canvas.DrawBitmap(source, 0, 0, new SKSamplingOptions(SKFilterMode.Nearest)); }
        canvas.Restore();
        return output;
    }
}
