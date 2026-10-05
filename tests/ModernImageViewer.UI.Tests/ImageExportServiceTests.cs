using System.IO;
using System.Security.Cryptography;

using ModernImageViewer.Application.Editing;
using ModernImageViewer.Application.Images;
using ModernImageViewer.Codecs;
using ModernImageViewer.Codecs.Editing;
using ModernImageViewer.Imaging;

using SkiaSharp;

namespace ModernImageViewer.UI.Tests;

public sealed class ImageExportServiceTests
{
    [Fact]
    public async Task CropThenRotateAndResizeClampsHighContrastEdgesAndKeepsOriginalHash()
    {
        using Files files = new();
        using (SKBitmap source = new(8, 8))
        {
            source.Erase(SKColors.Blue);
            for (int y = 2; y < 6; y++)
            {
                for (int x = 2; x < 6; x++) { source.SetPixel(x, y, SKColors.Red); }
            }
            using FileStream stream = File.Create(files.Source);
            Assert.True(source.Encode(stream, SKEncodedImageFormat.Png, 100));
        }
        byte[] hash = SHA256.HashData(File.ReadAllBytes(files.Source));
        ImageEditRecipe recipe = ImageEditRecipe.Create(new(8, 8)).WithCrop(new(2, 2, 4, 4)).RotateRight().FlipHorizontal().WithSize(new(2, 3));
        await new ImageExportService(new ImageDecoder()).ExportAsync(new(files.Source, files.Output, recipe), TestContext.Current.CancellationToken);
        using SKBitmap output = SKBitmap.Decode(files.Output);
        Assert.Equal(2, output.Width);
        Assert.Equal(3, output.Height);
        for (int y = 0; y < output.Height; y++)
        {
            for (int x = 0; x < output.Width; x++) { Assert.Equal(SKColors.Red, output.GetPixel(x, y)); }
        }
        Assert.Equal(hash, SHA256.HashData(File.ReadAllBytes(files.Source)));
        Assert.Empty(Directory.GetFiles(files.Directory, ".miv-export-*.tmp"));
    }

    [Fact]
    public async Task PngPreservesAlphaWhileJpegCompositesWhiteAndReusesFullSourcePixels()
    {
        using Files files = new();
        File.WriteAllBytes(files.Source, [1]);
        using PixelBuffer image = new(new(1, 1), 4, [0, 0, 128, 128]);
        ImageExportPixels pixels = new(image.Size, image.Stride, image.Pixels, new(1, File.GetLastWriteTimeUtc(files.Source)));
        image.Dispose();
        ImageEditRecipe recipe = ImageEditRecipe.Create(new(1, 1));
        ImageExportService service = new(new ForbiddenDecoder());
        ImageExportRequest png = new(files.Source, files.Output, recipe, ExpectedSourceLength: 1,
            ExpectedSourceModifiedUtc: File.GetLastWriteTimeUtc(files.Source), SourcePixels: pixels);
        await service.ExportAsync(png, TestContext.Current.CancellationToken);
        using SKBitmap preserved = SKBitmap.Decode(files.Output);
        Assert.Equal((byte)128, preserved.GetPixel(0, 0).Alpha);
        Assert.Equal((byte)255, preserved.GetPixel(0, 0).Red);
        string jpegPath = Path.Combine(files.Directory, "output.jpg");
        await service.ExportAsync(png with { DestinationPath = jpegPath, Format = ImageExportFormat.Jpeg, JpegQuality = 100 }, TestContext.Current.CancellationToken);
        using SKBitmap flattened = SKBitmap.Decode(jpegPath);
        SKColor color = flattened.GetPixel(0, 0);
        Assert.Equal((byte)255, color.Alpha);
        Assert.InRange(color.Red, 250, 255);
        Assert.InRange(color.Green, 122, 132);
        Assert.InRange(color.Blue, 122, 132);
    }

    [Fact]
    public async Task ReplacedSourceCannotRebindOldFullPixelsToAFreshFileStamp()
    {
        using Files files = new();
        static void WriteSolid(string path, SKColor color)
        {
            using SKBitmap bitmap = new(1, 1);
            bitmap.Erase(color);
            using FileStream file = File.Create(path);
            Assert.True(bitmap.Encode(file, SKEncodedImageFormat.Png, 100));
        }
        WriteSolid(files.Source, SKColors.Red);
        ImageDecoder decoder = new();
        using PixelBuffer loaded = await decoder.DecodeAsync(files.Source, TestContext.Current.CancellationToken);
        ImageFileStamp originalStamp = Assert.IsType<ImageFileStamp>(loaded.SourceFileStamp);
        ImageExportPixels oldPixels = new(loaded.Size, loaded.Stride, loaded.Pixels, originalStamp);
        WriteSolid(files.Source, SKColors.Blue);
        File.SetLastWriteTimeUtc(files.Source, originalStamp.ModifiedUtc.AddSeconds(5));
        ImageEditRecipe recipe = ImageEditRecipe.Create(loaded.SourceSize);
        ImageExportService exporter = new(decoder);
        ImageExportException stale = await Assert.ThrowsAsync<ImageExportException>(() => exporter.ExportAsync(new(files.Source, files.Output, recipe,
            ExpectedSourceLength: originalStamp.Length, ExpectedSourceModifiedUtc: originalStamp.ModifiedUtc, SourcePixels: oldPixels), TestContext.Current.CancellationToken));
        Assert.Equal(ImageExportError.SourceChanged, stale.Error);
        Assert.False(File.Exists(files.Output));
        FileInfo replacement = new(files.Source);
        await exporter.ExportAsync(new(files.Source, files.Output, recipe, ExpectedSourceLength: replacement.Length,
            ExpectedSourceModifiedUtc: replacement.LastWriteTimeUtc, SourcePixels: oldPixels), TestContext.Current.CancellationToken);
        using SKBitmap output = SKBitmap.Decode(files.Output);
        Assert.Equal(SKColors.Blue, output.GetPixel(0, 0));
    }

