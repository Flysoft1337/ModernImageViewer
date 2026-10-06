using System.Collections.Immutable;

using ModernImageViewer.Imaging;

namespace ModernImageViewer.Tests;

public sealed class ImageEditGeometryTests
{
    [Theory]
    [InlineData(17.5)]
    [InlineData(-45)]
    [InlineData(90)]
    [InlineData(180)]
    [InlineData(359.75)]
    public void ArbitraryAnglesFitCropAndRoundTripThroughEveryDirection(double angle)
    {
        ViewOrientation direction = default;
        for (int turn = 0; turn < 4; turn++, direction = direction.RotateRight())
        {
            foreach (ViewOrientation orientation in new[] { direction, direction.FlipHorizontal(), direction.FlipVertical(), direction.FlipHorizontal().FlipVertical() })
            {
                ImageEditRecipe recipe = new ImageEditRecipe(new(500, 400), new(20, 30, 200, 100), orientation,
                    orientation.GetDisplaySize(new(200, 100))).WithRotation(angle);
                Assert.Equal(recipe.NaturalSize, recipe.OutputSize);
                foreach (var point in new (double X, double Y)[] { (20, 30), (220, 30), (20, 130), (220, 130), (120, 80) })
                {
                    var output = recipe.ToOutput(point.X, point.Y);
                    Assert.InRange(output.X, -.000001, recipe.OutputSize.Width + .000001);
                    Assert.InRange(output.Y, -.000001, recipe.OutputSize.Height + .000001);
                    var source = recipe.ToSource(output.X, output.Y);
                    Assert.Equal(point.X, source.X, 8);
                    Assert.Equal(point.Y, source.Y, 8);
                    ImageEditRecipe stretched = recipe.WithSize(new(53, 137));
                    var resized = stretched.ToOutput(point.X, point.Y);
                    var roundTrip = stretched.ToSource(resized.X, resized.Y);
                    Assert.Equal(point.X, roundTrip.X, 8);
                    Assert.Equal(point.Y, roundTrip.Y, 8);
                }
            }
        }
    }

    [Fact]
    public void RotationExpandsAndQuarterTurnsDoNotAddRoundingPixels()
    {
        ImageEditRecipe recipe = ImageEditRecipe.Create(new(200, 100));
        Assert.Equal(new PixelSize(213, 213), recipe.WithRotation(45).NaturalSize);
        Assert.Equal(new PixelSize(100, 200), recipe.WithRotation(90).NaturalSize);
        Assert.Equal(new PixelSize(200, 100), recipe.WithRotation(180).NaturalSize);
        Assert.Equal(recipe.WithRotation(45), recipe.WithRotation(405));
        Assert.Equal(recipe, recipe.WithRotation(360));
        Assert.Throws<ArgumentOutOfRangeException>(() => recipe.WithRotation(double.NaN));
        Assert.Throws<ArgumentOutOfRangeException>(() => recipe.WithRotation(double.PositiveInfinity));
    }

    [Fact]
    public void EquivalentRotationDoesNotDiscardResizedOutput()
    {
        ImageEditRecipe resized = ImageEditRecipe.Create(new(200, 100)).WithRotation(-180).WithSize(new(40, 20));
        Assert.Same(resized, resized.WithRotation(180));
        Assert.Same(resized, resized.WithRotation(-180));
        ImageEditRecipe unrotated = ImageEditRecipe.Create(new(200, 100)).WithSize(new(40, 20));
        Assert.Same(unrotated, unrotated.WithRotation(360));
    }

    [Fact]
    public void ResizedRotationPreservesAxisDensitiesAndFlipsKeepOutputSize()
    {
        ImageEditRecipe original = ImageEditRecipe.Create(new(2000, 1000));
        ImageEditRecipe rotated = original.WithSize(new(200, 80)).WithRotation(45);
        Assert.Equal(new PixelSize(2122, 2122), rotated.NaturalSize);
        Assert.Equal(new PixelSize(212, 170), rotated.OutputSize);
        Assert.Equal(rotated.OutputSize, rotated.FlipHorizontal().OutputSize);
        Assert.Equal(rotated.OutputSize, rotated.FlipVertical().OutputSize);
        Assert.Equal(new PixelSize(170, 212), rotated.RotateRight().OutputSize);
        Assert.Equal(new PixelSize(100, 160), original.WithSize(new(200, 80)).WithRotation(90).OutputSize);
        Assert.Equal(new PixelSize(212, 212), original.WithSize(new(200, 100)).WithRotation(45).OutputSize);
        Assert.Equal(rotated.NaturalSize, original.WithRotation(45).OutputSize);
    }

