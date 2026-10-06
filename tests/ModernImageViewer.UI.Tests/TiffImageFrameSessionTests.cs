using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;

using ModernImageViewer.Application.Images;
using ModernImageViewer.Codecs;
using ModernImageViewer.Codecs.Frames;
using ModernImageViewer.Codecs.Wic;
using ModernImageViewer.Imaging;
using ModernImageViewer.UI.Tests.Fixtures;

namespace ModernImageViewer.UI.Tests;

[Collection("Region decoder slot")]
public sealed class TiffImageFrameSessionTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public void SamplesHaveFixedHashes()
    {
        Assert.Equal("AAE3EDEEFCA4EA41241E24D6FA18D43A55309DF6FE45AC0BFE39098845892D54", Convert.ToHexString(SHA256.HashData(TiffFixtures.Single())));
        Assert.Equal("7EF9C61A07DD06A4C54BEDB6A75DAEDB3081A8B7D3D29C589C6CC595496AE6A7", Convert.ToHexString(SHA256.HashData(TiffFixtures.Multiple())));
        Assert.Equal("382BCBDCA96B3428BAB588895E5DFF4253A2A0035F11F678EFCC0BFAB1AD5DD4", Convert.ToHexString(SHA256.HashData(TiffFixtures.Orientations())));
        Assert.Equal("340CC6ACC763FCCA0A41C85E385D5BE7A8862C3849067980E1E83DFA2CD28BA5", Convert.ToHexString(SHA256.HashData(TiffFixtures.BrokenPage())));
    }

    [Fact]
    public async Task SinglePageAndDisguisedNonTiffReturnNullAndReleaseHandles()
    {
        using Fixture file = new(TiffFixtures.Single());
        Assert.Null(await TiffImageFrameSession.TryOpenAsync(file.Path, Token));
        Assert.Equal(0, TiffImageFrameSession.ActiveSessionCount);
        Assert.Equal(0, TiffImageFrameSession.ActiveContextCount);
        file.AssertUnlocked();
        File.WriteAllBytes(file.Path, "not a TIFF"u8.ToArray());
        Assert.Null(await TiffImageFrameSession.TryOpenAsync(file.Path, Token));
        file.AssertUnlocked();
    }

    [Fact]
    public async Task RandomPagesHaveIndependentDimensionsPixelsAndFileStamp()
    {
        using Fixture file = new(TiffFixtures.Multiple());
        TiffImageFrameSession session = await Open(file.Path);
        try
        {
            Assert.Equal(ImageSequenceKind.Pages, session.Info.Kind);
            Assert.Equal(3, session.Info.Count);
            Assert.Equal(1, session.Info.TotalPlays);
            Assert.True(session.Info.CanRandomAccess);
            Assert.Equal(1, TiffImageFrameSession.ActiveSessionCount);
            Assert.Equal(1, TiffImageFrameSession.ActiveContextCount);
            Assert.Equal(0, session.Info.DurationMilliseconds);
            byte[] original = SHA256.HashData(File.ReadAllBytes(file.Path));
            foreach (int i in new[] { 2, 0, 1, 2 })
            {
                ImageFrameInfo info = session.Info.Frames[i];
                Assert.Equal(new PixelRect(0, 0, info.CanvasSize.Width, info.CanvasSize.Height), info.Bounds);
                Assert.Equal(ImageFrameBlend.Source, info.Blend);
                Assert.Equal(ImageFrameDisposal.Keep, info.Disposal);
                Assert.False(info.RequiresComposition);
                using PixelBuffer image = await session.DecodeFrameAsync(i, info.CanvasSize, 16 * 1024 * 1024, Token);
                Assert.Equal(info.CanvasSize, image.Size);
                Assert.Equal(session.FileStamp, image.SourceFileStamp);
                AssertPixel(image, i, 0, 0, 0, 0);
                AssertPixel(image, i, image.Size.Width - 1, image.Size.Height - 1, image.Size.Width - 1, image.Size.Height - 1);
                Assert.Equal(0, session.RetainedPixelBytes);
            }
            Assert.Equal(original, SHA256.HashData(File.ReadAllBytes(file.Path)));
            Assert.Throws<IOException>(() => file.AssertUnlocked());
        }
        finally { await Close(session); }
        file.AssertUnlocked();
        string? exportDirectory = Environment.GetEnvironmentVariable("MIV_FORMAT_FIXTURE_DIRECTORY");
        if (!string.IsNullOrWhiteSpace(exportDirectory))
        {
            Directory.CreateDirectory(exportDirectory);
            File.Copy(file.Path, Path.Combine(exportDirectory, "animation-pages.tiff"), overwrite: true);
        }
    }

    [Fact]
    public async Task DetailAbove4096UsesFullPageWhenBudgetAllowsIt()
    {
        using Fixture file = new(TiffFixtures.Create(new(8, 5), new(6000, 1000)));
        TiffImageFrameSession session = await Open(file.Path);
        try
        {
            using PixelBuffer image = await session.DecodeFrameAsync(1, new(6000, 1000), 92L * 1024 * 1024, Token);
            Assert.Equal(new PixelSize(6000, 1000), image.Size);
            Assert.Equal(24_000_000, image.Pixels.Length);
            AssertPixel(image, 1, 5999, 999, 5999, 999);
        }
        finally { await Close(session); }
    }

    [Fact]
    public async Task InputSourceAndPageCountLimitsReleaseContextsOnFailure()
    {
        using Fixture file = new(TiffFixtures.Create(new(8, 5), new(32769, 1)));
        await Assert.ThrowsAsync<ImageSizeLimitExceededException>(() => TiffImageFrameSession.TryOpenAsync(file.Path, Token));
        Assert.Equal(0, TiffImageFrameSession.ActiveContextCount);
        Assert.Equal(0, TiffImageFrameSession.ActiveSessionCount);
        file.AssertUnlocked();
        File.WriteAllBytes(file.Path, TiffFixtures.OversizedPage(11000, 10000));
        await Assert.ThrowsAsync<ImageSizeLimitExceededException>(() => TiffImageFrameSession.TryOpenAsync(file.Path, Token));
        Assert.Equal(0, TiffImageFrameSession.ActiveContextCount);
        Assert.Equal(0, TiffImageFrameSession.ActiveSessionCount);
        file.AssertUnlocked();
        File.WriteAllBytes(file.Path, TiffFixtures.Create(Enumerable.Repeat(new TiffFixtures.Page(1, 1), ImageFrameLimits.MaximumFrames + 1).ToArray()));
        await Assert.ThrowsAsync<ImageSizeLimitExceededException>(() => TiffImageFrameSession.TryOpenAsync(file.Path, Token));
        file.AssertUnlocked();
        using (FileStream stream = new(file.Path, FileMode.Open, FileAccess.Write, FileShare.None))
        { stream.SetLength(ImageFrameLimits.MaximumInputBytes + 1); }
        await Assert.ThrowsAsync<ImageSizeLimitExceededException>(() => TiffImageFrameSession.TryOpenAsync(file.Path, Token));
        Assert.Equal(0, TiffImageFrameSession.ActiveContextCount);
        Assert.Equal(0, TiffImageFrameSession.ActiveSessionCount);
        file.AssertUnlocked();
    }

    [Fact]
    public async Task ChangedFileStampIsRejectedBeforePixelsAreRetained()
    {
        using Fixture file = new(TiffFixtures.Multiple());
        TiffImageFrameSession session = await Open(file.Path);
        try
        {
            File.SetLastWriteTimeUtc(file.Path, session.FileStamp.ModifiedUtc.AddMinutes(1));
            await Assert.ThrowsAsync<IOException>(() => session.DecodeFrameAsync(1, new(5, 3), 60, Token));
            await Assert.ThrowsAsync<IOException>(() => session.DecodeRegionAsync(1, new(0, 0, 1, 1), new(5, 3), 4, Token));
            Assert.Equal(0, session.RetainedPixelBytes);
        }
        finally { await Close(session); }
    }

    [Fact]
    public async Task CancellationDuringStartedWorkWaitsForReturnAndDisposesLatePixelsBeforeCompletion()
    {
        using Fixture file = new(TiffFixtures.Multiple());
        TiffImageFrameSession session = await Open(file.Path);
        using CancellationTokenSource cancel = new();
        using ManualResetEventSlim release = new();
        TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        PixelBuffer latePixels = new(new(1, 1), 4, [1, 2, 3, 255]);
        Task<PixelBuffer> work = EnqueueBlocked(session, token =>
        {
            started.SetResult();
            release.Wait(CancellationToken.None);
            return latePixels;
        }, cancel.Token);
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
            cancel.Cancel();
            Assert.False(work.IsCompleted);
            Task<PixelBuffer> queued = session.DecodeFrameAsync(0, new(8, 5), 160, Token);
            Stopwatch watch = Stopwatch.StartNew();
            session.Dispose();
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(1));
            Assert.False(work.IsCompleted);
            Assert.False(queued.IsCompleted);
            Assert.False(session.Completion.IsCompleted);
            Assert.Equal(1, TiffImageFrameSession.ActiveContextCount);
            Assert.Equal(1, TiffImageFrameSession.ActiveSessionCount);
            Assert.Throws<IOException>(() => file.AssertUnlocked());
            Task<IDisposable> foreground = WicImageDecoder.DecodeSlot.AcquireAsync(DecodePriority.Foreground, Token);
            Assert.False(foreground.IsCompleted);
            release.Set();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => work.WaitAsync(TimeSpan.FromSeconds(5), Token));
            Assert.Throws<ObjectDisposedException>(() => latePixels.Pixels);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued);
            using IDisposable slot = await foreground.WaitAsync(TimeSpan.FromSeconds(5), Token);
            await session.Completion.WaitAsync(TimeSpan.FromSeconds(5), Token);
            Assert.Equal(0, TiffImageFrameSession.ActiveContextCount);
            Assert.Equal(0, TiffImageFrameSession.ActiveSessionCount);
            file.AssertUnlocked();
        }
        finally { release.Set(); await Close(session); latePixels.Dispose(); }
    }

    private static Task<PixelBuffer> EnqueueBlocked(TiffImageFrameSession session,
        Func<CancellationToken, PixelBuffer> work, CancellationToken token) =>
        (Task<PixelBuffer>)typeof(TiffImageFrameSession).GetMethod("Enqueue", BindingFlags.Instance | BindingFlags.NonPublic)!
            .MakeGenericMethod(typeof(PixelBuffer)).Invoke(session, [work, token, true])!;

    [Fact]
    public async Task EveryPageOrientationMatchesFixedPixelsAndAsymmetricRegions()
    {
        using Fixture file = new(TiffFixtures.Orientations());
        TiffImageFrameSession session = await Open(file.Path);
        try
        {
            for (int i = 0; i < 8; i++)
            {
                int width = 9 + i, height = 6 + i;
                PixelSize expected = i >= 4 ? new(height, width) : new(width, height);
                Assert.Equal(expected, session.Info.Frames[i].CanvasSize);
                using PixelBuffer image = await session.DecodeFrameAsync(i, expected, 4096, Token);
                Assert.Equal((ushort)(i + 1), image.Metadata.Orientation);
                for (int y = 0; y < expected.Height; y++)
                {
                    for (int x = 0; x < expected.Width; x++)
                    {
                        (int rx, int ry) = i switch
                        {
                            1 => (width - 1 - x, y),
                            2 => (width - 1 - x, height - 1 - y),
                            3 => (x, height - 1 - y),
                            4 => (y, x),
                            5 => (y, height - 1 - x),
                            6 => (width - 1 - y, height - 1 - x),
                            7 => (width - 1 - y, x),
                            _ => (x, y),
                        };
                        AssertPixel(image, i, x, y, rx, ry);
                    }
                }
                PixelRect bounds = new(1, 2, 3, 2);
                using DecodedImageRegion region = await session.DecodeRegionAsync(i, bounds, expected, 24, Token);
                Assert.Equal(bounds, region.Bounds);
                Assert.Equal(session.FileStamp, region.Image.SourceFileStamp);
                for (int y = 0; y < bounds.Height; y++)
                {
                    Assert.Equal(image.Pixels.Slice((bounds.Y + y) * image.Stride + bounds.X * 4, bounds.Width * 4).ToArray(),
                        region.Image.Pixels.Slice(y * region.Image.Stride, bounds.Width * 4).ToArray());
                }
            }
        }
        finally { await Close(session); }
    }

    [Fact]
    public async Task TargetsBudgetsBoundsAndPageIdentityAreEnforced()
    {
        using Fixture file = new(TiffFixtures.Multiple());
        TiffImageFrameSession session = await Open(file.Path);
        try
        {
            using PixelBuffer small = await session.DecodeFrameAsync(2, new(1024, 1024), 128, Token);
            Assert.InRange(small.Pixels.Length, 4, 128);
            Assert.Equal(new PixelSize(2050, 4), small.SourceSize);
            using PixelBuffer page = await session.DecodeFrameAsync(1, new(2, 2), 16, Token);
            Assert.InRange(page.Pixels.Span[2], (byte)19, (byte)25);
            using DecodedImageRegion edge = await session.DecodeRegionAsync(2, new(2, 0, 2048, 4), new(2050, 4), 32768, Token);
            AssertPixel(edge.Image, 2, 2047, 3, 2049, 3);
            await Assert.ThrowsAsync<ImageSizeLimitExceededException>(() => session.DecodeFrameAsync(0, new(5, 5), 3, Token));
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => session.DecodeFrameAsync(3, new(5, 5), 100, Token));
            await Assert.ThrowsAsync<ImageSizeLimitExceededException>(() => session.DecodeRegionAsync(2, new(0, 0, 2049, 1), new(2050, 4), 16 * 1024 * 1024, Token));
            await Assert.ThrowsAsync<ImageSizeLimitExceededException>(() => session.DecodeRegionAsync(1, new(0, 0, 2, 2), new(5, 3), 15, Token));
            await Assert.ThrowsAsync<ImageDecodeException>(() => session.DecodeRegionAsync(1, new(0, 0, 1, 1), new(8, 5), 4, Token));
            await Assert.ThrowsAsync<ImageDecodeException>(() => session.DecodeRegionAsync(1, new(4, 2, 2, 1), new(5, 3), 8, Token));
        }
        finally { await Close(session); }
    }

    [Fact]
    public async Task BrokenLaterStripFailsWithoutDamagingEarlierPageOrKeepingPixels()
    {
        using Fixture file = new(TiffFixtures.BrokenPage());
        TiffImageFrameSession session = await Open(file.Path);
        try
        {
            using PixelBuffer first = await session.DecodeFrameAsync(0, new(8, 5), 160, Token);
            await Assert.ThrowsAsync<ImageDecodeException>(() => session.DecodeFrameAsync(1, new(5, 3), 60, Token));
            AssertPixel(first, 0, 3, 2, 3, 2);
            using PixelBuffer again = await session.DecodeFrameAsync(0, new(8, 5), 160, Token);
            Assert.Equal(first.Pixels.ToArray(), again.Pixels.ToArray());
            Assert.Equal(0, session.RetainedPixelBytes);
        }
        finally { await Close(session); }
        Assert.Equal(0, TiffImageFrameSession.ActiveSessionCount);
        Assert.Equal(0, TiffImageFrameSession.ActiveContextCount);
        file.AssertUnlocked();
    }

    [Fact]
    public async Task QueuedCancellationDisposeAndRapidSwitchReleaseWithoutWaitingForSlot()
    {
        using Fixture file = new(TiffFixtures.Multiple());
        for (int i = 0; i < 8; i++)
        {
            TiffImageFrameSession session = await Open(file.Path);
            using (IDisposable slot = await WicImageDecoder.DecodeSlot.AcquireAsync(DecodePriority.Foreground, Token))
            {
                using CancellationTokenSource cancel = new();
                Task<PixelBuffer> canceled = session.DecodeFrameAsync(1, new(5, 3), 60, cancel.Token);
                cancel.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceled.WaitAsync(TimeSpan.FromSeconds(2), Token));
                Task<PixelBuffer> late = session.DecodeFrameAsync(2, new(2050, 4), 40000, Token);
                Stopwatch watch = Stopwatch.StartNew();
                session.Dispose();
                Assert.True(watch.Elapsed < TimeSpan.FromSeconds(1));
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => late.WaitAsync(TimeSpan.FromSeconds(2), Token));
                await Assert.ThrowsAsync<ObjectDisposedException>(() => session.DecodeFrameAsync(0, new(8, 5), 160, Token));
            }
            await session.Completion.WaitAsync(TimeSpan.FromSeconds(5), Token);
            session.Dispose();
            Assert.Equal(0, session.RetainedPixelBytes);
            Assert.Equal(0, TiffImageFrameSession.ActiveSessionCount);
            Assert.Equal(0, TiffImageFrameSession.ActiveContextCount);
            file.AssertUnlocked();
        }
    }

    private static void AssertPixel(PixelBuffer image, int page, int x, int y, int rawX, int rawY) =>
        Assert.Equal(new byte[] { (byte)(rawX * 17), (byte)(rawY * 29), (byte)(page * 19 + rawX + rawY), 255 },
            image.Pixels.Slice(y * image.Stride + x * 4, 4).ToArray());

    private static async Task<TiffImageFrameSession> Open(string path) =>
        Assert.IsType<TiffImageFrameSession>(await TiffImageFrameSession.TryOpenAsync(path, Token));
    private static async Task Close(TiffImageFrameSession session)
    {
        session.Dispose();
        Assert.Same(session.Completion, session.ReleaseCompletion);
        await session.ReleaseCompletion.WaitAsync(TimeSpan.FromSeconds(5), Token);
    }

    private sealed class Fixture : IDisposable
    {
        internal Fixture(byte[] bytes) { File.WriteAllBytes(Path, bytes); }
        internal string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"viewer-tiff-{Guid.NewGuid():N}.tif");
        internal void AssertUnlocked() { using FileStream stream = new(Path, FileMode.Open, FileAccess.ReadWrite, FileShare.None); }
        public void Dispose() => File.Delete(Path);
    }
}
