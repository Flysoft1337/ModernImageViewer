using ModernImageViewer.Imaging;

namespace ModernImageViewer.Tests;

public sealed class ViewportDpiTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(1.25)]
    [InlineData(1.5)]
    [InlineData(2)]
    public void FitCentersSourcePixelsInDipWithoutUsingDecodedPreviewSize(double dpi)
    {
        PixelSize source = new(2400, 1200);
        ViewportTransform transform = new();
        transform.Fit(source, 800, 600);

        Assert.Equal(1d / 3, transform.Scale, 10);
        Assert.Equal(0, transform.OffsetX, 10);
        Assert.Equal(100, transform.OffsetY, 10);
        Assert.Equal(800 * dpi, source.Width * transform.Scale * dpi, 8);
        Assert.Equal(400 * dpi, source.Height * transform.Scale * dpi, 8);
        Assert.Equal(ViewportMode.Fit, transform.Mode);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(1.25)]
    [InlineData(1.5)]
    [InlineData(2)]
    public void ActualSizeUsesOnePhysicalPixelPerSourcePixel(double dpi)
    {
        ViewportTransform transform = new();
        transform.ActualSize(new(1200, 800), 800, 600, 1 / dpi);

        Assert.Equal(1, transform.Scale * dpi, 10);
        Assert.Equal((800 - (1200 / dpi)) / 2, transform.OffsetX, 10);
        Assert.Equal((600 - (800 / dpi)) / 2, transform.OffsetY, 10);
        Assert.Equal(ViewportMode.ActualSize, transform.Mode);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(1.25)]
    [InlineData(1.5)]
    [InlineData(2)]
    public void MouseAnchorZoomAndDipPanRemainStable(double dpi)
    {
        ViewportTransform transform = new();
        transform.ActualSize(new(1200, 800), 800, 600, 1 / dpi);
        double sourceX = (237 - transform.OffsetX) / transform.Scale;
        double sourceY = (163 - transform.OffsetY) / transform.Scale;
        transform.ZoomAt(1.6, 237, 163);

        Assert.Equal(sourceX, (237 - transform.OffsetX) / transform.Scale, 8);
        Assert.Equal(sourceY, (163 - transform.OffsetY) / transform.Scale, 8);
        Assert.Equal(1.6, transform.Scale * dpi, 10);
        double oldX = transform.OffsetX;
        double oldY = transform.OffsetY;
        transform.Pan(23, -41);
        Assert.Equal(oldX + 23, transform.OffsetX, 10);
        Assert.Equal(oldY - 41, transform.OffsetY, 10);
        Assert.Equal(23 * dpi, (transform.OffsetX - oldX) * dpi, 8);
        Assert.Equal(ViewportMode.Custom, transform.Mode);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(1.25)]
    [InlineData(1.5)]
    [InlineData(2)]
    public void RotationAndFlipPreserveCustomSourceAnchorAndActualPixelDensity(double dpi)
    {
        PixelSize source = new(1200, 800);
        ViewOrientation next = default(ViewOrientation).RotateRight().FlipHorizontal();
        ViewportTransform transform = new();
        transform.ActualSize(source, 800, 600, 1 / dpi);
        transform.Reorient(source, default, next, 800, 600, 1 / dpi);
        Assert.Equal(1, transform.Scale * dpi, 10);
        Assert.Equal(ViewportMode.ActualSize, transform.Mode);
        transform.ZoomAt(1.7, 201, 155);
        transform.Pan(37, -29);
        var before = next.ToSourcePoint(source, (400 - transform.OffsetX) / transform.Scale,
            (300 - transform.OffsetY) / transform.Scale);
        ViewOrientation last = next.RotateLeft().FlipVertical();
        transform.Reorient(source, next, last, 800, 600, 1 / dpi);
        var after = last.ToSourcePoint(source, (400 - transform.OffsetX) / transform.Scale,
            (300 - transform.OffsetY) / transform.Scale);
        Assert.Equal(before.X, after.X, 8);
        Assert.Equal(before.Y, after.Y, 8);
        Assert.Equal(1.7, transform.Scale * dpi, 10);
        Assert.NotNull(transform.GetVisibleSourceRegion(source, last, 800, 600));
    }

    [Fact]
    public void InvalidLayoutAndPointerValuesDoNotPoisonTransform()
    {
        ViewportTransform transform = new();
        transform.ActualSize(new(1200, 800), 800, 600);
        double offsetX = transform.OffsetX;
        double offsetY = transform.OffsetY;

        transform.Fit(new(1200, 800), double.NaN, 600);
        transform.ActualSize(new(1200, 800), 800, 600, double.PositiveInfinity);
        transform.ZoomAt(double.NaN, 20, 20);
        transform.ZoomAt(2, double.NaN, 20);
        transform.Pan(10, double.NaN);
        transform.Reorient(new(1200, 800), default, default(ViewOrientation).RotateRight(), 0, 600);

        Assert.Equal(1, transform.Scale);
        Assert.Equal(offsetX, transform.OffsetX);
        Assert.Equal(offsetY, transform.OffsetY);
        Assert.Equal(ViewportMode.ActualSize, transform.Mode);
        Assert.Null(transform.GetVisibleSourceRegion(new(1200, 800), default, double.NaN, 600));
    }
}
