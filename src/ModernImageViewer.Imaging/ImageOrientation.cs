namespace ModernImageViewer.Imaging;

public static class ImageOrientation
{
    // WPF matrix coefficients: x' = M11*x + M21*y, y' = M12*x + M22*y.
    public static (double M11, double M12, double M21, double M22) GetMatrix(ushort orientation) => orientation switch
    {
        2 => (-1, 0, 0, 1),
        3 => (-1, 0, 0, -1),
        4 => (1, 0, 0, -1),
        5 => (0, 1, 1, 0),
        6 => (0, 1, -1, 0),
        7 => (0, -1, -1, 0),
        8 => (0, -1, 1, 0),
        _ => (1, 0, 0, 1),
    };
}
