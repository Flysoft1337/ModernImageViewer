namespace ModernImageViewer.Imaging;

public sealed class PixelBuffer : IDisposable
{
    private byte[]? _pixels;

    public PixelBuffer(PixelSize size, int stride, byte[] pixels)
    {
        ArgumentNullException.ThrowIfNull(pixels);

        int minimumStride = checked(size.Width * 4);
        ArgumentOutOfRangeException.ThrowIfLessThan(stride, minimumStride);

        long requiredLength = checked((long)stride * size.Height);
        if (pixels.LongLength < requiredLength)
        {
            throw new ArgumentException("The pixel buffer is too small.", nameof(pixels));
        }

        Size = size;
        Stride = stride;
        _pixels = pixels;
    }

    public PixelSize Size { get; }

    public int Stride { get; }

    public ReadOnlyMemory<byte> Pixels => _pixels ?? throw new ObjectDisposedException(nameof(PixelBuffer));

    public void Dispose()
    {
        _pixels = null;
    }
}