    [Fact]
    public async Task LargeCropUsesBoundedOverlappingRegionsWithCorrectRotatedTilePositions()
    {
        using Files files = new();
        File.WriteAllBytes(files.Source, [1]);
        TileDecoder decoder = new();
        ImageEditRecipe recipe = ImageEditRecipe.Create(TileDecoder.Source).WithCrop(new(10, 20, 2047, 2)).RotateRight();
        await new ImageExportService(decoder).ExportAsync(new(files.Source, files.Output, recipe), TestContext.Current.CancellationToken);
        Assert.Equal(2, decoder.Regions.Count);
        Assert.All(decoder.Regions, bounds => { Assert.InRange(bounds.Width, 1, 2048); Assert.InRange(bounds.Height, 1, 2048); });
        using SKBitmap result = SKBitmap.Decode(files.Output);
        Assert.Equal(2, result.Width);
        Assert.Equal(2047, result.Height);
        Assert.Equal(SKColors.Red, result.GetPixel(0, 0));
        Assert.Equal(SKColors.Red, result.GetPixel(0, 2045));
        Assert.Equal(SKColors.Blue, result.GetPixel(0, 2046));
    }

    [Fact]
    public async Task LargeWholeImageResizeUsesOnlyRequiredPreviewButLargeUnsupportedCropIsRejected()
    {
        using Files files = new();
        File.WriteAllBytes(files.Source, [1]);
        ReducedDecoder decoder = new();
        ImageExportService exporter = new(decoder);
        ImageEditRecipe recipe = ImageEditRecipe.Create(new(8000, 6000)).RotateRight().WithSize(new(60, 80));
        await exporter.ExportAsync(new(files.Source, files.Output, recipe), TestContext.Current.CancellationToken);
        Assert.InRange(decoder.Target.Width, 80, 82);
        Assert.InRange(decoder.Target.Height, 60, 62);
        using SKBitmap result = SKBitmap.Decode(files.Output);
        Assert.Equal(60, result.Width);
        Assert.Equal(80, result.Height);
        Assert.Equal(SKColors.Lime, result.GetPixel(30, 40));
        File.Delete(files.Output);
        ImageExportException exception = await Assert.ThrowsAsync<ImageExportException>(() => exporter.ExportAsync(
            new(files.Source, files.Output, recipe.WithCrop(new(0, 0, 4000, 3000)).WithSize(new(60, 80))), TestContext.Current.CancellationToken));
        Assert.Equal(ImageExportError.BudgetExceeded, exception.Error);
        Assert.False(File.Exists(files.Output));
    }

