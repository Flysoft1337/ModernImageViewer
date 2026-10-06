using ModernImageViewer.Application.Images;
using ModernImageViewer.Imaging;

namespace ModernImageViewer.Tests;

public sealed class NeighborPreviewCacheTests
{
    [Fact]
    public async Task DeletedFileDuringNativePrefetchCannotEnterCache()
    {
        using TestFiles files = new();
        string path = files.Create("deleted.png");
        TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<PixelBuffer?> native = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using NeighborPreviewCache cache = new(new FakeDecoder((_, _, _) => { started.SetResult(); return native.Task; }));
        cache.Schedule(path);
        Task pending = cache.WaitForPendingAsync();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        File.Delete(path);
        PixelBuffer pixels = CreateBuffer();
        native.SetResult(pixels);
        await pending;
        Assert.Equal(0, cache.RetainedBytes);
        AssertDisposed(pixels);
        Assert.Null(await cache.TryTakeAsync(path, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CancelPendingPreservesCompletedPreviewAndTakeTransfersOwnershipOnce()
    {
        using TestFiles files = new();
        string path = files.Create("next.png");
        PixelBuffer buffer = CreateBuffer();
        FakeDecoder decoder = new((_, size, _) =>
        {
            Assert.Equal(new PixelSize(1280, 800), size);
            return Task.FromResult<PixelBuffer?>(buffer);
        });
        NeighborPreviewCache cache = new(decoder);
        cache.Schedule(path);
        await cache.WaitForPendingAsync();

        cache.CancelPending();
        Assert.Same(buffer, await cache.TryTakeAsync(path, CancellationToken.None));
        Assert.Null(await cache.TryTakeAsync(path, CancellationToken.None));
        cache.Dispose();
        Assert.Equal(4, buffer.Pixels.Length);
        buffer.Dispose();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RejectsOversizedDimensionsOrBackingArray(bool oversizedDimensions)
    {
        using TestFiles files = new();
        string path = files.Create("next.png");
        PixelBuffer buffer = oversizedDimensions
            ? new(new PixelSize(1281, 1), 1281 * 4, new byte[1281 * 4])
            : new(new PixelSize(1, 1), 4, new byte[NeighborPreviewCache.MaximumCachedBytes + 1]);
        using NeighborPreviewCache cache = new(new FakeDecoder((_, _, _) => Task.FromResult<PixelBuffer?>(buffer)));
        cache.Schedule(path);
        await cache.WaitForPendingAsync();

        Assert.Null(await cache.TryTakeAsync(path, CancellationToken.None));
        AssertDisposed(buffer);
    }

    [Fact]
    public async Task LatestDirectionWinsEvenWhenCancelledDecoderReturnsLate()
    {
        using TestFiles files = new();
        string previous = files.Create("previous.png");
        string next = files.Create("next.png");
        TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<PixelBuffer?> late = new(TaskCreationOptions.RunContinuationsAsynchronously);
        PixelBuffer staleBuffer = CreateBuffer();
        PixelBuffer latestBuffer = CreateBuffer();
        FakeDecoder decoder = new((path, _, _) =>
        {
            if (path == previous)
            {
                started.SetResult();
                return late.Task;
            }

            return Task.FromResult<PixelBuffer?>(latestBuffer);
        });
        using NeighborPreviewCache cache = new(decoder);
        cache.Schedule(previous);
        Task oldPending = cache.WaitForPendingAsync();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        cache.Schedule(next);
        await cache.WaitForPendingAsync();
        late.SetResult(staleBuffer);
        await oldPending;

        AssertDisposed(staleBuffer);
        Assert.Null(await cache.TryTakeAsync(previous, CancellationToken.None));
        Assert.Same(latestBuffer, await cache.TryTakeAsync(next, CancellationToken.None));
        latestBuffer.Dispose();
    }

    [Fact]
    public async Task ClearRejectsLateDecodeAndDisposesCompletedCachedPreview()
    {
        using TestFiles files = new();
        string path = files.Create("next.png");
        TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<PixelBuffer?> late = new(TaskCreationOptions.RunContinuationsAsynchronously);
        PixelBuffer lateBuffer = CreateBuffer();
        PixelBuffer completedBuffer = CreateBuffer();
        FakeDecoder decoder = new((_, _, _) =>
        {
            if (started.Task.IsCompleted)
            {
                return Task.FromResult<PixelBuffer?>(completedBuffer);
            }

            started.SetResult();
            return late.Task;
        });
        using NeighborPreviewCache cache = new(decoder);
        cache.Schedule(path);
        Task pending = cache.WaitForPendingAsync();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        cache.Clear();
        late.SetResult(lateBuffer);
        await pending;
        AssertDisposed(lateBuffer);

        cache.Schedule(path);
        await cache.WaitForPendingAsync();
        cache.Clear();
        AssertDisposed(completedBuffer);
        Assert.Null(await cache.TryTakeAsync(path, CancellationToken.None));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task FileLengthAndWriteTimeAreRevalidatedBeforeTake(bool changeLength)
    {
        using TestFiles files = new();
        string path = files.Create("next.png");
        PixelBuffer buffer = CreateBuffer();
        using NeighborPreviewCache cache = new(new FakeDecoder((_, _, _) => Task.FromResult<PixelBuffer?>(buffer)));
        cache.Schedule(path);
        await cache.WaitForPendingAsync();

        if (changeLength)
        {
            File.WriteAllBytes(path, [1, 2]);
        }
        else
        {
            File.SetLastWriteTimeUtc(path, File.GetLastWriteTimeUtc(path).AddMinutes(10));
        }

        Assert.Null(await cache.TryTakeAsync(path, CancellationToken.None));
        AssertDisposed(buffer);
    }

    [Fact]
    public async Task BusyDecoderReturnsWithoutRetryOrQueueing()
    {
        using TestFiles files = new();
        string path = files.Create("next.png");
        int attempts = 0;
        using NeighborPreviewCache cache = new(new FakeDecoder((_, _, _) =>
        {
            Interlocked.Increment(ref attempts);
            return Task.FromResult<PixelBuffer?>(null);
        }));
        cache.Schedule(path);
        await cache.WaitForPendingAsync();

        Assert.Null(await cache.TryTakeAsync(path, CancellationToken.None));
        Assert.Equal(1, attempts);
    }

    private static PixelBuffer CreateBuffer() => new(new PixelSize(1, 1), 4, new byte[4]);

    private static void AssertDisposed(PixelBuffer buffer) => Assert.Throws<ObjectDisposedException>(() => buffer.Pixels);

    private sealed class FakeDecoder(Func<string, PixelSize, CancellationToken, Task<PixelBuffer?>> decode) : IPrefetchImageDecoder
    {
        public Task<PixelBuffer?> TryDecodePreviewAsync(string path, PixelSize maximumSize, CancellationToken cancellationToken) =>
            decode(path, maximumSize, cancellationToken);
    }

    private sealed class TestFiles : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "miv-neighbor-" + Guid.NewGuid().ToString("N"));

        public TestFiles()
        {
            Directory.CreateDirectory(_directory);
        }

        public string Create(string name)
        {
            string path = Path.Combine(_directory, name);
            File.WriteAllBytes(path, [1]);
            return path;
        }

        public void Dispose() => Directory.Delete(_directory, true);
    }
}
