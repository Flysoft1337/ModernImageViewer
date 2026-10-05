using ModernImageViewer.Imaging;

namespace ModernImageViewer.Tests;

public sealed class ViewOrientationTests
{
    [Fact]
    public void EveryRotationAndFlipCombinationMapsAndInvertsSourceCorners()
    {
        PixelSize source = new(320, 180);
        ViewOrientation rotated = default;
        for (int turns = 0; turns < 4; turns++)
        {
            ViewOrientation[] orientations = [rotated, rotated.FlipHorizontal(), rotated.FlipVertical(),
                rotated.FlipHorizontal().FlipVertical()];
            foreach (ViewOrientation orientation in orientations)
            {
                PixelSize display = orientation.GetDisplaySize(source);
                HashSet<(double, double)> corners = [];
                foreach (var point in new (double X, double Y)[] { (0, 0), (320, 0), (0, 180), (320, 180) })
                {
                    var mapped = orientation.ToDisplayPoint(source, point.X, point.Y);
                    Assert.Contains(mapped.X, new double[] { 0, display.Width });
                    Assert.Contains(mapped.Y, new double[] { 0, display.Height });
                    Assert.True(corners.Add(mapped));
                    Assert.Equal(point, orientation.ToSourcePoint(source, mapped.X, mapped.Y));
                }
            }
            rotated = rotated.RotateRight();
        }
        Assert.Equal(default, rotated);
    }

    [Fact]
    public void CommandsComposeOnDisplayedAxesAndRestoreIdentity()
    {
        PixelSize source = new(320, 180);
        ViewOrientation orientation = default;
        orientation = orientation.RotateRight().FlipHorizontal();
        Assert.Equal((0d, 0d), orientation.ToDisplayPoint(source, 0, 0));
        Assert.Equal((180d, 320d), orientation.ToDisplayPoint(source, 320, 180));
        orientation = orientation.RotateRight();
        Assert.Equal((320d, 0d), orientation.ToDisplayPoint(source, 0, 0));
        Assert.Equal((0d, 180d), orientation.ToDisplayPoint(source, 320, 180));
        Assert.Equal(default, orientation.RotateLeft().FlipHorizontal().RotateLeft());
        Assert.True(default(ViewOrientation).RotateRight().RotateRight().FlipHorizontal().FlipVertical().IsIdentity);
        Assert.False(default(ViewOrientation).RotateRight().IsIdentity);
    }

    [Fact]
    public void RegionBoundsRemainInSourceCoordinatesAfterRotationAndFlips()
    {
        PixelSize source = new(4000, 3000);
        PixelRect expected = new(100, 220, 700, 500);
        ViewOrientation rotated = default;
        for (int turns = 0; turns < 4; turns++)
        {
            foreach (ViewOrientation orientation in new[] { rotated, rotated.FlipHorizontal(), rotated.FlipVertical(),
                rotated.FlipHorizontal().FlipVertical() })
            {
                ViewportTransform transform = new();
                PixelSize display = orientation.GetDisplaySize(source);
                int width = turns % 2 == 0 ? expected.Width : expected.Height;
                int height = turns % 2 == 0 ? expected.Height : expected.Width;
                var center = orientation.ToDisplayPoint(source, expected.X + (expected.Width / 2d), expected.Y + (expected.Height / 2d));
                transform.ActualSize(display, width, height);
                transform.Pan((width / 2d) - center.X - transform.OffsetX,
                    (height / 2d) - center.Y - transform.OffsetY);
                Assert.Equal(expected, transform.GetVisibleSourceRegion(source, orientation, width, height));
            }
            rotated = rotated.RotateRight();
        }
    }

    [Fact]
    public void RegionRequestStaysBoundedAndRejectsPannedOffImage()
    {
        PixelSize source = new(8000, 6000);
        ViewportTransform transform = new();
        ViewOrientation orientation = default(ViewOrientation).RotateRight().FlipHorizontal();
        transform.Fit(orientation.GetDisplaySize(source), 1000, 1000);
        PixelRect region = transform.GetVisibleSourceRegion(source, orientation, 1000, 1000)!.Value;
        Assert.Equal(new PixelRect(2976, 1976, 2048, 2048), region);
        transform.Pan(10000, 10000);
        Assert.Null(transform.GetVisibleSourceRegion(source, orientation, 1000, 1000));
    }

    [Fact]
    public void ReorientationPreservesFitActualPixelDensityAndCustomSourceAnchor()
    {
        PixelSize source = new(400, 200);
        ViewOrientation orientation = default(ViewOrientation).RotateRight();
        ViewportTransform transform = new();
        transform.Fit(source, 300, 200);
        transform.Reorient(source, default, orientation, 300, 200);
        Assert.Equal(.5, transform.Scale);
        Assert.Equal(100, transform.OffsetX);
        Assert.Equal(0, transform.OffsetY);

        transform.ActualSize(source, 300, 200, .5);
        transform.Reorient(source, default, orientation, 300, 200, .5);
        Assert.Equal(.5, transform.Scale);
        Assert.Equal(ViewportMode.ActualSize, transform.Mode);

        transform.Pan(20, -15);
        var anchor = orientation.ToSourcePoint(source, (150 - transform.OffsetX) / transform.Scale,
            (100 - transform.OffsetY) / transform.Scale);
        ViewOrientation next = orientation.FlipHorizontal().RotateRight();
        transform.Reorient(source, orientation, next, 300, 200);
        var after = next.ToSourcePoint(source, (150 - transform.OffsetX) / transform.Scale,
            (100 - transform.OffsetY) / transform.Scale);
        Assert.Equal(anchor, after);
        transform.ZoomAt(2, 25, 60);
        var mouseAnchor = next.ToSourcePoint(source, (25 - transform.OffsetX) / transform.Scale,
            (60 - transform.OffsetY) / transform.Scale);
        transform.ZoomAt(2, 25, 60);
        Assert.Equal(mouseAnchor, next.ToSourcePoint(source, (25 - transform.OffsetX) / transform.Scale,
            (60 - transform.OffsetY) / transform.Scale));
    }
}
