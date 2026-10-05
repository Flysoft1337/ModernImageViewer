using ModernImageViewer.Application.Images;
using ModernImageViewer.Imaging;

namespace ModernImageViewer.Tests;

public sealed class AdaptiveImageTests
{
    [Fact]
    public async Task PreviewIsCommittedImmediatelyAndDetailIsDecodedOnlyOnRequest()
    {
        PreviewDecoder decoder = new();
        using ImageOpenCoordinator coordinator = new(new NullPicker(), decoder);
        Assert.True(await coordinator.OpenAsync("large.png", TestContext.Current.CancellationToken));
        PixelBuffer preview = coordinator.State.Image!;
        Assert.True(coordinator.State.IsPreview);
        Assert.Equal(new PixelSize(2560, 1600), decoder.MaximumSize);
        Assert.Empty(decoder.Details);

        Task<bool> refining = coordinator.RefineAsync(TestContext.Current.CancellationToken);
        Assert.True(coordinator.State.IsRefining);
        Assert.Equal(ImageOpenCoordinator.MainPixelBudgetBytes
            - 2 * ImageOpenCoordinator.PreviewMaximumSize.PixelCount * 4
            - NeighborPreviewCache.MaximumCachedBytes, decoder.DetailBudget);
        PixelBuffer full = Image(new PixelSize(2, 2));
        decoder.Details[0].SetResult(full);
        Assert.True(await refining);
        Assert.False(coordinator.State.IsPreview);
        Assert.False(coordinator.State.IsRefining);
        Assert.Same(full, coordinator.State.Image);
        Assert.Throws<ObjectDisposedException>(() => preview.Pixels);
        Assert.False(await coordinator.RefineAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task OverBudgetDetailKeepsPreviewWithoutStartingDecode()
    {
        PreviewDecoder decoder = new() { SourceSize = new PixelSize(10_000, 10_000) };
        using ImageOpenCoordinator coordinator = new(new NullPicker(), decoder);
        Assert.True(await coordinator.OpenAsync("large.png", TestContext.Current.CancellationToken));
        PixelBuffer preview = coordinator.State.Image!;
        Assert.False(await coordinator.RefineAsync(TestContext.Current.CancellationToken));
        Assert.Equal(ImageOpenStatus.Loaded, coordinator.State.Status);
        Assert.Equal(ImageOpenError.ImageTooLarge, coordinator.State.RefinementError);
        Assert.Same(preview, coordinator.State.Image);
        Assert.Equal(4, preview.Pixels.Length);
        Assert.Empty(decoder.Details);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LateDetailCannotReplaceNewImageOrSurviveDispose(bool dispose)
    {
        PreviewDecoder decoder = new();
        using ImageOpenCoordinator coordinator = new(new NullPicker(), decoder);
        Assert.True(await coordinator.OpenAsync("first.png", TestContext.Current.CancellationToken));
        Task<bool> refining = coordinator.RefineAsync(TestContext.Current.CancellationToken);
        PixelBuffer? newer = null;
        if (dispose)
        {
            coordinator.Dispose();
        }
        else
        {
            Assert.True(await coordinator.OpenAsync("next.png", TestContext.Current.CancellationToken));
            newer = coordinator.State.Image;
        }
        PixelBuffer stale = Image(new PixelSize(2, 2));
        decoder.Details[0].SetResult(stale);
        Assert.False(await refining);
        Assert.Throws<ObjectDisposedException>(() => stale.Pixels);
        if (!dispose)
        {
            Assert.Same(newer, coordinator.State.Image);
            Assert.True(coordinator.State.IsPreview);
            Assert.False(coordinator.State.IsRefining);
        }
    }

    [Fact]
    public async Task CancelledDetailAndFailedDetailKeepLivePreview()
    {
        PreviewDecoder decoder = new();
        using ImageOpenCoordinator coordinator = new(new NullPicker(), decoder);
        Assert.True(await coordinator.OpenAsync("image.png", TestContext.Current.CancellationToken));
        PixelBuffer preview = coordinator.State.Image!;
        using CancellationTokenSource cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        Task<bool> cancelled = coordinator.RefineAsync(cancellation.Token);
        cancellation.Cancel();
        PixelBuffer late = Image(new PixelSize(2, 2));
        decoder.Details[0].SetResult(late);
        Assert.False(await cancelled);
        Assert.False(coordinator.State.IsRefining);
        Assert.Throws<ObjectDisposedException>(() => late.Pixels);
        Assert.Same(preview, coordinator.State.Image);

        Task<bool> failed = coordinator.RefineAsync(TestContext.Current.CancellationToken);
        decoder.Details[1].SetException(new ImageDecodeException(ImageOpenError.CorruptFile));
        Assert.False(await failed);
        Assert.Equal(ImageOpenStatus.Loaded, coordinator.State.Status);
        Assert.True(coordinator.State.IsPreview);
        Assert.Equal(ImageOpenError.CorruptFile, coordinator.State.RefinementError);
        Assert.Equal(4, preview.Pixels.Length);
    }

    [Fact]
    public async Task SmallImageDoesNotRequireDetail()
    {
        PreviewDecoder decoder = new() { SourceSize = new PixelSize(1, 1) };
        using ImageOpenCoordinator coordinator = new(new NullPicker(), decoder);
        Assert.True(await coordinator.OpenAsync("small.png", TestContext.Current.CancellationToken));
        Assert.False(coordinator.State.IsPreview);
        Assert.False(await coordinator.RefineAsync(TestContext.Current.CancellationToken));
        Assert.Empty(decoder.Details);
    }

    private static PixelBuffer Image(PixelSize size, PixelSize? source = null) =>
        new(size, size.Width * 4, new byte[checked((int)size.PixelCount * 4)], sourceSize: source);

    private sealed class NullPicker : IImageFilePicker
    {
        public Task<string?> PickImageAsync(CancellationToken cancellationToken) => Task.FromResult<string?>(null);
    }

    private sealed class PreviewDecoder : IPreviewImageDecoder
    {
        public PixelSize SourceSize { get; init; } = new(2, 2);
        public PixelSize MaximumSize { get; private set; }
        public long DetailBudget { get; private set; }
        public List<TaskCompletionSource<PixelBuffer>> Details { get; } = [];

        public Task<PixelBuffer> DecodePreviewAsync(string path, PixelSize maximumSize, CancellationToken cancellationToken)
        {
            MaximumSize = maximumSize;
            return Task.FromResult(Image(new PixelSize(1, 1), SourceSize));
        }

        public Task<PixelBuffer> DecodeDetailAsync(string path, long maximumDecodedBytes, CancellationToken cancellationToken)
        {
            DetailBudget = maximumDecodedBytes;
            return DecodeAsync(path, cancellationToken);
        }

        public Task<PixelBuffer> DecodeAsync(string path, CancellationToken cancellationToken)
        {
            // Ignore cancellation deliberately to exercise a native decoder returning late.
            TaskCompletionSource<PixelBuffer> pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Details.Add(pending);
            return pending.Task;
        }
    }
}
