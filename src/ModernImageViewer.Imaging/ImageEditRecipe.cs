using System.Collections.Immutable;

namespace ModernImageViewer.Imaging;

/// <summary>Immutable edits in EXIF-corrected source coordinates; no pixel storage.</summary>
public sealed record ImageEditRecipe
{
    public const int MaximumAnnotations = ImageAnnotation.MaximumAnnotations;

    public ImageEditRecipe(PixelSize sourceSize, PixelRect crop, ViewOrientation orientation, PixelSize outputSize,
        double rotationDegrees = 0, ImageEditAdjustments? adjustments = null, ImmutableArray<ImageAnnotation> annotations = default)
    {
        if (sourceSize.Width <= 0 || sourceSize.Height <= 0 || crop.Width <= 0 || crop.Height <= 0
            || outputSize.Width <= 0 || outputSize.Height <= 0
            || crop.Right > sourceSize.Width || crop.Bottom > sourceSize.Height)
        {
            throw new ArgumentOutOfRangeException(nameof(crop));
        }
        ImageDecodeLimits.Default.ValidateAndGetStride(sourceSize);
        if (!double.IsFinite(rotationDegrees)) { throw new ArgumentOutOfRangeException(nameof(rotationDegrees)); }
        if (!annotations.IsDefault && (annotations.Length > MaximumAnnotations || annotations.Any(annotation => annotation is null)))
        {
            throw new ArgumentOutOfRangeException(nameof(annotations));
        }
        SourceSize = sourceSize;
        Crop = crop;
        Orientation = orientation;
        OutputSize = outputSize;
        RotationDegrees = NormalizeRotation(rotationDegrees);
        Adjustments = adjustments ?? new();
        Annotations = annotations.IsDefault ? [] : annotations;
        ImageAnnotation.ValidateCollection(Annotations);
    }

    public PixelSize SourceSize { get; }
    public PixelRect Crop { get; }
    public ViewOrientation Orientation { get; }
    public PixelSize OutputSize { get; }
    public double RotationDegrees { get; }
    public ImageEditAdjustments Adjustments { get; }
    public ImmutableArray<ImageAnnotation> Annotations { get; }
    public PixelSize NaturalSize => GetRotatedSize(Orientation.GetDisplaySize(Crop.Size), RotationDegrees);

    public static ImageEditRecipe Create(PixelSize source, ViewOrientation orientation = default) =>
        new(source, new PixelRect(0, 0, source.Width, source.Height), orientation, orientation.GetDisplaySize(source));

    public ImageEditRecipe WithCrop(PixelRect crop) => new(SourceSize, crop, Orientation,
        GetRotatedSize(Orientation.GetDisplaySize(crop.Size), RotationDegrees), RotationDegrees, Adjustments, Annotations);
    public ImageEditRecipe WithSize(PixelSize output) => new(SourceSize, Crop, Orientation, output, RotationDegrees, Adjustments, Annotations);
    public ImageEditRecipe WithRotation(double degrees)
    {
        if (!double.IsFinite(degrees)) { throw new ArgumentOutOfRangeException(nameof(degrees)); }
        double normalized = NormalizeRotation(degrees);
        if (normalized == RotationDegrees) { return this; }
        PixelSize previousNatural = NaturalSize;
        PixelSize natural = GetRotatedSize(Orientation.GetDisplaySize(Crop.Size), normalized);
        double densityX = (double)OutputSize.Width / previousNatural.Width;
        double densityY = (double)OutputSize.Height / previousNatural.Height;
        PixelSize output = new(Math.Max(1, checked((int)Math.Round(natural.Width * densityX))),
            Math.Max(1, checked((int)Math.Round(natural.Height * densityY))));
        return new(SourceSize, Crop, Orientation, output, normalized, Adjustments, Annotations);
    }
    public ImageEditRecipe WithAdjustments(ImageEditAdjustments adjustments) => new(SourceSize, Crop, Orientation, OutputSize,
        RotationDegrees, adjustments ?? throw new ArgumentNullException(nameof(adjustments)), Annotations);
    public ImageEditRecipe WithAnnotations(ImmutableArray<ImageAnnotation> annotations) => new(SourceSize, Crop, Orientation, OutputSize,
        RotationDegrees, Adjustments, annotations);
    public ImageEditRecipe RotateRight() => new(SourceSize, Crop, Orientation.RotateRight(), new(OutputSize.Height, OutputSize.Width),
        RotationDegrees, Adjustments, Annotations);
    public ImageEditRecipe RotateLeft() => new(SourceSize, Crop, Orientation.RotateLeft(), new(OutputSize.Height, OutputSize.Width),
        RotationDegrees, Adjustments, Annotations);
    // Moving a displayed-axis reflection before the arbitrary rotation reverses its angle.
    public ImageEditRecipe FlipHorizontal() => new(SourceSize, Crop, Orientation.FlipHorizontal(), OutputSize, -RotationDegrees, Adjustments, Annotations);
    public ImageEditRecipe FlipVertical() => new(SourceSize, Crop, Orientation.FlipVertical(), OutputSize, -RotationDegrees, Adjustments, Annotations);

