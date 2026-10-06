using ModernImageViewer.Application.Images;
using ModernImageViewer.Imaging;

namespace ModernImageViewer.Tests;

public sealed class BrowsingLifecycleTests
{
    [Fact]
    public void PrefetchPromotionDoesNotUseWindowResizeHysteresis()
    {
        using PixelBuffer preview = Buffer(new(380, 304));
        Assert.False(PreviewDecodePolicy.NeedsUpgrade(preview, new(400, 320)));
        Assert.True(PreviewDecodePolicy.NeedsUpgrade(preview, new(400, 320), initialPrefetch: true));
        Assert.False(PreviewDecodePolicy.NeedsUpgrade(preview, new(300, 240), initialPrefetch: true));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(1.25)]
    [InlineData(1.5)]
    [InlineData(2)]
    public void PreviewTargetsUsePhysicalPixelsWithExplicitBounds(double dpi)
    {
        PixelSize target = PreviewDecodePolicy.CalculateTarget(800, 600, dpi, dpi);
        Assert.Equal(new PixelSize((int)(800 * dpi), (int)(600 * dpi)), target);
        PixelSize large = PreviewDecodePolicy.CalculateTarget(8000, 6000, dpi, dpi);
        Assert.True(large.Width <= PreviewDecodePolicy.MaximumEdge);
        Assert.True(large.Height <= PreviewDecodePolicy.MaximumEdge);
        Assert.True(large.PixelCount * 4 <= PreviewDecodePolicy.MaximumBytes);
        Assert.Equal(new PixelSize(30, 20), PreviewDecodePolicy.FitSource(new(30, 20), target));
        Assert.Equal(new PixelSize(3840, 2160), PreviewDecodePolicy.CalculateTarget(1920, 1080, 2, 2));
    }

    [Theory]
    [InlineData(double.NaN, 600, 1)]
    [InlineData(0, 600, 1)]
    [InlineData(800, 600, double.PositiveInfinity)]
    public void InvalidLayoutFallsBackWithoutUnboundedAllocation(double width, double height, double dpi) =>
        Assert.Equal(PreviewDecodePolicy.FallbackTarget, PreviewDecodePolicy.CalculateTarget(width, height, dpi, dpi));

