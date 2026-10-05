namespace ModernImageViewer.Imaging;

/// <summary>Immutable edits in EXIF-corrected source coordinates; no pixel storage.</summary>
public sealed record ImageEditRecipe
{
    public ImageEditRecipe(PixelSize sourceSize, PixelRect crop, ViewOrientation orientation, PixelSize outputSize)
    {
        if (sourceSize.Width <= 0 || sourceSize.Height <= 0 || crop.Width <= 0 || crop.Height <= 0
            || outputSize.Width <= 0 || outputSize.Height <= 0
            || crop.Right > sourceSize.Width || crop.Bottom > sourceSize.Height)
        {
            throw new ArgumentOutOfRangeException(nameof(crop));
        }
        ImageDecodeLimits.Default.ValidateAndGetStride(sourceSize);
        SourceSize = sourceSize;
        Crop = crop;
        Orientation = orientation;
        OutputSize = outputSize;
    }

    public PixelSize SourceSize { get; }
    public PixelRect Crop { get; }
    public ViewOrientation Orientation { get; }
    public PixelSize OutputSize { get; }
    public PixelSize NaturalSize => Orientation.GetDisplaySize(Crop.Size);

    public static ImageEditRecipe Create(PixelSize source, ViewOrientation orientation = default) =>
        new(source, new PixelRect(0, 0, source.Width, source.Height), orientation, orientation.GetDisplaySize(source));

    public ImageEditRecipe WithCrop(PixelRect crop) => new(SourceSize, crop, Orientation, Orientation.GetDisplaySize(crop.Size));
    public ImageEditRecipe WithSize(PixelSize output) => new(SourceSize, Crop, Orientation, output);
    public ImageEditRecipe RotateRight() => new(SourceSize, Crop, Orientation.RotateRight(), new(OutputSize.Height, OutputSize.Width));
    public ImageEditRecipe RotateLeft() => new(SourceSize, Crop, Orientation.RotateLeft(), new(OutputSize.Height, OutputSize.Width));
    public ImageEditRecipe FlipHorizontal() => new(SourceSize, Crop, Orientation.FlipHorizontal(), OutputSize);
    public ImageEditRecipe FlipVertical() => new(SourceSize, Crop, Orientation.FlipVertical(), OutputSize);

    public (double M11, double M12, double M21, double M22, double OffsetX, double OffsetY) GetMatrix()
    {
        var matrix = Orientation.GetMatrix(Crop.Size);
        PixelSize natural = NaturalSize;
        double scaleX = (double)OutputSize.Width / natural.Width;
        double scaleY = (double)OutputSize.Height / natural.Height;
        return (matrix.M11 * scaleX, matrix.M12 * scaleY, matrix.M21 * scaleX, matrix.M22 * scaleY,
            (matrix.OffsetX - (matrix.M11 * Crop.X) - (matrix.M21 * Crop.Y)) * scaleX,
            (matrix.OffsetY - (matrix.M12 * Crop.X) - (matrix.M22 * Crop.Y)) * scaleY);
    }

    public (double X, double Y) ToOutput(double x, double y)
    {
        var m = GetMatrix();
        return ((m.M11 * x) + (m.M21 * y) + m.OffsetX, (m.M12 * x) + (m.M22 * y) + m.OffsetY);
    }

    public (double X, double Y) ToSource(double x, double y)
    {
        var m = GetMatrix();
        double determinant = (m.M11 * m.M22) - (m.M12 * m.M21);
        x -= m.OffsetX;
        y -= m.OffsetY;
        return (((m.M22 * x) - (m.M21 * y)) / determinant, ((m.M11 * y) - (m.M12 * x)) / determinant);
    }

    public PixelRect? GetVisibleSourceRegion(ViewportTransform viewport, double width, double height, int maximumEdge = 2048)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumEdge);
        if (width <= 0 || height <= 0 || viewport.Scale <= 0) { return null; }
        var first = ToSource(-viewport.OffsetX / viewport.Scale, -viewport.OffsetY / viewport.Scale);
        var opposite = ToSource((width - viewport.OffsetX) / viewport.Scale, (height - viewport.OffsetY) / viewport.Scale);
        int left = (int)Math.Clamp(Math.Floor(Math.Min(first.X, opposite.X)), Crop.X, Crop.Right);
        int top = (int)Math.Clamp(Math.Floor(Math.Min(first.Y, opposite.Y)), Crop.Y, Crop.Bottom);
        int right = (int)Math.Clamp(Math.Ceiling(Math.Max(first.X, opposite.X)), Crop.X, Crop.Right);
        int bottom = (int)Math.Clamp(Math.Ceiling(Math.Max(first.Y, opposite.Y)), Crop.Y, Crop.Bottom);
        if (right <= left || bottom <= top) { return null; }
        int regionWidth = Math.Min(maximumEdge, right - left);
        int regionHeight = Math.Min(maximumEdge, bottom - top);
        return new(left + ((right - left - regionWidth) / 2), top + ((bottom - top - regionHeight) / 2), regionWidth, regionHeight);
    }
}
