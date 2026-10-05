using ModernImageViewer.Application.Images;
using ModernImageViewer.Imaging;

namespace ModernImageViewer.Tests;

public sealed class RegionDetailTests
{
    [Fact]
    public async Task ReturningToRetainedRegionCancelsPendingRegionAndDisposesLatePixels()
    {
        RegionDecoder decoder = new();
        using ImageOpenCoordinator coordinator = new(new NullPicker(), decoder);
        Assert.True(await coordinator.OpenAsync("large.png", TestContext.Current.CancellationToken));
        PixelBuffer preview = coordinator.State.Image!;
        PixelRect first = new(100, 200, 4, 3);
        Task<bool> loading = coordinator.RequestRegionAsync(first, TestContext.Current.CancellationToken);
        DecodedImageRegion original = decoder.Complete(0, first);
        Assert.True(await loading);
        Assert.Equal(ImageOpenCoordinator.MaximumRegionBytes, decoder.LastBudget);
        PixelRect next = new(200, 300, 3, 4);
        Task<bool> pending = coordinator.RequestRegionAsync(next, TestContext.Current.CancellationToken);
        Assert.True(coordinator.State.IsRegionLoading);
        Assert.True(await coordinator.RequestRegionAsync(first, TestContext.Current.CancellationToken));
        Assert.True(decoder.Cancellations[1].IsCancellationRequested);
        DecodedImageRegion stale = decoder.Complete(1, next);
        Assert.False(await pending);
        Assert.Throws<ObjectDisposedException>(() => stale.Image.Pixels);
        Assert.Same(original, coordinator.State.Region);
        Assert.Same(preview, coordinator.State.Image);
        Assert.False(coordinator.State.IsRegionLoading);
        coordinator.ClearPreviewCache();
        Assert.Null(coordinator.State.Region);
        Assert.Throws<ObjectDisposedException>(() => original.Image.Pixels);
        Assert.Equal(4, preview.Pixels.Length);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LateRegionCannotReplaceNewImageOrSurviveDispose(bool dispose)
    {
        RegionDecoder decoder = new();
        using ImageOpenCoordinator coordinator = new(new NullPicker(), decoder);
        Assert.True(await coordinator.OpenAsync("first.png", TestContext.Current.CancellationToken));
        PixelRect bounds = new(10, 20, 3, 4);
        Task<bool> pending = coordinator.RequestRegionAsync(bounds, TestContext.Current.CancellationToken);
        if (dispose)
        {
            coordinator.Dispose();
        }
        else
        {
            Assert.True(await coordinator.OpenAsync("next.png", TestContext.Current.CancellationToken));
        }
        DecodedImageRegion late = decoder.Complete(0, bounds);
        Assert.False(await pending);
        Assert.Throws<ObjectDisposedException>(() => late.Image.Pixels);
        if (!dispose)
        {
            Assert.Null(coordinator.State.Region);
            Assert.EndsWith("next.png", coordinator.State.FilePath, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task SourceMismatchAndDecodeFailureKeepPreviewAndExistingDetail()
    {
        RegionDecoder decoder = new();
        using ImageOpenCoordinator coordinator = new(new NullPicker(), decoder);
        Assert.True(await coordinator.OpenAsync("large.png", TestContext.Current.CancellationToken));
        PixelRect first = new(20, 30, 4, 3);
        Task<bool> pending = coordinator.RequestRegionAsync(first, TestContext.Current.CancellationToken);
        DecodedImageRegion retained = decoder.Complete(0, first);
        Assert.True(await pending);
        PixelRect next = new(50, 60, 4, 3);
        pending = coordinator.RequestRegionAsync(next, TestContext.Current.CancellationToken);
        DecodedImageRegion mismatched = decoder.Complete(1, next, new PixelSize(9_999, 10_000));
        Assert.False(await pending);
        Assert.Throws<ObjectDisposedException>(() => mismatched.Image.Pixels);
        Assert.Same(retained, coordinator.State.Region);
        pending = coordinator.RequestRegionAsync(next, TestContext.Current.CancellationToken);
        decoder.Pending[2].SetException(new ImageDecodeException(ImageOpenError.UnsupportedFormat));
        Assert.False(await pending);
        Assert.Equal(ImageOpenError.UnsupportedFormat, coordinator.State.RefinementError);
        Assert.True(coordinator.State.IsPreview);
        Assert.Equal(ImageOpenStatus.Loaded, coordinator.State.Status);
        Assert.Same(retained, coordinator.State.Region);
    }

    private sealed class NullPicker : IImageFilePicker
    {
        public Task<string?> PickImageAsync(CancellationToken cancellationToken) => Task.FromResult<string?>(null);
    }

    private sealed class RegionDecoder : IPreviewImageDecoder, IRegionImageDecoder
    {
        private static readonly PixelSize SourceSize = new(10_000, 10_000);
        public List<TaskCompletionSource<DecodedImageRegion>> Pending { get; } = [];
        public List<CancellationToken> Cancellations { get; } = [];
        public long LastBudget { get; private set; }

        public Task<PixelBuffer> DecodeAsync(string path, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("A large image must not allocate full pixels.");

        public Task<PixelBuffer> DecodePreviewAsync(string path, PixelSize maximumSize, CancellationToken cancellationToken) =>
            Task.FromResult(new PixelBuffer(new PixelSize(1, 1), 4, new byte[4], sourceSize: SourceSize));

        public Task<DecodedImageRegion> DecodeRegionAsync(string path, PixelRect region, PixelSize expectedSourceSize,
            long maximumDecodedBytes, CancellationToken cancellationToken)
        {
            Assert.Equal(SourceSize, expectedSourceSize);
            LastBudget = maximumDecodedBytes;
            Cancellations.Add(cancellationToken);
            TaskCompletionSource<DecodedImageRegion> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Pending.Add(completion);
            return completion.Task;
        }

        public DecodedImageRegion Complete(int index, PixelRect bounds, PixelSize? sourceSize = null)
        {
            DecodedImageRegion region = new(new PixelBuffer(bounds.Size, bounds.Width * 4,
                new byte[checked((int)bounds.Size.PixelCount * 4)], sourceSize: sourceSize ?? SourceSize), bounds);
            Pending[index].SetResult(region);
            return region;
        }
    }
}
