using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Media;

using ImageMagick;

using ModernImageViewer.Application.Editing;
using ModernImageViewer.Application.Images;
using ModernImageViewer.Codecs;
using ModernImageViewer.Codecs.Editing;
using ModernImageViewer.Imaging;
using ModernImageViewer.UI.Controls;

using SkiaSharp;

namespace ModernImageViewer.UI.Tests;

public sealed class ExportMetadataTests
{
    private static readonly string[] ForbiddenChunks = ["XMP ", "tEXt", "iTXt", "zTXt", "sRGB"];
    private static readonly ExifTag[] SafeExifTags =
    [
        ExifTag.Model, ExifTag.LensModel, ExifTag.DateTimeOriginal, ExifTag.ISOSpeedRatings,
        ExifTag.ExposureTime, ExifTag.FNumber, ExifTag.FocalLength, ExifTag.Orientation,
        ExifTag.PixelXDimension, ExifTag.PixelYDimension, ExifTag.ColorSpace,
    ];

    [Theory]
    [InlineData(ImageExportFormat.Png, ".png", ImageExportMetadataMode.Remove)]
    [InlineData(ImageExportFormat.Png, ".png", ImageExportMetadataMode.PreserveCamera)]
    [InlineData(ImageExportFormat.Jpeg, ".jpg", ImageExportMetadataMode.Remove)]
    [InlineData(ImageExportFormat.Jpeg, ".jpg", ImageExportMetadataMode.PreserveCamera)]
    [InlineData(ImageExportFormat.Webp, ".webp", ImageExportMetadataMode.Remove)]
    [InlineData(ImageExportFormat.Webp, ".webp", ImageExportMetadataMode.PreserveCamera)]
    public async Task CameraPolicyRegeneratesSafeExifCorrectsOrientationAndDimensionsAndNeverMutatesSource(
        ImageExportFormat format, string extension, ImageExportMetadataMode mode)
    {
        using Files files = new();
        File.WriteAllBytes(files.Source, CreateCameraJpeg());
        byte[] originalHash = SHA256.HashData(File.ReadAllBytes(files.Source));
        ImageDecoder decoder = new();
        using PixelBuffer source = await decoder.DecodeAsync(files.Source, TestContext.Current.CancellationToken);
        Assert.Equal(new PixelSize(2, 3), source.SourceSize); // EXIF 6 is baked into source pixels.
        ImageEditRecipe recipe = ImageEditRecipe.Create(source.SourceSize).RotateRight().WithSize(new(6, 4));
        string destination = Path.Combine(files.Directory, "output" + extension);
        await new ImageExportService(decoder).ExportAsync(new(files.Source, destination, recipe,
            Format: format, JpegQuality: 100, WebpLossless: true, MetadataMode: mode), TestContext.Current.CancellationToken);

        using SKBitmap output = SKBitmap.Decode(destination);
        Assert.Equal(6, output.Width);
        Assert.Equal(4, output.Height);
        var profiles = ReadProfiles(File.ReadAllBytes(destination), format);
        Assert.Equal(ColorProfiles.SRGB.ToByteArray(), profiles.Icc);
        if (mode == ImageExportMetadataMode.Remove) { Assert.Null(profiles.Exif); }
        else
        {
            Assert.NotNull(profiles.Exif);
            ExifProfile exif = new(profiles.Exif);
            Assert.Equal("Test camera", exif.GetValue(ExifTag.Model)?.Value);
            Assert.Equal("Test lens", exif.GetValue(ExifTag.LensModel)?.Value);
            Assert.Equal("2026:10:06 09:10:11", exif.GetValue(ExifTag.DateTimeOriginal)?.Value);
            Assert.Equal(new ushort[] { 400 }, exif.GetValue(ExifTag.ISOSpeedRatings)?.Value);
            Assert.Equal(.005, exif.GetValue(ExifTag.ExposureTime)!.Value.ToDouble(), 6);
            Assert.Equal(2.8, exif.GetValue(ExifTag.FNumber)!.Value.ToDouble(), 6);
            Assert.Equal(50, exif.GetValue(ExifTag.FocalLength)!.Value.ToDouble(), 6);
            Assert.Equal((ushort)1, exif.GetValue(ExifTag.Orientation)?.Value);
            Assert.Equal(6u, (uint)exif.GetValue(ExifTag.PixelXDimension)!.Value);
            Assert.Equal(4u, (uint)exif.GetValue(ExifTag.PixelYDimension)!.Value);
            Assert.Equal((ushort)1, exif.GetValue(ExifTag.ColorSpace)?.Value);
            Assert.Null(exif.GetValue(ExifTag.GPSLatitude));
            Assert.Null(exif.GetValue(ExifTag.GPSLongitude));
            Assert.Null(exif.GetValue(ExifTag.GPSIFDOffset));
            Assert.Null(exif.GetValue(ExifTag.Artist));
            Assert.Null(exif.GetValue(ExifTag.Software));
            Assert.Null(exif.GetValue(ExifTag.ImageWidth));
            Assert.Null(exif.GetValue(ExifTag.ImageLength));
            Assert.Equal(0u, exif.ThumbnailLength);
            Assert.All(exif.Values, value => Assert.Contains(value.Tag, SafeExifTags));
        }
        Assert.Equal(originalHash, SHA256.HashData(File.ReadAllBytes(files.Source)));
        Assert.Empty(Directory.GetFiles(files.Directory, ".miv-export-*.tmp"));
    }

