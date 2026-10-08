namespace ModernImageViewer.Imaging;

public sealed class PixelBuffer : IDisposable
{
    private byte[]? _pixels;
    private long _observedBytes;
    private static long s_observedBytes;
    private static int s_observedCount;

    // Explicit development observations only; ordinary image buffers do not update counters.
    public static bool ObserveLifetime { get; set; }
    public static long ObservedActiveBytes => Interlocked.Read(ref s_observedBytes);
    public static int ObservedActiveCount => Volatile.Read(ref s_observedCount);

    public PixelBuffer(PixelSize size, int stride, byte[] pixels, ImageMetadata? metadata = null, PixelSize? sourceSize = null, ImageFileStamp? sourceFileStamp = null)
    {
        ArgumentNullException.ThrowIfNull(pixels);

        int minimumStride = checked(size.Width * 4);
        ArgumentOutOfRangeException.ThrowIfLessThan(stride, minimumStride);

        long requiredLength = checked((long)stride * size.Height);
        if (pixels.LongLength < requiredLength)
        {
            throw new ArgumentException("The pixel buffer is too small.", nameof(pixels));
        }

        SourceFileStamp = sourceFileStamp;
        Metadata = metadata ?? ImageMetadata.Empty;
        Size = size;
        SourceSize = sourceSize ?? size;
        Stride = stride;
        _pixels = pixels;
        if (ObserveLifetime)
        {
            _observedBytes = pixels.LongLength;
            Interlocked.Add(ref s_observedBytes, _observedBytes);
            Interlocked.Increment(ref s_observedCount);
        }
    }

    public ImageFileStamp? SourceFileStamp { get; }

    public ImageMetadata Metadata { get; }

    public PixelSize Size { get; }

    public PixelSize SourceSize { get; }

    public int Stride { get; }

    public ReadOnlyMemory<byte> Pixels => _pixels ?? throw new ObjectDisposedException(nameof(PixelBuffer));

    public void Dispose()
    {
        _pixels = null;
        if (_observedBytes != 0)
        {
            long bytes = Interlocked.Exchange(ref _observedBytes, 0);
            if (bytes != 0)
            {
                Interlocked.Add(ref s_observedBytes, -bytes);
                Interlocked.Decrement(ref s_observedCount);
            }
        }
    }
}
