using ModernImageViewer.Imaging;

namespace ModernImageViewer.Application.Editing;

public enum ImageExportFormat { Png, Jpeg, Webp }
public enum ImageExportMetadataMode { Remove, PreserveCamera }
public enum ImageExportError { InvalidDestination, DestinationExists, SourceChanged, BudgetExceeded, DecodeFailed, WriteFailed }

public sealed class ImageExportException(ImageExportError error, Exception? inner = null) : Exception(error.ToString(), inner)
{
    public ImageExportError Error { get; } = error;
}

// JpegQuality is shared by JPEG and lossy WebP (1-100); keep the name for existing callers.
public sealed record ImageExportRequest(string? SourcePath, string DestinationPath, ImageEditRecipe Recipe,
    ImageExportFormat Format = ImageExportFormat.Png, int JpegQuality = 90,
    long? ExpectedSourceLength = null, DateTime? ExpectedSourceModifiedUtc = null,
    ImageExportPixels? SourcePixels = null, Guid? MemorySourceIdentity = null,
    bool WebpLossless = false, ImageExportMetadataMode MetadataMode = ImageExportMetadataMode.Remove);

// Memory-only callers supply EXIF-corrected premultiplied BGRA in sRGB. File snapshots
// retain the decoder's color space and are checked against the locked source profile.
public sealed record ImageExportPixels(PixelSize Size, int Stride, ReadOnlyMemory<byte> Pixels,
    ImageFileStamp? SourceFileStamp = null, bool IsSrgb = true);

public interface IImageExportService
{
    Task<byte[]?> GetSourceColorProfileAsync(string path, CancellationToken cancellationToken = default) => Task.FromResult<byte[]?>(null);

    Task ExportAsync(ImageExportRequest request, CancellationToken cancellationToken = default);
}
