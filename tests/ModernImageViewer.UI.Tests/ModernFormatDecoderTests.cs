using System.Buffers.Binary;
using System.IO;

using ModernImageViewer.Application.Images;
using ModernImageViewer.Codecs;
using ModernImageViewer.Codecs.Modern;
using ModernImageViewer.Imaging;

namespace ModernImageViewer.UI.Tests;

public sealed class ModernFormatDecoderTests
{
    // Test-owned synthetic pixels. AVIF: Magick.NET 14.17.2, color bands + alpha + irot + matching EXIF.
    // HEIC: FFmpeg/libx265 lossless red 64x32, hvcC/sample wrapped in a minimal HEIF container.
    private const string Avif = "AAAAHGZ0eXBhdmlmAAAAAG1pZjFhdmlmbWlhZgAAAbBtZXRhAAAAAAAAACFoZGxyAAAAAAAAAABwaWN0AAAAAAAAAAAAAAAAAAAAAEZpbG9jAAAAAERAAAMAAQAAAAAB1AABAAAAAAAAADMAAgAAAAACBwABAAAAAAAAABUAAwAAAAACHAABAAAAAAAAACgAAABNaWluZgAAAAAAAwAAABVpbmZlAgAAAAABAABhdjAxAAAAABVpbmZlAgAAAAACAABhdjAxAAAAABVpbmZlAgAAAQADAABFeGlmAAAAAA5waXRtAAAAAAABAAAAumlwcnAAAACTaXBjbwAAAAxhdjFDgQAMAAAAABRpc3BlAAAAAAAAAAYAAAAEAAAAEHBpeGkAAAAAAwgICAAAAAlpcm90AwAAAAxhdjFDgQAcAAAAAA5waXhpAAAAAAEIAAAAOGF1eEMAAAAAdXJuOm1wZWc6bXBlZ0I6Y2ljcDpzeXN0ZW1zOmF1eGlsaWFyeTphbHBoYQAAAAAfaXBtYQAAAAAAAAACAAEEgQIDhAACBYUCBoeEAAAAKGlyZWYAAAAAAAAADmF1eGwAAgABAAEAAAAOY2RzYwADAAEAAQAAAHhtZGF0EgAKCBgIbsEBDQaEMiUQAACJ1MH3z451YctOjKkff3/nYpLrLIlEnFB33sgQOJiCz9rQEgAKBRgIbsKgMgoQAIalgeMug2uRAAAABkV4aWYAAElJKgAIAAAAAQASAQMAAQAAAAYAAAAAAAAAAAAAAA==";
    private const string Heic = "AAAAGGZ0eXBoZWljAAAAAG1pZjFoZWljAAAKJ21ldGEAAAAAAAAAIWhkbHIAAAAAAAAAAHBpY3QAAAAAAAAAAAAAAAAAAAAADnBpdG0AAAAAAAEAAAAeaWxvYwAAAABEAAABAAEAAAABAAAKRwAAAGMAAAAjaWluZgAAAAAAAQAAABVpbmZlAgAAAAABAABodmMxAAAACatpcHJwAAAJjWlwY28AAAlhaHZjQwEBYAAAAJAAAAAAAP/wAPz9+PgAAA8EoAABABhAAQwB//8BYAAAAwCQAAADAAADAP+VmAmhAAEAKUIBAQFgAAADAJAAAAMAAAMA/6AgghZZWa5MrwFoCAAAAwAIAAADAMhAogABAAZEAcFxqRInAAEI504BBf//////////4iyi3gm1F0fbu1Wk/n/C/E54MjY1IChidWlsZCAyMTYpIC0gNC4yKzMtM2Y0MTIwZDpbV2luZG93c11bR0NDIDE1LjIuMF1bNjQgYml0XSA4Yml0KzEwYml0KzEyYml0IC0gSC4yNjUvSEVWQyBjb2RlYyAtIENvcHlyaWdodCAyMDEzLTIwMTggKGMpIE11bHRpY29yZXdhcmUsIEluYyAtIGh0dHA6Ly94MjY1Lm9yZyAtIG9wdGlvbnM6IGNwdWlkPTExMTEwMzkgZnJhbWUtdGhyZWFkcz0xIG51bWEtcG9vbHM9MSBuby13cHAgbm8tcG1vZGUgbm8tcG1lIG5vLXBzbnIgbm8tc3NpbSBsb2ctbGV2ZWw9MCBiaXRkZXB0aD04IGlucHV0LWNzcD0xIGZwcz0yNS8xIGlucHV0LXJlcz02NHgzMiBpbnRlcmxhY2U9MCB0b3RhbC1mcmFtZXM9MCBsZXZlbC1pZGM9MCBoaWdoLXRpZXI9MSB1aGQtYmQ9MCByZWY9MyBuby1hbGxvdy1ub24tY29uZm9ybWFuY2Ugbm8tcmVwZWF0LWhlYWRlcnMgYW5uZXhiIG5vLWF1ZCBuby1lb2Igbm8tZW9zIG5vLWhyZCBpbmZvIGhhc2g9MCB0ZW1wb3JhbC1sYXllcnM9MCBvcGVuLWdvcCBtaW4ta2V5aW50PTI1IGtleWludD0yNTAgZ29wLWxvb2thaGVhZD0wIGJmcmFtZXM9NCBiLWFkYXB0PTIgYi1weXJhbWlkIGJmcmFtZS1iaWFzPTAgcmMtbG9va2FoZWFkPTIwIGxvb2thaGVhZC1zbGljZXM9MCBzY2VuZWN1dD00MCBuby1oaXN0LXNjZW5lY3V0IHJhZGw9MCBuby1zcGxpY2Ugbm8taW50cmEtcmVmcmVzaCBjdHU9MzIgbWluLWN1LXNpemU9OCBuby1yZWN0IG5vLWFtcCBtYXgtdHUtc2l6ZT0zMiB0dS1pbnRlci1kZXB0aD0xIHR1LWludHJhLWRlcHRoPTEgbGltaXQtdHU9MCByZG9xLWxldmVsPTAgZHluYW1pYy1yZD0wLjAwIG5vLXNzaW0tcmQgc2lnbmhpZGUgbm8tdHNraXAgbnItaW50cmE9MCBuci1pbnRlcj0wIG5vLWNvbnN0cmFpbmVkLWludHJhIHN0cm9uZy1pbnRyYS1zbW9vdGhpbmcgbWF4LW1lcmdlPTMgbGltaXQtcmVmcz0xIG5vLWxpbWl0LW1vZGVzIG1lPTEgc3VibWU9MiBtZXJhbmdlPTU3IHRlbXBvcmFsLW12cCBuby1mcmFtZS1kdXAgbm8taG1lIHdlaWdodHAgbm8td2VpZ2h0YiBuby1hbmFseXplLXNyYy1waWNzIGRlYmxvY2s9MDowIHNhbyBuby1zYW8tbm9uLWRlYmxvY2sgcmQ9MyBzZWxlY3RpdmUtc2FvPTQgZWFybHktc2tpcCByc2tpcCBuby1mYXN0LWludHJhIG5vLXRza2lwLWZhc3Qgbm8tY3UtbG9zc2xlc3MgYi1pbnRyYSBuby1zcGxpdHJkLXNraXAgcmRwZW5hbHR5PTAgcHN5LXJkPTIuMDAgcHN5LXJkb3E9MC4wMCBuby1yZC1yZWZpbmUgbG9zc2xlc3MgY2JxcG9mZnM9MCBjcnFwb2Zmcz0wIHJjPWNxcCBxcD00IGlwcmF0aW89MS40MCBwYnJhdGlvPTEuMzAgYXEtbW9kZT0wIGFxLXN0cmVuZ3RoPTAuMDAgbm8tY3V0cmVlIHpvbmUtY291bnQ9MCBuby1zdHJpY3QtY2JyIHFnLXNpemU9MzIgbm8tcmMtZ3JhaW4gcXBtYXg9NjkgcXBtaW49MCBuby1jb25zdC12YnYgc2FyPTEgb3ZlcnNjYW49MCB2aWRlb2Zvcm1hdD01IHJhbmdlPTAgY29sb3JwcmltPTIgdHJhbnNmZXI9MiBjb2xvcm1hdHJpeD0yIGNocm9tYWxvYz0wIGRpc3BsYXktd2luZG93PTAgY2xsPTAsMCBtaW4tbHVtYT0wIG1heC1sdW1hPTI1NSBsb2cyLW1heC1wb2MtbHNiPTggdnVpLXRpbWluZy1pbmZvIHZ1aS1ocmQtaW5mbyBzbGljZXM9MSBuby1vcHQtcXAtcHBzIG5vLW9wdC1yZWYtbGlzdC1sZW5ndGgtcHBzIG5vLW11bHRpLXBhc3Mtb3B0LXJwcyBzY2VuZWN1dC1iaWFzPTAuMDUgbm8tb3B0LWN1LWRlbHRhLXFwIG5vLWFxLW1vdGlvbiBuby1oZHIxMCBuby1oZHIxMC1vcHQgbm8tZGhkcjEwLW9wdCBuby1pZHItcmVjb3Zlcnktc2VpIGFuYWx5c2lzLXJldXNlLWxldmVsPTAgYW5hbHlzaXMtc2F2ZS1yZXVzZS1sZXZlbD0wIGFuYWx5c2lzLWxvYWQtcmV1c2UtbGV2ZWw9MCBzY2FsZS1mYWN0b3I9MCByZWZpbmUtaW50cmE9MCByZWZpbmUtaW50ZXI9MCByZWZpbmUtbXY9MSByZWZpbmUtY3R1LWRpc3RvcnRpb249MCBuby1saW1pdC1zYW8gY3R1LWluZm89MCBuby1sb3dwYXNzLWRjdCByZWZpbmUtYW5hbHlzaXMtdHlwZT0wIGNvcHktcGljPTEgbWF4LWF1c2l6ZS1mYWN0b3I9MS4wIG5vLWR5bmFtaWMtcmVmaW5lIG5vLXNpbmdsZS1zZWkgbm8taGV2Yy1hcSBuby1zdnQgbm8tZmllbGQgcXAtYWRhcHRhdGlvbi1yYW5nZT0xLjAwIHNjZW5lY3V0LWF3YXJlLXFwPTBjb25mb3JtYW5jZS13aW5kb3ctb2Zmc2V0cyByaWdodD0wIGJvdHRvbT0wIGRlY29kZXItbWF4LXJhdGU9MCBuby12YnYtbGl2ZS1tdWx0aS1wYXNzIG5vLW1jc3RmIG5vLXNicmMgbm8tZnJhbWUtcmOAAAAAFGlzcGUAAAAAAAAAQAAAACAAAAAQcGl4aQAAAAADCAgIAAAAFmlwbWEAAAAAAAAAAQABA4ECAwAAAGttZGF0AAAAXygBrwW4GC4ATD//5X8n8X27du3bu3bt27du3f87X//yJ2v//0fqJ07fNmzZs8aNGjRo0YbP/2PdAD/q/9V/Tfnvjvjvjvjvj/j/j/j/j/j/j/j3O2bUVVS2Hqvgweuw";
    // Test-owned ICC v2 matrix/TRC linear RGB profile, gray 128: no system/vendor ICC redistribution.
    private const string LinearAvif = "AAAAHGZ0eXBhdmlmAAAAAG1pZjFhdmlmbWlhZgAAAhttZXRhAAAAAAAAACFoZGxyAAAAAAAAAABwaWN0AAAAAAAAAAAAAAAAAAAAACJpbG9jAAAAAERAAAEAAQAAAAACPwABAAAAAAAAABMAAAAjaWluZgAAAAAAAQAAABVpbmZlAgAAAAABAABhdjAxAAAAAA5waXRtAAAAAAABAAABm2lwcnAAAAF8aXBjbwAAAAxhdjFDgQAMAAAAAURjb2xycHJvZgAAATgAAAAAAgAAAG1udHJSR0IgWFlaIAfqAAoABQAAAAAAAGFjc3AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAD21gABAAAAANMtTUlWIAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAB3JYWVoAAADYAAAAFGdYWVoAAADsAAAAFGJYWVoAAAEAAAAAFHd0cHQAAAEUAAAAFHJUUkMAAAEoAAAADmdUUkMAAAEoAAAADmJUUkMAAAEoAAAADlhZWiAAAAAAAABvowAAOPYAAAORWFlaIAAAAAAAAGKUAAC3hQAAGNxYWVogAAAAAAAAJKEAAA+FAAC21FhZWiAAAAAAAAD21gABAAAAANMtY3VydgAAAAAAAAABAQAAAAAAABRpc3BlAAAAAAAAAAQAAAACAAAAEHBpeGkAAAAAAwgICAAAABdpcG1hAAAAAAAAAAEAAQSBAgMEAAAAG21kYXQSAAoIGAQ7BAQ0GhAyBRAAAASA";

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task MainPreviewThumbnailAndDetailUseBundledModernCodecWithoutChangingOriginal(bool avif)
    {
        string path = WriteFixture(avif ? Avif : Heic, avif ? ".avif" : ".heic");
        try
        {
            byte[] original = File.ReadAllBytes(path);
            ImageDecoder decoder = new();
            using PixelBuffer full = await decoder.DecodeAsync(path, TestContext.Current.CancellationToken);
            Assert.Equal(avif ? new PixelSize(4, 6) : new PixelSize(64, 32), full.SourceSize);
            Assert.Equal(full.SourceSize, full.Size);
            // Native container orientation is applied once; matching EXIF must not rotate it again.
            Assert.Equal((ushort)1, full.Metadata.Orientation);
            if (avif)
            {
                Assert.Equal((byte)128, full.Pixels.Span[3]);
                Assert.InRange(full.Pixels.Span[2], (byte)126, (byte)128);
                int bottom = (full.Size.Height - 1) * full.Stride;
                Assert.InRange(full.Pixels.Span[bottom], (byte)253, (byte)255);
            }
            else { Assert.Equal(new byte[] { 0, 0, 255, 255 }, full.Pixels.Span[..4].ToArray()); }
            using PixelBuffer preview = await decoder.DecodePreviewAsync(path, new PixelSize(3, 3), TestContext.Current.CancellationToken);
            using PixelBuffer thumbnail = await decoder.DecodeThumbnailAsync(path, new PixelSize(2, 2), TestContext.Current.CancellationToken);
            Assert.InRange(preview.Size.Width, 1, 3);
            Assert.InRange(preview.Size.Height, 1, 3);
            Assert.InRange(thumbnail.Size.Width, 1, 2);
            Assert.InRange(thumbnail.Size.Height, 1, 2);
            Assert.Equal(full.SourceSize, thumbnail.SourceSize);
            using PixelBuffer detail = await decoder.DecodeDetailAsync(path, full.Pixels.Length, TestContext.Current.CancellationToken);
            Assert.Equal(full.Pixels.ToArray(), detail.Pixels.ToArray());
            await Assert.ThrowsAsync<ImageSizeLimitExceededException>(() => decoder.DecodeDetailAsync(path, 4, TestContext.Current.CancellationToken));
            Assert.Equal(original, File.ReadAllBytes(path));
            ExportFixture(original, avif ? ".avif" : ".heic");
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task PsdUsesSavedCompositeForBrowsingAndThumbnails(bool layered, bool rle)
    {
        byte[] composite = CreatePsd(layered, rle);
        string path = WriteFixture(Convert.ToBase64String(composite), ".psd");
        try
        {
            CancellationToken token = TestContext.Current.CancellationToken;
            ImageDecoder decoder = new();
            using PixelBuffer full = await decoder.DecodeAsync(path, token);
            Assert.Equal(new PixelSize(16, 8), full.SourceSize);
            Assert.Equal(layered ? new byte[] { 128, 128, 128, 128 } : [0x11, 0x33, 0xCC, 0xFF],
                full.Pixels.Span[..4].ToArray());
            using PixelBuffer thumbnail = await decoder.DecodeThumbnailAsync(path, new PixelSize(6, 6), token);
            Assert.Equal(new PixelSize(6, 3), thumbnail.Size);
            Assert.Equal(full.SourceSize, thumbnail.SourceSize);
            Assert.Equal(composite, File.ReadAllBytes(path));
            if (rle) { ExportFixture(composite, ".psd"); }
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task PsdReportsMissingAndCorruptComposites()
    {
        byte[] bytes = CreatePsd(layered: false);
        byte[] resources = Convert.FromHexString("000000123842494D0421000000000005000000010000");
        byte[] noComposite = [.. bytes[..30], .. resources, .. bytes[34..]];
        CancellationToken token = TestContext.Current.CancellationToken;
        await AssertPsdError(noComposite, ImageOpenError.UnsupportedFormat, token);
        await AssertPsdError(bytes[..45], ImageOpenError.CorruptFile, token);
        bytes[5] = 2;
        await AssertPsdError(bytes, ImageOpenError.UnsupportedFormat, token);
    }

    private static async Task AssertPsdError(byte[] bytes, ImageOpenError expected, CancellationToken token)
    {
        string path = WriteFixture(Convert.ToBase64String(bytes), ".psd");
        try
        {
            ImageDecodeException error = await Assert.ThrowsAsync<ImageDecodeException>(() => new ImageDecoder().DecodeAsync(path, token));
            Assert.Equal(expected, error.Error);
        }
        finally { File.Delete(path); }
    }

    private static byte[] CreatePsd(bool layered, bool rle = false)
    {
        byte[] header = Convert.FromHexString("38425053000100000000000000030000000800000010000800030000000000000000");
        // Deliberately unusable layer records. The saved composite remains valid and carries alpha.
        byte[] layers = layered ? Convert.FromHexString("0000000E00000006FFDFDEADBEEF00000000") : new byte[4];
        int channelCount = layered ? 4 : 3;
        int rowCount = channelCount * 8;
        byte[] psd = new byte[header.Length + layers.Length + 2 + (rle ? rowCount * 4 : channelCount * 128)];
        header.CopyTo(psd, 0);
        psd[13] = (byte)channelCount;
        layers.CopyTo(psd, header.Length);
        Span<byte> pixels = psd.AsSpan(header.Length + layers.Length + 2);
        if (rle)
        {
            psd[header.Length + layers.Length + 1] = 1;
            byte[] channels = [0xCC, 0x33, 0x11];
            for (int row = 0; row < rowCount; row++)
            {
                pixels[2 * row + 1] = 2;
                pixels[2 * (rowCount + row)] = 0xF1; // PackBits run of 16 pixels.
                pixels[2 * (rowCount + row) + 1] = channels[row / 8];
            }
        }
        else if (layered)
        {
            pixels[..384].Fill(255);
            pixels[384..].Fill(128);
        }
        else
        {
            pixels[..128].Fill(0xCC);
            pixels[128..256].Fill(0x33);
            pixels[256..].Fill(0x11);
        }
        return psd;
    }

    [Fact]
    public async Task EmbeddedRgbIccConvertsGrayToSrgb()
    {
        string path = WriteFixture(LinearAvif, ".avif");
        try
        {
            using PixelBuffer image = await new ImageDecoder().DecodeAsync(path, TestContext.Current.CancellationToken);
            Assert.Equal(new PixelSize(4, 2), image.Size);
            Assert.InRange(image.Pixels.Span[0], (byte)186, (byte)190);
            Assert.InRange(image.Pixels.Span[1], (byte)186, (byte)190);
            Assert.InRange(image.Pixels.Span[2], (byte)186, (byte)190);
            Assert.Equal((byte)255, image.Pixels.Span[3]);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void DetectionIsBoundedAndRestoresStreamPosition()
    {
        using MemoryStream image = new(Convert.FromBase64String(Avif));
        Assert.True(HeifContainer.TryDetect(image, out bool avif));
        Assert.True(avif);
        Assert.Equal(0, image.Position);
        byte[] malformed = Convert.FromBase64String(Avif);
        BinaryPrimitives.WriteUInt32BigEndian(malformed, 4097);
        using MemoryStream bad = new(malformed);
        Assert.False(HeifContainer.TryDetect(bad, out _));
        Assert.Equal(0, bad.Position);
        using MemoryStream truncated = new(malformed[..8]);
        Assert.False(HeifContainer.TryDetect(truncated, out _));
        Assert.Equal(0, truncated.Position);
    }

    [Fact]
    public void CancellationSequencesCorruptInputsAndSourceBudgetFailExplicitly()
    {
        using CancellationTokenSource cancelled = new();
        cancelled.Cancel();
        using MemoryStream valid = new(Convert.FromBase64String(Avif));
        Assert.Throws<OperationCanceledException>(() => MagickPreviewDecoder.Decode(valid, true, null, null, cancelled.Token));
        byte[] sequence = Convert.FromBase64String(Avif);
        "avis"u8.CopyTo(sequence.AsSpan(8));
        using MemoryStream animated = new(sequence);
        ImageDecodeException sequenceError = Assert.Throws<ImageDecodeException>(() => MagickPreviewDecoder.Decode(animated, true, null, null, TestContext.Current.CancellationToken));
        Assert.Equal(ImageOpenError.UnsupportedFormat, sequenceError.Error);
        using MemoryStream broken = new(Convert.FromBase64String(Avif)[..28]);
        ImageDecodeException brokenError = Assert.Throws<ImageDecodeException>(() => MagickPreviewDecoder.Decode(broken, true, null, null, TestContext.Current.CancellationToken));
        Assert.Equal(ImageOpenError.CorruptFile, brokenError.Error);
        Assert.Throws<ImageSizeLimitExceededException>(() => MagickPreviewDecoder.ValidateSourceSize(new PixelSize(8000, 6000)));
        MagickPreviewDecoder.ValidateSourceSize(new PixelSize(8000, 4000));
        byte[] oversized = Convert.FromBase64String(Heic);
        int sizeProperty = oversized.AsSpan().IndexOf("ispe"u8);
        Assert.True(sizeProperty > 0);
        BinaryPrimitives.WriteUInt32BigEndian(oversized.AsSpan(sizeProperty + 8), 8000);
        BinaryPrimitives.WriteUInt32BigEndian(oversized.AsSpan(sizeProperty + 12), 6000);
        using MemoryStream oversizedInput = new(oversized);
        Assert.Throws<ImageSizeLimitExceededException>(() => MagickPreviewDecoder.Decode(oversizedInput, false, new PixelSize(2, 2), null, TestContext.Current.CancellationToken));
    }

    private static string WriteFixture(string base64, string extension)
    {
        byte[] bytes = Convert.FromBase64String(base64);
        string path = Path.Combine(Path.GetTempPath(), $"viewer-modern-{Guid.NewGuid():N}{extension}");
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private static void ExportFixture(byte[] bytes, string extension)
    {
        string? exportDirectory = Environment.GetEnvironmentVariable("MIV_FORMAT_FIXTURE_DIRECTORY");
        if (!string.IsNullOrWhiteSpace(exportDirectory))
        {
            Directory.CreateDirectory(exportDirectory);
            File.WriteAllBytes(Path.Combine(exportDirectory, $"fixture{extension}"), bytes);
        }

    }
}




