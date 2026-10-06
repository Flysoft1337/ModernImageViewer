using ModernImageViewer.Imaging;

namespace ModernImageViewer.Application.Images;

public interface IMemoryImageDecoder
{
    // Memory readers share the file decoder's admission and cancellation lifetime.
    Task<PixelBuffer> ReadMemoryPixelsAsync(MemoryImageInput source, PixelSize maximumSize,
        long maximumDecodedBytes, bool detail, CancellationToken cancellationToken);
}
