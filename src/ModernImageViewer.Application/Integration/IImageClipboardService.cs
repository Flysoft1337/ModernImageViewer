using ModernImageViewer.Application.Images;
using ModernImageViewer.Imaging;

namespace ModernImageViewer.Application.Integration;

public sealed record ImageClipboardInput(IReadOnlyList<string> Files, MemoryImageInput? Image = null);

public sealed record ImageClipboardPixels(PixelSize Size, int Stride, ReadOnlyMemory<byte> Pixels,
    ViewOrientation Orientation, bool OriginalSize = false);

public static class ClipboardImageLimits
{
    public const long SourceBytes = 64L * 1024 * 1024;
    public const long EncodedBytes = 32L * 1024 * 1024;
    public static PixelSize PreviewSize { get; } = new(2560, 1600);
}

public interface IImageClipboardService
{
    // Snapshot and final clipboard write happen on the caller's UI STA thread.
    ImageClipboardInput ReadInput();
    Task WriteAsync(ImageClipboardPixels image, CancellationToken cancellationToken = default);
}