    [Fact]
    public async Task UpgradeKeepsOldPixelsAndSourceUntilSuccessfulReplacement()
    {
        DelayedDecoder decoder = new();
        using ImageOpenCoordinator coordinator = new(new NullPicker(), decoder);
        coordinator.SetPreviewTarget(new(100, 80));
        Task<bool> opening = coordinator.OpenAsync("sample.png", TestContext.Current.CancellationToken);
        PixelBuffer first = Buffer(new(100, 80));
        decoder.CompletePreview(0, first);
        Assert.True(await opening);
        Guid identity = coordinator.State.Source!.Identity;
        Task<bool> upgrading = coordinator.UpgradePreviewAsync(new(800, 600), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Same(first, coordinator.State.Image);
        Assert.Equal(ImageOpenStatus.Loaded, coordinator.State.Status);
        Assert.Equal(new PixelSize(800, 600), decoder.Targets[1]);
        PixelBuffer replacement = Buffer(new(750, 600));
        decoder.CompletePreview(1, replacement);
        Assert.True(await upgrading);
        Assert.Equal(identity, coordinator.State.Source!.Identity);
        Assert.Same(replacement, coordinator.State.Image);
        AssertDisposed(first);
        Assert.False(await coordinator.UpgradePreviewAsync(new(850, 620), cancellationToken: TestContext.Current.CancellationToken));
        Assert.False(await coordinator.UpgradePreviewAsync(new(200, 160), cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(2, decoder.Previews.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UpgradeResultCannotSurviveNewOpenOrDispose(bool dispose)
    {
        DelayedDecoder decoder = new();
        using ImageOpenCoordinator coordinator = new(new NullPicker(), decoder);
        Task<bool> first = coordinator.OpenAsync("first.png", TestContext.Current.CancellationToken);
        decoder.CompletePreview(0, Buffer(new(100, 80)));
        Assert.True(await first);
        Task<bool> upgrade = coordinator.UpgradePreviewAsync(new(800, 600), cancellationToken: TestContext.Current.CancellationToken);
        if (dispose) { coordinator.Dispose(); }
        else
        {
            Task<bool> next = coordinator.OpenAsync("next.png", TestContext.Current.CancellationToken);
            decoder.CompletePreview(2, Buffer(new(50, 40)));
            Assert.True(await next);
        }
        PixelBuffer late = Buffer(new(750, 600));
        decoder.CompletePreview(1, late);
        Assert.False(await upgrade);
        AssertDisposed(late);
        if (!dispose) { Assert.EndsWith("next.png", coordinator.State.FilePath); }
    }

    [Fact]
    public async Task RapidOpensCommitOnlyLatestAndReleaseEveryLateBuffer()
    {
        DelayedDecoder decoder = new();
        using ImageOpenCoordinator coordinator = new(new NullPicker(), decoder);
        Task<bool>[] opens = Enumerable.Range(0, 80).Select(index => coordinator.OpenAsync($"image-{index}.png")).ToArray();
        PixelBuffer[] pixels = Enumerable.Range(0, 80).Select(_ => Buffer(new(1, 1))).ToArray();
        decoder.CompletePreview(79, pixels[79]);
        Assert.True(await opens[79]);
        for (int index = 78; index >= 0; index--)
        {
            Assert.True(decoder.Tokens[index].IsCancellationRequested);
            decoder.CompletePreview(index, pixels[index]);
            Assert.False(await opens[index]);
            AssertDisposed(pixels[index]);
        }
        Assert.Same(pixels[79], coordinator.State.Image);
        coordinator.Dispose();
        AssertDisposed(pixels[79]);
    }

    [Fact]
    public async Task CacheInvalidationRejectsInFlightDetailAndRegion()
    {
        DelayedDecoder decoder = new();
        using ImageOpenCoordinator coordinator = new(new NullPicker(), decoder);
        Task<bool> first = coordinator.OpenAsync("small.png", TestContext.Current.CancellationToken);
        decoder.CompletePreview(0, Buffer(new(1, 1)));
        Assert.True(await first);
        Task<bool> detail = coordinator.RefineAsync(TestContext.Current.CancellationToken);
        coordinator.ClearPreviewCache();
        PixelBuffer staleDetail = Buffer(new(2, 2));
        decoder.Detail.SetResult(staleDetail);
        Assert.False(await detail);
        AssertDisposed(staleDetail);

        Task<bool> large = coordinator.OpenAsync("large.png", TestContext.Current.CancellationToken);
        decoder.CompletePreview(1, Buffer(new(1, 1), new(10000, 10000)));
        Assert.True(await large);
        PixelRect bounds = new(0, 0, 2, 2);
        Task<bool> region = coordinator.RequestRegionAsync(bounds, TestContext.Current.CancellationToken);
        coordinator.CancelPendingOpen();
        coordinator.ClearPreviewCache();
        PixelBuffer staleRegion = Buffer(bounds.Size, new(10000, 10000));
        decoder.Region.SetResult(new(staleRegion, bounds));
        Assert.False(await region);
        AssertDisposed(staleRegion);
        Assert.Null(coordinator.State.Region);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ChangedSourceStampRejectsDetailOrPreviewUpgrade(bool upgrade)
    {
        DelayedDecoder decoder = new();
        using ImageOpenCoordinator coordinator = new(new NullPicker(), decoder);
        Task<bool> first = coordinator.OpenAsync("sample.png", TestContext.Current.CancellationToken);
        ImageFileStamp originalStamp = new(100, DateTime.UnixEpoch);
        PixelBuffer firstPixels = Buffer(new(100, 80), stamp: originalStamp);
        decoder.CompletePreview(0, firstPixels);
        Assert.True(await first);
        Task<bool> work = upgrade ? coordinator.UpgradePreviewAsync(new(800, 600), cancellationToken: TestContext.Current.CancellationToken)
            : coordinator.RefineAsync(TestContext.Current.CancellationToken);
        PixelBuffer changed = Buffer(new(750, 600), stamp: originalStamp with { Length = 101 });
        if (upgrade) { decoder.CompletePreview(1, changed); } else { decoder.Detail.SetResult(changed); }
        Assert.False(await work);
        Assert.Same(firstPixels, coordinator.State.Image);
        AssertDisposed(changed);
    }

    [Fact]
    public async Task HundredsOfAlternatingImagesRetainOnlyCurrentBufferWithoutCollecting()
    {
        List<PixelBuffer> produced = [];
        ImmediateDecoder decoder = new(produced);
        using ImageOpenCoordinator coordinator = new(new NullPicker(), decoder);
        for (int index = 0; index < 500; index++)
        {
            Assert.True(await coordinator.OpenAsync($"sample-{index}.png", TestContext.Current.CancellationToken));
            if (index > 0) { AssertDisposed(produced[index - 1]); }
            Assert.NotEmpty(coordinator.State.Image!.Pixels.ToArray());
        }
        coordinator.Dispose();
        Assert.All(produced, AssertDisposed);
    }

    private static PixelBuffer Buffer(PixelSize size, PixelSize? source = null, ImageFileStamp? stamp = null) =>
        new(size, size.Width * 4, new byte[checked((int)size.PixelCount * 4)], sourceSize: source ?? new(1000, 800), sourceFileStamp: stamp);
    private static void AssertDisposed(PixelBuffer pixels) => Assert.Throws<ObjectDisposedException>(() => pixels.Pixels);
    private sealed class NullPicker : IImageFilePicker
    {
        public Task<string?> PickImageAsync(CancellationToken cancellationToken) => Task.FromResult<string?>(null);
    }
    private sealed class DelayedDecoder : IPreviewImageDecoder, IRegionImageDecoder
    {
        public List<TaskCompletionSource<PixelBuffer>> Previews { get; } = [];
        public List<PixelSize> Targets { get; } = [];
        public List<CancellationToken> Tokens { get; } = [];
        public TaskCompletionSource<PixelBuffer> Detail { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<DecodedImageRegion> Region { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<PixelBuffer> DecodeAsync(string path, CancellationToken cancellationToken) => Detail.Task;
        public Task<PixelBuffer> DecodePreviewAsync(string path, PixelSize maximumSize, CancellationToken cancellationToken)
        {
            Targets.Add(maximumSize);
            Tokens.Add(cancellationToken);
            TaskCompletionSource<PixelBuffer> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Previews.Add(completion);
            return completion.Task;
        }
        public void CompletePreview(int index, PixelBuffer pixels) => Previews[index].SetResult(pixels);
        public Task<DecodedImageRegion> DecodeRegionAsync(string path, PixelRect region, PixelSize expectedSourceSize,
            long maximumDecodedBytes, CancellationToken cancellationToken) => Region.Task;
    }
    private sealed class ImmediateDecoder(List<PixelBuffer> produced) : IPreviewImageDecoder
    {
        public Task<PixelBuffer> DecodeAsync(string path, CancellationToken cancellationToken) => DecodePreviewAsync(path, new(64, 64), cancellationToken);
        public Task<PixelBuffer> DecodePreviewAsync(string path, PixelSize maximumSize, CancellationToken cancellationToken)
        {
            PixelSize size = produced.Count % 2 == 0 ? new(1, 1) : new(64, 64);
            PixelBuffer pixels = Buffer(size, size);
            produced.Add(pixels);
            return Task.FromResult(pixels);
        }
    }
}
