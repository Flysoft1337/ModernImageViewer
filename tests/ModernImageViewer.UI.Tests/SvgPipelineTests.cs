using System.IO;
using System.Security.Cryptography;
using System.Text;

using ModernImageViewer.Application.Images;
using ModernImageViewer.Codecs;
using ModernImageViewer.Imaging;

namespace ModernImageViewer.UI.Tests;

public sealed class SvgPipelineTests
{
    [Fact]
    public async Task RestrictedSvgUsesSupportedFilePipelinesAndPreservesSourceBeforeFixtureExport()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"viewer-svg-pipeline-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "fixture.svg");
        try
        {
            const string markup = "<svg xmlns='http://www.w3.org/2000/svg' width='120' height='80'><rect width='120' height='80' fill='#336699' opacity='.5'/><circle cx='60' cy='40' r='20' fill='white'/></svg>";
            File.WriteAllBytes(path, Encoding.UTF8.GetBytes(markup));
            byte[] before = SHA256.HashData(File.ReadAllBytes(path));
            ImageDecoder decoder = new();
            using PixelBuffer main = await decoder.DecodeAsync(path, TestContext.Current.CancellationToken);
            Assert.Equal(new PixelSize(120, 80), main.Size);
            Assert.Equal(main.Size, main.SourceSize);
            using PixelBuffer preview = await decoder.DecodePreviewAsync(path, new PixelSize(64, 48), TestContext.Current.CancellationToken);
            Assert.Equal(main.Size, preview.SourceSize);
            Assert.InRange(preview.Size.Width, 1, 64);
            Assert.InRange(preview.Size.Height, 1, 48);
            using PixelBuffer thumbnail = await decoder.DecodeThumbnailAsync(path, new PixelSize(32, 20), TestContext.Current.CancellationToken);
            Assert.Equal(main.Size, thumbnail.SourceSize);
            Assert.InRange(thumbnail.Size.Width, 1, 32);
            Assert.InRange(thumbnail.Size.Height, 1, 20);
            await Assert.ThrowsAsync<ImageSizeLimitExceededException>(() =>
                decoder.DecodeDetailAsync(path, 4, TestContext.Current.CancellationToken));
            PixelRect bounds = new(17, 9, 31, 19);
            ImageDecodeException exception = await Assert.ThrowsAsync<ImageDecodeException>(() =>
                decoder.DecodeRegionAsync(path, bounds, main.SourceSize,
                    bounds.Width * bounds.Height * 4, TestContext.Current.CancellationToken));
            Assert.Equal(ImageOpenError.UnsupportedFormat, exception.Error);
            Assert.Equal(before, SHA256.HashData(File.ReadAllBytes(path)));
            string? exportDirectory = Environment.GetEnvironmentVariable("MIV_FORMAT_FIXTURE_DIRECTORY");
            if (!string.IsNullOrWhiteSpace(exportDirectory))
            {
                Directory.CreateDirectory(exportDirectory);
                File.Copy(path, Path.Combine(exportDirectory, "fixture.svg"), overwrite: true);
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