    [Fact]
    public void FourCornerRoiContainsVisibleAreaAtFortyFiveDegrees()
    {
        ImageEditRecipe recipe = ImageEditRecipe.Create(new(200, 100)).WithRotation(45);
        ViewportTransform viewport = new();
        viewport.ActualSize(recipe.OutputSize, 20, 20);
        Assert.Equal(new PixelRect(85, 35, 30, 30), recipe.GetVisibleSourceRegion(viewport, 20, 20));
        Assert.Equal(new PixelRect(90, 40, 20, 20), recipe.GetVisibleSourceRegion(viewport, 20, 20, maximumEdge: 20));
        viewport.Fit(recipe.OutputSize, 300, 300);
        Assert.Equal(recipe.Crop, recipe.GetVisibleSourceRegion(viewport, 300, 300));
        Assert.Null(recipe.GetVisibleSourceRegion(viewport, double.NaN, 300));
    }

    [Fact]
    public void ExistingOrientationIsAppliedBeforeArbitraryRotation()
    {
        ViewOrientation orientation = new ViewOrientation().RotateRight().FlipHorizontal();
        ImageEditRecipe recipe = ImageEditRecipe.Create(new(200, 100), orientation).WithRotation(30);
        var oriented = orientation.ToDisplayPoint(recipe.Crop.Size, 10, 20);
        PixelSize orientedSize = orientation.GetDisplaySize(recipe.Crop.Size);
        double x = oriented.X - (orientedSize.Width / 2d);
        double y = oriented.Y - (orientedSize.Height / 2d);
        double cosine = Math.Sqrt(3) / 2;
        var output = recipe.ToOutput(10, 20);
        Assert.Equal((cosine * x) - (.5 * y) + (recipe.NaturalSize.Width / 2d), output.X, 8);
        Assert.Equal((.5 * x) + (cosine * y) + (recipe.NaturalSize.Height / 2d), output.Y, 8);
    }

    [Fact]
    public void RotateAndFlipOperateOnDisplayedAxesAndKeepAllEditParameters()
    {
        ImageEditAdjustments adjustments = new() { Exposure = 1, Gamma = 2 };
        ImmutableArray<ImageAnnotation> annotations = [new(ImageAnnotationKind.Rectangle, [new(10, 10), new(30, 25)])];
        ImageEditRecipe recipe = ImageEditRecipe.Create(new(200, 100)).WithRotation(33)
            .WithAdjustments(adjustments).WithAnnotations(annotations).WithSize(new(71, 103));
        var point = recipe.ToOutput(10, 20);
        var horizontal = recipe.FlipHorizontal().ToOutput(10, 20);
        var vertical = recipe.FlipVertical().ToOutput(10, 20);
        var clockwise = recipe.RotateRight().ToOutput(10, 20);
        Assert.Equal(recipe.OutputSize.Width - point.X, horizontal.X, 8);
        Assert.Equal(point.Y, horizontal.Y, 8);
        Assert.Equal(point.X, vertical.X, 8);
        Assert.Equal(recipe.OutputSize.Height - point.Y, vertical.Y, 8);
        Assert.Equal(recipe.OutputSize.Height - point.Y, clockwise.X, 8);
        Assert.Equal(point.X, clockwise.Y, 8);
        Assert.Equal(recipe, recipe.RotateRight().RotateLeft());
        Assert.Equal(recipe, recipe.FlipHorizontal().FlipHorizontal());
        Assert.Equal(-33, recipe.FlipHorizontal().RotationDegrees);
        Assert.Equal(-33, recipe.FlipVertical().RotationDegrees);
        foreach (ImageEditRecipe changed in new[] { recipe.WithCrop(new(5, 5, 100, 70)), recipe.RotateRight(), recipe.RotateLeft(), recipe.FlipHorizontal(), recipe.FlipVertical() })
        {
            Assert.Equal(33, Math.Abs(changed.RotationDegrees));
            Assert.Same(adjustments, changed.Adjustments);
            Assert.Equal(annotations, changed.Annotations);
        }
        Assert.Empty(recipe.WithAnnotations(default).Annotations);
        Assert.Throws<ArgumentOutOfRangeException>(() => recipe.WithAnnotations([.. Enumerable.Repeat(annotations[0], ImageEditRecipe.MaximumAnnotations + 1)]));
        Assert.Throws<ArgumentOutOfRangeException>(() => recipe.WithAnnotations([null!]));
    }
}
