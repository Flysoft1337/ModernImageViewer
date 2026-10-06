using System.Collections.Immutable;

namespace ModernImageViewer.Imaging;

public enum ImageAnnotationKind { Arrow, Rectangle, Ellipse, Pen, Text, Mosaic, RegionBlur }

public readonly record struct ImageEditPoint(double X, double Y);

/// <summary>Immutable annotations in EXIF-corrected source coordinates; never stores pixels.</summary>
public sealed record ImageAnnotation
{
    public const int MaximumAnnotations = 128;
    public const int MaximumPoints = 2048;
    public const int MaximumTotalPoints = 16384;
    public const int MaximumTextLength = 512;
    public const int MaximumTotalTextLength = 8192;
    public const double MinimumScaledValue = 1d / 1024;
    public const double MaximumScaledValue = 32768;

    public ImageAnnotation(ImageAnnotationKind Kind, ImmutableArray<ImageEditPoint> Points,
        uint Color = 0xFFE84C4C, double StrokeWidth = 3, string Text = "", double FontSize = 24, double Strength = 12)
    {
        if (!Enum.IsDefined(Kind)) { throw new ArgumentOutOfRangeException(nameof(Kind)); }
        int minimum = Kind == ImageAnnotationKind.Text ? 1 : 2;
        int maximum = Kind == ImageAnnotationKind.Pen ? MaximumPoints : minimum;
        if (Points.IsDefault || Points.Length < minimum || Points.Length > maximum)
        {
            throw new ArgumentException("Invalid annotation point count.", nameof(Points));
        }
        foreach (ImageEditPoint point in Points)
        {
            if (!double.IsFinite(point.X) || !double.IsFinite(point.Y) || point.X is < 0 or > 100_000
                || point.Y is < 0 or > 100_000)
            {
                throw new ArgumentOutOfRangeException(nameof(Points));
            }
        }
        if (Kind is ImageAnnotationKind.Rectangle or ImageAnnotationKind.Ellipse or ImageAnnotationKind.Mosaic or ImageAnnotationKind.RegionBlur
            && (Points[0].X == Points[1].X || Points[0].Y == Points[1].Y))
        {
            throw new ArgumentException("An annotation region must have positive area.", nameof(Points));
        }
        if (Kind == ImageAnnotationKind.Arrow && Points[0] == Points[1])
        {
            throw new ArgumentException("An arrow must have positive length.", nameof(Points));
        }
        ArgumentNullException.ThrowIfNull(Text);
        if (Text.Length > MaximumTextLength || (Kind == ImageAnnotationKind.Text && string.IsNullOrWhiteSpace(Text))
            || Text.Any(character => char.IsControl(character) && character is not '\n' and not '\r' and not '\t'))
        {
            throw new ArgumentException("Invalid annotation text.", nameof(Text));
        }
        if (!double.IsFinite(StrokeWidth) || StrokeWidth < MinimumScaledValue || StrokeWidth > MaximumScaledValue) { throw new ArgumentOutOfRangeException(nameof(StrokeWidth)); }
        if (!double.IsFinite(FontSize) || FontSize < MinimumScaledValue || FontSize > MaximumScaledValue) { throw new ArgumentOutOfRangeException(nameof(FontSize)); }
        if (!double.IsFinite(Strength) || Strength < MinimumScaledValue || Strength > MaximumScaledValue) { throw new ArgumentOutOfRangeException(nameof(Strength)); }
        this.Kind = Kind;
        this.Points = Points;
        this.Color = Color;
        this.StrokeWidth = StrokeWidth;
        this.Text = Text;
        this.FontSize = FontSize;
        this.Strength = Strength;
    }

    public ImageAnnotationKind Kind { get; }
    public ImmutableArray<ImageEditPoint> Points { get; }
    public uint Color { get; }
    public double StrokeWidth { get; }
    public string Text { get; }
    public double FontSize { get; }
    public double Strength { get; }

    public static void ValidateCollection(ImmutableArray<ImageAnnotation> annotations)
    {
        if (annotations.IsDefault || annotations.Length > MaximumAnnotations)
        {
            throw new ArgumentException("Too many annotations or an uninitialized collection.", nameof(annotations));
        }
        int points = 0;
        int characters = 0;
        foreach (ImageAnnotation annotation in annotations)
        {
            ArgumentNullException.ThrowIfNull(annotation);
            points += annotation.Points.Length;
            characters += annotation.Text.Length;
        }
        if (points > MaximumTotalPoints || characters > MaximumTotalTextLength)
        {
            throw new ArgumentException("Annotation history budget exceeded.", nameof(annotations));
        }
    }
}