    [Fact]
    public async Task SourceIsLockedThroughCommitAndDestinationRacePreservesBothFilesAndCleansTemporary()
    {
        using Files files = new();
        File.WriteAllBytes(files.Source, [1, 2, 3]);
        WaitingDecoder decoder = new();
        Task saving = new ImageExportService(decoder).ExportAsync(new(files.Source, files.Output, ImageEditRecipe.Create(new(1, 1))), TestContext.Current.CancellationToken);
        await decoder.Started.Task.WaitAsync(TestContext.Current.CancellationToken);
        Assert.Throws<IOException>(() => new FileStream(files.Source, FileMode.Open, FileAccess.Write, FileShare.Read).Dispose());
        File.WriteAllBytes(files.Output, [9, 8, 7]);
        decoder.Finish.SetResult();
        ImageExportException exception = await Assert.ThrowsAsync<ImageExportException>(() => saving);
        Assert.Equal(ImageExportError.DestinationExists, exception.Error);
        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(files.Source));
        Assert.Equal(new byte[] { 9, 8, 7 }, File.ReadAllBytes(files.Output));
        Assert.Empty(Directory.GetFiles(files.Directory, ".miv-export-*.tmp"));
    }

    [Fact]
    public async Task SameSourcePathExistingDestinationStaleSourceBudgetAndCancellationNeverWrite()
    {
        using Files files = new();
        File.WriteAllBytes(files.Source, [1]);
        ImageExportService exporter = new(new ForbiddenDecoder());
        ImageEditRecipe recipe = ImageEditRecipe.Create(new(1, 1));
        ImageExportException same = await Assert.ThrowsAsync<ImageExportException>(() => exporter.ExportAsync(new(files.Source, files.Source, recipe), TestContext.Current.CancellationToken));
        Assert.Equal(ImageExportError.InvalidDestination, same.Error);
        File.WriteAllBytes(files.Output, [2]);
        ImageExportException existing = await Assert.ThrowsAsync<ImageExportException>(() => exporter.ExportAsync(new(files.Source, files.Output, recipe), TestContext.Current.CancellationToken));
        Assert.Equal(ImageExportError.DestinationExists, existing.Error);
        File.Delete(files.Output);
        ImageExportException stale = await Assert.ThrowsAsync<ImageExportException>(() => exporter.ExportAsync(new(files.Source, files.Output, recipe, ExpectedSourceLength: 2), TestContext.Current.CancellationToken));
        Assert.Equal(ImageExportError.SourceChanged, stale.Error);
        ImageExportException budget = await Assert.ThrowsAsync<ImageExportException>(() => exporter.ExportAsync(new(files.Source, files.Output, recipe.WithSize(new(5000, 5000))), TestContext.Current.CancellationToken));
        Assert.Equal(ImageExportError.BudgetExceeded, budget.Error);
        using CancellationTokenSource cancellation = new();
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => exporter.ExportAsync(new(files.Source, files.Output, recipe), cancellation.Token));
        Assert.False(File.Exists(files.Output));
        Assert.Empty(Directory.GetFiles(files.Directory, ".miv-export-*.tmp"));
    }

    private sealed class Files : IDisposable
    {
        public Files() => System.IO.Directory.CreateDirectory(Directory);
        public string Directory { get; } = Path.Combine(Path.GetTempPath(), $"miv-edit-{Guid.NewGuid():N}");
        public string Source => Path.Combine(Directory, "source.png");
        public string Output => Path.Combine(Directory, "output.png");
        public void Dispose() => System.IO.Directory.Delete(Directory, true);
    }

    private sealed class ForbiddenDecoder : IImageDecoder
    {
        public Task<PixelBuffer> DecodeAsync(string path, CancellationToken cancellationToken) => throw new InvalidOperationException("Decoded source pixels must not be copied again.");
    }

    private sealed class WaitingDecoder : IPreviewImageDecoder
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Finish { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<PixelBuffer> DecodeAsync(string path, CancellationToken cancellationToken)
        {
            Started.SetResult();
            await Finish.Task.WaitAsync(cancellationToken);
            return new(new(1, 1), 4, [0, 0, 255, 255]);
        }
        public Task<PixelBuffer> DecodePreviewAsync(string path, PixelSize maximumSize, CancellationToken cancellationToken) => DecodeAsync(path, cancellationToken);
    }

    private sealed class TileDecoder : IPreviewImageDecoder, IRegionImageDecoder
    {
        public static PixelSize Source { get; } = new(5000, 4000);
        public List<PixelRect> Regions { get; } = [];
        public Task<PixelBuffer> DecodeAsync(string path, CancellationToken cancellationToken) => throw new InvalidOperationException("A large crop must not allocate a whole source.");
        public Task<PixelBuffer> DecodePreviewAsync(string path, PixelSize maximumSize, CancellationToken cancellationToken) => DecodeAsync(path, cancellationToken);
        public Task<DecodedImageRegion> DecodeRegionAsync(string path, PixelRect region, PixelSize expectedSourceSize, long maximumDecodedBytes, CancellationToken cancellationToken)
        {
            Assert.Equal(Source, expectedSourceSize);
            Assert.InRange(region.Size.PixelCount * 4, 4, maximumDecodedBytes);
            Regions.Add(region);
            byte[] pixels = new byte[(int)region.Size.PixelCount * 4];
            for (int y = 0; y < region.Height; y++)
            {
                for (int x = 0; x < region.Width; x++)
                {
                    int offset = ((y * region.Width) + x) * 4;
                    pixels[offset + (x + region.X >= 2056 ? 0 : 2)] = 255;
                    pixels[offset + 3] = 255;
                }
            }
            return Task.FromResult(new DecodedImageRegion(new(region.Size, region.Width * 4, pixels, sourceSize: Source), region));
        }
    }

    private sealed class ReducedDecoder : IPreviewImageDecoder
    {
        public PixelSize Target { get; private set; }
        public Task<PixelBuffer> DecodeAsync(string path, CancellationToken cancellationToken) => throw new InvalidOperationException("Whole source exceeds the budget.");
        public Task<PixelBuffer> DecodePreviewAsync(string path, PixelSize maximumSize, CancellationToken cancellationToken)
        {
            Target = maximumSize;
            byte[] pixels = new byte[(int)maximumSize.PixelCount * 4];
            for (int i = 0; i < pixels.Length; i += 4) { pixels[i + 1] = 255; pixels[i + 3] = 255; }
            return Task.FromResult(new PixelBuffer(maximumSize, maximumSize.Width * 4, pixels, sourceSize: new(8000, 6000)));
        }
    }
}
