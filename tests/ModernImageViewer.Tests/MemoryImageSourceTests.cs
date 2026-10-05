using ModernImageViewer.Application.Browsing;
using ModernImageViewer.Application.Images;
using ModernImageViewer.Imaging;

namespace ModernImageViewer.Tests;

public sealed class MemoryImageSourceTests
{
    [Fact]
    public async Task MemoryOpeningClearsNavigationAndRefinesWithoutAFileDecoder()
    {
        ImageBrowseSession session = new();
        FileDecoder decoder = new();
        using ImageOpenCoordinator coordinator = new(new NullPicker(), decoder);
        Assert.True(await coordinator.OpenCandidatesAsync([Path.GetFullPath("a.png"), Path.GetFullPath("b.png")], session, selection: true, cancellationToken: TestContext.Current.CancellationToken));
        PixelBuffer old = coordinator.State.Image!;
        PixelSize source = new(4, 3);
        List<PixelSize> targets = [];
        MemoryImageInput input = new(source, (target, budget, token) =>
        {
            targets.Add(target);
            PixelSize size = target == source ? source : new(2, 1);
            return Task.FromResult(new PixelBuffer(size, size.Width * 4, new byte[size.Width * size.Height * 4], sourceSize: source));
        });
        Assert.True(await coordinator.OpenMemoryAsync(input, session, TestContext.Current.CancellationToken));
        Assert.Throws<ObjectDisposedException>(() => old.Pixels);
        Assert.Empty(session.Items);
        Assert.Null(session.CurrentPath);
        Assert.False(session.CanSort);
        Assert.True(coordinator.State.IsMemorySource);
        Assert.Null(coordinator.State.FilePath);
        Guid identity = coordinator.State.Source!.Identity;
        PixelBuffer preview = coordinator.State.Image!;
        Assert.True(coordinator.State.IsPreview);
        Assert.True(await coordinator.RefineAsync(TestContext.Current.CancellationToken));
        Assert.Equal(source, coordinator.State.Image!.Size);
        Assert.Equal(identity, coordinator.State.Source!.Identity);
        Assert.False(coordinator.State.IsPreview);
        Assert.Throws<ObjectDisposedException>(() => preview.Pixels);
        Assert.Equal([ImageOpenCoordinator.PreviewMaximumSize, source], targets);
        Assert.Equal(1, decoder.Calls);
    }

    [Fact]
    public async Task InvalidSourceIsRejectedBeforeReadingAndKeepsImageAndNavigation()
    {
        ImageBrowseSession session = new();
        using ImageOpenCoordinator coordinator = new(new NullPicker(), new FileDecoder());
        Assert.True(await coordinator.OpenCandidatesAsync([Path.GetFullPath("a.png"), Path.GetFullPath("b.png")], session, selection: true, cancellationToken: TestContext.Current.CancellationToken));
        PixelBuffer previous = coordinator.State.Image!;
        MemoryImageInput oversized = new(new PixelSize(8192, 8192), (_, _, _) => throw new InvalidOperationException("Must not read oversized pixels"));
        Assert.False(await coordinator.OpenMemoryAsync(oversized, session, TestContext.Current.CancellationToken));
        Assert.Same(previous, coordinator.State.Image);
        Assert.Equal(ImageOpenError.ImageTooLarge, coordinator.State.Error);
        Assert.Equal(2, session.Count);
        MemoryImageInput malformed = new(new PixelSize(2, 2), (_, _, _) =>
            Task.FromResult(new PixelBuffer(new PixelSize(1, 1), 4, new byte[4], sourceSize: new PixelSize(3, 3))));
        Assert.False(await coordinator.OpenMemoryAsync(malformed, session, TestContext.Current.CancellationToken));
        Assert.Same(previous, coordinator.State.Image);
        Assert.False(coordinator.State.IsMemorySource);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LateMemoryDecodeIsReleasedAfterReplacementOrDisposal(bool dispose)
    {
        using ImageOpenCoordinator coordinator = new(new NullPicker(), new FileDecoder());
        TaskCompletionSource<PixelBuffer> pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        MemoryImageInput input = new(new PixelSize(2, 1), (_, _, _) => pending.Task);
        Task<bool> opening = coordinator.OpenMemoryAsync(input, cancellationToken: TestContext.Current.CancellationToken);
        if (dispose) { coordinator.Dispose(); }
        else { Assert.True(await coordinator.OpenAsync("latest.png", TestContext.Current.CancellationToken)); }
        PixelBuffer late = new(new PixelSize(2, 1), 8, new byte[8]);
        pending.SetResult(late);
        Assert.False(await opening);
        Assert.Throws<ObjectDisposedException>(() => late.Pixels);
        if (!dispose) { Assert.EndsWith("latest.png", coordinator.State.FilePath); }
    }

    private sealed class NullPicker : IImageFilePicker
    {
        public Task<string?> PickImageAsync(CancellationToken cancellationToken) => Task.FromResult<string?>(null);
    }

    private sealed class FileDecoder : IImageDecoder
    {
        public int Calls { get; private set; }
        public Task<PixelBuffer> DecodeAsync(string path, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new PixelBuffer(new PixelSize(1, 1), 4, new byte[4]));
        }
    }
}