    [Theory]
    [InlineData(ImageExportFormat.Png, ".png")]
    [InlineData(ImageExportFormat.Jpeg, ".jpg")]
    [InlineData(ImageExportFormat.Webp, ".webp")]
    public async Task WicRgbProfileTransformsPixelsBeforeExplicitSrgbDeclaration(ImageExportFormat format, string extension)
    {
        using Files files = new();
        File.WriteAllBytes(files.Source, CreateCameraJpeg(CreateLinearRgbProfile()));
        string destination = Path.Combine(files.Directory, "output" + extension);
        await new ImageExportService(new ImageDecoder()).ExportAsync(new(files.Source, destination,
            ImageEditRecipe.Create(new(2, 3)), Format: format, JpegQuality: 100, WebpLossless: true), TestContext.Current.CancellationToken);
        using SKBitmap image = SKBitmap.Decode(destination);
        SKColor pixel = image.GetPixel(0, 0);
        Assert.InRange(pixel.Red, (byte)185, (byte)191);
        Assert.InRange(pixel.Green, (byte)185, (byte)191);
        Assert.InRange(pixel.Blue, (byte)185, (byte)191);
        Assert.Equal(ColorProfiles.SRGB.ToByteArray(), ReadProfiles(File.ReadAllBytes(destination), format).Icc);
    }

