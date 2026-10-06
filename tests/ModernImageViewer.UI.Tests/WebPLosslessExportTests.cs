using System.IO;

using ModernImageViewer.Application.Editing;
using ModernImageViewer.Application.Images;
using ModernImageViewer.Codecs.Editing;
using ModernImageViewer.Imaging;

namespace ModernImageViewer.UI.Tests;

public sealed class WebPLosslessExportTests
{
    [Fact]
    public async Task LosslessPreservePolicyCancellationAndExistingDestinationNeverCommitOrOverwrite()
    {
        using Files files = new();
        ImageExportRequest request = new(null, files.Output, ImageEditRecipe.Create(new(1, 1)),
            Format: ImageExportFormat.Webp, WebpLossless: true, MetadataMode: ImageExportMetadataMode.PreserveCamera,
            SourcePixels: new(new(1, 1), 4, new byte[] { 0, 0, 128, 128 }), MemorySourceIdentity: Guid.NewGuid());
        ImageExportService exporter = new(new ForbiddenDecoder());
        using CancellationTokenSource cancelled = new();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => exporter.ExportAsync(request, cancelled.Token));
        Assert.False(File.Exists(files.Output));
        File.WriteAllBytes(files.Output, [9, 8, 7]);
        ImageExportException exists = await Assert.ThrowsAsync<ImageExportException>(() => exporter.ExportAsync(request,
            TestContext.Current.CancellationToken));
        Assert.Equal(ImageExportError.DestinationExists, exists.Error);
        Assert.Equal(new byte[] { 9, 8, 7 }, File.ReadAllBytes(files.Output));
        Assert.Empty(Directory.GetFiles(files.Directory, ".miv-export-*.tmp"));
    }

    private sealed class ForbiddenDecoder : IImageDecoder
    {
        public Task<PixelBuffer> DecodeAsync(string path, CancellationToken cancellationToken) => throw new InvalidOperationException("Memory sources must not decode a file.");
    }

    private sealed class Files : IDisposable
    {
        public Files() => System.IO.Directory.CreateDirectory(Directory);
        public string Directory { get; } = Path.Combine(Path.GetTempPath(), $"miv-webp-lossless-{Guid.NewGuid():N}");
        public string Output => Path.Combine(Directory, "output.webp");
        public void Dispose() => System.IO.Directory.Delete(Directory, recursive: true);
    }
}
