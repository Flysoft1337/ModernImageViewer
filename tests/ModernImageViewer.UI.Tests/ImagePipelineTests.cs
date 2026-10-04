using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

using ModernImageViewer.Codecs.Wic;
using ModernImageViewer.Imaging;
using ModernImageViewer.UI.Rendering;

using SkiaSharp;

namespace ModernImageViewer.UI.Tests;

public sealed class ImagePipelineTests
{
    [Fact]
    public void RendererSharesPixelsAndKeepsThemAliveUntilBitmapRelease()
    {
        byte[] pixels = [255, 0, 0, 255, 0, 0, 255, 255, 0, 0, 0, 0];
        using PixelBuffer buffer = new(new PixelSize(2, 1), 12, pixels);
        using SKBitmap bitmap = SharedPixelBitmap.Create(buffer);
        Assert.Equal(SKColors.Blue, bitmap.GetPixel(0, 0));
        pixels[0] = 64;
        Assert.Equal((byte)64, bitmap.GetPixel(0, 0).Blue);
        buffer.Dispose();
        GC.Collect();
        Assert.Equal(SKColors.Red, bitmap.GetPixel(1, 0));
    }

    [Fact]
    public async Task JpegExifAndAllOrientationsProduceCorrectDisplayedCorners()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"viewer-exif-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            SKColor[] colors = [SKColors.Red, SKColors.Lime, SKColors.Blue, SKColors.Yellow];
            int[][] expected = [[0, 1, 2, 3], [1, 0, 3, 2], [3, 2, 1, 0], [2, 3, 0, 1],
                [0, 2, 1, 3], [2, 0, 3, 1], [3, 1, 2, 0], [1, 3, 0, 2]];
            byte[] pixels = new byte[80 * 60 * 4];
            for (int y = 0; y < 60; y++)
            {
                for (int x = 0; x < 80; x++)
                {
                    SKColor color = colors[(y >= 30 ? 2 : 0) + (x >= 40 ? 1 : 0)];
                    int offset = ((y * 80) + x) * 4;
                    pixels[offset] = color.Blue;
                    pixels[offset + 1] = color.Green;
                    pixels[offset + 2] = color.Red;
                    pixels[offset + 3] = 255;
                }
            }
            BitmapSource source = BitmapSource.Create(80, 60, 96, 96, PixelFormats.Bgra32, null, pixels, 80 * 4);
            source.Freeze();
            WicImageDecoder decoder = new();
            for (ushort orientation = 1; orientation <= 8; orientation++)
            {
                string path = Path.Combine(directory, $"{orientation}.jpg");
                BitmapMetadata metadata = new("jpg");
                metadata.SetQuery("/app1/ifd/{ushort=274}", orientation);
                metadata.SetQuery("/app1/ifd/{ushort=271}", "Canon");
                metadata.SetQuery("/app1/ifd/{ushort=272}", "Test camera");
                metadata.SetQuery("/app1/ifd/exif/{ushort=34855}", (ushort)200);
                metadata.SetQuery("/app1/ifd/exif/{ushort=33434}", ((ulong)125 << 32) | 1);
                JpegBitmapEncoder encoder = new() { QualityLevel = 100 };
                encoder.Frames.Add(BitmapFrame.Create(source, null, metadata, null));
                using (FileStream file = File.Create(path))
                {
                    encoder.Save(file);
                }
                using PixelBuffer image = await decoder.DecodeAsync(path, TestContext.Current.CancellationToken);
                Assert.Equal("Canon Test camera", image.Metadata.Camera);
                Assert.Equal((uint)200, image.Metadata.Iso);
                Assert.Equal(1.0 / 125, image.Metadata.ExposureSeconds);
                Assert.Equal(orientation, image.Metadata.Orientation);
                Assert.Equal(orientation >= 5 ? new PixelSize(60, 80) : new PixelSize(80, 60), image.Size);
                using SKBitmap bitmap = SharedPixelBitmap.Create(image);
                for (int corner = 0; corner < 4; corner++)
                {
                    int x = corner % 2 == 0 ? image.Size.Width / 4 : image.Size.Width * 3 / 4;
                    int y = corner < 2 ? image.Size.Height / 4 : image.Size.Height * 3 / 4;
                    SKColor actual = bitmap.GetPixel(x, y);
                    SKColor target = colors[expected[orientation - 1][corner]];
                    Assert.InRange(Math.Abs(actual.Red - target.Red), 0, 12);
                    Assert.InRange(Math.Abs(actual.Green - target.Green), 0, 12);
                    Assert.InRange(Math.Abs(actual.Blue - target.Blue), 0, 12);
                }
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
