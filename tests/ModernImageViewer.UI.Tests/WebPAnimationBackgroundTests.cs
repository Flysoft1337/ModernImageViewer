using System.Buffers.Binary;
using System.IO;
using System.Text;

using ModernImageViewer.Application.Images;
using ModernImageViewer.Codecs;
using ModernImageViewer.Codecs.Frames;
using ModernImageViewer.Codecs.Wic;
using ModernImageViewer.Imaging;

using SkiaSharp;

namespace ModernImageViewer.UI.Tests;

[Collection("Region decoder slot")]
public sealed class WebPAnimationBackgroundTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    private static readonly SKColor Transparent = new(17, 111, 193, 0);
    private static readonly SKColor Red = new(255, 0, 0, 128);
    private static readonly SKColor Green = new(0, 255, 0, 128);
    private static readonly SKColor Blue = new(0, 0, 255, 128);

    [Theory]
    [InlineData(0, false)]
    [InlineData(128, false)]
    [InlineData(255, false)]
    [InlineData(0, true)]
    [InlineData(128, true)]
    [InlineData(255, true)]
    public async Task BackgroundSourceOverDisposalRandomSeekAndRestartMatchFixedPremultipliedPixels(int alpha, bool firstOver)
    {
        using Fixture fixture = new(Create((byte)alpha, firstOver));
        using IImageFrameSession session = (await SkiaImageFrameSession.TryOpenAsync(fixture.Path, Token))!;
        Assert.Equal(5, session.Info.Count);
        Assert.Null(session.Info.TotalPlays);
        Assert.Equal(new PixelRect(2, 0, 2, 2), session.Info.Frames[0].Bounds);
        Assert.Equal(ImageFrameDisposal.Background, session.Info.Frames[1].Disposal);
        byte[][] expected = Expected(alpha, firstOver);
        List<PixelBuffer> published = [];
        try
        {
            int[] requests = [0, 1, 2, 3, 4, 2, 0, 4, 1];
            foreach (int index in requests)
            {
                PixelBuffer image = await session.DecodeFrameAsync(index, new PixelSize(6, 2), 48, Token);
                published.Add(image);
                Assert.Equal(expected[index], image.Pixels.ToArray());
                Assert.InRange(session.RetainedPixelBytes, 0, 48);
            }
            for (int i = 0; i < requests.Length; i++) { Assert.Equal(expected[requests[i]], published[i].Pixels.ToArray()); }
        }
        finally
        {
            foreach (PixelBuffer image in published) { image.Dispose(); }
            await Close(session);
        }
        Assert.Equal(0, session.RetainedPixelBytes);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task SingleAndMultiFrameRepresentativePreviewThumbnailAndPrefetchAgree(bool oneFrame, bool firstOver)
    {
        using Fixture fixture = new(Create(128, firstOver, oneFrame));
        byte[] expected = Expected(128, firstOver)[0];
        int sessions = SkiaImageFrameSession.ActiveSessionCount;
        int decoders = SkiaImageFrameSession.ActiveDecoderCount;
        ImageDecoder decoder = new();
        if (oneFrame) { Assert.Null(await decoder.TryOpenFrameSessionAsync(fixture.Path, Token)); }
        using PixelBuffer full = await decoder.DecodeAsync(fixture.Path, Token);
        using PixelBuffer preview = await decoder.DecodePreviewAsync(fixture.Path, new PixelSize(6, 2), Token);
        using PixelBuffer thumbnail = await decoder.DecodeThumbnailAsync(fixture.Path, new PixelSize(6, 2), Token);
        using PixelBuffer prefetch = await Prefetch(decoder, fixture.Path, new PixelSize(6, 2));
        foreach (PixelBuffer image in new[] { full, preview, thumbnail, prefetch })
        {
            Assert.Equal(new PixelSize(6, 2), image.Size);
            Assert.Equal(new PixelSize(6, 2), image.SourceSize);
            Assert.Equal(expected, image.Pixels.ToArray());
            Assert.Equal(full.SourceFileStamp, image.SourceFileStamp);
        }
        Assert.Equal(sessions, SkiaImageFrameSession.ActiveSessionCount);
        Assert.Equal(decoders, SkiaImageFrameSession.ActiveDecoderCount);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    public async Task ScaledLocalFramesPreserveBackgroundSourceTransparencyAndAllEightOrientations(int origin)
    {
        AnimationCodecFixtures.WebPFrame[] frames =
        [
            new(2, 0, 2, 2, SKColors.Red),
            new(0, 2, 2, 2, Transparent),
        ];
        using Fixture fixture = new(AnimationCodecFixtures.WebP(orientation: (ushort)origin,
            background: new SKColor(120, 80, 40, 128), width: 6, height: 4, frames: frames));
        using IImageFrameSession session = (await SkiaImageFrameSession.TryOpenAsync(fixture.Path, Token))!;
        PixelSize size = origin >= 5 ? new(2, 3) : new(3, 2);
        int[][] orders =
        [
            [0, 1, 2, 3, 4, 5], [2, 1, 0, 5, 4, 3],
            [5, 4, 3, 2, 1, 0], [3, 4, 5, 0, 1, 2],
            [0, 3, 1, 4, 2, 5], [3, 0, 4, 1, 5, 2],
            [5, 2, 4, 1, 3, 0], [2, 5, 1, 4, 0, 3],
        ];
        byte[] bg = [20, 40, 60, 128];
        byte[][] first = [bg, [0, 0, 255, 255], bg, bg, bg, bg];
        byte[][] second = [bg, [0, 0, 255, 255], bg, [0, 0, 0, 0], bg, bg];
        using PixelBuffer published = await session.DecodeFrameAsync(0, size, 24, Token);
        foreach (int index in new[] { 1, 0, 1 })
        {
            using PixelBuffer image = await session.DecodeFrameAsync(index, size, 24, Token);
            byte[][] raw = index == 0 ? first : second;
            Assert.Equal(size, image.Size);
            Assert.Equal(origin >= 5 ? new PixelSize(4, 6) : new PixelSize(6, 4), image.SourceSize);
            Assert.Equal(orders[origin - 1].SelectMany(i => raw[i]).ToArray(), image.Pixels.ToArray());
        }
        byte[] expectedFirst = orders[origin - 1].SelectMany(i => first[i]).ToArray();
        Assert.Equal(expectedFirst, published.Pixels.ToArray());
        ImageDecoder decoder = new();
        using PixelBuffer thumbnail = await decoder.DecodeThumbnailAsync(fixture.Path, size, Token);
        using PixelBuffer prefetch = await Prefetch(decoder, fixture.Path, size);
        Assert.Equal(expectedFirst, thumbnail.Pixels.ToArray());
        Assert.Equal(expectedFirst, prefetch.Pixels.ToArray());
        await Close(session);
    }

    [Fact]
    public async Task CancelledDependencyDecodeCanRestartAndDisposeWaitsForQueuedWorkThenReleasesTheFile()
    {
        int sessions = SkiaImageFrameSession.ActiveSessionCount;
        int decoders = SkiaImageFrameSession.ActiveDecoderCount;
        using Fixture fixture = new(Create(128, false));
        IImageFrameSession session = (await SkiaImageFrameSession.TryOpenAsync(fixture.Path, Token))!;
        try
        {
            using PixelBuffer first = await session.DecodeFrameAsync(0, new PixelSize(6, 2), 48, Token);
            using CancellationTokenSource cancellation = CancellationTokenSource.CreateLinkedTokenSource(Token);
            using (IDisposable busy = await WicImageDecoder.DecodeSlot.AcquireAsync(DecodePriority.Foreground, Token))
            {
                Task<PixelBuffer> queued = session.DecodeFrameAsync(4, new PixelSize(6, 2), 48, cancellation.Token);
                Assert.False(queued.IsCompleted);
                cancellation.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued.WaitAsync(Timeout, Token));
            }
            Assert.Equal(0, session.RetainedPixelBytes);
            using PixelBuffer restarted = await session.DecodeFrameAsync(4, new PixelSize(6, 2), 48, Token);
            Assert.Equal(Expected(128, false)[4], restarted.Pixels.ToArray());
            using (IDisposable busy = await WicImageDecoder.DecodeSlot.AcquireAsync(DecodePriority.Foreground, Token))
            {
                Task<PixelBuffer> queued = session.DecodeFrameAsync(2, new PixelSize(6, 2), 48, Token);
                Assert.False(queued.IsCompleted);
                session.Dispose();
                Assert.False(session.ReleaseCompletion.IsCompleted);
                busy.Dispose();
                await Assert.ThrowsAsync<ObjectDisposedException>(() => queued.WaitAsync(Timeout, Token));
            }
            await session.ReleaseCompletion.WaitAsync(Timeout, Token);
            Assert.Equal(Expected(128, false)[0], first.Pixels.ToArray());
            Assert.Equal(0, session.RetainedPixelBytes);
            Assert.Equal(sessions, SkiaImageFrameSession.ActiveSessionCount);
            Assert.Equal(decoders, SkiaImageFrameSession.ActiveDecoderCount);
            using FileStream unlocked = new(fixture.Path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        }
        finally { await Close(session); }
    }

    [Fact]
    public async Task AnimatedInputMetadataCanvasAndOutputLimitsAlsoApplyToRepresentatives()
    {
        ImageDecoder decoder = new();
        using Fixture input = new(Create(128, false));
        using (FileStream file = new(input.Path, FileMode.Open, FileAccess.Write))
        {
            file.SetLength(ImageFrameLimits.MaximumInputBytes + 1);
        }
        await Assert.ThrowsAsync<ImageSizeLimitExceededException>(() => decoder.DecodeThumbnailAsync(input.Path, new PixelSize(6, 2), Token));
        using Fixture profile = new(AnimationCodecFixtures.WebP(profile: new byte[checked((int)ImageFrameLimits.MaximumMetadataBytes)]));
        await Assert.ThrowsAsync<ImageSizeLimitExceededException>(() => decoder.DecodePreviewAsync(profile.Path, new PixelSize(4, 2), Token));
        using Fixture large = new(AnimationCodecFixtures.WebP(width: 6000, height: 6000));
        await Assert.ThrowsAsync<ImageSizeLimitExceededException>(() => decoder.TryOpenFrameSessionAsync(large.Path, Token));
        await Assert.ThrowsAsync<ImageSizeLimitExceededException>(() => decoder.DecodeAsync(large.Path, Token));
        using PixelBuffer thumbnail = await decoder.DecodeThumbnailAsync(large.Path, new PixelSize(64, 64), Token);
        Assert.InRange(thumbnail.Pixels.Length, 4, 64 * 64 * 4);
        Assert.Equal(new PixelSize(6000, 6000), thumbnail.SourceSize);
        Assert.Equal(new byte[] { 255, 255, 255, 255 }, thumbnail.Pixels.Slice(63 * thumbnail.Stride + 63 * 4, 4).ToArray());
        using Fixture normal = new(Create(128, false));
        using IImageFrameSession session = (await decoder.TryOpenFrameSessionAsync(normal.Path, Token))!;
        await Assert.ThrowsAsync<ImageSizeLimitExceededException>(() => session.DecodeFrameAsync(0, new PixelSize(6, 2), 3, Token));
        using PixelBuffer tiny = await session.DecodeFrameAsync(4, new PixelSize(6, 2), 8, Token);
        Assert.InRange(tiny.Pixels.Length, 4, 8);
        await Close(session);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TruncatedOrMismatchedAnmfNeverPublishesARepresentative(bool mismatch)
    {
        byte[] data = Create(128, false, oneFrame: true);
        int frame = FindChunk(data, "ANMF"u8);
        if (mismatch) { data[frame + 8 + 6] = 2; } // Declare width 3 around an actual width-2 payload.
        else { data = data[..^2]; }
        using Fixture fixture = new(data);
        ImageDecoder decoder = new();
        ImageDecodeException error = await Assert.ThrowsAsync<ImageDecodeException>(() => decoder.DecodeAsync(fixture.Path, Token));
        Assert.Equal(ImageOpenError.CorruptFile, error.Error);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IccBackgroundAndHalfTransparentSourceHaveIdenticalSingleAndAnimatedRepresentatives(bool oneFrame)
    {
        AnimationCodecFixtures.WebPFrame first = new(2, 0, 2, 2, new SKColor(128, 128, 128, 128));
        AnimationCodecFixtures.WebPFrame second = new(0, 0, 2, 2, Transparent);
        using Fixture fixture = new(AnimationCodecFixtures.WebP(background: new SKColor(128, 128, 128, 128),
            width: 6, frames: oneFrame ? [first] : [first, second], profile: LinearProfile()));
        byte[] expected = Enumerable.Repeat(new byte[] { 94, 94, 94, 128 }, 12).SelectMany(pixel => pixel).ToArray();
        ImageDecoder decoder = new();
        using PixelBuffer representative = await decoder.DecodeAsync(fixture.Path, Token);
        using PixelBuffer thumbnail = await decoder.DecodeThumbnailAsync(fixture.Path, new PixelSize(6, 2), Token);
        using PixelBuffer prefetch = await Prefetch(decoder, fixture.Path, new PixelSize(6, 2));
        Assert.Equal(expected, representative.Pixels.ToArray());
        Assert.Equal(expected, thumbnail.Pixels.ToArray());
        Assert.Equal(expected, prefetch.Pixels.ToArray());
        IImageFrameSession? session = await decoder.TryOpenFrameSessionAsync(fixture.Path, Token);
        if (oneFrame) { Assert.Null(session); }
        else
        {
            Assert.NotNull(session);
            try
            {
                using PixelBuffer animated = await session.DecodeFrameAsync(0, new PixelSize(6, 2), 48, Token);
                using PixelBuffer next = await session.DecodeFrameAsync(1, new PixelSize(6, 2), 48, Token);
                Assert.Equal(expected, animated.Pixels.ToArray());
                Assert.Equal(new byte[] { 0, 0, 0, 0 }, next.Pixels.Slice(0, 4).ToArray());
                Assert.Equal(new byte[] { 94, 94, 94, 128 }, next.Pixels.Slice(2 * 4, 4).ToArray());
            }
            finally { await Close(session); }
        }
    }

    [Fact]
    public async Task LossyAlphaPayloadUsesBackgroundDisposalAndSourceTransparency()
    {
        AnimationCodecFixtures.WebPFrame[] frames =
        [
            new(0, 0, 4, 2, SKColors.Red),
            new(0, 0, 2, 2, Green, 1, Lossy: true),
            new(2, 0, 2, 2, Transparent, Lossy: true),
        ];
        using Fixture fixture = new(AnimationCodecFixtures.WebP(background: new SKColor(120, 80, 40, 128), frames: frames));
        using IImageFrameSession session = (await SkiaImageFrameSession.TryOpenAsync(fixture.Path, Token))!;
        using PixelBuffer over = await session.DecodeFrameAsync(1, new PixelSize(4, 2), 32, Token);
        Assert.InRange(over.Pixels.Span[1], (byte)126, (byte)130);
        Assert.InRange(over.Pixels.Span[2], (byte)126, (byte)130);
        Assert.Equal((byte)255, over.Pixels.Span[3]);
        using PixelBuffer last = await session.DecodeFrameAsync(2, new PixelSize(4, 2), 32, Token);
        Assert.Equal(new byte[] { 20, 40, 60, 128, 20, 40, 60, 128, 0, 0, 0, 0, 0, 0, 0, 0,
            20, 40, 60, 128, 20, 40, 60, 128, 0, 0, 0, 0, 0, 0, 0, 0 }, last.Pixels.ToArray());
        await Close(session);
    }

    [Fact]
    public async Task NonopaqueBackgroundDoesNotRequireTheVp8xAlphaFlagForOpaqueFrames()
    {
        AnimationCodecFixtures.WebPFrame[] frames =
        [
            new(2, 0, 2, 2, SKColors.Red), new(0, 0, 2, 2, SKColors.Blue),
        ];
        byte[] bytes = AnimationCodecFixtures.WebP(background: new SKColor(120, 80, 40, 128), width: 6, frames: frames);
        bytes[20] &= 0xef;
        using Fixture fixture = new(bytes);
        using IImageFrameSession session = (await SkiaImageFrameSession.TryOpenAsync(fixture.Path, Token))!;
        using PixelBuffer frame = await session.DecodeFrameAsync(0, new PixelSize(6, 2), 48, Token);
        using PixelBuffer representative = await new ImageDecoder().DecodeAsync(fixture.Path, Token);
        byte[] expected = new byte[] { 20, 40, 60, 128, 20, 40, 60, 128, 0, 0, 255, 255,
            0, 0, 255, 255, 20, 40, 60, 128, 20, 40, 60, 128 };
        Assert.Equal(expected.Concat(expected).ToArray(), frame.Pixels.ToArray());
        Assert.Equal(frame.Pixels.ToArray(), representative.Pixels.ToArray());
        await Close(session);
    }

    [Fact]
    public async Task TooManyAnmfChunksCannotBypassLimitsViaTheRepresentativePath()
    {
        byte[] template = Create(128, false, oneFrame: true);
        int offset = FindChunk(template, "ANMF"u8);
        int length = BinaryPrimitives.ReadInt32LittleEndian(template.AsSpan(offset + 4, 4));
        byte[] repeated = new byte[offset + (8 + length + (length & 1)) * (ImageFrameLimits.MaximumFrames + 1)];
        template.AsSpan(0, offset).CopyTo(repeated);
        for (int frame = 0; frame <= ImageFrameLimits.MaximumFrames; frame++)
        {
            template.AsSpan(offset).CopyTo(repeated.AsSpan(offset + frame * (template.Length - offset)));
        }
        BinaryPrimitives.WriteInt32LittleEndian(repeated.AsSpan(4), repeated.Length - 8);
        using Fixture fixture = new(repeated);
        ImageDecoder decoder = new();
        await Assert.ThrowsAsync<ImageSizeLimitExceededException>(() => decoder.TryOpenFrameSessionAsync(fixture.Path, Token));
        await Assert.ThrowsAsync<ImageSizeLimitExceededException>(() => decoder.DecodeThumbnailAsync(fixture.Path, new PixelSize(6, 2), Token));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MismatchedBitstreamDimensionsAreRejectedEvenWhenTheFrameScalesToZero(bool lossy)
    {
        byte[] bytes = AnimationCodecFixtures.WebP(width: 6000, height: 6000,
            frames: [new(2, 0, 2, 2, Green, Lossy: lossy)]);
        int frame = FindChunk(bytes, "ANMF"u8);
        bytes[frame + 8 + 6] = 2;
        using Fixture fixture = new(bytes);
        ImageDecodeException error = await Assert.ThrowsAsync<ImageDecodeException>(() =>
            new ImageDecoder().DecodeThumbnailAsync(fixture.Path, new PixelSize(1, 1), Token));
        Assert.Equal(ImageOpenError.CorruptFile, error.Error);
    }

    // Test-owned ICC v2 matrix/TRC profile, matching the existing static WebP ICC fixture.
    private static byte[] LinearProfile()
    {
        byte[] profile = new byte[312];
        static void UInt(byte[] target, int offset, uint value) => BinaryPrimitives.WriteUInt32BigEndian(target.AsSpan(offset), value);
        static void Text(byte[] target, int offset, string value) => Encoding.ASCII.GetBytes(value).CopyTo(target, offset);
        static void Xyz(byte[] target, int offset, double[] values)
        {
            for (int i = 0; i < values.Length; i++)
            {
                BinaryPrimitives.WriteInt32BigEndian(target.AsSpan(offset + i * 4), (int)Math.Round(values[i] * 65536));
            }
        }
        UInt(profile, 0, (uint)profile.Length);
        UInt(profile, 8, 0x02000000);
        Text(profile, 12, "mntr"); Text(profile, 16, "RGB "); Text(profile, 20, "XYZ ");
        BinaryPrimitives.WriteUInt16BigEndian(profile.AsSpan(24), 2026);
        BinaryPrimitives.WriteUInt16BigEndian(profile.AsSpan(26), 10);
        BinaryPrimitives.WriteUInt16BigEndian(profile.AsSpan(28), 5);
        Text(profile, 36, "acsp"); Text(profile, 80, "MIV ");
        Xyz(profile, 68, [.9642, 1, .8249]);
        UInt(profile, 128, 7);
        string[] names = ["rXYZ", "gXYZ", "bXYZ", "wtpt", "rTRC", "gTRC", "bTRC"];
        for (int i = 0; i < names.Length; i++)
        {
            int record = 132 + i * 12;
            Text(profile, record, names[i]);
            UInt(profile, record + 4, i < 4 ? (uint)(216 + i * 20) : 296);
            UInt(profile, record + 8, i < 4 ? 20u : 14u);
        }
        double[][] values = [[.4360747, .2225045, .0139322], [.3850649, .7168786, .0971045], [.1430804, .0606169, .7141733], [.9642, 1, .8249]];
        for (int i = 0; i < values.Length; i++)
        {
            int offset = 216 + i * 20;
            Text(profile, offset, "XYZ ");
            Xyz(profile, offset + 8, values[i]);
        }
        Text(profile, 296, "curv"); UInt(profile, 304, 1);
        BinaryPrimitives.WriteUInt16BigEndian(profile.AsSpan(308), 256);
        return profile;
    }

    private static byte[] Create(byte alpha, bool firstOver, bool oneFrame = false)
    {
        AnimationCodecFixtures.WebPFrame[] frames =
        [
            new(2, 0, 2, 2, Transparent, firstOver ? 0 : 2, Colors: [Transparent, Red, Transparent, Red]),
            new(0, 0, 2, 2, Transparent, 1, Colors: [Transparent, Green, Transparent, Green]),
            new(2, 0, 2, 2, Transparent, 0, Colors: [Transparent, Blue, Transparent, Blue]),
            new(0, 0, 2, 2, Transparent),
            new(4, 0, 2, 2, SKColors.Red),
        ];
        return AnimationCodecFixtures.WebP(0, background: new SKColor(120, 80, 40, alpha), width: 6,
            frames: oneFrame ? [frames[0]] : frames);
    }

    private static byte[][] Expected(int alpha, bool firstOver)
    {
        byte[] bg = alpha switch { 0 => [0, 0, 0, 0], 128 => [20, 40, 60, 128], _ => [40, 80, 120, 255] };
        byte[] green = alpha switch { 0 => [0, 128, 0, 128], 128 => [10, 148, 30, 192], _ => [20, 168, 60, 255] };
        byte[] red = firstOver ? alpha switch
        {
            0 => [0, 0, 128, 128],
            128 => [10, 20, 158, 192],
            _ => [20, 40, 188, 255],
        } : [0, 0, 128, 128];
        byte[] blue = firstOver ? alpha switch
        {
            0 => [128, 0, 64, 192],
            128 => [133, 10, 79, 224],
            _ => [138, 20, 94, 255],
        } : [128, 0, 64, 192];
        byte[] clear = [0, 0, 0, 0];
        byte[] initialTransparent = firstOver ? bg : clear;
        static byte[] Rows(params byte[][] row) => row.Concat(row).SelectMany(pixel => pixel).ToArray();
        return
        [
            Rows(bg, bg, initialTransparent, red, bg, bg),
            Rows(bg, green, initialTransparent, red, bg, bg),
            Rows(bg, bg, initialTransparent, blue, bg, bg),
            Rows(clear, clear, initialTransparent, blue, bg, bg),
            Rows(clear, clear, initialTransparent, blue, [0, 0, 255, 255], [0, 0, 255, 255]),
        ];
    }

    private static int FindChunk(byte[] bytes, ReadOnlySpan<byte> name)
    {
        int offset = 12;
        while (offset < bytes.Length)
        {
            if (bytes.AsSpan(offset, 4).SequenceEqual(name)) { return offset; }
            int length = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset + 4, 4));
            offset += 8 + length + (length & 1);
        }
        throw new InvalidOperationException("The fixture chunk is absent.");
    }

    private static async Task<PixelBuffer> Prefetch(ImageDecoder decoder, string path, PixelSize size)
    {
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(Token);
        timeout.CancelAfter(Timeout);
        while (true)
        {
            PixelBuffer? image = await decoder.TryDecodePreviewAsync(path, size, timeout.Token);
            if (image is not null) { return image; }
            await Task.Delay(10, timeout.Token);
        }
    }

    private static async Task Close(IImageFrameSession session)
    {
        session.Dispose();
        await session.ReleaseCompletion.WaitAsync(Timeout, Token);
    }

    private sealed class Fixture : IDisposable
    {
        internal Fixture(byte[] bytes)
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"viewer-webp-background-{Guid.NewGuid():N}.webp");
            File.WriteAllBytes(Path, bytes);
        }
        internal string Path { get; }
        public void Dispose() => File.Delete(Path);
    }
}
