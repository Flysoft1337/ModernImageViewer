using System.IO;

using ModernImageViewer.Application.Images;
using ModernImageViewer.Codecs;
using ModernImageViewer.Imaging;

using SkiaSharp;

namespace ModernImageViewer.UI.Tests;

public sealed class WebPOrientationTests
{
    private static readonly SKColor[] Colors =
        [SKColors.Red, SKColors.Lime, SKColors.Blue, SKColors.Yellow, SKColors.Magenta, SKColors.Cyan];

    [Fact]
    public async Task AllEightWebPOriginsCorrectMainPreviewAndThumbnailPixels()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"viewer-webp-orientation-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            ImageDecoder decoder = new();
            int[][] expected =
            [
                [0, 1, 2, 3, 4, 5], [2, 1, 0, 5, 4, 3],
                [5, 4, 3, 2, 1, 0], [3, 4, 5, 0, 1, 2],
                [0, 3, 1, 4, 2, 5], [3, 0, 4, 1, 5, 2],
                [5, 2, 4, 1, 3, 0], [2, 5, 1, 4, 0, 3],
            ];
            for (ushort orientation = 1; orientation <= 8; orientation++)
            {
                string path = Path.Combine(directory, $"origin-{orientation}.webp");
                File.WriteAllBytes(path, CreateWebP(orientation));
                PixelSize source = orientation >= 5 ? new PixelSize(64, 96) : new PixelSize(96, 64);
                using PixelBuffer main = await decoder.DecodeAsync(path, TestContext.Current.CancellationToken);
                Assert.Equal(source, main.Size);
                Assert.Equal(source, main.SourceSize);
                AssertTiles(main, expected[orientation - 1], orientation >= 5);
                PixelSize bound = new(48, 40);
                using PixelBuffer preview = await decoder.DecodePreviewAsync(path, bound, TestContext.Current.CancellationToken);
                using PixelBuffer thumbnail = await decoder.DecodeThumbnailAsync(path, bound, TestContext.Current.CancellationToken);
                foreach (PixelBuffer image in new[] { preview, thumbnail })
                {
                    Assert.Equal(source, image.SourceSize);
                    Assert.InRange(image.Size.Width, 1, bound.Width);
                    Assert.InRange(image.Size.Height, 1, bound.Height);
                    AssertTiles(image, expected[orientation - 1], orientation >= 5);
                }
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task CorruptContainerAndCancelledDecodeNeverReturnPixels()
    {
        string path = Path.Combine(Path.GetTempPath(), $"viewer-webp-invalid-{Guid.NewGuid():N}.webp");
        try
        {
            File.WriteAllBytes(path, "RIFF\0\0\0\0WEBPbroken"u8.ToArray());
            ImageDecoder decoder = new();
            ImageDecodeException exception = await Assert.ThrowsAsync<ImageDecodeException>(() =>
                decoder.DecodeAsync(path, TestContext.Current.CancellationToken));
            Assert.Equal(ImageOpenError.CorruptFile, exception.Error);
            File.WriteAllBytes(path, CreateWebP(6));
            using CancellationTokenSource cancellation = new();
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                decoder.DecodePreviewAsync(path, new PixelSize(48, 40), cancellation.Token));
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static void AssertTiles(PixelBuffer image, int[] expected, bool swapped)
    {
        int columns = swapped ? 2 : 3;
        int rows = swapped ? 3 : 2;
        for (int index = 0; index < expected.Length; index++)
        {
            int x = (((index % columns) * 2) + 1) * image.Size.Width / (columns * 2);
            int y = (((index / columns) * 2) + 1) * image.Size.Height / (rows * 2);
            int offset = (y * image.Stride) + (x * 4);
            SKColor color = Colors[expected[index]];
            Assert.Equal(new[] { color.Blue, color.Green, color.Red, color.Alpha }, image.Pixels.Slice(offset, 4).ToArray());
        }
    }

    private static byte[] CreateWebP(ushort orientation)
    {
        using SKBitmap bitmap = new(new SKImageInfo(96, 64, SKColorType.Bgra8888, SKAlphaType.Premul));
        for (int y = 0; y < bitmap.Height; y++)
        {
            for (int x = 0; x < bitmap.Width; x++)
            {
                bitmap.SetPixel(x, y, Colors[((y / 32) * 3) + (x / 32)]);
            }
        }
        using SKPixmap pixmap = bitmap.PeekPixels();
        using SKData encoded = SKWebpEncoder.Encode(pixmap, new SKWebpEncoderOptions(SKWebpEncoderCompression.Lossless, 100))
            ?? throw new InvalidOperationException("Could not encode the generated WebP fixture.");
        byte[] original = encoded.ToArray();
        using MemoryStream output = new();
        using BinaryWriter writer = new(output);
        writer.Write("RIFF"u8);
        writer.Write(0); // patched after adding chunks
        writer.Write("WEBP"u8);
        writer.Write("VP8X"u8);
        writer.Write(10);
        writer.Write(new byte[] { 8, 0, 0, 0, 95, 0, 0, 63, 0, 0 }); // EXIF, 96 x 64
        writer.Write(original, 12, original.Length - 12);
        writer.Write("EXIF"u8);
        byte[] exif = [73, 73, 42, 0, 8, 0, 0, 0, 1, 0, 18, 1, 3, 0, 1, 0, 0, 0,
            (byte)orientation, 0, 0, 0, 0, 0, 0, 0];
        writer.Write(exif.Length);
        writer.Write(exif);
        writer.Flush();
        byte[] result = output.ToArray();
        BitConverter.GetBytes(result.Length - 8).CopyTo(result, 4);
        return result;
    }
}
