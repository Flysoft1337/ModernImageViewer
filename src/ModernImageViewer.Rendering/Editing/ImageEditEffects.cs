using ModernImageViewer.Imaging;

using SkiaSharp;

namespace ModernImageViewer.Rendering.Editing;

/// <summary>Shared preview/export filters for a fully composed source layer.</summary>
public static class ImageEditEffects
{
    public static SKPaint CreatePaint(ImageEditAdjustments adjustments, double pixelScale = 1)
    {
        ArgumentNullException.ThrowIfNull(adjustments);
        if (!double.IsFinite(pixelScale) || pixelScale <= 0 || pixelScale > float.MaxValue / 20d)
        {
            throw new ArgumentOutOfRangeException(nameof(pixelScale));
        }
        SKPaint paint = new();
        try
        {
            if (adjustments.IsIdentity) { return paint; }
            using SKColorFilter? colors = CreateColorFilter(adjustments);
            paint.ColorFilter = colors;
            using SKImageFilter? blur = adjustments.Blur > 0
                ? SKImageFilter.CreateBlur((float)(adjustments.Blur * pixelScale), (float)(adjustments.Blur * pixelScale), SKShaderTileMode.Decal)
                : null;
            if (adjustments.Sharpen > 0)
            {
                int step = (int)Math.Clamp(Math.Round(pixelScale), 1, 32);
                int edge = (step * 2) + 1;
                float amount = (float)(adjustments.Sharpen / 100 * Math.Min(1, pixelScale));
                float[] kernel = new float[edge * edge];
                kernel[(step * edge) + step] = 1 + (4 * amount);
                kernel[step] = -amount;
                kernel[(edge - 1) * edge + step] = -amount;
                kernel[step * edge] = -amount;
                kernel[(step * edge) + edge - 1] = -amount;
                // Preserve alpha rather than convolving it along with the sharpened RGB.
                using SKImageFilter sharpen = SKImageFilter.CreateMatrixConvolution(new(edge, edge), kernel, 1, 0,
                    new(step, step), SKShaderTileMode.Clamp, false, blur);
                paint.ImageFilter = sharpen;
            }
            else
            {
                paint.ImageFilter = blur;
            }
            return paint;
        }
        catch
        {
            paint.Dispose();
            throw;
        }
    }

    private static SKColorFilter? CreateColorFilter(ImageEditAdjustments adjustments)
    {
        bool matrixNeeded = adjustments.Exposure != 0 || adjustments.Brightness != 0 || adjustments.Contrast != 0
            || adjustments.Saturation != 0 || adjustments.Temperature != 0;
        SKColorFilter? matrix = matrixNeeded ? SKColorFilter.CreateColorMatrix(CreateColorMatrix(adjustments)) : null;
        if (adjustments.Gamma == 1) { return matrix; }
        try
        {
            SKColorFilter gamma = CreateGamma(adjustments.Gamma);
            if (matrix is null) { return gamma; }
            using (gamma) { return SKColorFilter.CreateCompose(gamma, matrix); }
        }
        finally
        {
            matrix?.Dispose();
        }
    }

    private static float[] CreateColorMatrix(ImageEditAdjustments adjustments)
    {
        double contrast = 1 + (adjustments.Contrast / 100);
        double gain = Math.Pow(2, adjustments.Exposure) * contrast;
        double offset = (.5 * (1 - contrast)) + (adjustments.Brightness / 100);
        double saturation = 1 + (adjustments.Saturation / 100);
        double desaturation = 1 - saturation;
        double warmth = adjustments.Temperature / 400;
        return
        [
            (float)(gain * ((desaturation * .2126) + saturation)), (float)(gain * desaturation * .7152), (float)(gain * desaturation * .0722), 0, (float)(offset + warmth),
            (float)(gain * desaturation * .2126), (float)(gain * ((desaturation * .7152) + saturation)), (float)(gain * desaturation * .0722), 0, (float)offset,
            (float)(gain * desaturation * .2126), (float)(gain * desaturation * .7152), (float)(gain * ((desaturation * .0722) + saturation)), 0, (float)(offset - warmth),
            0, 0, 0, 1, 0,
        ];
    }

    private static SKColorFilter CreateGamma(double gamma)
    {
        byte[] alpha = new byte[256];
        byte[] rgb = new byte[256];
        for (int index = 0; index < rgb.Length; index++)
        {
            alpha[index] = (byte)index;
            rgb[index] = (byte)Math.Clamp(Math.Round(Math.Pow(index / 255d, 1 / gamma) * 255), 0, 255);
        }
        return SKColorFilter.CreateTable(alpha, rgb, rgb, rgb);
    }
}
