using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;

using ModernImageViewer.Application.Images;
using ModernImageViewer.Codecs;
using ModernImageViewer.Codecs.Raw;
using ModernImageViewer.Imaging;

namespace ModernImageViewer.UI.Tests;

public sealed class RawPreviewDecoderTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task DngJpegAndBitmapPreviewsRespectDirectionBoundsAndOriginalFile(bool jpeg, bool sixteenBit)
    {
        string directory = CreateDirectory();
        try
        {
            string path = Path.Combine(directory, "fixture.dng");
            File.WriteAllBytes(path, CreateDng(jpeg, sixteenBit, withPreview: true));
            byte[] hash = SHA256.HashData(File.ReadAllBytes(path));
            ImageDecoder decoder = new();
            using PixelBuffer full = await decoder.DecodeAsync(path, TestContext.Current.CancellationToken);
            Assert.True(full.Metadata.IsEmbeddedPreview);
            Assert.Equal(new PixelSize(64, 96), full.SourceSize);
            Assert.Equal(full.SourceSize, full.Size);
            Assert.Equal((ushort)6, full.Metadata.Orientation);
            Assert.Contains("Synthetic", full.Metadata.Camera ?? string.Empty, StringComparison.Ordinal);
            // The JPEG fixture also contains EXIF 6. A second tflip rotation would return 96 x 64.
            Assert.InRange(full.Pixels.Span[0], (byte)0, (byte)5);
            Assert.InRange(full.Pixels.Span[1], (byte)250, (byte)255);
            Assert.InRange(full.Pixels.Span[2], (byte)250, (byte)255);
            PixelSize[] bounds = [new PixelSize(48, 40), new PixelSize(32, 20)];
            for (int index = 0; index < bounds.Length; index++)
            {
                PixelSize bound = bounds[index];
                using PixelBuffer image = index == 0
                    ? await decoder.DecodePreviewAsync(path, bound, TestContext.Current.CancellationToken)
                    : await decoder.DecodeThumbnailAsync(path, bound, TestContext.Current.CancellationToken);
                Assert.Equal(full.SourceSize, image.SourceSize);
                Assert.True(image.Metadata.IsEmbeddedPreview);
                Assert.InRange(image.Size.Width, 1, bound.Width);
                Assert.InRange(image.Size.Height, 1, bound.Height);
                Assert.InRange(image.Pixels.Length, 4, bound.Width * bound.Height * 4);
            }
            await Assert.ThrowsAsync<ImageSizeLimitExceededException>(() =>
                decoder.DecodeDetailAsync(path, 4, TestContext.Current.CancellationToken));
            ImageDecodeException region = await Assert.ThrowsAsync<ImageDecodeException>(() =>
                decoder.DecodeRegionAsync(path, new PixelRect(0, 0, 10, 10), full.SourceSize, 400, TestContext.Current.CancellationToken));
            Assert.Equal(ImageOpenError.UnsupportedFormat, region.Error);
            Assert.Equal(hash, SHA256.HashData(File.ReadAllBytes(path)));
            string? export = Environment.GetEnvironmentVariable("MIV_FORMAT_FIXTURE_DIRECTORY");
            if (!string.IsNullOrWhiteSpace(export) && jpeg)
            {
                Directory.CreateDirectory(export);
                File.Copy(path, Path.Combine(export, "fixture.dng"), overwrite: true);
            }
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void MissingPreviewCorruptionAndCancellationRemainDistinctAndReleaseTheSlot()
    {
        string directory = CreateDirectory();
        try
        {
            string path = Path.Combine(directory, "no-preview.dng");
            File.WriteAllBytes(path, CreateDng(jpeg: false, sixteenBit: false, withPreview: false));
            ImageDecodeException missing = Assert.Throws<ImageDecodeException>(() =>
                RawPreviewImageDecoder.Decode(path, null, null, TestContext.Current.CancellationToken));
            Assert.Equal(ImageOpenError.RawNoPreview, missing.Error);
            File.WriteAllBytes(path, "not a camera file"u8.ToArray());
            ImageDecodeException corrupt = Assert.Throws<ImageDecodeException>(() =>
                RawPreviewImageDecoder.Decode(path, null, null, TestContext.Current.CancellationToken));
            Assert.Equal(ImageOpenError.CorruptFile, corrupt.Error);
            using CancellationTokenSource cancelled = new();
            cancelled.Cancel();
            Assert.Throws<OperationCanceledException>(() => RawPreviewImageDecoder.Decode(path, null, null, cancelled.Token));
            File.WriteAllBytes(path, CreateDng(jpeg: false, sixteenBit: false, withPreview: true));
            using PixelBuffer recovered = RawPreviewImageDecoder.Decode(path, new PixelSize(32, 20), null, TestContext.Current.CancellationToken);
            Assert.True(recovered.Metadata.IsEmbeddedPreview);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void RealCameraSamplesArePreviewOnlyAndPreserveFileHashes()
    {
        string? directory = Environment.GetEnvironmentVariable("MIV_RAW_CAMERA_SAMPLE_DIRECTORY");
        if (string.IsNullOrWhiteSpace(directory)) { Assert.Skip("Real camera fixtures are local verification inputs; CI uses the generated DNG."); }
        int tested = 0;
        foreach (string path in Directory.GetFiles(directory!))
        {
            if (!new[] { ".cr2", ".nef", ".arw", ".raf", ".rw2", ".orf", ".pef", ".cr3" }
                .Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase)) { continue; }
            tested++;
            byte[] before = SHA256.HashData(File.ReadAllBytes(path));
            using PixelBuffer image = RawPreviewImageDecoder.Decode(path, new PixelSize(256, 160), null, TestContext.Current.CancellationToken);
            Assert.True(image.Metadata.IsEmbeddedPreview);
            Assert.InRange(image.Size.Width, 1, 256);
            Assert.InRange(image.Size.Height, 1, 160);
            Assert.Equal(before, SHA256.HashData(File.ReadAllBytes(path)));
        }
        Assert.True(tested > 0, "The configured camera fixture directory must contain a real RAW sample.");
    }

    private static string CreateDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), $"viewer-raw-preview-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    // Minimal owned TIFF/DNG: one reduced RGB/JPEG IFD and one 256 x 192 Bayer sensor IFD.
    // Sensor pixels are present for identification, but the preview reader never unpacks them.
    private static byte[] CreateDng(bool jpeg, bool sixteenBit, bool withPreview)
    {
        byte[] rgb = new byte[96 * 64 * 3];
        byte[][] colors = [[255, 0, 0], [0, 255, 0], [0, 0, 255], [255, 255, 0], [255, 0, 255], [0, 255, 255]];
        for (int y = 0; y < 64; y++)
        {
            for (int x = 0; x < 96; x++) { colors[((y / 32) * 3) + (x / 32)].CopyTo(rgb, ((y * 96) + x) * 3); }
        }
        byte[] preview = rgb;
        if (jpeg)
        {
            BitmapSource bitmap = BitmapSource.Create(96, 64, 96, 96, PixelFormats.Rgb24, null, rgb, 96 * 3);
            BitmapMetadata metadata = new("jpg");
            metadata.SetQuery("/app1/ifd/{ushort=274}", (ushort)6);
            JpegBitmapEncoder encoder = new() { QualityLevel = 100 };
            encoder.Frames.Add(BitmapFrame.Create(bitmap, null, metadata, null));
            using MemoryStream encoded = new();
            encoder.Save(encoded);
            preview = encoded.ToArray();
        }
        else if (sixteenBit)
        {
            preview = new byte[rgb.Length * 2];
            for (int i = 0; i < rgb.Length; i++) { BitConverter.GetBytes((ushort)(rgb[i] * 257)).CopyTo(preview, i * 2); }
        }
        byte[] sensor = new byte[256 * 192 * 2];
        for (int offset = 0; offset < sensor.Length; offset += 2) { sensor[offset + 1] = 32; }
        List<TiffTag> common =
        [
            Text(271, sixteenBit ? "Imacon" : "MIV"), Text(272, "Synthetic"), Bytes(50706, [1, 4, 0, 0]), Bytes(50707, [1, 1, 0, 0]),
            Text(50708, sixteenBit ? "Imacon Synthetic" : "MIV Synthetic"),
        ];
        List<TiffTag> raw =
        [
            Long(254, 0), Long(256, 256), Long(257, 192), Shorts(258, [16]), Shorts(259, [1]),
            Shorts(262, [32803]), Long(273, 0), Shorts(274, [3]), Shorts(277, [1]), Long(278, 192),
            Long(279, (uint)sensor.Length), Shorts(284, [1]), Shorts(33421, [2, 2]), Bytes(33422, [0, 1, 1, 2]),
            Long(50717, 65535), Shorts(50778, [21]),
            new(50721, 10, 9, Enumerable.Range(0, 9).SelectMany(i => BitConverter.GetBytes(i % 4 == 0 ? 1 : 0).Concat(BitConverter.GetBytes(1))).ToArray()),
        ];
        List<TiffTag> first = withPreview
            ? [Long(254, 1), Long(256, 96), Long(257, 64), Shorts(258, jpeg ? [8, 8, 8] : sixteenBit ? [16, 16, 16] : [8, 8, 8]),
                Shorts(259, [jpeg ? (ushort)7 : (ushort)1]), Shorts(262, [2]), Long(273, 0), Shorts(274, [6]),
                Shorts(277, [3]), Long(278, 64), Long(279, (uint)preview.Length), Shorts(284, [1]), Long(330, 0)]
            : raw;
        first.AddRange(common);
        first.Sort((a, b) => a.Id.CompareTo(b.Id));
        raw.Sort((a, b) => a.Id.CompareTo(b.Id));
        int secondOffset = 8 + 2 + (first.Count * 12) + 4;
        int extras = secondOffset + (withPreview ? 2 + (raw.Count * 12) + 4 : 0);
        List<TiffTag> all = withPreview ? [.. first, .. raw] : first;
        int payloadOffset = extras + all.Where(tag => tag.Value.Length > 4).Sum(tag => (tag.Value.Length + 1) & ~1);
        first.Single(tag => tag.Id == 273).Value = BitConverter.GetBytes((uint)payloadOffset);
        if (withPreview)
        {
            first.Single(tag => tag.Id == 330).Value = BitConverter.GetBytes((uint)secondOffset);
            raw.Single(tag => tag.Id == 273).Value = BitConverter.GetBytes((uint)(payloadOffset + preview.Length));
        }
        using MemoryStream file = new();
        using BinaryWriter writer = new(file);
        writer.Write((ushort)0x4949);
        writer.Write((ushort)42);
        writer.Write(8U);
        int extraPosition = extras;
        WriteIfd(writer, first, ref extraPosition);
        if (withPreview) { WriteIfd(writer, raw, ref extraPosition); }
        foreach (TiffTag tag in all.Where(tag => tag.Value.Length > 4))
        {
            writer.Write(tag.Value);
            if ((tag.Value.Length & 1) != 0) { writer.Write((byte)0); }
        }
        if (withPreview) { writer.Write(preview); }
        writer.Write(sensor);
        return file.ToArray();
    }

    private static void WriteIfd(BinaryWriter writer, List<TiffTag> tags, ref int extraPosition)
    {
        writer.Write((ushort)tags.Count);
        foreach (TiffTag tag in tags)
        {
            writer.Write(tag.Id);
            writer.Write(tag.Type);
            writer.Write(tag.Count);
            if (tag.Value.Length <= 4)
            {
                writer.Write(tag.Value);
                writer.Write(new byte[4 - tag.Value.Length]);
            }
            else
            {
                writer.Write((uint)extraPosition);
                extraPosition += (tag.Value.Length + 1) & ~1;
            }
        }
        writer.Write(0U);
    }

    private sealed class TiffTag(ushort id, ushort type, uint count, byte[] value)
    {
        public ushort Id { get; } = id;
        public ushort Type { get; } = type;
        public uint Count { get; } = count;
        public byte[] Value { get; set; } = value;
    }
    private static TiffTag Long(ushort id, uint value) => new(id, 4, 1, BitConverter.GetBytes(value));
    private static TiffTag Shorts(ushort id, ushort[] values) => new(id, 3, (uint)values.Length, values.SelectMany(BitConverter.GetBytes).ToArray());
    private static TiffTag Bytes(ushort id, byte[] values) => new(id, 1, (uint)values.Length, values);
    private static TiffTag Text(ushort id, string text) => new(id, 2, (uint)text.Length + 1, Encoding.ASCII.GetBytes(text + '\0'));
}
