using ModernImageViewer.Application.Images;
using ModernImageViewer.Imaging;

namespace ModernImageViewer.Tests;

public sealed class ImageOpenCoordinatorTests
{
    [Fact]
    public async Task SupersededFailureDoesNotEscapeOrReplaceNewImage()
    {
        ControlledDecoder decoder = new();
        using ImageOpenCoordinator coordinator = new(new NullFilePicker(), decoder);
        Task<bool> first = coordinator.OpenAsync("first.png", TestContext.Current.CancellationToken);
        Task<bool> second = coordinator.OpenAsync("second.png", TestContext.Current.CancellationToken);
        PixelBuffer image = CreateImage();
        decoder.Requests[1].SetResult(image);
        Assert.True(await second);

        decoder.Requests[0].SetException(new ImageDecodeException(ImageOpenError.CorruptFile));

        Assert.False(await first);
        Assert.Equal(ImageOpenStatus.Loaded, coordinator.State.Status);
        Assert.Equal(Path.GetFullPath("second.png"), coordinator.State.FilePath);
        Assert.Same(image, coordinator.State.Image);
    }

    [Fact]
    public async Task SupersededSuccessDisposesItsBuffer()
    {
        ControlledDecoder decoder = new();
        using ImageOpenCoordinator coordinator = new(new NullFilePicker(), decoder);
        Task<bool> first = coordinator.OpenAsync("first.png", TestContext.Current.CancellationToken);
        Task<bool> second = coordinator.OpenAsync("second.png", TestContext.Current.CancellationToken);
        PixelBuffer staleImage = CreateImage();
        decoder.Requests[0].SetResult(staleImage);

        Assert.False(await first);
        Assert.Throws<ObjectDisposedException>(() => staleImage.Pixels);
        Assert.Equal(ImageOpenStatus.Loading, coordinator.State.Status);

        decoder.Requests[1].SetResult(CreateImage());
        Assert.True(await second);
    }

    [Fact]
    public async Task CurrentFailurePreservesPreviousImage()
    {
        ControlledDecoder decoder = new();
        using ImageOpenCoordinator coordinator = new(new NullFilePicker(), decoder);
        Task<bool> first = coordinator.OpenAsync("first.png", TestContext.Current.CancellationToken);
        PixelBuffer image = CreateImage();
        decoder.Requests[0].SetResult(image);
        Assert.True(await first);
        Task<bool> second = coordinator.OpenAsync("second.png", TestContext.Current.CancellationToken);
        decoder.Requests[1].SetException(new FileNotFoundException());

        Assert.False(await second);
        Assert.Equal(ImageOpenStatus.Error, coordinator.State.Status);
        Assert.Equal(ImageOpenError.FileNotFound, coordinator.State.Error);
        Assert.Equal(Path.GetFullPath("first.png"), coordinator.State.FilePath);
        Assert.Same(image, coordinator.State.Image);
        Assert.Equal(4, image.Pixels.Length);
    }

    [Fact]
    public async Task CancelledRequestRestoresPreviousImage()
    {
        ControlledDecoder decoder = new();
        using ImageOpenCoordinator coordinator = new(new NullFilePicker(), decoder);
        Task<bool> first = coordinator.OpenAsync("first.png", TestContext.Current.CancellationToken);
        PixelBuffer image = CreateImage();
        decoder.Requests[0].SetResult(image);
        Assert.True(await first);
        using CancellationTokenSource cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        Task<bool> second = coordinator.OpenAsync("second.png", cancellation.Token);
        cancellation.Cancel();
        decoder.Requests[1].SetCanceled(cancellation.Token);

        Assert.False(await second);
        Assert.Equal(ImageOpenStatus.Loaded, coordinator.State.Status);
        Assert.Same(image, coordinator.State.Image);
        Assert.Equal(4, image.Pixels.Length);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DisposedCoordinatorIgnoresPendingCompletion(bool fails)
    {
        ControlledDecoder decoder = new();
        ImageOpenCoordinator coordinator = new(new NullFilePicker(), decoder);
        Task<bool> pending = coordinator.OpenAsync("pending.png", TestContext.Current.CancellationToken);
        int changes = 0;
        coordinator.PropertyChanged += (_, _) => changes++;
        coordinator.Dispose();
        PixelBuffer image = CreateImage();

        if (fails)
        {
            image.Dispose();
            decoder.Requests[0].SetException(new ImageDecodeException(ImageOpenError.CorruptFile));
        }
        else
        {
            decoder.Requests[0].SetResult(image);
        }

        Assert.False(await pending);
        Assert.Equal(0, changes);
        Assert.Throws<ObjectDisposedException>(() => image.Pixels);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => coordinator.OpenAsync("new.png", TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => coordinator.PickAndOpenAsync(TestContext.Current.CancellationToken));
        coordinator.Dispose();
    }

    private static PixelBuffer CreateImage() => new(new PixelSize(1, 1), 4, new byte[4]);

    private sealed class NullFilePicker : IImageFilePicker
    {
        public Task<string?> PickImageAsync(CancellationToken cancellationToken) => Task.FromResult<string?>(null);
    }

    private sealed class ControlledDecoder : IImageDecoder
    {
        public List<TaskCompletionSource<PixelBuffer>> Requests { get; } = [];

        public Task<PixelBuffer> DecodeAsync(string path, CancellationToken cancellationToken)
        {
            // Deliberately ignore cancellation to exercise codecs that complete late.
            TaskCompletionSource<PixelBuffer> request = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Requests.Add(request);
            return request.Task;
        }
    }
}
