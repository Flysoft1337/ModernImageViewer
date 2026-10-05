using System.Buffers.Binary;
using System.IO;
using System.Text;

using ModernImageViewer.Codecs;
using ModernImageViewer.Imaging;

using SkiaSharp;

namespace ModernImageViewer.UI.Tests;

public sealed class WebPColorProfileTests
{
    [Fact]
    public async Task EmbeddedLinearRgbProfileConvertsToSrgbInTheDecodedOutput()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"viewer-webp-icc-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            using SKBitmap bitmap = new(new SKImageInfo(4, 2, SKColorType.Bgra8888, SKAlphaType.Premul));
            bitmap.Erase(new SKColor(128, 128, 128));
            using SKPixmap pixmap = bitmap.PeekPixels();
            using SKData encoded = SKWebpEncoder.Encode(pixmap, new SKWebpEncoderOptions(SKWebpEncoderCompression.Lossless, 100))
                ?? throw new InvalidOperationException("Could not encode the generated WebP fixture.");
            byte[] raw = encoded.ToArray();
            string plainPath = Path.Combine(directory, "plain.webp");
            File.WriteAllBytes(plainPath, raw);
            string path = Path.Combine(directory, "linear.webp");
            File.WriteAllBytes(path, AddProfile(raw, CreateLinearRgbProfile()));
            byte[] original = File.ReadAllBytes(path);
            ImageDecoder decoder = new();
            using PixelBuffer plain = await decoder.DecodeAsync(plainPath, TestContext.Current.CancellationToken);
            using PixelBuffer corrected = await decoder.DecodeAsync(path, TestContext.Current.CancellationToken);
            Assert.Equal(new PixelSize(4, 2), corrected.SourceSize);
            Assert.InRange(plain.Pixels.Span[0], (byte)127, (byte)129);
            Assert.InRange(corrected.Pixels.Span[0], (byte)187, (byte)189);
            Assert.InRange(corrected.Pixels.Span[1], (byte)187, (byte)189);
            Assert.InRange(corrected.Pixels.Span[2], (byte)187, (byte)189);
            Assert.Equal((byte)255, corrected.Pixels.Span[3]);
            Assert.Equal(original, File.ReadAllBytes(path));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    // A test-owned ICC v2 matrix/TRC profile: D50-adapted sRGB primaries, linear gamma.
    // No system/vendor ICC file is copied or redistributed.
    private static byte[] CreateLinearRgbProfile()
    {
        byte[] profile = new byte[312];
        WriteUInt(profile, 0, (uint)profile.Length);
        WriteUInt(profile, 8, 0x02000000);
        WriteText(profile, 12, "mntr");
        WriteText(profile, 16, "RGB ");
        WriteText(profile, 20, "XYZ ");
        BinaryPrimitives.WriteUInt16BigEndian(profile.AsSpan(24), 2026);
        BinaryPrimitives.WriteUInt16BigEndian(profile.AsSpan(26), 10);
        BinaryPrimitives.WriteUInt16BigEndian(profile.AsSpan(28), 5);
        WriteText(profile, 36, "acsp");
        WriteText(profile, 80, "MIV ");
        WriteXyzValues(profile, 68, .9642, 1, .8249);
        WriteUInt(profile, 128, 7);
        string[] names = ["rXYZ", "gXYZ", "bXYZ", "wtpt", "rTRC", "gTRC", "bTRC"];
        for (int i = 0; i < names.Length; i++)
        {
            int record = 132 + (i * 12);
            WriteText(profile, record, names[i]);
            WriteUInt(profile, record + 4, i < 4 ? (uint)(216 + (i * 20)) : 296);
            WriteUInt(profile, record + 8, i < 4 ? 20u : 14u);
        }
        double[][] values = [[.4360747, .2225045, .0139322], [.3850649, .7168786, .0971045], [.1430804, .0606169, .7141733], [.9642, 1, .8249]];
        for (int i = 0; i < values.Length; i++)
        {
            int offset = 216 + (i * 20);
            WriteText(profile, offset, "XYZ ");
            WriteXyzValues(profile, offset + 8, values[i][0], values[i][1], values[i][2]);
        }
        WriteText(profile, 296, "curv");
        WriteUInt(profile, 304, 1);
        BinaryPrimitives.WriteUInt16BigEndian(profile.AsSpan(308), 256);
        return profile;
    }

    private static byte[] AddProfile(byte[] webp, byte[] profile)
    {
        using MemoryStream output = new();
        using BinaryWriter writer = new(output, Encoding.ASCII, leaveOpen: true);
        writer.Write("RIFF"u8);
        writer.Write(0u);
        writer.Write("WEBP"u8);
        writer.Write("VP8X"u8);
        writer.Write(10u);
        writer.Write(new byte[] { 0x20, 0, 0, 0, 3, 0, 0, 1, 0, 0 });
        writer.Write("ICCP"u8);
        writer.Write((uint)profile.Length);
        writer.Write(profile);
        writer.Write(webp.AsSpan(12));
        byte[] bytes = output.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), (uint)(bytes.Length - 8));
        return bytes;
    }

    private static void WriteUInt(byte[] data, int offset, uint value) => BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(offset), value);
    private static void WriteText(byte[] data, int offset, string value) => Encoding.ASCII.GetBytes(value).CopyTo(data, offset);
    private static void WriteXyzValues(byte[] data, int offset, double x, double y, double z)
    {
        double[] values = [x, y, z];
        for (int i = 0; i < values.Length; i++)
        {
            BinaryPrimitives.WriteInt32BigEndian(data.AsSpan(offset + (i * 4)), (int)Math.Round(values[i] * 65536));
        }
    }
}
