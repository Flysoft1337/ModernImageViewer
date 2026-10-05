using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

using ModernImageViewer.Application.Images;
using ModernImageViewer.Codecs;
using ModernImageViewer.Codecs.Wic;
using ModernImageViewer.Imaging;

namespace ModernImageViewer.UI.Tests;

[Collection("Region decoder slot")]
public sealed class RegionDecoderTests
{
    [Fact]
    public void OrientationMapsCornersAndAsymmetricRegionWithoutOverflow()
    {
        PixelSize source = new(80, 60);
        (int X, int Y)[] topLeft = [(0, 0), (79, 0), (79, 59), (0, 59), (0, 0), (0, 59), (79, 59), (79, 0)];
        for (ushort orientation = 1; orientation <= 8; orientation++)
        {
            Assert.Equal(topLeft[orientation - 1], RegionOrientation.ToRaw(0, 0, source, orientation));
            PixelRect region = new(7, 11, 13, 17);
            PixelRect raw = RegionOrientation.ToRawBounds(region, source, orientation);
            Assert.Equal(orientation >= 5 ? new PixelSize(17, 13) : region.Size, raw.Size);
            for (int y = region.Y; y < region.Bottom; y++)
            {
                for (int x = region.X; x < region.Right; x++)
                {
                    (int rawX, int rawY) = RegionOrientation.ToRaw(x, y, source, orientation);
                    Assert.InRange(rawX, raw.X, raw.Right - 1);
                    Assert.InRange(rawY, raw.Y, raw.Bottom - 1);
                }
            }
        }
        Assert.Throws<ArgumentOutOfRangeException>(() => new PixelRect(int.MaxValue, 0, 1, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PixelRect(0, int.MaxValue, 1, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PixelRect(-1, 0, 1, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PixelRect(0, 0, 0, 1));
    }

    [Fact]
    public async Task EveryExifOrientationRegionMatchesTheCorrespondingFullImagePixels()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"viewer-regions-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            ImageDecoder decoder = new();
            for (ushort orientation = 1; orientation <= 8; orientation++)
            {
                string path = Path.Combine(directory, $"{orientation}.jpg");
                WriteJpeg(path, orientation);
                using PixelBuffer full = await decoder.DecodeAsync(path, TestContext.Current.CancellationToken);
                PixelRect[] regions = [new(7, 11, 13, 17), new(0, 0, 1, 1),
                    new(full.Size.Width - 9, full.Size.Height - 5, 9, 5)];
                foreach (PixelRect bounds in regions)
                {
                    using DecodedImageRegion decoded = await decoder.DecodeRegionAsync(path, bounds, full.SourceSize,
                        16 * 1024 * 1024, TestContext.Current.CancellationToken);
                    Assert.Equal(bounds, decoded.Bounds);
                    Assert.Equal(bounds.Size, decoded.Image.Size);
                    Assert.Equal(full.SourceSize, decoded.Image.SourceSize);
                    Assert.Equal(orientation, decoded.Image.Metadata.Orientation);
                    Assert.Equal(bounds.Width * bounds.Height * 4, decoded.Image.Pixels.Length);
                    for (int y = 0; y < bounds.Height; y++)
                    {
                        byte[] expected = full.Pixels.Slice(((bounds.Y + y) * full.Stride) + (bounds.X * 4), bounds.Width * 4).ToArray();
                        byte[] actual = decoded.Image.Pixels.Slice(y * decoded.Image.Stride, bounds.Width * 4).ToArray();
                        Assert.Equal(expected, actual);
                    }
                }
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task RegionRejectsChangedSourceInvalidBoundsBudgetAndWebPBeforeOutputAllocation()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"viewer-region-limits-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            string path = Path.Combine(directory, "source.jpg");
            WriteJpeg(path, 1);
            ImageDecoder decoder = new();
            CancellationToken token = TestContext.Current.CancellationToken;
            PixelRect region = new(7, 11, 13, 17);
            ImageDecodeException changed = await Assert.ThrowsAsync<ImageDecodeException>(() =>
                decoder.DecodeRegionAsync(path, region, new PixelSize(81, 60), 16 * 1024 * 1024, token));
            Assert.Equal(ImageOpenError.CorruptFile, changed.Error);
            ImageDecodeException outside = await Assert.ThrowsAsync<ImageDecodeException>(() =>
                decoder.DecodeRegionAsync(path, new PixelRect(79, 59, 2, 1), new PixelSize(80, 60), 16 * 1024 * 1024, token));
            Assert.Equal(ImageOpenError.CorruptFile, outside.Error);
            await Assert.ThrowsAsync<ImageSizeLimitExceededException>(() =>
                decoder.DecodeRegionAsync(path, region, new PixelSize(80, 60), (13 * 17 * 4) - 1, token));
            await Assert.ThrowsAsync<ImageSizeLimitExceededException>(() =>
                decoder.DecodeRegionAsync(path, new PixelRect(0, 0, 2049, 1), new PixelSize(80, 60), 16 * 1024 * 1024, token));
            await Assert.ThrowsAsync<ImageSizeLimitExceededException>(() =>
                decoder.DecodeRegionAsync(path, default, new PixelSize(80, 60), 16 * 1024 * 1024, token));

            string webP = Path.Combine(directory, "source.webp");
            // Signature alone must reject the region path, before initializing Skia or decoding any pixels.
            await File.WriteAllBytesAsync(webP, [82, 73, 70, 70, 4, 0, 0, 0, 87, 69, 66, 80], token);
            ImageDecodeException unsupported = await Assert.ThrowsAsync<ImageDecodeException>(() =>
                decoder.DecodeRegionAsync(webP, new PixelRect(0, 0, 1, 1), new PixelSize(80, 60), 4, token));
            Assert.Equal(ImageOpenError.UnsupportedFormat, unsupported.Error);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task PrefetchSkipsBusyForegroundSlotAndReleasesItAfterFailure()
    {
        ImageDecoder decoder = new();
        CancellationToken token = TestContext.Current.CancellationToken;
        await WicImageDecoder.DecodeSlot.WaitAsync(token);
        try
        {
            Assert.Null(await decoder.TryDecodePreviewAsync("must-not-open.jpg", new PixelSize(1280, 800), token));
        }
        finally
        {
            WicImageDecoder.DecodeSlot.Release();
        }
        await Assert.ThrowsAsync<FileNotFoundException>(() =>
            decoder.TryDecodePreviewAsync("must-not-open.jpg", new PixelSize(1280, 800), token));
        Assert.True(await WicImageDecoder.DecodeSlot.WaitAsync(0, token));
        WicImageDecoder.DecodeSlot.Release();
    }

    private static void WriteJpeg(string path, ushort orientation)
    {
        byte[] pixels = new byte[80 * 60 * 4];
        for (int y = 0; y < 60; y++)
        {
            for (int x = 0; x < 80; x++)
            {
                int offset = ((y * 80) + x) * 4;
                pixels[offset] = (byte)(x * 3);
                pixels[offset + 1] = (byte)(y * 4);
                pixels[offset + 2] = (byte)(x + y);
                pixels[offset + 3] = 255;
            }
        }
        BitmapSource source = BitmapSource.Create(80, 60, 96, 96, PixelFormats.Bgra32, null, pixels, 80 * 4);
        BitmapMetadata metadata = new("jpg");
        metadata.SetQuery("/app1/ifd/{ushort=274}", orientation);
        JpegBitmapEncoder encoder = new() { QualityLevel = 100 };
        encoder.Frames.Add(BitmapFrame.Create(source, null, metadata, null));
        using FileStream stream = File.Create(path);
        encoder.Save(stream);
    }
}

[CollectionDefinition("Region decoder slot", DisableParallelization = true)]
public sealed class RegionDecoderSlotTests;
