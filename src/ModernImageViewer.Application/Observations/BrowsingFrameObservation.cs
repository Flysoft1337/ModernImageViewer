using ModernImageViewer.Application.Images;
using ModernImageViewer.Imaging;

namespace ModernImageViewer.Application.Observations;

public static class BrowsingFrameObservation
{
    public static bool IsCurrent(ImageOpenState expected, ImageOpenState painted, long requestId) =>
        expected.Status == ImageOpenStatus.Loaded && painted.Status == ImageOpenStatus.Loaded
        && expected.RequestId == requestId && painted.RequestId == requestId
        && expected.Source?.Identity == painted.Source?.Identity
        && expected.Image is not null && ReferenceEquals(expected.Image, painted.Image)
        && ReferenceEquals(expected.Region, painted.Region);

    public static bool HasRequiredDetail(ImageOpenState state, double physicalPixelsPerSourcePixel, PixelRect? visible)
    {
        if (state.Image is not { } image || state.IsRefining || state.IsRegionLoading
            || !double.IsFinite(physicalPixelsPerSourcePixel) || physicalPixelsPerSourcePixel <= 0) { return false; }
        double needed = Math.Min(1, physicalPixelsPerSourcePixel);
        // Aspect-ratio rounding has at most one physical output pixel of tolerance per axis.
        if (image.SourceSize.Width * needed <= image.Size.Width + 1.0
            && image.SourceSize.Height * needed <= image.Size.Height + 1.0) { return true; }
        if (state.Region is not { } region || visible is not { } bounds) { return false; }
        return region.Bounds.X <= bounds.X && region.Bounds.Y <= bounds.Y
            && region.Bounds.Right >= bounds.Right && region.Bounds.Bottom >= bounds.Bottom
            && region.Bounds.Width * needed <= region.Image.Size.Width + 1.0
            && region.Bounds.Height * needed <= region.Image.Size.Height + 1.0;
    }
}
