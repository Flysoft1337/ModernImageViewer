using ModernImageViewer.Codecs;
using ModernImageViewer.Imaging;

namespace ModernImageViewer.UI.Tests;

public sealed class DecodeSchedulerTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ForegroundOvertakesQueuedDetailAndThumbnailWithoutAddingAMainSlot()
    {
        DecodeScheduler scheduler = new();
        using IDisposable active = await scheduler.AcquireAsync(DecodePriority.Detail, Token);
        Task<IDisposable> detail = scheduler.AcquireAsync(DecodePriority.Detail, Token);
        Task<IDisposable> thumbnail = scheduler.AcquireAsync(DecodePriority.Thumbnail, Token);
        Task<IDisposable> foreground = scheduler.AcquireAsync(DecodePriority.Foreground, Token);
        Assert.False(foreground.IsCompleted);
        active.Dispose();
        using IDisposable foregroundLease = await foreground.WaitAsync(Timeout, Token);
        Assert.False(detail.IsCompleted);
        Assert.False(thumbnail.IsCompleted);
        Assert.Null(scheduler.TryAcquirePrefetch(Token));
        foregroundLease.Dispose();
        using IDisposable detailLease = await detail.WaitAsync(Timeout, Token);
        Assert.False(thumbnail.IsCompleted);
        detailLease.Dispose();
        using IDisposable thumbnailLease = await thumbnail.WaitAsync(Timeout, Token);
    }

    [Fact]
    public async Task ThumbnailsKeepTwoSlotsAndForegroundDoesNotWaitForTheirAdmission()
    {
        DecodeScheduler scheduler = new();
        using IDisposable first = await scheduler.AcquireAsync(DecodePriority.Thumbnail, Token);
        using IDisposable second = await scheduler.AcquireAsync(DecodePriority.Thumbnail, Token);
        Assert.Null(scheduler.TryAcquirePrefetch(Token));
        Task<IDisposable> third = scheduler.AcquireAsync(DecodePriority.Thumbnail, Token);
        using IDisposable foreground = await scheduler.AcquireAsync(DecodePriority.Foreground, Token).WaitAsync(Timeout, Token);
        first.Dispose();
        Assert.False(third.IsCompleted);
        foreground.Dispose();
        using IDisposable thirdLease = await third.WaitAsync(Timeout, Token);
        Task<IDisposable> fourth = scheduler.AcquireAsync(DecodePriority.Thumbnail, Token);
        Assert.False(fourth.IsCompleted);
        second.Dispose();
        using IDisposable fourthLease = await fourth.WaitAsync(Timeout, Token);
    }

    [Fact]
    public async Task CancellingQueuedForegroundPreventsItsWorkAndUnblocksRemainingQueues()
    {
        DecodeScheduler scheduler = new();
        using IDisposable active = await scheduler.AcquireAsync(DecodePriority.Detail, Token);
        using CancellationTokenSource cancellation = CancellationTokenSource.CreateLinkedTokenSource(Token);
        bool ran = false;
        Task<FakeResource> foreground = scheduler.RunAsync(DecodePriority.Foreground, () =>
        {
            ran = true;
            return new FakeResource();
        }, cancellation.Token);
        Task<IDisposable> detail = scheduler.AcquireAsync(DecodePriority.Detail, Token);
        Task<IDisposable> thumbnail = scheduler.AcquireAsync(DecodePriority.Thumbnail, Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => foreground.WaitAsync(Timeout, Token));
        Assert.False(ran);
        active.Dispose();
        using IDisposable detailLease = await detail.WaitAsync(Timeout, Token);
        Assert.False(thumbnail.IsCompleted);
        detailLease.Dispose();
        using IDisposable thumbnailLease = await thumbnail.WaitAsync(Timeout, Token);
    }

    [Fact]
    public async Task CancelledThumbnailDoesNotConsumeAReleasedSlot()
    {
        DecodeScheduler scheduler = new();
        using IDisposable first = await scheduler.AcquireAsync(DecodePriority.Thumbnail, Token);
        using IDisposable second = await scheduler.AcquireAsync(DecodePriority.Thumbnail, Token);
        using CancellationTokenSource cancellation = CancellationTokenSource.CreateLinkedTokenSource(Token);
        Task<IDisposable> cancelled = scheduler.AcquireAsync(DecodePriority.Thumbnail, cancellation.Token);
        Task<IDisposable> next = scheduler.AcquireAsync(DecodePriority.Thumbnail, Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled.WaitAsync(Timeout, Token));
        first.Dispose();
        using IDisposable lease = await next.WaitAsync(Timeout, Token);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task CancellingRunningWorkWaitsForNativeReturnAndDisposesLatePixels(int priorityValue)
    {
        DecodePriority priority = (DecodePriority)priorityValue;
        DecodeScheduler scheduler = new();
        using CancellationTokenSource cancellation = CancellationTokenSource.CreateLinkedTokenSource(Token);
        using ManualResetEventSlim finish = new();
        TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        PixelBuffer pixels = new(new PixelSize(1, 1), 4, new byte[4]);
        Task<PixelBuffer> running = scheduler.RunAsync(priority, () =>
        {
            started.SetResult();
            Assert.True(finish.Wait(Timeout, Token));
            return pixels;
        }, cancellation.Token);
        try
        {
            await started.Task.WaitAsync(Timeout, Token);
            cancellation.Cancel();
            Assert.False(running.IsCompleted);
            if (priority != DecodePriority.Thumbnail)
            {
                Assert.Null(scheduler.TryAcquirePrefetch(Token));
                Task<IDisposable> next = scheduler.AcquireAsync(DecodePriority.Foreground, Token);
                Assert.False(next.IsCompleted);
                finish.Set();
                using IDisposable nextLease = await next.WaitAsync(Timeout, Token);
            }
        }
        finally
        {
            finish.Set();
        }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running.WaitAsync(Timeout, Token));
        Assert.Throws<ObjectDisposedException>(() => pixels.Pixels);
        using IDisposable available = await scheduler.AcquireAsync(priority, Token).WaitAsync(Timeout, Token);
    }

    [Fact]
    public async Task CancelledRunningPrefetchHoldsSlotUntilReturnAndDisposesRegion()
    {
        DecodeScheduler scheduler = new();
        using CancellationTokenSource cancellation = CancellationTokenSource.CreateLinkedTokenSource(Token);
        using ManualResetEventSlim finish = new();
        TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        PixelBuffer pixels = new(new PixelSize(1, 1), 4, new byte[4]);
        DecodedImageRegion region = new(pixels, new PixelRect(0, 0, 1, 1));
        Task<DecodedImageRegion?> running = scheduler.TryRunPrefetchAsync(() =>
        {
            started.SetResult();
            Assert.True(finish.Wait(Timeout, Token));
            return region;
        }, cancellation.Token);
        try
        {
            await started.Task.WaitAsync(Timeout, Token);
            cancellation.Cancel();
            Assert.False(running.IsCompleted);
            Task<IDisposable> foreground = scheduler.AcquireAsync(DecodePriority.Foreground, Token);
            Task<IDisposable> thumbnail = scheduler.AcquireAsync(DecodePriority.Thumbnail, Token);
            Assert.False(foreground.IsCompleted);
            Assert.False(thumbnail.IsCompleted);
            Assert.Null(await scheduler.TryRunPrefetchAsync<FakeResource>(() => throw new InvalidOperationException(), Token));
            finish.Set();
            using IDisposable foregroundLease = await foreground.WaitAsync(Timeout, Token);
            Assert.False(thumbnail.IsCompleted);
            foregroundLease.Dispose();
            using IDisposable thumbnailLease = await thumbnail.WaitAsync(Timeout, Token);
        }
        finally
        {
            finish.Set();
        }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running.WaitAsync(Timeout, Token));
        Assert.Throws<ObjectDisposedException>(() => pixels.Pixels);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task FailedWorkReleasesItsSlot(int priorityValue)
    {
        DecodePriority priority = (DecodePriority)priorityValue;
        DecodeScheduler scheduler = new();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            scheduler.RunAsync<FakeResource>(priority, () => throw new InvalidOperationException(), Token));
        using IDisposable next = await scheduler.AcquireAsync(priority, Token).WaitAsync(Timeout, Token);
    }

    [Fact]
    public async Task FailedPrefetchReleasesTheOnlyMainSlot()
    {
        DecodeScheduler scheduler = new();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            scheduler.TryRunPrefetchAsync<FakeResource>(() => throw new InvalidOperationException(), Token));
        using IDisposable foreground = await scheduler.AcquireAsync(DecodePriority.Foreground, Token).WaitAsync(Timeout, Token);
    }

    [Fact]
    public async Task RepeatedCancellationAndIdempotentLeaseDisposalDoNotLeakOrDuplicateSlots()
    {
        DecodeScheduler scheduler = new();
        for (int i = 0; i < 200; i++)
        {
            using IDisposable active = await scheduler.AcquireAsync(DecodePriority.Detail, Token);
            using CancellationTokenSource cancellation = CancellationTokenSource.CreateLinkedTokenSource(Token);
            Task<IDisposable> stale = scheduler.AcquireAsync(DecodePriority.Foreground, cancellation.Token);
            Task<IDisposable> next = scheduler.AcquireAsync(DecodePriority.Foreground, Token);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => stale.WaitAsync(Timeout, Token));
            active.Dispose();
            active.Dispose();
            using IDisposable lease = await next.WaitAsync(Timeout, Token);
            Assert.Null(scheduler.TryAcquirePrefetch(Token));
        }
        using IDisposable? idle = scheduler.TryAcquirePrefetch(Token);
        Assert.NotNull(idle);
    }

    [Fact]
    public async Task WicMetadataGateAndDecodeLeasesShareTheSameMainSlot()
    {
        DecodeScheduler scheduler = new();
        scheduler.Wait(Token);
        Task<IDisposable> foreground = scheduler.AcquireAsync(DecodePriority.Foreground, Token);
        Assert.False(foreground.IsCompleted);
        Assert.False(await scheduler.WaitAsync(0, Token));
        scheduler.Release();
        using IDisposable lease = await foreground.WaitAsync(Timeout, Token);
        Assert.False(await scheduler.WaitAsync(0, Token));
        lease.Dispose();
        Assert.True(await scheduler.WaitAsync(0, Token));
        scheduler.Release();
    }

    private sealed class FakeResource : IDisposable
    {
        public void Dispose() { }
    }

    [Fact]
    public async Task SerializedNativeGateGivesForegroundAndDetailPriorityOverWaitingThumbnails()
    {
        DecodeScheduler scheduler = new(serializeThumbnails: true);
        using IDisposable active = await scheduler.AcquireAsync(DecodePriority.Thumbnail, Token);
        Task<IDisposable> thumbnail = scheduler.AcquireAsync(DecodePriority.Thumbnail, Token);
        Task<IDisposable> detail = scheduler.AcquireAsync(DecodePriority.Detail, Token);
        Task<IDisposable> foreground = scheduler.AcquireAsync(DecodePriority.Foreground, Token);
        Assert.False(thumbnail.IsCompleted);
        Assert.False(detail.IsCompleted);
        Assert.False(foreground.IsCompleted);
        active.Dispose();
        using IDisposable foregroundLease = await foreground.WaitAsync(Timeout, Token);
        Assert.False(detail.IsCompleted);
        Assert.False(thumbnail.IsCompleted);
        foregroundLease.Dispose();
        using IDisposable detailLease = await detail.WaitAsync(Timeout, Token);
        Assert.False(thumbnail.IsCompleted);
        detailLease.Dispose();
        using IDisposable thumbnailLease = await thumbnail.WaitAsync(Timeout, Token);
        Assert.Null(scheduler.TryAcquirePrefetch(Token));
    }
}
