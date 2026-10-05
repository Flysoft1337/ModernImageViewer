using ModernImageViewer.Application.Editing;
using ModernImageViewer.Imaging;

namespace ModernImageViewer.Tests;

public sealed class ImageCropGeometryTests
{
    [Fact]
    public void PresetsFitDisplayedAxesInsideExistingOffsetCropAndCanBeUndone()
    {
        PixelRect existing = new(71, 39, 800, 600);
        foreach (ViewOrientation orientation in Orientations())
        {
            foreach (PixelSize ratio in new PixelSize[] { new(1, 1), new(4, 3), new(3, 2), new(16, 9), new(9, 16) })
            {
                PixelRect crop = ImageCropGeometry.Fit(existing, orientation, ratio);
                Assert.InRange(crop.X, existing.X, existing.Right - crop.Width);
                Assert.InRange(crop.Y, existing.Y, existing.Bottom - crop.Height);
                PixelSize displayed = orientation.GetDisplaySize(crop.Size);
                Assert.InRange(Math.Abs(displayed.Width - ((double)displayed.Height * ratio.Width / ratio.Height)),
                    0, Math.Max(1, (double)ratio.Width / ratio.Height));
                Assert.InRange(Math.Abs((2 * crop.X) + crop.Width - ((2 * existing.X) + existing.Width)), 0, 1);
                Assert.InRange(Math.Abs((2 * crop.Y) + crop.Height - ((2 * existing.Y) + existing.Height)), 0, 1);
                ImageEditSession session = new(new(1000, 800), orientation);
                session.Apply(session.Current.WithCrop(existing));
                ImageEditRecipe original = session.Current;
                session.Apply(session.Current.WithCrop(crop));
                if (crop == existing) { Assert.Equal(original, session.Current); continue; }
                session.Undo();
                Assert.Equal(original, session.Current);
                session.Redo();
                Assert.Equal(crop, session.Current.Crop);
            }
        }
    }

    [Fact]
    public void LockedDragHandlesAllDirectionsRotationsAndFlipsInSourceCoordinates()
    {
        PixelSize source = new(800, 600);
        foreach (ViewOrientation orientation in Orientations())
        {
            var start = orientation.ToSourcePoint(source, 300, 300);
            foreach (int dx in new[] { -1, 1 })
            {
                foreach (int dy in new[] { -1, 1 })
                {
                    var end = orientation.ToSourcePoint(source, 300 + (160 * dx), 300 + (180 * dy));
                    PixelRect crop = ImageCropGeometry.Drag(source, orientation, start, end, new(16, 9))!.Value;
                    Assert.Equal(new PixelSize(160, 90), orientation.GetDisplaySize(crop.Size));
                    var first = orientation.ToDisplayPoint(source, crop.X, crop.Y);
                    var last = orientation.ToDisplayPoint(source, crop.Right, crop.Bottom);
                    Assert.Equal(dx > 0 ? 300 : 140, Math.Min(first.X, last.X));
                    Assert.Equal(dy > 0 ? 300 : 210, Math.Min(first.Y, last.Y));
                }
            }
        }
    }

    [Fact]
    public void OriginalRatioClipsToEdgesAndFreeDragRetainsSourcePixelBounds()
    {
        PixelSize source = new(800, 600);
        foreach (ViewOrientation orientation in Orientations())
        {
            PixelSize ratio = orientation.GetDisplaySize(source);
            PixelRect crop = ImageCropGeometry.Drag(source, orientation, (0, 0), (900, 900), ratio)!.Value;
            Assert.Equal(new PixelRect(0, 0, 800, 600), crop);
            Assert.Equal(new PixelRect(12, 33, 67, 68), ImageCropGeometry.Drag(source, orientation, (12.2, 33.8), (78.4, 100.1)));
            Assert.Null(ImageCropGeometry.Drag(source, orientation, (5, 5), (5, 100), new(1, 1)));
        }
        Assert.Equal(new PixelRect(0, 0, 1, 1), ImageCropGeometry.Fit(new(0, 0, 1, 1), default, new(16, 9)));
        Assert.Throws<ArgumentOutOfRangeException>(() => ImageCropGeometry.Fit(new(0, 0, 1, 1), default, default));
    }

    private static IEnumerable<ViewOrientation> Orientations()
    {
        ViewOrientation direction = default;
        for (int turn = 0; turn < 4; turn++)
        {
            yield return direction;
            yield return direction.FlipHorizontal();
            yield return direction.FlipVertical();
            yield return direction.FlipHorizontal().FlipVertical();
            direction = direction.RotateRight();
        }
    }
}
