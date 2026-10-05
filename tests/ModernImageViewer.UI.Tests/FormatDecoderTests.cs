using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

using ModernImageViewer.Application.Images;
using ModernImageViewer.Codecs;
using ModernImageViewer.Imaging;

using SkiaSharp;

namespace ModernImageViewer.UI.Tests;

public sealed class FormatDecoderTests
{
    [Fact]
    public async Task WicContainersAndPngBackedIconDecodeWithBoundedThumbnails()
    {
        string directory = CreateDirectory();
        try
        {
            byte[] pixels = new byte[256 * 128 * 4];
            for (int offset = 0; offset < pixels.Length; offset += 4)
            {
                pixels[offset + 2] = 255;
                pixels[offset + 3] = 128;
            }
            BitmapSource source = BitmapSource.Create(256, 128, 96, 96, PixelFormats.Bgra32, null, pixels, 256 * 4);
            source.Freeze();
            (string Extension, Func<BitmapEncoder> Create)[] encoders =
            [
                ("jpg", () => new JpegBitmapEncoder()), ("png", () => new PngBitmapEncoder()),
                ("bmp", () => new BmpBitmapEncoder()), ("gif", () => new GifBitmapEncoder()),
                ("tiff", () => new TiffBitmapEncoder()),
            ];
            ImageDecoder decoder = new();
            foreach (var (extension, create) in encoders)
            {
                BitmapEncoder encoder = create();
                encoder.Frames.Add(BitmapFrame.Create(source));
                string path = Path.Combine(directory, "fixture." + extension);
                using (FileStream file = File.Create(path))
                {
                    encoder.Save(file);
                }
                await AssertDecodedAndExportAsync(decoder, path);
            }

            string iconPath = Path.Combine(directory, "fixture.ico");
            byte[] png = File.ReadAllBytes(Path.Combine(directory, "fixture.png"));
            using (BinaryWriter writer = new(File.Create(iconPath)))
            {
                writer.Write((ushort)0); // reserved
                writer.Write((ushort)1); // icon
                writer.Write((ushort)1); // entry count
                writer.Write((byte)0); // 256 pixels
                writer.Write((byte)128);
                writer.Write((byte)0); // colors
                writer.Write((byte)0);
                writer.Write((ushort)1); // planes
                writer.Write((ushort)32);
                writer.Write((uint)png.Length);
                writer.Write((uint)22); // directory + entry
                writer.Write(png);
            }
            await AssertDecodedAndExportAsync(decoder, iconPath);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task LossyAndLosslessWebPDecodeDirectlyToBoundedPremultipliedPixels()
    {
        string directory = CreateDirectory();
        try
        {
            using SKBitmap bitmap = new(new SKImageInfo(256, 128, SKColorType.Bgra8888, SKAlphaType.Premul));
            bitmap.Erase(new SKColor(255, 0, 0, 128));
            using SKPixmap pixmap = bitmap.PeekPixels();
            ImageDecoder decoder = new();
            foreach (SKWebpEncoderCompression compression in new[] { SKWebpEncoderCompression.Lossless, SKWebpEncoderCompression.Lossy })
            {
                using SKData encoded = SKWebpEncoder.Encode(pixmap, new SKWebpEncoderOptions(compression, 100))
                    ?? throw new InvalidOperationException("Could not encode the generated WebP fixture.");
                string path = Path.Combine(directory, compression == SKWebpEncoderCompression.Lossless
                    ? "fixture.webp" : "fixture-lossy.webp");
                using (FileStream file = File.Create(path))
                {
                    encoded.SaveTo(file);
                }
                await AssertDecodedAndExportAsync(decoder, path);
                using PixelBuffer main = await decoder.DecodeAsync(path, TestContext.Current.CancellationToken);
                byte[] firstPixel = main.Pixels.Span[..4].ToArray();
                Assert.InRange(firstPixel[0], (byte)0, (byte)2);
                Assert.InRange(firstPixel[1], (byte)0, (byte)2);
                Assert.InRange(firstPixel[2], (byte)126, (byte)130);
                Assert.Equal((byte)128, firstPixel[3]);
            }
            string corrupt = Path.Combine(directory, "corrupt.webp");
            File.WriteAllBytes(corrupt, "RIFF\0\0\0\0WEBPbroken"u8.ToArray());
            ImageDecodeException exception = await Assert.ThrowsAsync<ImageDecodeException>(
                () => decoder.DecodeAsync(corrupt, TestContext.Current.CancellationToken));
            Assert.Equal(ImageOpenError.CorruptFile, exception.Error);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static string CreateDirectory()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"viewer-more-formats-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static async Task AssertDecodedAndExportAsync(ImageDecoder decoder, string path)
    {
        using PixelBuffer main = await decoder.DecodeAsync(path, TestContext.Current.CancellationToken);
        Assert.Equal(new PixelSize(256, 128), main.Size);
        Assert.Equal(main.Size, main.SourceSize);
        using PixelBuffer preview = await decoder.DecodePreviewAsync(path, new PixelSize(128, 80), TestContext.Current.CancellationToken);
        Assert.Equal(main.Size, preview.SourceSize);
        Assert.InRange(preview.Size.Width, 1, 128);
        Assert.InRange(preview.Size.Height, 1, 80);
        await Assert.ThrowsAsync<ImageSizeLimitExceededException>(() =>
            decoder.DecodeDetailAsync(path, 4, TestContext.Current.CancellationToken));
        using PixelBuffer thumbnail = await decoder.DecodeThumbnailAsync(path, new PixelSize(224, 140), TestContext.Current.CancellationToken);
        Assert.InRange(thumbnail.Size.Width, 1, 224);
        Assert.InRange(thumbnail.Size.Height, 1, 140);
        Assert.InRange(thumbnail.Pixels.Length, 4, 224 * 140 * 4);
        string? exportDirectory = Environment.GetEnvironmentVariable("MIV_FORMAT_FIXTURE_DIRECTORY");
        if (!string.IsNullOrWhiteSpace(exportDirectory))
        {
            // Only generated fixtures which have passed both pipelines are exported for publish/install smoke checks.
            Directory.CreateDirectory(exportDirectory);
            File.Copy(path, Path.Combine(exportDirectory, Path.GetFileName(path)), overwrite: true);
        }
    }
}
