using ModernImageViewer.Application.Editing;
using ModernImageViewer.Imaging;

namespace ModernImageViewer.Tests;

public sealed class ImageEditSessionTests
{
    [Fact]
    public void SuccessfulExportCheckpointFollowsUndoRedoAndReset()
    {
        ImageEditSession session = new(new(100, 80));
        Assert.False(session.HasUnexportedChanges);
        session.Apply(session.Current.WithCrop(new(10, 10, 50, 40)));
        Assert.True(session.HasUnexportedChanges);
        session.MarkExported();
        Assert.False(session.HasUnexportedChanges);
        session.Undo();
        Assert.True(session.HasUnexportedChanges);
        session.Redo();
        Assert.False(session.HasUnexportedChanges);
        session.Apply(session.Current.FlipHorizontal());
        Assert.True(session.HasUnexportedChanges);
        session.Undo();
        Assert.False(session.HasUnexportedChanges);
        session.Reset();
        Assert.True(session.HasUnexportedChanges);
    }

    [Fact]
    public void CropRotateResizeMapsOriginalCornersAndInverseRegionCorrectly()
    {
        ViewOrientation direction = default;
        for (int turns = 0; turns < 4; turns++)
        {
            foreach (ViewOrientation orientation in new[] { direction, direction.FlipHorizontal(), direction.FlipVertical(), direction.FlipHorizontal().FlipVertical() })
            {
                ImageEditRecipe recipe = new(new(4000, 3000), new(100, 220, 700, 500), orientation, new(300, 200));
                HashSet<(double, double)> corners = [];
                foreach (var source in new (double X, double Y)[] { (100, 220), (800, 220), (100, 720), (800, 720) })
                {
                    var output = recipe.ToOutput(source.X, source.Y);
                    Assert.InRange(Math.Min(Math.Abs(output.X), Math.Abs(output.X - 300)), 0, .000001);
                    Assert.InRange(Math.Min(Math.Abs(output.Y), Math.Abs(output.Y - 200)), 0, .000001);
                    Assert.True(corners.Add(output));
                    var inverse = recipe.ToSource(output.X, output.Y);
                    Assert.Equal(source.X, inverse.X, 8);
                    Assert.Equal(source.Y, inverse.Y, 8);
                }
                ViewportTransform viewport = new();
                viewport.Fit(recipe.OutputSize, 300, 200);
                Assert.Equal(recipe.Crop, recipe.GetVisibleSourceRegion(viewport, 300, 200));
            }
            direction = direction.RotateRight();
        }
    }

    [Fact]
    public void UndoRedoBranchesAndBoundedHistoryStoreRecipesOnly()
    {
        ImageEditSession session = new(new(400, 200));
        ImageEditRecipe original = session.Current;
        session.Apply(session.Current.WithCrop(new(20, 10, 100, 50)));
        ImageEditRecipe cropped = session.Current;
        session.Apply(session.Current.RotateRight().WithSize(new(25, 50)));
        ImageEditRecipe resized = session.Current;
        session.Undo();
        Assert.Equal(cropped, session.Current);
        session.Undo();
        Assert.Equal(original, session.Current);
        session.Redo(); session.Redo();
        Assert.Equal(resized, session.Current);
        session.Undo();
        session.Apply(session.Current.FlipHorizontal());
        Assert.False(session.CanRedo);
        session.Reset();
        Assert.False(session.IsModified);
        for (int i = 0; i < ImageEditSession.HistoryLimit + 20; i++) { session.Apply(session.Current.RotateRight()); }
        int undos = 0;
        while (session.CanUndo) { session.Undo(); undos++; }
        Assert.Equal(ImageEditSession.HistoryLimit, undos);
    }

    [Fact]
    public void InvalidAndDefaultGeometryCannotEnterRecipe()
    {
        PixelSize size = new(100, 100);
        PixelRect crop = new(0, 0, 100, 100);
        Assert.Throws<ArgumentOutOfRangeException>(() => new ImageEditRecipe(default, crop, default, size));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ImageEditRecipe(size, default, default, size));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ImageEditRecipe(size, crop, default, default));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ImageEditRecipe(size, new(90, 0, 20, 10), default, size));
    }
}
