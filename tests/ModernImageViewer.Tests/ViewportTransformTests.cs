using ModernImageViewer.Imaging;

namespace ModernImageViewer.Tests;

public sealed class ViewportTransformTests
{
    [Fact]
    public void FitCentersImageAndPreservesAspectRatio()
    {
        ViewportTransform transform = new();

        transform.Fit(new PixelSize(200, 100), 300, 300);

        Assert.Equal(1.5, transform.Scale);
        Assert.Equal(0, transform.OffsetX);
        Assert.Equal(75, transform.OffsetY);
        Assert.Equal(ViewportMode.Fit, transform.Mode);
    }

    [Fact]
    public void ZoomAtKeepsImagePointUnderAnchor()
    {
        ViewportTransform transform = new();
        transform.ActualSize(new PixelSize(100, 100), 100, 100);

        transform.ZoomAt(2, 25, 40);

        Assert.Equal(2, transform.Scale);
        Assert.Equal(-25, transform.OffsetX);
        Assert.Equal(-40, transform.OffsetY);
    }

    [Fact]
    public void PanChangesOffsetAndMode()
    {
        ViewportTransform transform = new();
        transform.Pan(12, -7);

        Assert.Equal(12, transform.OffsetX);
        Assert.Equal(-7, transform.OffsetY);
        Assert.Equal(ViewportMode.Custom, transform.Mode);
    }
}