    [Fact]
    public async Task LosslessWebpKeepsOpaqueRgbAndAlphaWithBoundedPremultiplicationRounding()
    {
        using Files files = new();
        string sourcePath = Path.Combine(files.Directory, "source.png");
        using (SKBitmap fixture = new(new SKImageInfo(4, 2, SKColorType.Bgra8888, SKAlphaType.Premul)))
        {
            fixture.SetPixel(0, 0, new SKColor(17, 91, 203, 255));
            fixture.SetPixel(1, 0, new SKColor(231, 53, 117, 128));
            fixture.SetPixel(2, 0, new SKColor(173, 229, 41, 64));
            fixture.SetPixel(3, 0, SKColors.Transparent);
            fixture.SetPixel(0, 1, new SKColor(227, 31, 109, 255));
            fixture.SetPixel(1, 1, new SKColor(81, 157, 243, 192));
            fixture.SetPixel(2, 1, new SKColor(251, 129, 63, 255));
            fixture.SetPixel(3, 1, new SKColor(39, 211, 97, 128));
            using FileStream stream = new(sourcePath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            Assert.True(fixture.Encode(stream, SKEncodedImageFormat.Png, 100));
        }
        byte[] fileHash = SHA256.HashData(File.ReadAllBytes(sourcePath));
        ImageDecoder decoder = new();
        using PixelBuffer source = await decoder.DecodeAsync(sourcePath, TestContext.Current.CancellationToken);
        // Compare the actual 8-bit premultiplied BGRA, including alpha quantization,
        // rather than the unpremultiplied colors used to create the PNG fixture.
        byte[] expected = source.Pixels.ToArray();
        byte[] pixelHash = SHA256.HashData(expected);
        Assert.Equal(new PixelSize(4, 2), source.Size);
        Assert.Equal((byte)255, expected[3]);
        Assert.Equal((byte)128, expected[7]);
        Assert.Equal((byte)64, expected[11]);
        Assert.Equal((byte)0, expected[15]);
        ImageFileStamp stamp = Assert.IsType<ImageFileStamp>(source.SourceFileStamp);
        string destination = Path.Combine(files.Directory, "lossless.webp");
        await new ImageExportService(new ForbiddenDecoder()).ExportAsync(new(sourcePath, destination,
            ImageEditRecipe.Create(source.SourceSize), Format: ImageExportFormat.Webp,
            JpegQuality: 1, WebpLossless: true, ExpectedSourceLength: stamp.Length, ExpectedSourceModifiedUtc: stamp.ModifiedUtc,
            SourcePixels: new(source.Size, source.Stride, source.Pixels, stamp)), TestContext.Current.CancellationToken);
        using PixelBuffer output = await decoder.DecodeAsync(destination, TestContext.Current.CancellationToken);
        Assert.Equal(source.Size, output.Size);
        Assert.Equal(source.Stride, output.Stride);
        // WebP losslessly stores straight 8-bit channels. Converting back to premultiplied
        // BGRA can round a translucent channel down by one; opaque RGB and alpha remain exact.
        for (int offset = 0; offset < expected.Length; offset += 4)
        {
            byte alpha = expected[offset + 3];
            Assert.Equal(alpha, output.Pixels.Span[offset + 3]);
            for (int channel = 0; channel < 3; channel++)
            {
                byte actual = output.Pixels.Span[offset + channel];
                if (alpha == 255) { Assert.Equal(expected[offset + channel], actual); }
                else if (alpha == 0)
                {
                    Assert.Equal((byte)0, expected[offset + channel]);
                    Assert.Equal((byte)0, actual);
                }
                else
                {
                    Assert.InRange(actual, (byte)0, alpha);
                    Assert.InRange(Math.Abs(expected[offset + channel] - actual), 0, 1);
                }
            }
        }
        Assert.Equal(pixelHash, SHA256.HashData(source.Pixels.Span));
        Assert.Equal(fileHash, SHA256.HashData(File.ReadAllBytes(sourcePath)));
        byte[] encoded = File.ReadAllBytes(destination);
        bool hasLosslessRaster = false;
        for (int offset = 12; offset < encoded.Length;)
        {
            Assert.False(encoded.AsSpan(offset, 4).SequenceEqual("VP8 "u8));
            hasLosslessRaster |= encoded.AsSpan(offset, 4).SequenceEqual("VP8L"u8);
            int length = BinaryPrimitives.ReadInt32LittleEndian(encoded.AsSpan(offset + 4));
            offset += 8 + length + (length & 1);
        }
        Assert.True(hasLosslessRaster);
        Assert.Empty(Directory.GetFiles(files.Directory, ".miv-export-*.tmp"));
    }

    [Fact]
    public async Task EditorProfileInterfaceReturnsActualWicProfileAndHonorsCancellation()
    {
        using Files files = new();
        byte[] profile = CreateLinearRgbProfile();
        File.WriteAllBytes(files.Source, CreateCameraJpeg(profile));
        IImageExportService exporter = new ImageExportService(new ImageDecoder());
        Assert.Equal(profile, await exporter.GetSourceColorProfileAsync(files.Source, TestContext.Current.CancellationToken));
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => exporter.GetSourceColorProfileAsync(files.Source, cancellation.Token));
        Assert.Equal(profile, ReadProfiles(File.ReadAllBytes(files.Source), ImageExportFormat.Jpeg).Icc);
    }

    [Fact]
    public async Task UnprofiledDngBitmapPreviewRemainsEditableAndPreservesCameraWithoutChangingSource()
    {
        using Files files = new();
        byte[] fixture = Assert.IsType<byte[]>(typeof(RawPreviewDecoderTests)
            .GetMethod("CreateDng", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [false, false, true]));
        string sourcePath = Path.Combine(files.Directory, "bitmap-preview.dng");
        File.WriteAllBytes(sourcePath, fixture);
        byte[] hash = SHA256.HashData(fixture);
        ImageDecoder decoder = new();
        using PixelBuffer source = await decoder.DecodeAsync(sourcePath, TestContext.Current.CancellationToken);
        Assert.True(source.Metadata.IsEmbeddedPreview);
        Assert.Equal(new PixelSize(64, 96), source.SourceSize);
        Assert.Equal((ushort)6, source.Metadata.Orientation);
        IImageExportService exporter = new ImageExportService(decoder);
        Assert.Null(await exporter.GetSourceColorProfileAsync(sourcePath, TestContext.Current.CancellationToken));
        ImageEditRecipe recipe = ImageEditRecipe.Create(source.SourceSize).WithCrop(new(0, 0, 32, 48)).RotateRight()
            .WithAdjustments(new() { Exposure = -.25, Brightness = 5 });
        string destination = Path.Combine(files.Directory, "edited-bitmap-preview.png");
        await exporter.ExportAsync(new(sourcePath, destination, recipe, MetadataMode: ImageExportMetadataMode.PreserveCamera),
            TestContext.Current.CancellationToken);
        using SKBitmap output = SKBitmap.Decode(destination);
        Assert.Equal(48, output.Width);
        Assert.Equal(32, output.Height);
        SKColor center = output.GetPixel(output.Width / 2, output.Height / 2);
        Assert.Equal((byte)255, center.Alpha);
        Assert.NotEqual(SKColors.Black, center);
        var profiles = ReadProfiles(File.ReadAllBytes(destination), ImageExportFormat.Png);
        Assert.Equal(ColorProfiles.SRGB.ToByteArray(), profiles.Icc);
        ExifProfile exif = new(Assert.IsType<byte[]>(profiles.Exif));
        Assert.Equal(source.Metadata.Camera, exif.GetValue(ExifTag.Model)?.Value);
        Assert.Contains("Synthetic", exif.GetValue(ExifTag.Model)?.Value ?? string.Empty, StringComparison.Ordinal);
        Assert.Equal((ushort)1, exif.GetValue(ExifTag.Orientation)?.Value);
        Assert.Equal(48u, (uint)exif.GetValue(ExifTag.PixelXDimension)!.Value);
        Assert.Equal(32u, (uint)exif.GetValue(ExifTag.PixelYDimension)!.Value);
        Assert.Null(exif.GetValue(ExifTag.GPSIFDOffset));
        Assert.Equal(hash, SHA256.HashData(File.ReadAllBytes(sourcePath)));
        Assert.Empty(Directory.GetFiles(files.Directory, ".miv-export-*.tmp"));
    }

