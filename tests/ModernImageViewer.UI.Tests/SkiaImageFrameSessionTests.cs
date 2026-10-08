using System.IO;

using ModernImageViewer.Application.Images;
using ModernImageViewer.Codecs;
using ModernImageViewer.Codecs.Frames;
using ModernImageViewer.Codecs.Wic;
using ModernImageViewer.Imaging;

namespace ModernImageViewer.UI.Tests;

[Collection("Region decoder slot")]
public sealed class SkiaImageFrameSessionTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    private static readonly int[] RawGifDurations = [0, 10, 20];
    private static readonly int[] EffectiveGifDurations = [100, 100, 20];
    private static readonly int[] LargeGifDurations = [50, 70, 110];

    [Fact]
    public async Task GifSequentialAndRandomFramesMatchFixedCompositedPixelsAndNeverMutatePublishedArrays()
    {
        using Fixture fixture = new(AnimationCodecFixtures.Gif(repeats: 0), ".gif");
        using IImageFrameSession session = (await SkiaImageFrameSession.TryOpenAsync(fixture.Path, Token))!;
        Assert.Equal(ImageSequenceKind.Animation, session.Info.Kind);
        Assert.Equal(5, session.Info.Count);
        Assert.Null(session.Info.TotalPlays);
        Assert.Equal(0, session.RetainedPixelBytes);
        Assert.Equal(ImageFrameDisposal.Previous, session.Info.Frames[1].Disposal);
        Assert.Equal(0, session.Info.Frames[2].RequiredFrame);
        Assert.Equal(new PixelRect(2, 0, 2, 2), session.Info.Frames[2].Bounds);
        byte[][] expected =
        [
            Pixels(1, 1, 1, 1, 1, 1, 1, 1),
            Pixels(2, 1, 1, 1, 1, 2, 1, 1),
            Pixels(1, 1, 3, 3, 1, 1, 3, 3),
            Pixels(2, 2, 3, 3, 2, 2, 3, 3),
            Pixels(0, 0, 3, 3, 0, 0, 3, 3),
        ];
        List<PixelBuffer> published = [];
        try
        {
            foreach (int index in new[] { 0, 1, 2, 3, 4, 2, 0, 4, 1 })
            {
                PixelBuffer image = await session.DecodeFrameAsync(index, new PixelSize(4, 2), 32, Token);
                published.Add(image);
                Assert.Equal(expected[index], image.Pixels.ToArray());
                Assert.Equal(session.FileStamp, image.SourceFileStamp);
                Assert.InRange(session.RetainedPixelBytes, 0, 32);
            }
            for (int i = 0; i < 5; i++) { Assert.Equal(expected[i], published[i].Pixels.ToArray()); }
            using PixelBuffer scaled = await session.DecodeFrameAsync(4, new PixelSize(2, 1), 8, Token);
            Assert.Equal(new PixelSize(2, 1), scaled.Size);
            Assert.Equal(Pixels(0, 3), scaled.Pixels.ToArray());
            Assert.Equal(new PixelSize(4, 2), scaled.SourceSize);
        }
        finally { foreach (PixelBuffer image in published) { image.Dispose(); } }
        await Close(session);
        Assert.Equal(0, session.RetainedPixelBytes);
        AnimationCodecFixtures.ExportValidated(fixture.Path, "animation-infinite.gif");
    }

    [Theory]
    [InlineData(-1, 1)]
    [InlineData(0, -1)]
    [InlineData(1, 2)]
    [InlineData(2, 3)]
    public async Task GifLoopCountsAndShortDurationsAreNormalized(int repeats, int totalPlays)
    {
        AnimationCodecFixtures.GifFrame[] frames =
        [
            new(0, 0, 1, 1, [1], Delay: 0), new(0, 0, 1, 1, [2], Delay: 1),
            new(0, 0, 1, 1, [3], Delay: 2),
        ];
        using Fixture fixture = new(AnimationCodecFixtures.Gif(1, 1, repeats < 0 ? null : repeats, frames), ".gif");
        using IImageFrameSession session = (await SkiaImageFrameSession.TryOpenAsync(fixture.Path, Token))!;
        Assert.Equal(totalPlays < 0 ? (int?)null : totalPlays, session.Info.TotalPlays);
        Assert.Equal(RawGifDurations, session.Info.Frames.Select(frame => frame.RawDurationMilliseconds));
        Assert.Equal(EffectiveGifDurations, session.Info.Frames.Select(frame => frame.DurationMilliseconds));
        await Close(session);
    }

    [Fact]
    public async Task GifConsecutivePreviousDisposalsAndPartialFirstFrameRestoreTheCorrectCanvas()
    {
        AnimationCodecFixtures.GifFrame[] frames =
        [
            new(1, 0, 1, 1, [1]), new(0, 0, 1, 1, [2], Disposal: 3),
            new(2, 0, 1, 1, [3], Disposal: 3), new(3, 0, 1, 1, [2]),
        ];
        using Fixture fixture = new(AnimationCodecFixtures.Gif(4, 1, frames: frames), ".gif");
        using IImageFrameSession session = (await SkiaImageFrameSession.TryOpenAsync(fixture.Path, Token))!;
        foreach (int index in new[] { 0, 1, 2, 3, 1, 3 })
        {
            using PixelBuffer image = await session.DecodeFrameAsync(index, new PixelSize(4, 1), 16, Token);
            Assert.Equal(index switch
            {
                0 => Pixels(0, 1, 0, 0),
                1 => Pixels(2, 1, 0, 0),
                2 => Pixels(0, 1, 3, 0),
                _ => Pixels(0, 1, 0, 2),
            }, image.Pixels.ToArray());
        }
        await Close(session);
    }

    [Theory]
    [InlineData(0, -1)]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    public async Task WebPBlendOffsetDisposalAndLoopsUseTheSameSession(int loops, int totalPlays)
    {
        using Fixture fixture = new(AnimationCodecFixtures.WebP((ushort)loops), ".webp");
        using IImageFrameSession session = (await SkiaImageFrameSession.TryOpenAsync(fixture.Path, Token))!;
        Assert.Equal(totalPlays < 0 ? (int?)null : totalPlays, session.Info.TotalPlays);
        Assert.Equal(3, session.Info.Count);
        Assert.Equal(ImageFrameBlend.Over, session.Info.Frames[1].Blend);
        Assert.Equal(ImageFrameBlend.Source, session.Info.Frames[2].Blend);
        Assert.Equal(ImageFrameDisposal.Background, session.Info.Frames[1].Disposal);
        Assert.Equal(10, session.Info.Frames[1].RawDurationMilliseconds);
        Assert.Equal(100, session.Info.Frames[1].DurationMilliseconds);
        foreach (int index in new[] { 0, 1, 2, 0, 2, 1 })
        {
            using PixelBuffer image = await session.DecodeFrameAsync(index, new PixelSize(4, 2), 32, Token);
            byte[] expected = index switch
            {
                0 => Pixels(1, 1, 1, 1, 1, 1, 1, 1),
                1 => [0, 128, 127, 255, 0, 128, 127, 255, 0, 0, 255, 255, 0, 0, 255, 255,
                      0, 128, 127, 255, 0, 128, 127, 255, 0, 0, 255, 255, 0, 0, 255, 255],
                _ => [255, 255, 255, 255, 255, 255, 255, 255, 255, 0, 0, 255, 255, 0, 0, 255,
                      255, 255, 255, 255, 255, 255, 255, 255, 255, 0, 0, 255, 255, 0, 0, 255],
            };
            Assert.Equal(expected, image.Pixels.ToArray());
        }
        await Close(session);
        if (loops == 0) { AnimationCodecFixtures.ExportValidated(fixture.Path, "animation-infinite.webp"); }
        if (loops == 2) { AnimationCodecFixtures.ExportValidated(fixture.Path, "animation-finite.webp"); }
    }

    [Fact]
    public async Task LargePlayableGifHasValidatedScaledPixelsAndExportsForAnimationObservation()
    {
        using Fixture fixture = new(AnimationCodecFixtures.LargeGif(), ".gif");
        ImageDecoder decoder = new();
        using IImageFrameSession session = (await decoder.TryOpenFrameSessionAsync(fixture.Path, Token))!;
        Assert.Null(session.Info.TotalPlays);
        Assert.Equal(3, session.Info.Count);
        Assert.Equal(new PixelSize(4096, 4096), session.Info.Frames[0].CanvasSize);
        Assert.Equal(LargeGifDurations, session.Info.Frames.Select(frame => frame.DurationMilliseconds));
        Assert.True(session.Info.Frames[0].CanvasSize.PixelCount * 4 < SkiaImageFrameSession.MaximumSourceWorkspaceBytes);
        PixelSize target = new(1024, 1024);
        PixelRect rectangle = new(384, 384, 256, 256);
        foreach (int index in new[] { 0, 1, 2, 0, 2 })
        {
            using PixelBuffer image = await session.DecodeFrameAsync(index, target, ImageFrameLimits.MaximumFrameBytes, Token);
            AssertCanvasRectangle(image, target, new PixelSize(4096, 4096), rectangle, index + 1, ImageFrameLimits.MaximumFrameBytes);
            Assert.InRange(session.RetainedPixelBytes, 0, ImageFrameLimits.MaximumFrameBytes);
        }
        using PixelBuffer representative = await decoder.DecodePreviewAsync(fixture.Path, target, Token);
        AssertCanvasRectangle(representative, target, new PixelSize(4096, 4096), rectangle, 1, ImageFrameLimits.MaximumFrameBytes);
        await Close(session);
        AnimationCodecFixtures.ExportValidated(fixture.Path, "animation-large.gif");
    }

    [Fact]
    public async Task WebPOrientationKeepsRawReferencesSeparateFromImmutableOrientedOutput()
    {
        using Fixture fixture = new(AnimationCodecFixtures.WebP(2, 6), ".webp");
        using IImageFrameSession session = (await SkiaImageFrameSession.TryOpenAsync(fixture.Path, Token))!;
        Assert.Equal(new PixelSize(2, 4), session.Info.Frames[0].CanvasSize);
        Assert.Equal(new PixelRect(0, 2, 2, 2), session.Info.Frames[2].Bounds);
        using PixelBuffer first = await session.DecodeFrameAsync(0, new PixelSize(2, 4), 32, Token);
        using PixelBuffer last = await session.DecodeFrameAsync(2, new PixelSize(2, 4), 32, Token);
        Assert.Equal(new byte[] { 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255,
            255, 0, 0, 255, 255, 0, 0, 255, 255, 0, 0, 255, 255, 0, 0, 255 }, last.Pixels.ToArray());
        Assert.Equal(Pixels(1, 1, 1, 1, 1, 1, 1, 1), first.Pixels.ToArray());
        await Close(session);
    }

    [Fact]
    public async Task StaticFilesReturnNullAndNoncandidateExtensionsDoNotOpenAFile()
    {
        ImageDecoder decoder = new();
        Assert.Null(await decoder.TryOpenFrameSessionAsync("does-not-exist.jpg", Token));
        Assert.Null(await decoder.TryOpenFrameSessionAsync("does-not-exist.png", Token));
        using Fixture gif = new(AnimationCodecFixtures.Gif(1, 1, frames: [new(0, 0, 1, 1, [1])]), ".gif");
        Assert.Null(await decoder.TryOpenFrameSessionAsync(gif.Path, Token));
        using Fixture renamedPng = new([137, 80, 78, 71, 13, 10, 26, 10], ".webp");
        Assert.Null(await decoder.TryOpenFrameSessionAsync(renamedPng.Path, Token));
        using Fixture renamedGif = new(AnimationCodecFixtures.Gif(), ".webp");
        using IImageFrameSession session = (await decoder.TryOpenFrameSessionAsync(renamedGif.Path, Token))!;
        Assert.Equal(5, session.Info.Count);
        await Close(session);
    }

    [Fact]
    public async Task OversizedWorkspaceFrameCountInputAndSmallOutputBudgetHaveExplicitFailures()
    {
        using Fixture large = new(AnimationCodecFixtures.Gif(6000, 6000), ".gif");
        await Assert.ThrowsAsync<ImageSizeLimitExceededException>(() => SkiaImageFrameSession.TryOpenAsync(large.Path, Token));
        AnimationCodecFixtures.GifFrame frame = new(0, 0, 1, 1, [1]);
        using Fixture many = new(AnimationCodecFixtures.Gif(1, 1, frames:
            Enumerable.Repeat(frame, ImageFrameLimits.MaximumFrames + 1).ToArray()), ".gif");
        await Assert.ThrowsAsync<ImageSizeLimitExceededException>(() => SkiaImageFrameSession.TryOpenAsync(many.Path, Token));
        using Fixture input = new(AnimationCodecFixtures.Gif(), ".gif");
        using (FileStream file = new(input.Path, FileMode.Open, FileAccess.Write))
        {
            file.SetLength(ImageFrameLimits.MaximumInputBytes + 1);
        }
        await Assert.ThrowsAsync<ImageSizeLimitExceededException>(() => SkiaImageFrameSession.TryOpenAsync(input.Path, Token));
        using Fixture normal = new(AnimationCodecFixtures.Gif(), ".gif");
        using IImageFrameSession session = (await SkiaImageFrameSession.TryOpenAsync(normal.Path, Token))!;
        await Assert.ThrowsAsync<ImageSizeLimitExceededException>(() => session.DecodeFrameAsync(0, new PixelSize(4, 2), 3, Token));
        using PixelBuffer bounded = await session.DecodeFrameAsync(0, new PixelSize(4, 2), 8, Token);
        Assert.InRange(bounded.Pixels.Length, 4, 8);
        await Close(session);
    }

    [Fact]
    public async Task CancelledQueuedDecodeAndDisposeReleaseTheSessionWithoutChangingPublishedPixels()
    {
        using Fixture fixture = new(AnimationCodecFixtures.Gif(), ".gif");
        IImageFrameSession session = (await SkiaImageFrameSession.TryOpenAsync(fixture.Path, Token))!;
        using PixelBuffer image = await session.DecodeFrameAsync(0, new PixelSize(4, 2), 32, Token);
        using IDisposable busy = await WicImageDecoder.DecodeSlot.AcquireAsync(DecodePriority.Foreground, Token);
        using CancellationTokenSource cancellation = CancellationTokenSource.CreateLinkedTokenSource(Token);
        Task<PixelBuffer> queued = session.DecodeFrameAsync(2, new PixelSize(4, 2), 32, cancellation.Token);
        Assert.False(queued.IsCompleted);
        session.Dispose();
        session.Dispose();
        Assert.False(session.ReleaseCompletion.IsCompleted);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued.WaitAsync(Timeout, Token));
        await Close(session);
        Assert.Equal(0, session.RetainedPixelBytes);
        Assert.Equal(Pixels(1, 1, 1, 1, 1, 1, 1, 1), image.Pixels.ToArray());
        await Assert.ThrowsAsync<ObjectDisposedException>(() => session.DecodeFrameAsync(0, new PixelSize(4, 2), 32, Token));
        using FileStream unlocked = new(fixture.Path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }

    [Fact]
    public async Task TruncatedLaterGifFrameDoesNotPreventOpeningOrPublishingTheFirstFrame()
    {
        byte[] bytes = AnimationCodecFixtures.Gif();
        using Fixture fixture = new(bytes[..^4], ".gif");
        using IImageFrameSession session = (await SkiaImageFrameSession.TryOpenAsync(fixture.Path, Token))!;
        Assert.Equal(5, session.Info.Count);
        using PixelBuffer first = await session.DecodeFrameAsync(0, new PixelSize(4, 2), 32, Token);
        ImageDecodeException error = await Assert.ThrowsAsync<ImageDecodeException>(() =>
            session.DecodeFrameAsync(4, new PixelSize(4, 2), 32, Token));
        Assert.Equal(ImageOpenError.CorruptFile, error.Error);
        Assert.Equal(Pixels(1, 1, 1, 1, 1, 1, 1, 1), first.Pixels.ToArray());
        await Close(session);
    }

    [Fact]
    public async Task FrameByteCapAndCountersTrackRealReleaseWhilePublishedPixelsStayAlive()
    {
        int sessions = SkiaImageFrameSession.ActiveSessionCount;
        int decoders = SkiaImageFrameSession.ActiveDecoderCount;
        using Fixture fixture = new(AnimationCodecFixtures.Gif(2000, 2000), ".gif");
        IImageFrameSession session = (await SkiaImageFrameSession.TryOpenAsync(fixture.Path, Token))!;
        Assert.Equal(sessions + 1, SkiaImageFrameSession.ActiveSessionCount);
        Assert.Equal(decoders + 1, SkiaImageFrameSession.ActiveDecoderCount);
        using PixelBuffer image = await session.DecodeFrameAsync(0, new PixelSize(2000, 2000), long.MaxValue, Token);
        Assert.InRange(image.Pixels.Length, 4, (int)ImageFrameLimits.MaximumFrameBytes);
        Assert.Equal(image.Pixels.Length, session.RetainedPixelBytes);
        await Close(session);
        Assert.Equal(sessions, SkiaImageFrameSession.ActiveSessionCount);
        Assert.Equal(decoders, SkiaImageFrameSession.ActiveDecoderCount);
        Assert.Equal(0, session.RetainedPixelBytes);
        Assert.True(image.Pixels.Length > 0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GifRepresentativeIncludesLogicalCanvasOffsetForSingleAndAnimatedFiles(bool animated)
    {
        AnimationCodecFixtures.GifFrame first = new(2, 0, 2, 2, [1, 1, 1, 1]);
        AnimationCodecFixtures.GifFrame[] frames = animated
            ? [first, new(0, 0, 2, 2, [2, 2, 2, 2])]
            : [first];
        using Fixture fixture = new(AnimationCodecFixtures.Gif(frames: frames), ".gif");
        int sessions = SkiaImageFrameSession.ActiveSessionCount;
        int decoders = SkiaImageFrameSession.ActiveDecoderCount;
        await Task.Run(() =>
        {
            using FileStream file = File.OpenRead(fixture.Path);
            using PixelBuffer full = ImageDecoder.DecodeGifRepresentative(file, null, 32, Token);
            Assert.Equal(new PixelSize(4, 2), full.Size);
            Assert.Equal(Pixels(0, 0, 1, 1, 0, 0, 1, 1), full.Pixels.ToArray());
            file.Position = 0;
            using PixelBuffer preview = ImageDecoder.DecodeGifRepresentative(file, new PixelSize(2, 1), 8, Token);
            Assert.Equal(new PixelSize(2, 1), preview.Size);
            Assert.Equal(new PixelSize(4, 2), preview.SourceSize);
            Assert.Equal(Pixels(0, 1), preview.Pixels.ToArray());
            file.Position = 0;
            Assert.Throws<ImageSizeLimitExceededException>(() => ImageDecoder.DecodeGifRepresentative(file, null, 8, Token));
        }, Token);
        ImageDecoder decoder = new();
        using PixelBuffer main = await decoder.DecodeAsync(fixture.Path, Token);
        Assert.Equal(new PixelSize(4, 2), main.Size);
        Assert.Equal(Pixels(0, 0, 1, 1, 0, 0, 1, 1), main.Pixels.ToArray());
        using PixelBuffer actualPreview = await decoder.DecodePreviewAsync(fixture.Path, new PixelSize(2, 1), Token);
        using PixelBuffer thumbnail = await decoder.DecodeThumbnailAsync(fixture.Path, new PixelSize(2, 1), Token);
        using PixelBuffer prefetch = await PrefetchWhenIdle(decoder, fixture.Path, new PixelSize(2, 1));
        foreach (PixelBuffer image in new[] { actualPreview, thumbnail, prefetch })
        {
            Assert.Equal(new PixelSize(2, 1), image.Size);
            Assert.Equal(new PixelSize(4, 2), image.SourceSize);
            Assert.Equal(Pixels(0, 1), image.Pixels.ToArray());
            Assert.Equal(main.SourceFileStamp, image.SourceFileStamp);
        }
        Assert.Equal(sessions, SkiaImageFrameSession.ActiveSessionCount);
        Assert.Equal(decoders, SkiaImageFrameSession.ActiveDecoderCount);
    }

    [Fact]
    public async Task LargeGifRepresentativeScalesBelowOutputBudgetsThroughPreviewThumbnailAndPrefetch()
    {
        AnimationCodecFixtures.GifFrame first = new(2048, 1024, 256, 256,
            Enumerable.Repeat((byte)1, 256 * 256).ToArray());
        AnimationCodecFixtures.GifFrame second = new(0, 0, 1, 1, [2]);
        using Fixture fixture = new(AnimationCodecFixtures.Gif(8192, 4096, frames: [first, second]), ".gif");
        int sessions = SkiaImageFrameSession.ActiveSessionCount;
        int decoders = SkiaImageFrameSession.ActiveDecoderCount;
        ImageDecoder decoder = new();
        await Assert.ThrowsAsync<ImageSizeLimitExceededException>(() => decoder.TryOpenFrameSessionAsync(fixture.Path, Token));
        using PixelBuffer preview = await decoder.DecodePreviewAsync(fixture.Path, new PixelSize(2560, 1600), Token);
        using PixelBuffer thumbnail = await decoder.DecodeThumbnailAsync(fixture.Path, new PixelSize(224, 140), Token);
        using PixelBuffer prefetch = await PrefetchWhenIdle(decoder, fixture.Path, new PixelSize(1280, 800));
        AssertRepresentative(preview, new PixelSize(2560, 1280), new PixelRect(640, 320, 80, 80), 32L * 1024 * 1024);
        AssertRepresentative(thumbnail, new PixelSize(224, 112), new PixelRect(56, 28, 7, 7), 224L * 140 * 4);
        AssertRepresentative(prefetch, new PixelSize(1280, 640), new PixelRect(320, 160, 40, 40), 4L * 1024 * 1024);
        await Task.Run(() =>
        {
            using FileStream file = File.OpenRead(fixture.Path);
            using PixelBuffer bounded = ImageDecoder.DecodeGifRepresentative(file, new PixelSize(224, 140), 224L * 140 * 4, Token);
            AssertRepresentative(bounded, new PixelSize(224, 112), new PixelRect(56, 28, 7, 7), 224L * 140 * 4);
            file.Position = 0;
            Assert.Throws<ImageSizeLimitExceededException>(() => ImageDecoder.DecodeGifRepresentative(file,
                new PixelSize(224, 140), (224L * 112 * 4) - 1, Token));
        }, Token);
        Assert.Equal(sessions, SkiaImageFrameSession.ActiveSessionCount);
        Assert.Equal(decoders, SkiaImageFrameSession.ActiveDecoderCount);
    }

    private static void AssertRepresentative(PixelBuffer image, PixelSize size, PixelRect redBounds, long budget)
        => AssertCanvasRectangle(image, size, new PixelSize(8192, 4096), redBounds, 1, budget);

    private static void AssertCanvasRectangle(PixelBuffer image, PixelSize size, PixelSize source, PixelRect bounds,
        int color, long budget)
    {
        Assert.Equal(size, image.Size);
        Assert.Equal(source, image.SourceSize);
        Assert.InRange(image.Pixels.Length, 4, budget);
        ReadOnlySpan<byte> pixels = image.Pixels.Span;
        byte[] clearRow = new byte[image.Stride];
        byte[] coloredRow = new byte[image.Stride];
        byte[] colorPixels = Pixels(color);
        for (int x = bounds.X; x < bounds.Right; x++)
        {
            colorPixels.CopyTo(coloredRow, x * 4);
        }
        for (int y = 0; y < size.Height; y++)
        {
            byte[] expected = y >= bounds.Y && y < bounds.Bottom ? coloredRow : clearRow;
            Assert.True(pixels.Slice(y * image.Stride, image.Stride).SequenceEqual(expected), $"Unexpected pixels in row {y}.");
        }
    }

    private static async Task<PixelBuffer> PrefetchWhenIdle(ImageDecoder decoder, string path, PixelSize maximum)
    {
        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(Token);
        deadline.CancelAfter(Timeout);
        while (true)
        {
            PixelBuffer? image = await decoder.TryDecodePreviewAsync(path, maximum, deadline.Token);
            if (image is not null) { return image; }
            await Task.Delay(10, deadline.Token);
        }
    }

    [Fact]
    public async Task LongGifSeekReconstructsDependenciesIterativelyWithoutRetainingAFrameCache()
    {
        AnimationCodecFixtures.GifFrame[] frames = Enumerable.Range(0, 512)
            .Select(index => new AnimationCodecFixtures.GifFrame(index % 4, 0, 1, 1, [(byte)((index % 3) + 1)]))
            .ToArray();
        using Fixture fixture = new(AnimationCodecFixtures.Gif(4, 1, frames: frames), ".gif");
        using IImageFrameSession session = (await SkiaImageFrameSession.TryOpenAsync(fixture.Path, Token))!;
        using PixelBuffer image = await session.DecodeFrameAsync(511, new PixelSize(4, 1), 16, Token);
        Assert.Equal(Pixels(2, 3, 1, 2), image.Pixels.ToArray());
        Assert.Equal(16, session.RetainedPixelBytes);
        await Close(session);
    }

    private static byte[] Pixels(params int[] colors) => colors.SelectMany(color => color switch
    {
        1 => new byte[] { 0, 0, 255, 255 },
        2 => new byte[] { 0, 255, 0, 255 },
        3 => new byte[] { 255, 0, 0, 255 },
        _ => new byte[4],
    }).ToArray();

    private static async Task Close(IImageFrameSession session)
    {
        session.Dispose();
        await session.ReleaseCompletion.WaitAsync(Timeout, Token);
    }

    private sealed class Fixture : IDisposable
    {
        internal Fixture(byte[] bytes, string extension)
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"viewer-animation-{Guid.NewGuid():N}{extension}");
            File.WriteAllBytes(Path, bytes);
        }
        internal string Path { get; }
        public void Dispose() => File.Delete(Path);
    }
}
