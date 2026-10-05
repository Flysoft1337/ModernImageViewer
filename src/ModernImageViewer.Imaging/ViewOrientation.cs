namespace ModernImageViewer.Imaging;

public readonly record struct ViewOrientation
{
    private ViewOrientation(int quarterTurns, bool flippedHorizontally, bool flippedVertically)
    {
        QuarterTurns = (quarterTurns + 4) % 4;
        IsFlippedHorizontally = flippedHorizontally;
        IsFlippedVertically = flippedVertically;
    }

    public int QuarterTurns { get; }
    public int RotationDegrees => QuarterTurns * 90;
    public bool IsFlippedHorizontally { get; }
    public bool IsFlippedVertically { get; }
    public bool IsIdentity => GetMatrix(new PixelSize(1, 1)) == (1, 0, 0, 1, 0, 0);

    // Flips act on the displayed axes. Rotating exchanges those axes.
    public ViewOrientation RotateRight() => new(QuarterTurns + 1, IsFlippedVertically, IsFlippedHorizontally);
    public ViewOrientation RotateLeft() => new(QuarterTurns + 3, IsFlippedVertically, IsFlippedHorizontally);
    public ViewOrientation FlipHorizontal() => new(QuarterTurns, !IsFlippedHorizontally, IsFlippedVertically);
    public ViewOrientation FlipVertical() => new(QuarterTurns, IsFlippedHorizontally, !IsFlippedVertically);

    public PixelSize GetDisplaySize(PixelSize source) => QuarterTurns % 2 == 0 ? source : new(source.Height, source.Width);

    public (double M11, double M12, double M21, double M22, double OffsetX, double OffsetY) GetMatrix(PixelSize source)
    {
        (double m11, double m12, double m21, double m22) = ImageOrientation.GetMatrix(QuarterTurns switch
        {
            1 => 6,
            2 => 3,
            3 => 8,
            _ => (ushort)1,
        });
        if (IsFlippedHorizontally)
        {
            m11 = -m11;
            m21 = -m21;
        }
        if (IsFlippedVertically)
        {
            m12 = -m12;
            m22 = -m22;
        }
        return (m11, m12, m21, m22,
            (m11 < 0 ? source.Width : 0) + (m21 < 0 ? source.Height : 0),
            (m12 < 0 ? source.Width : 0) + (m22 < 0 ? source.Height : 0));
    }

    public (double X, double Y) ToDisplayPoint(PixelSize source, double x, double y)
    {
        var matrix = GetMatrix(source);
        return ((matrix.M11 * x) + (matrix.M21 * y) + matrix.OffsetX,
            (matrix.M12 * x) + (matrix.M22 * y) + matrix.OffsetY);
    }

    public (double X, double Y) ToSourcePoint(PixelSize source, double x, double y)
    {
        var matrix = GetMatrix(source);
        x -= matrix.OffsetX;
        y -= matrix.OffsetY;
        return ((matrix.M11 * x) + (matrix.M12 * y), (matrix.M21 * x) + (matrix.M22 * y));
    }
}
