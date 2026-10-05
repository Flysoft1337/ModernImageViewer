using ModernImageViewer.Imaging;

namespace ModernImageViewer.Application.Images;

public enum ImageSourceKind { File, Memory }

/// <summary>Distinct identity for in-memory images; their file path is always absent.</summary>
public sealed record ImageSource(Guid Identity, ImageSourceKind Kind, MemoryImageInput? Memory = null);

/// <summary>Owns a frozen source snapshot through its bounded asynchronous pixel reader.</summary>
public sealed record MemoryImageInput(PixelSize SourceSize,
    Func<PixelSize, long, CancellationToken, Task<PixelBuffer>> ReadPixelsAsync);
