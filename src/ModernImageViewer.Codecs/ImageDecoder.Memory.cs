using ModernImageViewer.Application.Images;
using ModernImageViewer.Codecs.Wic;
using ModernImageViewer.Imaging;

namespace ModernImageViewer.Codecs;

public sealed partial class ImageDecoder
{
    public Task<PixelBuffer> ReadMemoryPixelsAsync(MemoryImageInput source, PixelSize maximumSize,
        long maximumDecodedBytes, bool detail, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumDecodedBytes);
        return WicImageDecoder.DecodeSlot.RunTaskAsync(detail ? DecodePriority.Detail : DecodePriority.Foreground,
            () => source.ReadPixelsAsync(maximumSize, maximumDecodedBytes, cancellationToken), cancellationToken);
    }
}
