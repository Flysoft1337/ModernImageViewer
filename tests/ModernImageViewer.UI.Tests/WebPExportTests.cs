using System.Buffers.Binary;
using System.IO;
using System.Security.Cryptography;
using System.Text;

using ModernImageViewer.Application.Editing;
using ModernImageViewer.Application.Images;
using ModernImageViewer.Codecs;
using ModernImageViewer.Codecs.Editing;
using ModernImageViewer.Imaging;

using SkiaSharp;

namespace ModernImageViewer.UI.Tests;

public sealed class WebPExportTests
{
    [Theory]
    [InlineData(1, false)]
    [InlineData(100, false)]
    [InlineData(90, true)]
    public async Task LossyWebpKeepsAlphaCropDirectionAndSizeWithoutSourceMetadataOrMutation(int quality, bool memorySource)
    {
        using Files files = new();
        byte[] source = CreateSourceWithMetadata();
        File.WriteAllBytes(files.Source, source);
        byte[] hash = SHA256.HashData(source);
        Assert.Contains("EXIF", ReadChunks(source));
        Assert.Contains("XMP ", ReadChunks(source));
        ImageEditRecipe recipe = ImageEditRecipe.Create(new(96, 64)).WithCrop(new(16, 0, 64, 64))
            .RotateRight().FlipHorizontal().WithSize(new(32, 64));
        ImageDecoder decoder = new();
        using PixelBuffer? memory = memorySource ? await decoder.DecodeAsync(files.Source, TestContext.Current.CancellationToken) : null;
        byte[]? memoryHash = memory is null ? null : SHA256.HashData(memory.Pixels.Span);
        ImageExportService exporter = new(memorySource ? new ForbiddenDecoder() : decoder);
        await exporter.ExportAsync(new(memorySource ? null : files.Source, files.Output, recipe,
            Format: ImageExportFormat.Webp, JpegQuality: quality,
            SourcePixels: memory is null ? null : new(memory.Size, memory.Stride, memory.Pixels),
            MemorySourceIdentity: memorySource ? Guid.NewGuid() : null), TestContext.Current.CancellationToken);
        if (memory is not null) { Assert.Equal(memoryHash, SHA256.HashData(memory.Pixels.Span)); }

        using SKBitmap output = SKBitmap.Decode(files.Output);
        Assert.Equal(32, output.Width);
        Assert.Equal(64, output.Height);
        SKColor opaque = output.GetPixel(16, 8);
        SKColor translucent = output.GetPixel(16, 32);
        Assert.Equal((byte)255, opaque.Alpha);
        Assert.Equal((byte)128, translucent.Alpha);
        Assert.Equal((byte)0, output.GetPixel(16, 56).Alpha);
        foreach (SKColor red in new[] { opaque, translucent })
        {
            // Lossy RGB is approximate; alpha and edited coordinates remain exact.
            Assert.InRange(red.Red, (byte)235, (byte)255);
            Assert.InRange(red.Green, (byte)0, (byte)20);
            Assert.InRange(red.Blue, (byte)0, (byte)20);
        }
        string[] chunks = ReadChunks(File.ReadAllBytes(files.Output));
        Assert.Contains("VP8 ", chunks); // The quality-100 path is still lossy, not VP8L.
        Assert.DoesNotContain("EXIF", chunks);
        Assert.DoesNotContain("XMP ", chunks);
        Assert.Contains("ICCP", chunks);
        Assert.Equal(hash, SHA256.HashData(File.ReadAllBytes(files.Source)));
        Assert.Empty(Directory.GetFiles(files.Directory, ".miv-export-*.tmp"));
    }

