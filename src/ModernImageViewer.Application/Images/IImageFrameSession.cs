using ModernImageViewer.Imaging;

namespace ModernImageViewer.Application.Images;

/// <summary>Owns a container context, never the immutable pixels returned to its caller.</summary>
public interface IImageFrameSession : IDisposable
{
    ImageSequenceInfo Info { get; }
    ImageFileStamp FileStamp { get; }
    long RetainedPixelBytes { get; }
    Task ReleaseCompletion => Task.CompletedTask;
    Task<PixelBuffer> DecodeFrameAsync(int index, PixelSize maximumSize, long maximumDecodedBytes,
        CancellationToken cancellationToken);
    Task<DecodedImageRegion> DecodeRegionAsync(int index, PixelRect bounds, PixelSize expectedSourceSize,
        long maximumDecodedBytes, CancellationToken cancellationToken) =>
        Task.FromException<DecodedImageRegion>(new ImageDecodeException(ImageOpenError.UnsupportedFormat));
}

public interface IImageFrameDecoder
{
    // A null result keeps static/single-page images on the existing lightweight path.
    Task<IImageFrameSession?> TryOpenFrameSessionAsync(string path, CancellationToken cancellationToken);
}
