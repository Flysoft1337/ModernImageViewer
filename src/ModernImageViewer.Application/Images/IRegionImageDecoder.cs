using ModernImageViewer.Imaging;

namespace ModernImageViewer.Application.Images;

public interface IRegionImageDecoder
{
    // Bounds are expressed in source pixels after EXIF orientation is applied.
    // The budget limits returned BGRA pixels; orientation may need one region-sized scratch buffer.
    Task<DecodedImageRegion> DecodeRegionAsync(string path, PixelRect region,
        PixelSize expectedSourceSize, long maximumDecodedBytes, CancellationToken cancellationToken);
}