    [Fact]
    public async Task ProfileAwareEditorPreviewAndExportMatchAfterBrightnessAndExposure()
    {
        using Files files = new();
        byte[] profile = CreateLinearRgbProfile();
        File.WriteAllBytes(files.Source, CreateCameraJpeg(profile));
        byte[] fileHash = SHA256.HashData(File.ReadAllBytes(files.Source));
        ImageDecoder decoder = new();
        using PixelBuffer source = await decoder.DecodeAsync(files.Source, TestContext.Current.CancellationToken);
        // WIC provides device-profile samples here, not the approximately 188 sRGB
        // value for this linear-RGB mid-gray. Both edit paths must perform the conversion.
        Assert.InRange(source.Pixels.Span[0], (byte)127, (byte)129);
        byte[] pixelHash = SHA256.HashData(source.Pixels.Span);
        IImageExportService exporter = new ImageExportService(decoder);
        byte[]? actualProfile = await exporter.GetSourceColorProfileAsync(files.Source, TestContext.Current.CancellationToken);
        Assert.Equal(profile, actualProfile);
        ImageEditRecipe recipe = ImageEditRecipe.Create(source.SourceSize)
            .WithAdjustments(new() { Exposure = -.5, Brightness = 10 });
        TaskCompletionSource<SKColor[]> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Thread thread = new(() =>
        {
            try
            {
                SKColor[] pixels;
                using (ImageViewport viewport = new())
                {
                    viewport.Resources["CheckerDarkBrush"] = Brushes.Black;
                    viewport.Resources["CheckerLightBrush"] = Brushes.Black;
                    viewport.Presentation = new(ImageOpenStatus.Loaded, source, files.Source);
                    viewport.Measure(new Size(16, 24));
                    viewport.Arrange(new Rect(0, 0, 16, 24));
                    viewport.SetEditSourceColorProfile(actualProfile);
                    viewport.SetEditRecipe(recipe);
                    SKBitmap preview = Assert.IsType<SKBitmap>(typeof(ImageViewport)
                        .GetMethod("GetEditorPreview", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(viewport, [recipe]));
                    Assert.Equal(recipe.OutputSize.Width, preview.Width);
                    Assert.Equal(recipe.OutputSize.Height, preview.Height);
                    Assert.InRange(preview.GetPixel(0, 0).Red, (byte)155, (byte)162);
                    pixels = new SKColor[preview.Width * preview.Height];
                    for (int y = 0; y < preview.Height; y++)
                    {
                        for (int x = 0; x < preview.Width; x++) { pixels[y * preview.Width + x] = preview.GetPixel(x, y); }
                    }
                }
                completion.SetResult(pixels);
            }
            catch (Exception exception) { completion.SetException(exception); }
        })
        { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        SKColor[] previewPixels = await completion.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        string destination = Path.Combine(files.Directory, "edited.png");
        ImageFileStamp stamp = Assert.IsType<ImageFileStamp>(source.SourceFileStamp);
        await exporter.ExportAsync(new(files.Source, destination, recipe,
            ExpectedSourceLength: stamp.Length, ExpectedSourceModifiedUtc: stamp.ModifiedUtc,
            SourcePixels: new(source.Size, source.Stride, source.Pixels, stamp)), TestContext.Current.CancellationToken);
        using SKBitmap exported = SKBitmap.Decode(destination);
        Assert.Equal(recipe.OutputSize.Width, exported.Width);
        Assert.Equal(recipe.OutputSize.Height, exported.Height);
        for (int y = 0; y < exported.Height; y++)
        {
            for (int x = 0; x < exported.Width; x++) { Assert.Equal(previewPixels[y * exported.Width + x], exported.GetPixel(x, y)); }
        }
        Assert.Equal(ColorProfiles.SRGB.ToByteArray(), ReadProfiles(File.ReadAllBytes(destination), ImageExportFormat.Png).Icc);
        Assert.Equal(pixelHash, SHA256.HashData(source.Pixels.Span));
        Assert.Equal(fileHash, SHA256.HashData(File.ReadAllBytes(files.Source)));
        Assert.Empty(Directory.GetFiles(files.Directory, ".miv-export-*.tmp"));
    }

    [Theory]
    [InlineData(ImageExportFormat.Png, ".png")]
    [InlineData(ImageExportFormat.Webp, ".webp")]
    public async Task PreserveCameraReadsRegeneratedMetadataFromModernOutput(ImageExportFormat format, string extension)
    {
        using Files files = new();
        File.WriteAllBytes(files.Source, CreateCameraJpeg());
        ImageExportService exporter = new(new ImageDecoder());
        string first = Path.Combine(files.Directory, "first" + extension);
        await exporter.ExportAsync(new(files.Source, first, ImageEditRecipe.Create(new(2, 3)), Format: format,
            WebpLossless: true, MetadataMode: ImageExportMetadataMode.PreserveCamera), TestContext.Current.CancellationToken);
        string second = Path.Combine(files.Directory, "second.jpg");
        await exporter.ExportAsync(new(first, second, ImageEditRecipe.Create(new(2, 3)), Format: ImageExportFormat.Jpeg,
            MetadataMode: ImageExportMetadataMode.PreserveCamera), TestContext.Current.CancellationToken);
        ExifProfile exif = new(ReadProfiles(File.ReadAllBytes(second), ImageExportFormat.Jpeg).Exif!);
        Assert.Equal("Test camera", exif.GetValue(ExifTag.Model)?.Value);
        Assert.Equal("Test lens", exif.GetValue(ExifTag.LensModel)?.Value);
        Assert.Null(exif.GetValue(ExifTag.GPSLatitude));
    }

    [Fact]
    public async Task NonSrgbMemoryAndInvalidMetadataModeFailBeforeDecodeOrWrite()
    {
        using Files files = new();
        ImageExportRequest request = new(null, Path.Combine(files.Directory, "output.png"), ImageEditRecipe.Create(new(1, 1)),
            SourcePixels: new(new(1, 1), 4, new byte[] { 0, 0, 255, 255 }, IsSrgb: false), MemorySourceIdentity: Guid.NewGuid());
        ImageExportService exporter = new(new ForbiddenDecoder());
        ImageExportException error = await Assert.ThrowsAsync<ImageExportException>(() => exporter.ExportAsync(request,
            TestContext.Current.CancellationToken));
        Assert.Equal(ImageExportError.DecodeFailed, error.Error);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => exporter.ExportAsync(request with
        {
            SourcePixels = request.SourcePixels! with { IsSrgb = true },
            MetadataMode = (ImageExportMetadataMode)99,
        }, TestContext.Current.CancellationToken));
        Assert.Empty(Directory.GetFiles(files.Directory));
    }

    [Fact]
    public async Task EffectsBudgetRejectsOtherwiseValidOutputBeforeAllocatingOrDecoding()
    {
        using Files files = new();
        ImageEditRecipe recipe = ImageEditRecipe.Create(new(4000, 3000)).WithAdjustments(new() { Blur = 1 });
        ImageExportException error = await Assert.ThrowsAsync<ImageExportException>(() => new ImageExportService(new ForbiddenDecoder())
            .ExportAsync(new(files.Source, Path.Combine(files.Directory, "output.png"), recipe), TestContext.Current.CancellationToken));
        Assert.Equal(ImageExportError.BudgetExceeded, error.Error);
        Assert.Empty(Directory.GetFiles(files.Directory));
    }

    [Fact]
    public async Task CancellationAfterDecodeNeverWritesAndPreservesSource()
    {
        using Files files = new();
        File.WriteAllBytes(files.Source, CreateCameraJpeg());
        byte[] original = File.ReadAllBytes(files.Source);
        using CancellationTokenSource cancellation = new();
        string output = Path.Combine(files.Directory, "output.png");
        ImageExportRequest request = new(files.Source, output, ImageEditRecipe.Create(new(2, 3)), MetadataMode: ImageExportMetadataMode.PreserveCamera);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new ImageExportService(new CancelDecoder(cancellation))
            .ExportAsync(request, cancellation.Token));
        Assert.False(File.Exists(output));
        Assert.Equal(original, File.ReadAllBytes(files.Source));
        Assert.Empty(Directory.GetFiles(files.Directory, ".miv-export-*.tmp"));
    }

