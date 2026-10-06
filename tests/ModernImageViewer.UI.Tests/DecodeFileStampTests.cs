using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

using ModernImageViewer.Codecs;
using ModernImageViewer.Imaging;

using SkiaSharp;

namespace ModernImageViewer.UI.Tests;

public sealed class DecodeFileStampTests
{
    [Fact]
    public async Task PreviewFullAndRegionPublishTheSameSourceFileStamp()
    {
        string path = Path.Combine(Path.GetTempPath(), $"viewer-stamp-{Guid.NewGuid():N}.png");
        try
        {
            WritePng(path);
            ImageFileStamp expected = new(new FileInfo(path).Length, File.GetLastWriteTimeUtc(path));
            ImageDecoder decoder = new();
            CancellationToken token = TestContext.Current.CancellationToken;
            using PixelBuffer preview = await decoder.DecodePreviewAsync(path, new PixelSize(2, 2), token);
            using PixelBuffer full = await decoder.DecodeDetailAsync(path, 1024, token);
            using DecodedImageRegion region = await decoder.DecodeRegionAsync(path, new PixelRect(1, 1, 2, 2),
                full.SourceSize, 1024, token);
            Assert.Equal(expected, preview.SourceFileStamp);
            Assert.Equal(expected, full.SourceFileStamp);
            Assert.Equal(expected, region.Image.SourceFileStamp);
            Assert.Equal(new PixelSize(4, 4), region.Image.SourceSize);
            Assert.Equal(16, region.Image.Pixels.Length);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WebPCodecPreservesCallerStreamAndSourceLockThroughValidation(bool corrupt)
    {
        string path = Path.Combine(Path.GetTempPath(), $"viewer-webp-lock-{Guid.NewGuid():N}.webp");
        try
        {
            if (corrupt) { File.WriteAllBytes(path, "RIFF\0\0\0\0WEBPbroken"u8.ToArray()); }
            else { WriteWebP(path); }
            using (FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                ImageFileStamp expected = ImageDecoder.ReadFileStamp(path, stream);
                if (corrupt)
                {
                    Assert.Throws<ModernImageViewer.Application.Images.ImageDecodeException>(() =>
                        ImageDecoder.DecodeWebP(stream, null, null, TestContext.Current.CancellationToken));
                }
                else
                {
                    using PixelBuffer decoded = ImageDecoder.DecodeWebP(stream, null, null, TestContext.Current.CancellationToken);
                    Assert.Equal(new PixelSize(4, 4), decoded.Size);
                }
                // DecodeWebP has already disposed its codec and native stream here.
                Assert.Equal(expected.Length, stream.Length);
                ImageDecoder.ValidateFileStamp(path, stream, expected);
                Assert.Throws<IOException>(() =>
                {
                    using FileStream writer = new(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
                });
                Assert.Throws<IOException>(() => File.Delete(path));
            }
            using (FileStream writer = new(path, FileMode.Open, FileAccess.Write, FileShare.None))
            {
                Assert.True(writer.CanWrite);
            }
            File.Delete(path);
            Assert.False(File.Exists(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task WebPMainPreviewDetailAndThumbnailCompleteWithSourceFileStamp()
    {
        string path = Path.Combine(Path.GetTempPath(), $"viewer-webp-stamp-{Guid.NewGuid():N}.webp");
        try
        {
            WriteWebP(path);
            ImageFileStamp expected = new(new FileInfo(path).Length, File.GetLastWriteTimeUtc(path));
            ImageDecoder decoder = new();
            CancellationToken token = TestContext.Current.CancellationToken;
            using PixelBuffer main = await decoder.DecodeAsync(path, token);
            using PixelBuffer preview = await decoder.DecodePreviewAsync(path, new PixelSize(2, 2), token);
            using PixelBuffer detail = await decoder.DecodeDetailAsync(path, 1024, token);
            using PixelBuffer thumbnail = await decoder.DecodeThumbnailAsync(path, new PixelSize(2, 2), token);
            foreach (PixelBuffer image in new[] { main, preview, detail, thumbnail })
            {
                Assert.Equal(expected, image.SourceFileStamp);
                Assert.Equal(new PixelSize(4, 4), image.SourceSize);
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void StampValidationRejectsRealModificationAndDeletion(bool delete)
    {
        string path = Path.Combine(Path.GetTempPath(), $"viewer-stamp-race-{Guid.NewGuid():N}.bin");
        try
        {
            File.WriteAllBytes(path, new byte[4]);
            // Permit mutations here to exercise the final validation independently of the
            // production read lock; native/filesystem changes must still be rejected.
            using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            ImageFileStamp expected = ImageDecoder.ReadFileStamp(path, stream);
            ImageDecoder.ValidateFileStamp(path, stream, expected);
            if (delete) { File.Delete(path); }
            else
            {
                File.WriteAllBytes(path, new byte[8]);
                File.SetLastWriteTimeUtc(path, expected.ModifiedUtc.AddSeconds(5));
            }
            Assert.Throws<IOException>(() => ImageDecoder.ValidateFileStamp(path, stream, expected));
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static void WritePng(string path)
    {
        byte[] pixels = new byte[4 * 4 * 4];
        for (int i = 3; i < pixels.Length; i += 4) { pixels[i] = 255; }
        BitmapSource source = BitmapSource.Create(4, 4, 96, 96, PixelFormats.Pbgra32, null, pixels, 16);
        PngBitmapEncoder encoder = new();
        encoder.Frames.Add(BitmapFrame.Create(source));
        using FileStream stream = File.Create(path);
        encoder.Save(stream);
    }

    private static void WriteWebP(string path)
    {
        using SKBitmap bitmap = new(new SKImageInfo(4, 4, SKColorType.Bgra8888, SKAlphaType.Premul));
        bitmap.Erase(SKColors.CornflowerBlue);
        using SKPixmap pixmap = bitmap.PeekPixels();
        using SKData encoded = SKWebpEncoder.Encode(pixmap, new SKWebpEncoderOptions(SKWebpEncoderCompression.Lossless, 100))
            ?? throw new InvalidOperationException("Could not encode the generated WebP fixture.");
        using FileStream stream = File.Create(path);
        encoded.SaveTo(stream);
    }
}
