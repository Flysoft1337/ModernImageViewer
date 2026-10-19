using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.Windows.Media;
using System.Windows.Media.Imaging;

using ImageMagick;

using ModernImageViewer.Application.Images;
using ModernImageViewer.Codecs;
using ModernImageViewer.Codecs.Wic;
using ModernImageViewer.Imaging;

namespace ModernImageViewer.UI.Tests;

[Collection("Region decoder slot")]
public sealed class WicPngPreviewTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "miv-png-preview-" + Guid.NewGuid().ToString("N"));
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public WicPngPreviewTests()
    {
        Directory.CreateDirectory(_directory);
        WicPngPreview.ObserveLifetime = true;
    }

    [Theory]
    [InlineData(false, 101, 67, 37, 29)]
    [InlineData(true, 101, 67, 37, 29)]
    [InlineData(true, 4, 3, 40, 30)]
    [InlineData(false, 2048, 2048, 968, 391)]
    public void DirectWicMatchesExistingWpfPixelsAndReleasesEverySession(bool alpha, int width, int height, int boundWidth, int boundHeight)
    {
        string path = WritePng(alpha, width, height);
        byte[] original = File.ReadAllBytes(path);
        PixelSize bound = new(boundWidth, boundHeight);
        using MemoryStream referenceStream = new(original, writable: false);
        using PixelBuffer expected = new WicImageDecoder().Decode(referenceStream, bound, Token);
        for (int i = 0; i < 3; i++)
        {
            using FileStream file = File.OpenRead(path);
            // Exercise FileStream's read buffer before WIC borrows the same handle.
            _ = file.ReadByte();
            file.Position = 0;
            using PixelBuffer? actual = WicPngPreview.TryDecode(file, bound, 32 * 1024 * 1024, Token);
            Assert.NotNull(actual);
            Assert.Equal(expected.Size, actual.Size);
            Assert.Equal(expected.SourceSize, actual.SourceSize);
            Assert.Equal(expected.Metadata, actual.Metadata);
            Assert.Equal(expected.Pixels.ToArray(), actual.Pixels.ToArray());
            Assert.Equal(0, WicPngPreview.ObservedActiveHandles);
            Assert.Equal(0, file.Position);
            Assert.Equal(137, file.ReadByte()); // The caller still owns a usable file handle.
        }
        Assert.Equal(original, File.ReadAllBytes(path));
    }

    [Fact]
    public async Task ProfilesHighBitDepthAndInterlacingRetainWpfFallback()
    {
        string profiled = Path.Combine(_directory, "profile.png");
        byte[] plain = File.ReadAllBytes(WritePng(true, 17, 13));
        using MemoryStream compressed = new();
        using (ZLibStream zlib = new(compressed, CompressionLevel.Optimal, leaveOpen: true))
        {
            zlib.Write(ColorProfiles.AdobeRGB1998.ToByteArray());
        }
        File.WriteAllBytes(profiled, WithChunk(plain, "iCCP"u8, [.. "test\0\0"u8, .. compressed.ToArray()]));
        string exif = Path.Combine(_directory, "exif.png");
        File.WriteAllBytes(exif, WithChunk(plain, "eXIf"u8,
            [73, 73, 42, 0, 8, 0, 0, 0, 1, 0, 18, 1, 3, 0, 1, 0, 0, 0, 6, 0, 0, 0, 0, 0, 0, 0]));
        string highDepth = Path.Combine(_directory, "high-depth.png");
        BitmapSource highSource = BitmapSource.Create(17, 13, 96, 96, PixelFormats.Rgb48, null, new byte[17 * 13 * 6], 17 * 6);
        PngBitmapEncoder highEncoder = new();
        highEncoder.Frames.Add(BitmapFrame.Create(highSource));
        using (FileStream file = File.Create(highDepth)) { highEncoder.Save(file); }
        string interlaced = WritePng(true, 17, 13, interlaced: true);
        foreach (string path in new[] { profiled, highDepth, interlaced, exif })
        {
            using (FileStream file = File.OpenRead(path))
            {
                Assert.Null(WicPngPreview.TryDecode(file, new(11, 9), null, Token));
                Assert.Equal(0, file.Position);
                Assert.Equal(0, WicPngPreview.ObservedActiveHandles);
            }
            using MemoryStream reference = new(File.ReadAllBytes(path), writable: false);
            using PixelBuffer expected = new WicImageDecoder().Decode(reference, new(11, 9), Token);
            using PixelBuffer actual = await new ImageDecoder().DecodePreviewAsync(path, new(11, 9), Token);
            Assert.Equal(expected.Metadata, actual.Metadata);
            Assert.Equal(expected.Pixels.ToArray(), actual.Pixels.ToArray());
        }
    }

    [Fact]
    public void BudgetCancellationAndCorruptNativeOutputReleaseHandlesAndKeepSource()
    {
        string path = WritePng(true, 101, 67);
        byte[] original = File.ReadAllBytes(path);
        using FileStream input = File.OpenRead(path);
        Assert.Throws<ImageSizeLimitExceededException>(() => WicPngPreview.TryDecode(input, new(37, 29), 3, Token));
        Assert.Equal(0, WicPngPreview.ObservedActiveHandles);
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => WicPngPreview.TryDecode(input, new(37, 29), null, cancellation.Token));
        Assert.Equal(0, WicPngPreview.ObservedActiveHandles);
        string broken = Path.Combine(_directory, "broken.png");
        // Keep a complete, correctly checksummed chunk table, with no decodable pixel stream.
        File.WriteAllBytes(broken, WithChunk(original, "IDAT"u8, [255, 255]));
        using FileStream damaged = File.OpenRead(broken);
        using PixelBuffer? partial = WicPngPreview.TryDecode(damaged, new(37, 29), null, Token);
        using MemoryStream reference = new(File.ReadAllBytes(broken), writable: false);
        try
        {
            // WIC can accept incomplete PNG data. Preserve the old output/error boundary.
            using PixelBuffer expected = new WicImageDecoder().Decode(reference, new(37, 29), Token);
            Assert.NotNull(partial);
            Assert.Equal(expected.Pixels.ToArray(), partial.Pixels.ToArray());
        }
        catch (ImageDecodeException) { Assert.Null(partial); }
        Assert.Equal(0, WicPngPreview.ObservedActiveHandles);
        Assert.Equal(original, File.ReadAllBytes(path));
    }

    private string WritePng(bool alpha, int width, int height, bool interlaced = false)
    {
        int channels = alpha ? 4 : 3;
        byte[] pixels = new byte[width * height * channels];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int offset = (y * width + x) * channels;
                pixels[offset] = (byte)(x * 17);
                pixels[offset + 1] = (byte)(y * 11);
                pixels[offset + 2] = (byte)(x + y * 3);
                if (alpha) { pixels[offset + 3] = (byte)((x % 4) * 85); }
            }
        }
        BitmapSource source = BitmapSource.Create(width, height, 96, 96,
            alpha ? PixelFormats.Bgra32 : PixelFormats.Bgr24, null, pixels, width * channels);
        PngBitmapEncoder encoder = new() { Interlace = interlaced ? PngInterlaceOption.On : PngInterlaceOption.Off };
        encoder.Frames.Add(BitmapFrame.Create(source));
        string path = Path.Combine(_directory, Guid.NewGuid().ToString("N") + ".png");
        using FileStream file = File.Create(path);
        encoder.Save(file);
        return path;
    }

    private static byte[] WithChunk(byte[] png, ReadOnlySpan<byte> type, ReadOnlySpan<byte> payload)
    {
        using MemoryStream output = new();
        output.Write(png.AsSpan(0, 33)); // Signature and IHDR.
        Span<byte> word = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(word, (uint)payload.Length);
        output.Write(word);
        output.Write(type);
        output.Write(payload);
        uint crc = uint.MaxValue;
        foreach (byte value in type.ToArray().Concat(payload.ToArray()))
        {
            crc ^= value;
            for (int bit = 0; bit < 8; bit++) { crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xedb88320u : crc >> 1; }
        }
        BinaryPrimitives.WriteUInt32BigEndian(word, ~crc);
        output.Write(word);
        for (int offset = 33; offset < png.Length;)
        {
            int length = checked((int)BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(offset)) + 12);
            ReadOnlySpan<byte> existing = png.AsSpan(offset + 4, 4);
            if (type.SequenceEqual("IDAT"u8) ? existing.SequenceEqual("IEND"u8)
                : !type.SequenceEqual("iCCP"u8)
                || (!existing.SequenceEqual("sRGB"u8) && !existing.SequenceEqual("gAMA"u8) && !existing.SequenceEqual("cHRM"u8)))
            {
                output.Write(png.AsSpan(offset, length));
            }
            offset += length;
        }
        return output.ToArray();
    }

    public void Dispose()
    {
        WicPngPreview.ObserveLifetime = false;
        Assert.Equal(0, WicPngPreview.ObservedActiveHandles);
        Directory.Delete(_directory, recursive: true);
    }
}
