using System.Collections.Concurrent;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

using ModernImageViewer.Application.Images;
using ModernImageViewer.Codecs;
using ModernImageViewer.Codecs.Wic;
using ModernImageViewer.Imaging;

namespace ModernImageViewer.UI.Tests;

[Collection("Region decoder slot")]
public sealed class MemoryDecodeSchedulingTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task CancelledMemoryReadRetainsMainSlotAndDisposesLatePixelsBeforeFileOrMemoryAdmission()
    {
        using PngFixture file = new();
        ImageDecoder decoder = new();
        using CancellationTokenSource cancellation = CancellationTokenSource.CreateLinkedTokenSource(Token);
        TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<PixelBuffer> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        MemoryImageInput source = new(new(4, 4), (_, _, _) =>
        {
            started.SetResult();
            return completion.Task;
        });
        Task<PixelBuffer> running = decoder.ReadMemoryPixelsAsync(source, source.SourceSize, 64, true, cancellation.Token);
        await started.Task.WaitAsync(Timeout, Token);
        PixelBuffer late = Pixels(new(4, 4));
        MemoryImageInput next = new(new(1, 1), (_, _, _) =>
        {
            Assert.Throws<ObjectDisposedException>(() => late.Pixels);
            return Task.FromResult(Pixels(new(1, 1)));
        });
        Task<PixelBuffer> nextMemory = decoder.ReadMemoryPixelsAsync(next, next.SourceSize, 4, false, Token);
        Task<PixelBuffer> nextFile = decoder.DecodePreviewAsync(file.Path, new(4, 4), Token);
        cancellation.Cancel();
        Assert.False(running.IsCompleted);
        Assert.False(nextMemory.IsCompleted);
        Assert.False(nextFile.IsCompleted);
        Assert.Null(await decoder.TryDecodePreviewAsync(file.Path, new(4, 4), Token));
        completion.SetResult(late);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running.WaitAsync(Timeout, Token));
        using PixelBuffer memoryPixels = await nextMemory.WaitAsync(Timeout, Token);
        using PixelBuffer filePixels = await nextFile.WaitAsync(Timeout, Token);
        Assert.Equal(new PixelSize(4, 4), filePixels.Size);
        Assert.NotNull(filePixels.SourceFileStamp);
    }

    [Fact]
    public async Task QueuedCancelledMemoryReadNeverInvokesReaderOrLeaksSlot()
    {
        using PngFixture file = new();
        ImageDecoder decoder = new();
        using IDisposable active = await WicImageDecoder.DecodeSlot.AcquireAsync(DecodePriority.Foreground, Token);
        using CancellationTokenSource cancellation = CancellationTokenSource.CreateLinkedTokenSource(Token);
        bool invoked = false;
        MemoryImageInput source = new(new(1, 1), (_, _, _) =>
        {
            invoked = true;
            return Task.FromResult(Pixels(new(1, 1)));
        });
        Task<PixelBuffer> waiting = decoder.ReadMemoryPixelsAsync(source, source.SourceSize, 4, false, cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting.WaitAsync(Timeout, Token));
        Assert.False(invoked);
        active.Dispose();
        using PixelBuffer decoded = await decoder.DecodeAsync(file.Path, Token).WaitAsync(Timeout, Token);
    }

    [Fact]
    public async Task ForegroundMemoryReadOvertakesQueuedMemoryDetailAndForwardsBounds()
    {
        ImageDecoder decoder = new();
        using IDisposable active = await WicImageDecoder.DecodeSlot.AcquireAsync(DecodePriority.Foreground, Token);
        ConcurrentQueue<string> order = new();
        MemoryImageInput detailSource = new(new(4, 4), (size, budget, token) =>
        {
            Assert.Equal(new PixelSize(4, 4), size);
            Assert.Equal(64, budget);
            Assert.Equal(Token, token);
            order.Enqueue("detail");
            return Task.FromResult(Pixels(size));
        });
        MemoryImageInput previewSource = new(new(4, 4), (size, budget, _) =>
        {
            Assert.Equal(new PixelSize(2, 2), size);
            Assert.Equal(16, budget);
            order.Enqueue("preview");
            return Task.FromResult(Pixels(size, new(4, 4)));
        });
        Task<PixelBuffer> detail = decoder.ReadMemoryPixelsAsync(detailSource, new(4, 4), 64, true, Token);
        Task<PixelBuffer> preview = decoder.ReadMemoryPixelsAsync(previewSource, new(2, 2), 16, false, Token);
        Assert.Empty(order);
        active.Dispose();
        using PixelBuffer previewPixels = await preview.WaitAsync(Timeout, Token);
        using PixelBuffer detailPixels = await detail.WaitAsync(Timeout, Token);
        Assert.Collection(order, item => Assert.Equal("preview", item), item => Assert.Equal("detail", item));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SynchronousOrAsynchronousReaderFailureReleasesSlotForFile(bool asynchronous)
    {
        using PngFixture file = new();
        ImageDecoder decoder = new();
        MemoryImageInput source = new(new(1, 1), (_, _, _) => asynchronous
            ? Task.FromException<PixelBuffer>(new IOException("Reader failed."))
            : throw new IOException("Reader failed."));
        await Assert.ThrowsAsync<IOException>(() => decoder.ReadMemoryPixelsAsync(source, new(1, 1), 4, true, Token));
        using PixelBuffer decoded = await decoder.DecodeAsync(file.Path, Token).WaitAsync(Timeout, Token);
    }

    [Fact]
    public async Task SynchronousReaderWorkRunsOffCallerWithoutReleasingSlotEarly()
    {
        ImageDecoder decoder = new();
        using ManualResetEventSlim finish = new();
        TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        MemoryImageInput source = new(new(1, 1), (_, _, _) =>
        {
            started.SetResult();
            Assert.True(finish.Wait(Timeout, Token));
            return Task.FromResult(Pixels(new(1, 1)));
        });
        Task<PixelBuffer> running = decoder.ReadMemoryPixelsAsync(source, new(1, 1), 4, false, Token);
        try
        {
            await started.Task.WaitAsync(Timeout, Token);
            Assert.False(running.IsCompleted);
            Assert.Null(WicImageDecoder.DecodeSlot.TryAcquirePrefetch(Token));
        }
        finally
        {
            finish.Set();
        }
        using PixelBuffer pixels = await running.WaitAsync(Timeout, Token);
    }

    [Fact]
    public async Task CoordinatorMemoryOpenAndAdaptiveUpgradeWaitForSharedMainSlot()
    {
        ImageDecoder decoder = new();
        using ImageOpenCoordinator coordinator = new(new NullPicker(), decoder);
        int reads = 0;
        MemoryImageInput source = new(new(4, 4), (size, _, _) =>
        {
            Interlocked.Increment(ref reads);
            return Task.FromResult(Pixels(new(Math.Min(4, size.Width), Math.Min(4, size.Height)), sourceSize: new(4, 4)));
        });
        coordinator.SetPreviewTarget(new(1, 1));
        using IDisposable firstGate = await WicImageDecoder.DecodeSlot.AcquireAsync(DecodePriority.Foreground, Token);
        Task<bool> opening = coordinator.OpenMemoryAsync(source, cancellationToken: Token);
        Assert.Equal(0, Volatile.Read(ref reads));
        Assert.False(opening.IsCompleted);
        firstGate.Dispose();
        Assert.True(await opening.WaitAsync(Timeout, Token));
        PixelBuffer preview = coordinator.State.Image!;
        using IDisposable upgradeGate = await WicImageDecoder.DecodeSlot.AcquireAsync(DecodePriority.Foreground, Token);
        Task<bool> upgrading = coordinator.UpgradePreviewAsync(new(4, 4), cancellationToken: Token);
        Assert.Equal(1, Volatile.Read(ref reads));
        Assert.False(upgrading.IsCompleted);
        Assert.Same(preview, coordinator.State.Image);
        upgradeGate.Dispose();
        Assert.True(await upgrading.WaitAsync(Timeout, Token));
        Assert.Equal(2, Volatile.Read(ref reads));
        Assert.Throws<ObjectDisposedException>(() => preview.Pixels);
        Assert.Equal(new PixelSize(4, 4), coordinator.State.Image!.Size);
    }

    [Fact]
    public async Task CoordinatorWithoutMemoryDecoderKeepsReaderFallback()
    {
        using ImageOpenCoordinator coordinator = new(new NullPicker(), new FileOnlyDecoder());
        int reads = 0;
        MemoryImageInput source = new(new(4, 4), (size, _, _) =>
        {
            reads++;
            return Task.FromResult(Pixels(new(Math.Min(4, size.Width), Math.Min(4, size.Height)), new(4, 4)));
        });
        coordinator.SetPreviewTarget(new(1, 1));
        Assert.True(await coordinator.OpenMemoryAsync(source, cancellationToken: Token));
        Assert.True(await coordinator.UpgradePreviewAsync(new(4, 4), cancellationToken: Token));
        Assert.Equal(2, reads);
    }

    [Fact]
    public async Task SwitchingFromRunningMemoryDetailToFileWaitsForLateResultDisposal()
    {
        using PngFixture file = new();
        using ImageOpenCoordinator coordinator = new(new NullPicker(), new ImageDecoder());
        TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<PixelBuffer> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int reads = 0;
        CancellationToken detailToken = default;
        MemoryImageInput source = new(new(4, 4), (_, _, token) =>
        {
            if (Interlocked.Increment(ref reads) == 1)
            {
                return Task.FromResult(Pixels(new(1, 1), new(4, 4)));
            }
            detailToken = token;
            started.SetResult();
            return completion.Task;
        });
        coordinator.SetPreviewTarget(new(1, 1));
        Assert.True(await coordinator.OpenMemoryAsync(source, cancellationToken: Token));
        PixelBuffer preview = coordinator.State.Image!;
        Task<bool> refining = coordinator.RefineAsync(Token);
        await started.Task.WaitAsync(Timeout, Token);
        Task<bool> openingFile = coordinator.OpenAsync(file.Path, Token);
        Assert.True(detailToken.IsCancellationRequested);
        Assert.False(refining.IsCompleted);
        Assert.False(openingFile.IsCompleted);
        Assert.Same(preview, coordinator.State.Image);
        PixelBuffer late = Pixels(new(4, 4));
        completion.SetResult(late);
        Assert.False(await refining.WaitAsync(Timeout, Token));
        Assert.Throws<ObjectDisposedException>(() => late.Pixels);
        Assert.True(await openingFile.WaitAsync(Timeout, Token));
        Assert.Throws<ObjectDisposedException>(() => preview.Pixels);
        Assert.False(coordinator.State.IsMemorySource);
        Assert.Equal(file.Path, coordinator.State.FilePath);
        Assert.NotNull(coordinator.State.Image!.SourceFileStamp);
    }

    private static PixelBuffer Pixels(PixelSize size, PixelSize? sourceSize = null) =>
        new(size, size.Width * 4, new byte[checked((int)size.PixelCount * 4)], sourceSize: sourceSize);

    private sealed class NullPicker : IImageFilePicker
    {
        public Task<string?> PickImageAsync(CancellationToken cancellationToken) => Task.FromResult<string?>(null);
    }

    private sealed class FileOnlyDecoder : IImageDecoder
    {
        public Task<PixelBuffer> DecodeAsync(string path, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Memory fallback must not decode a file.");
    }

    private sealed class PngFixture : IDisposable
    {
        internal string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"viewer-memory-slot-{Guid.NewGuid():N}.png");

        internal PngFixture()
        {
            byte[] pixels = new byte[64];
            for (int i = 3; i < pixels.Length; i += 4) { pixels[i] = 255; }
            BitmapSource source = BitmapSource.Create(4, 4, 96, 96, PixelFormats.Pbgra32, null, pixels, 16);
            PngBitmapEncoder encoder = new();
            encoder.Frames.Add(BitmapFrame.Create(source));
            using FileStream stream = File.Create(Path);
            encoder.Save(stream);
        }

        public void Dispose() => File.Delete(Path);
    }
}