    public (double M11, double M12, double M21, double M22, double OffsetX, double OffsetY) GetMatrix()
    {
        var (cosine, sine) = GetRotation(RotationDegrees);
        var direction = Orientation.GetMatrix(Crop.Size);
        double m11 = (cosine * direction.M11) - (sine * direction.M12);
        double m12 = (sine * direction.M11) + (cosine * direction.M12);
        double m21 = (cosine * direction.M21) - (sine * direction.M22);
        double m22 = (sine * direction.M21) + (cosine * direction.M22);
        // Apply the existing orientation, rotate around the crop center, then scale.
        double centerX = Crop.X + (Crop.Width / 2d);
        double centerY = Crop.Y + (Crop.Height / 2d);
        PixelSize natural = NaturalSize;
        double offsetX = natural.Width / 2d;
        double offsetY = natural.Height / 2d;
        double scaleX = (double)OutputSize.Width / natural.Width;
        double scaleY = (double)OutputSize.Height / natural.Height;
        return (m11 * scaleX, m12 * scaleY, m21 * scaleX, m22 * scaleY,
            (offsetX - (m11 * centerX) - (m21 * centerY)) * scaleX,
            (offsetY - (m12 * centerX) - (m22 * centerY)) * scaleY);
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
        if (!double.IsFinite(width) || !double.IsFinite(height) || !double.IsFinite(viewport.Scale)
            || !double.IsFinite(viewport.OffsetX) || !double.IsFinite(viewport.OffsetY)
            || width <= 0 || height <= 0 || viewport.Scale <= 0) { return null; }
        var first = ToSource(-viewport.OffsetX / viewport.Scale, -viewport.OffsetY / viewport.Scale);
        var second = ToSource((width - viewport.OffsetX) / viewport.Scale, -viewport.OffsetY / viewport.Scale);
        var third = ToSource(-viewport.OffsetX / viewport.Scale, (height - viewport.OffsetY) / viewport.Scale);
        var opposite = ToSource((width - viewport.OffsetX) / viewport.Scale, (height - viewport.OffsetY) / viewport.Scale);
        int left = (int)Math.Clamp(Math.Floor(Math.Min(Math.Min(first.X, opposite.X), Math.Min(second.X, third.X))), Crop.X, Crop.Right);
        int top = (int)Math.Clamp(Math.Floor(Math.Min(Math.Min(first.Y, opposite.Y), Math.Min(second.Y, third.Y))), Crop.Y, Crop.Bottom);
        int right = (int)Math.Clamp(Math.Ceiling(Math.Max(Math.Max(first.X, opposite.X), Math.Max(second.X, third.X))), Crop.X, Crop.Right);
        int bottom = (int)Math.Clamp(Math.Ceiling(Math.Max(Math.Max(first.Y, opposite.Y), Math.Max(second.Y, third.Y))), Crop.Y, Crop.Bottom);
        if (right <= left || bottom <= top) { return null; }
        int regionWidth = Math.Min(maximumEdge, right - left);
        int regionHeight = Math.Min(maximumEdge, bottom - top);
        return new(left + ((right - left - regionWidth) / 2), top + ((bottom - top - regionHeight) / 2), regionWidth, regionHeight);
    }

    private static double NormalizeRotation(double degrees)
    {
        double normalized = degrees % 360;
        return normalized >= 180 ? normalized - 360 : normalized < -180 ? normalized + 360 : normalized;
    }

    private static (double Cosine, double Sine) GetRotation(double degrees)
    {
        if (!double.IsFinite(degrees)) { throw new ArgumentOutOfRangeException(nameof(degrees)); }
        double angle = NormalizeRotation(degrees);
        return angle switch
        {
            0 => (1, 0),
            90 => (0, 1),
            -90 => (0, -1),
            -180 => (-1, 0),
            _ => (Math.Cos(angle * Math.PI / 180), Math.Sin(angle * Math.PI / 180)),
        };
    }

    private static PixelSize GetRotatedSize(PixelSize size, double degrees)
    {
        var (cosine, sine) = GetRotation(degrees);
        return new(checked((int)Math.Ceiling((Math.Abs(cosine) * size.Width) + (Math.Abs(sine) * size.Height))),
            checked((int)Math.Ceiling((Math.Abs(sine) * size.Width) + (Math.Abs(cosine) * size.Height))));
    }
}