    [Fact]
    public async Task DestinationAppearingBeforeMetadataCommitIsNeverReplacedAndBothStagesAreCleaned()
    {
        using Files files = new();
        File.WriteAllBytes(files.Source, CreateCameraJpeg());
        byte[] original = File.ReadAllBytes(files.Source);
        string output = Path.Combine(files.Directory, "output.webp");
        ImageExportException error = await Assert.ThrowsAsync<ImageExportException>(() => new ImageExportService(new CommitRaceDecoder(output))
            .ExportAsync(new(files.Source, output, ImageEditRecipe.Create(new(2, 3)), Format: ImageExportFormat.Webp,
                WebpLossless: true, MetadataMode: ImageExportMetadataMode.PreserveCamera), TestContext.Current.CancellationToken));
        Assert.Equal(ImageExportError.DestinationExists, error.Error);
        Assert.Equal(new byte[] { 9, 8, 7 }, File.ReadAllBytes(output));
        Assert.Equal(original, File.ReadAllBytes(files.Source));
        Assert.Empty(Directory.GetFiles(files.Directory, ".miv-export-*.tmp"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void MetadataRewriteCancellationAndTruncatedEncodedInputCleanTheEnrichedTemporary(bool cancel)
    {
        using Files files = new();
        byte[] encoded = cancel ? CreateCameraJpeg() : [255, 216, 255];
        string temporary = Path.Combine(files.Directory, ".miv-export-encoded.tmp");
        File.WriteAllBytes(temporary, encoded);
        using CancellationTokenSource cancellation = new();
        if (cancel) { cancellation.Cancel(); }
        void Rewrite() => ImageExportMetadata.AddToEncodedFile(temporary, ImageExportFormat.Jpeg, new(3, 2), null, cancellation.Token);
        if (cancel) { Assert.ThrowsAny<OperationCanceledException>(Rewrite); }
        else { Assert.Throws<EndOfStreamException>(Rewrite); }
        Assert.Equal(encoded, File.ReadAllBytes(temporary));
        Assert.Empty(Directory.GetFiles(files.Directory, "*.metadata.tmp"));
    }

    [Fact]
    public async Task GlobalBlurFiltersTheComposedRasterAcrossTileEdgesThenDrawsUnfilteredAnnotations()
    {
        using Files files = new();
        File.WriteAllBytes(files.Source, CreateCameraJpeg());
        TileDecoder decoder = new();
        ImageEditRecipe recipe = ImageEditRecipe.Create(new(8192, 4096)).WithCrop(new(0, 0, 4092, 64))
            .WithAdjustments(new() { Blur = 2 })
            .WithAnnotations([new(ImageAnnotationKind.Rectangle, [new(20, 10), new(60, 50)], Color: 0xffff0000, StrokeWidth: 3)]);
        string output = Path.Combine(files.Directory, "output.png");
        await new ImageExportService(decoder).ExportAsync(new(files.Source, output, recipe), TestContext.Current.CancellationToken);
        using SKBitmap image = SKBitmap.Decode(output);
        Assert.Equal(2, decoder.RegionCount);
        Assert.InRange(image.GetPixel(2045, 32).Red, (byte)70, (byte)160);
        Assert.InRange(image.GetPixel(2046, 32).Red, (byte)95, (byte)190);
        Assert.Equal(SKColors.Red, image.GetPixel(20, 25));
        Assert.Empty(Directory.GetFiles(files.Directory, ".miv-export-*.tmp"));
    }

    internal static (byte[]? Exif, byte[]? Icc) ReadProfiles(byte[] bytes, ImageExportFormat format)
    {
        byte[]? exif = null;
        byte[]? icc = null;
        if (format == ImageExportFormat.Jpeg)
        {
            for (int offset = 2; offset < bytes.Length;)
            {
                Assert.Equal((byte)255, bytes[offset]);
                byte marker = bytes[offset + 1];
                if (marker == 0xda) { break; }
                int length = BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(offset + 2));
                byte[] value = bytes.AsSpan(offset + 4, length - 2).ToArray();
                if (marker == 0xe1 && value.AsSpan().StartsWith("Exif\0\0"u8)) { Assert.Null(exif); exif = value; }
                if (marker == 0xe2 && value.AsSpan().StartsWith("ICC_PROFILE\0"u8)) { Assert.Null(icc); icc = value[14..]; }
                offset += 2 + length;
            }
        }
        else
        {
            bool png = format == ImageExportFormat.Png;
            for (int offset = png ? 8 : 12; offset < bytes.Length;)
            {
                int length = png ? BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(offset))
                    : BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset + 4));
                string type = Encoding.ASCII.GetString(bytes, png ? offset + 4 : offset, 4);
                byte[] value = bytes.AsSpan(offset + 8, length).ToArray();
                if (type is "eXIf" or "EXIF") { Assert.Null(exif); exif = value; }
                if (type == "ICCP") { Assert.Null(icc); icc = value; }
                if (type == "iCCP")
                {
                    Assert.Null(icc);
                    int start = Array.IndexOf(value, (byte)0) + 2;
                    using MemoryStream compressed = new(value, start, value.Length - start);
                    using ZLibStream zlib = new(compressed, CompressionMode.Decompress);
                    using MemoryStream profile = new();
                    zlib.CopyTo(profile);
                    icc = profile.ToArray();
                }
                Assert.DoesNotContain(type, ForbiddenChunks);
                offset += png ? 12 + length : 8 + length + (length & 1);
            }
        }
        if (exif is not null && !exif.AsSpan().StartsWith("Exif\0\0"u8)) { exif = [.. "Exif\0\0"u8, .. exif]; }
        return (exif, icc);
    }

    private static byte[] CreateCameraJpeg(byte[]? icc = null)
    {
        using SKBitmap bitmap = new(new SKImageInfo(3, 2, SKColorType.Bgra8888, SKAlphaType.Premul));
        bitmap.Erase(new SKColor(128, 128, 128));
        using SKData encoded = bitmap.Encode(SKEncodedImageFormat.Jpeg, 100);
        byte[] raster = encoded.ToArray();
        ExifProfile exif = new();
        exif.SetValue(ExifTag.Model, "Test camera");
        exif.SetValue(ExifTag.LensModel, "Test lens");
        exif.SetValue(ExifTag.DateTimeOriginal, "2026:10:06 09:10:11");
        exif.SetValue(ExifTag.ISOSpeedRatings, new ushort[] { 400 });
        exif.SetValue(ExifTag.ExposureTime, new Rational(1, 200));
        exif.SetValue(ExifTag.FNumber, new Rational(28, 10));
        exif.SetValue(ExifTag.FocalLength, new Rational(50));
        exif.SetValue(ExifTag.Orientation, (ushort)6);
        exif.SetValue(ExifTag.PixelXDimension, new Number(999));
        exif.SetValue(ExifTag.PixelYDimension, new Number(888));
        exif.SetValue(ExifTag.ImageWidth, new Number(999));
        exif.SetValue(ExifTag.ImageLength, new Number(888));
        exif.SetValue(ExifTag.Artist, "private author");
        exif.SetValue(ExifTag.Software, "private source");
        exif.SetValue(ExifTag.GPSLatitudeRef, "N");
        exif.SetValue(ExifTag.GPSLongitudeRef, "E");
        exif.SetValue(ExifTag.GPSLatitude, new[] { new Rational(22), new Rational(30), new Rational(0) });
        exif.SetValue(ExifTag.GPSLongitude, new[] { new Rational(114), new Rational(10), new Rational(0) });
        using MemoryStream stream = new();
        stream.Write(raster.AsSpan(0, 2));
        WriteJpegSegment(stream, 0xe1, exif.ToByteArray());
        if (icc is not null) { WriteJpegSegment(stream, 0xe2, [.. "ICC_PROFILE\0"u8, 1, 1, .. icc]); }
        stream.Write(raster.AsSpan(2));
        return stream.ToArray();
    }

    private static void WriteJpegSegment(Stream stream, byte marker, byte[] data)
    {
        Span<byte> header = stackalloc byte[4];
        header[0] = 255;
        header[1] = marker;
        BinaryPrimitives.WriteUInt16BigEndian(header[2..], (ushort)(data.Length + 2));
        stream.Write(header);
        stream.Write(data);
    }

    private static byte[] CreateLinearRgbProfile()
    {
        byte[] profile = new byte[312];
        void UInt(int offset, uint value) => BinaryPrimitives.WriteUInt32BigEndian(profile.AsSpan(offset), value);
        void Text(int offset, string value) => Encoding.ASCII.GetBytes(value).CopyTo(profile, offset);
        void Xyz(int offset, double x, double y, double z)
        {
            double[] values = [x, y, z];
            for (int index = 0; index < values.Length; index++)
            {
                BinaryPrimitives.WriteInt32BigEndian(profile.AsSpan(offset + index * 4), (int)Math.Round(values[index] * 65536));
            }
        }
        UInt(0, (uint)profile.Length);
        UInt(8, 0x02000000);
        Text(12, "mntr"); Text(16, "RGB "); Text(20, "XYZ "); Text(36, "acsp"); Text(80, "MIV ");
        BinaryPrimitives.WriteUInt16BigEndian(profile.AsSpan(24), 2026);
        BinaryPrimitives.WriteUInt16BigEndian(profile.AsSpan(26), 10);
        BinaryPrimitives.WriteUInt16BigEndian(profile.AsSpan(28), 6);
        Xyz(68, .9642, 1, .8249);
        UInt(128, 7);
        string[] names = ["rXYZ", "gXYZ", "bXYZ", "wtpt", "rTRC", "gTRC", "bTRC"];
        double[][] primaries = [[.4360747, .2225045, .0139322], [.3850649, .7168786, .0971045], [.1430804, .0606169, .7141733], [.9642, 1, .8249]];
        for (int index = 0; index < names.Length; index++)
        {
            int record = 132 + index * 12;
            Text(record, names[index]);
            UInt(record + 4, index < 4 ? (uint)(216 + index * 20) : 296);
            UInt(record + 8, index < 4 ? 20u : 14u);
            if (index < 4) { Text(216 + index * 20, "XYZ "); Xyz(224 + index * 20, primaries[index][0], primaries[index][1], primaries[index][2]); }
        }
        Text(296, "curv"); UInt(304, 1);
        BinaryPrimitives.WriteUInt16BigEndian(profile.AsSpan(308), 256);
        return profile;
    }

    private sealed class CancelDecoder(CancellationTokenSource cancellation) : IPreviewImageDecoder
    {
        public Task<PixelBuffer> DecodeAsync(string path, CancellationToken cancellationToken)
        {
            cancellation.Cancel();
            return Task.FromResult(new PixelBuffer(new(2, 3), 8, new byte[24]));
        }
        public Task<PixelBuffer> DecodePreviewAsync(string path, PixelSize maximumSize, CancellationToken cancellationToken) => DecodeAsync(path, cancellationToken);
    }

    private sealed class CommitRaceDecoder(string destination) : IPreviewImageDecoder
    {
        public async Task<PixelBuffer> DecodeAsync(string path, CancellationToken cancellationToken)
        {
            PixelBuffer pixels = await new ImageDecoder().DecodeAsync(path, cancellationToken);
            File.WriteAllBytes(destination, [9, 8, 7]);
            return pixels;
        }
        public Task<PixelBuffer> DecodePreviewAsync(string path, PixelSize maximumSize, CancellationToken cancellationToken) => DecodeAsync(path, cancellationToken);
    }

    private sealed class ForbiddenDecoder : IImageDecoder
    {
        public Task<PixelBuffer> DecodeAsync(string path, CancellationToken cancellationToken) => throw new InvalidOperationException("Must fail before decoding.");
    }

    private sealed class TileDecoder : IImageDecoder, IRegionImageDecoder
    {
        public int RegionCount { get; private set; }
        public Task<PixelBuffer> DecodeAsync(string path, CancellationToken cancellationToken) => throw new InvalidOperationException("Must use bounded tiles.");
        public Task<DecodedImageRegion> DecodeRegionAsync(string path, PixelRect region, PixelSize expectedSourceSize,
            long maximumDecodedBytes, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int stride = region.Width * 4;
            byte[] pixels = new byte[stride * region.Height];
            Assert.True(pixels.LongLength <= maximumDecodedBytes);
            for (int y = 0; y < region.Height; y++)
            {
                for (int x = 0; x < region.Width; x++)
                {
                    int offset = y * stride + x * 4;
                    byte gray = region.X + x < 2046 ? (byte)0 : (byte)255;
                    pixels[offset] = gray; pixels[offset + 1] = gray; pixels[offset + 2] = gray; pixels[offset + 3] = 255;
                }
            }
            RegionCount++;
            return Task.FromResult(new DecodedImageRegion(new(region.Size, stride, pixels, sourceSize: expectedSourceSize), region));
        }
    }

    private sealed class Files : IDisposable
    {
        public Files() => System.IO.Directory.CreateDirectory(Directory);
        public string Directory { get; } = Path.Combine(Path.GetTempPath(), $"miv-export-metadata-{Guid.NewGuid():N}");
        public string Source => Path.Combine(Directory, "source.jpg");
        public void Dispose() => System.IO.Directory.Delete(Directory, recursive: true);
    }
}