    [Theory]
    [InlineData(ImageExportFormat.Webp, ".png", 90)]
    [InlineData(ImageExportFormat.Png, ".webp", 90)]
    [InlineData(ImageExportFormat.Jpeg, ".webp", 90)]
    [InlineData((ImageExportFormat)99, ".webp", 90)]
    [InlineData(ImageExportFormat.Webp, ".webp", 0)]
    [InlineData(ImageExportFormat.Webp, ".webp", 101)]
    public async Task InvalidFormatExtensionOrQualityIsRejectedBeforeDecodeAndWrite(ImageExportFormat format, string extension, int quality)
    {
        using Files files = new();
        string output = Path.ChangeExtension(files.Output, extension);
        ImageExportRequest request = new(files.Source, output, ImageEditRecipe.Create(new(1, 1)), format, quality);
        ImageExportService exporter = new(new ForbiddenDecoder());
        if (quality is < 1 or > 100)
        {
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => exporter.ExportAsync(request, TestContext.Current.CancellationToken));
        }
        else
        {
            ImageExportException error = await Assert.ThrowsAsync<ImageExportException>(() => exporter.ExportAsync(request, TestContext.Current.CancellationToken));
            Assert.Equal(ImageExportError.InvalidDestination, error.Error);
        }
        Assert.False(File.Exists(output));
        Assert.Empty(Directory.GetFiles(files.Directory, ".miv-export-*.tmp"));
    }

    private static byte[] CreateSourceWithMetadata()
    {
        using SKBitmap bitmap = new(new SKImageInfo(96, 64, SKColorType.Bgra8888, SKAlphaType.Premul));
        for (int y = 0; y < bitmap.Height; y++)
        {
            for (int x = 0; x < bitmap.Width; x++)
            {
                bitmap.SetPixel(x, y, x < 32 ? SKColors.Red : x < 64 ? new SKColor(255, 0, 0, 128) : SKColors.Transparent);
            }
        }
        using SKPixmap pixels = bitmap.PeekPixels();
        using SKData encoded = pixels.Encode(new SKWebpEncoderOptions(SKWebpEncoderCompression.Lossless, 100))
            ?? throw new InvalidOperationException("Could not encode the source fixture.");
        byte[] raster = encoded.ToArray();
        using MemoryStream stream = new();
        using BinaryWriter writer = new(stream);
        writer.Write("RIFF"u8);
        writer.Write(0); // Filled after all chunks are written.
        writer.Write("WEBP"u8);
        writer.Write("VP8X"u8);
        writer.Write(10);
        writer.Write(new byte[] { 28, 0, 0, 0, 95, 0, 0, 63, 0, 0 }); // Alpha, EXIF, XMP, 96 x 64.
        for (int offset = 12; offset < raster.Length;)
        {
            int length = BinaryPrimitives.ReadInt32LittleEndian(raster.AsSpan(offset + 4, 4));
            int chunkSize = 8 + length + (length & 1);
            if (!raster.AsSpan(offset, 4).SequenceEqual("VP8X"u8)) { writer.Write(raster, offset, chunkSize); }
            offset += chunkSize;
        }
        writer.Write("EXIF"u8);
        byte[] exif = [73, 73, 42, 0, 8, 0, 0, 0, 1, 0, 18, 1, 3, 0, 1, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0];
        writer.Write(exif.Length);
        writer.Write(exif);
        writer.Write("XMP "u8);
        byte[] xmp = "<x:xmpmeta xmlns:x='adobe:ns:meta/'>private</x:xmpmeta>"u8.ToArray();
        writer.Write(xmp.Length);
        writer.Write(xmp);
        if ((xmp.Length & 1) != 0) { writer.Write((byte)0); }
        writer.Flush();
        byte[] result = stream.ToArray();
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(4, 4), result.Length - 8);
        return result;
    }

    private static string[] ReadChunks(byte[] bytes)
    {
        Assert.Equal("RIFF"u8.ToArray(), bytes[..4]);
        Assert.Equal("WEBP"u8.ToArray(), bytes[8..12]);
        Assert.Equal(bytes.Length - 8, BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(4, 4)));
        List<string> chunks = [];
        for (int offset = 12; offset < bytes.Length;)
        {
            chunks.Add(Encoding.ASCII.GetString(bytes, offset, 4));
            int length = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset + 4, 4));
            offset += 8 + length + (length & 1);
        }
        return [.. chunks];
    }

    private sealed class Files : IDisposable
    {
        public Files() => System.IO.Directory.CreateDirectory(Directory);
        public string Directory { get; } = Path.Combine(Path.GetTempPath(), $"miv-webp-export-{Guid.NewGuid():N}");
        public string Source => Path.Combine(Directory, "source.webp");
        public string Output => Path.Combine(Directory, "output.WebP");
        public void Dispose() => System.IO.Directory.Delete(Directory, recursive: true);
    }

    private sealed class ForbiddenDecoder : IImageDecoder
    {
        public Task<PixelBuffer> DecodeAsync(string path, CancellationToken cancellationToken) => throw new InvalidOperationException("Invalid requests must not decode.");
    }
}
