using ModernImageViewer.Imaging;

namespace ModernImageViewer.Codecs.Wic;

internal static class RegionOrientation
{
    internal static (int X, int Y) ToRaw(int x, int y, PixelSize source, ushort orientation) => orientation switch
    {
        2 => (source.Width - 1 - x, y),
        3 => (source.Width - 1 - x, source.Height - 1 - y),
        4 => (x, source.Height - 1 - y),
        5 => (y, x),
        6 => (y, source.Height - 1 - x),
        7 => (source.Width - 1 - y, source.Height - 1 - x),
        8 => (source.Width - 1 - y, x),
        _ => (x, y),
    };

    internal static PixelRect ToRawBounds(PixelRect region, PixelSize source, ushort orientation)
    {
        (int x1, int y1) = ToRaw(region.X, region.Y, source, orientation);
        (int x2, int y2) = ToRaw(region.Right - 1, region.Bottom - 1, source, orientation);
        return new PixelRect(Math.Min(x1, x2), Math.Min(y1, y2), Math.Abs(x2 - x1) + 1, Math.Abs(y2 - y1) + 1);
    }

    internal static void CopyOriented(byte[] sourcePixels, int sourceStride, PixelRect rawBounds,
        PixelRect orientedBounds, PixelSize originalSize, ushort orientation, byte[] output,
        int outputStride, CancellationToken cancellationToken)
    {
        for (int y = 0; y < orientedBounds.Height; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (int x = 0; x < orientedBounds.Width; x++)
            {
                (int rawX, int rawY) = ToRaw(orientedBounds.X + x, orientedBounds.Y + y, originalSize, orientation);
                int inputOffset = ((rawY - rawBounds.Y) * sourceStride) + ((rawX - rawBounds.X) * 4);
                int outputOffset = (y * outputStride) + (x * 4);
                sourcePixels.AsSpan(inputOffset, 4).CopyTo(output.AsSpan(outputOffset, 4));
            }
        }
    }
}
