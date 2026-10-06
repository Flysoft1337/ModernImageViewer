using ModernImageViewer.Imaging;

namespace ModernImageViewer.Application.Images;

public static class PreviewDecodePolicy
{
    public const int MaximumEdge = 4096;
    public const long MaximumBytes = 32L * 1024 * 1024;
    public static PixelSize FallbackTarget { get; } = new(2560, 1600);

    // Viewport dimensions are DIP; decoder targets and budgets are physical BGRA pixels.
    public static PixelSize CalculateTarget(double widthDip, double heightDip, double dpiX, double dpiY)
    {
        if (!double.IsFinite(widthDip) || !double.IsFinite(heightDip) || widthDip <= 0 || heightDip <= 0
            || !double.IsFinite(dpiX) || !double.IsFinite(dpiY) || dpiX <= 0 || dpiY <= 0)
        {
            return FallbackTarget;
        }
        double width = Math.Min(MaximumEdge, Math.Ceiling(widthDip * dpiX));
        double height = Math.Min(MaximumEdge, Math.Ceiling(heightDip * dpiY));
        return Constrain(new PixelSize((int)Math.Max(1, width), (int)Math.Max(1, height)));
    }

    public static PixelSize Constrain(PixelSize target)
    {
        double scale = Math.Min(1, Math.Min((double)MaximumEdge / target.Width, (double)MaximumEdge / target.Height));
        scale = Math.Min(scale, Math.Sqrt((double)(MaximumBytes / 4) / target.PixelCount));
        return new PixelSize(Math.Max(1, (int)Math.Floor(target.Width * scale)),
            Math.Max(1, (int)Math.Floor(target.Height * scale)));
    }

    public static PixelSize FitSource(PixelSize source, PixelSize target)
    {
        double scale = Math.Min(1, Math.Min((double)target.Width / source.Width, (double)target.Height / source.Height));
        return new PixelSize(Math.Max(1, (int)Math.Floor(source.Width * scale)),
            Math.Max(1, (int)Math.Floor(source.Height * scale)));
    }

    public static bool NeedsUpgrade(PixelBuffer image, PixelSize target, bool initialPrefetch = false)
    {
        PixelSize desired = FitSource(image.SourceSize, target);
        if (initialPrefetch) { return desired.Width > image.Size.Width + 1 || desired.Height > image.Size.Height + 1; }
        // Small resize oscillations reuse the existing pixels. Never decode a smaller replacement.
        return desired.Width > image.Size.Width * 1.15 || desired.Height > image.Size.Height * 1.15;
    }
}
