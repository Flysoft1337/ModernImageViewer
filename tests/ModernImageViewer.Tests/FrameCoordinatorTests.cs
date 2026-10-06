using ModernImageViewer.Application.Browsing;
using ModernImageViewer.Application.Images;
using ModernImageViewer.Imaging;

namespace ModernImageViewer.Tests;

public sealed class FrameCoordinatorTests
{
    [Fact]
    public async Task StaticImageUsesNoFrameSessionOrFrameDecode()
    {
        Decoder decoder = new((IImageFrameSession?)null);
        using ImageOpenCoordinator coordinator = new(new Picker(), decoder);
        Assert.True(await coordinator.OpenAsync("static.png", TestContext.Current.CancellationToken));
        Assert.False(coordinator.HasFrameSession);
        Assert.Null(coordinator.State.Sequence);
        Assert.Equal(0, coordinator.RetainedFrameBytes);
        Assert.Equal(1, decoder.StaticCalls);
        Assert.False(await coordinator.PresentFrameAsync(0, TestContext.Current.CancellationToken));
        Assert.Equal((byte)77, coordinator.State.Image!.Pixels.Span[2]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FileSwitchOrRefreshInvalidatesFramesBeforeNewPixelsAndReleasesLateResult(bool refresh)
    {
        Session oldSession = new();
        Session newSession = new();
        Decoder decoder = new(oldSession, newSession);
        using ImageOpenCoordinator coordinator = new(new Picker(), decoder);
        await OpenFirst(coordinator, oldSession);
        PixelBuffer first = coordinator.State.Image!;
        Task<bool> lateFrame = coordinator.PresentFrameAsync(1, TestContext.Current.CancellationToken);
        Task<bool> next = coordinator.OpenAsync(refresh ? "frames.gif" : "next.gif", TestContext.Current.CancellationToken);
        Assert.True(oldSession.Disposed);
        Assert.True(oldSession.Requests[1].Token.IsCancellationRequested);
        Assert.False(coordinator.HasFrameSession);
        Assert.Equal(0, coordinator.RetainedFrameBytes);
        Assert.Equal(ImageOpenStatus.Loading, coordinator.State.Status);
        PixelBuffer late = oldSession.Requests[1].Complete(99);
        Assert.False(await lateFrame);
        AssertReleased(late);
        Assert.Same(first, coordinator.State.Image);
        Assert.Equal(0, coordinator.State.FrameIndex);
        newSession.Requests[0].Complete(33);
        Assert.True(await next);
        AssertReleased(first);
        Assert.Equal((byte)33, coordinator.State.Image!.Pixels.Span[2]);
    }

    [Fact]
    public async Task RapidSeekCommitsLatestRequestEvenWhenOldDecodeFinishesLast()
    {
        Session session = new();
        using ImageOpenCoordinator coordinator = new(new Picker(), new Decoder(session));
        await OpenFirst(coordinator, session);
        PixelBuffer first = coordinator.State.Image!;
        Guid identity = coordinator.State.Source!.Identity;
        long version = coordinator.RequestVersion;
        Task<bool> old = coordinator.PresentFrameAsync(1, TestContext.Current.CancellationToken);
        Task<bool> latest = coordinator.PresentFrameAsync(2, TestContext.Current.CancellationToken);
        Assert.True(session.Requests[1].Token.IsCancellationRequested);
        PixelBuffer winner = session.Requests[2].Complete(42);
        Assert.True(await latest);
        AssertReleased(first);
        PixelBuffer stale = session.Requests[1].Complete(91);
        Assert.False(await old);
        AssertReleased(stale);
        Assert.Same(winner, coordinator.State.Image);
        Assert.Equal(2, coordinator.State.FrameIndex);
        Assert.Equal((byte)42, winner.Pixels.Span[2]);
        Assert.Equal(identity, coordinator.State.Source!.Identity);
        Assert.Equal(version, coordinator.RequestVersion);
    }

    [Fact]
    public async Task NewAnimationWhileFirstFrameDecodesReleasesUncommittedSessionAndPixels()
    {
        Session first = new();
        Session second = new();
        using ImageOpenCoordinator coordinator = new(new Picker(), new Decoder(first, second));
        Task<bool> oldOpen = coordinator.OpenAsync("first.gif", TestContext.Current.CancellationToken);
        Task<bool> newOpen = coordinator.OpenAsync("second.gif", TestContext.Current.CancellationToken);
        second.Requests[0].Complete(52);
        Assert.True(await newOpen);
        Guid identity = coordinator.State.Source!.Identity;
        PixelBuffer stale = first.Requests[0].Complete(19);
        Assert.False(await oldOpen);
        Assert.True(first.Disposed);
        AssertReleased(stale);
        Assert.Equal(identity, coordinator.State.Source!.Identity);
        Assert.Equal(Path.GetFullPath("second.gif"), coordinator.State.FilePath);
        Assert.Equal((byte)52, coordinator.State.Image!.Pixels.Span[2]);
    }

    [Fact]
    public async Task LateNativeSessionAfterSwitchIsDisposedWithoutCommit()
    {
        Session native = new();
        Session next = new();
        TaskCompletionSource<IImageFrameSession?> nativeOpen = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Decoder decoder = new(next) { PendingOpen = nativeOpen };
        using ImageOpenCoordinator coordinator = new(new Picker(), decoder);
        Task<bool> old = coordinator.OpenAsync("native.gif", TestContext.Current.CancellationToken);
        Task<bool> latest = coordinator.OpenAsync("latest.gif", TestContext.Current.CancellationToken);
        next.Requests[0].Complete(63);
        Assert.True(await latest);
        nativeOpen.SetResult(native);
        Request request = await native.Started.Task.WaitAsync(TestContext.Current.CancellationToken);
        Assert.True(request.Token.IsCancellationRequested);
        PixelBuffer stale = request.Complete(7);
        Assert.False(await old);
        Assert.True(native.Disposed);
        AssertReleased(stale);
        Assert.Equal((byte)63, coordinator.State.Image!.Pixels.Span[2]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DisposeReleasesLoadedAndLatePixelsWithoutPublishing(bool firstDecode)
    {
        Session session = new();
        ImageOpenCoordinator coordinator = new(new Picker(), new Decoder(session));
        Task<bool> pending;
        PixelBuffer? loaded = null;
        if (firstDecode) { pending = coordinator.OpenAsync("frames.gif", TestContext.Current.CancellationToken); }
        else
        {
            await OpenFirst(coordinator, session);
            loaded = coordinator.State.Image;
            pending = coordinator.PresentFrameAsync(1, TestContext.Current.CancellationToken);
        }
        int changes = 0;
        coordinator.PropertyChanged += (_, _) => changes++;
        coordinator.Dispose();
        PixelBuffer late = session.Requests[^1].Complete(81);
        Assert.False(await pending);
        AssertReleased(late);
        if (loaded is not null) { AssertReleased(loaded); }
        Assert.True(session.Disposed);
        Assert.Equal(0, session.RetainedPixelBytes);
        Assert.Equal(0, changes);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => coordinator.PresentFrameAsync(0, TestContext.Current.CancellationToken));
        coordinator.Dispose();
    }

    [Theory]
    [InlineData(ImageSequenceKind.Animation)]
    [InlineData(ImageSequenceKind.Pages)]
    public async Task PreviewUpgradeAndFullDetailDecodeSelectedFrameWithinActualBudget(ImageSequenceKind kind)
    {
        Session session = new(kind, new(800, 400));
        Decoder decoder = new(session);
        using ImageOpenCoordinator coordinator = new(new Picker(), decoder);
        coordinator.SetPreviewTarget(new(80, 40));
        await OpenFirst(coordinator, session);
        Task<bool> seek = coordinator.PresentFrameAsync(2, TestContext.Current.CancellationToken);
        session.Requests[^1].Complete(22);
        Assert.True(await seek);
        Task<bool> upgrade = coordinator.UpgradePreviewAsync(new(160, 80), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(2, session.Requests[^1].Index);
        session.Requests[^1].Complete(32);
        Assert.True(await upgrade);
        Assert.Equal(new PixelSize(160, 80), coordinator.State.Image!.Size);
        Task<bool> detail = coordinator.RefineAsync(TestContext.Current.CancellationToken);
        Assert.Equal(2, session.Requests[^1].Index);
        session.Requests[^1].Complete(42);
        Assert.True(await detail);
        Assert.False(coordinator.State.IsPreview);
        Assert.Equal(new PixelSize(800, 400), coordinator.State.Image!.Size);
        Assert.Equal<int>([0, 2, 2, 2], session.Requests.Select(request => request.Index));
        Assert.All(session.Requests, request => Assert.InRange(request.OutputBytes, 1, request.Budget));
        Assert.Equal(kind == ImageSequenceKind.Animation ? ImageFrameLimits.MaximumFrameBytes : PreviewDecodePolicy.MaximumBytes,
            session.Requests[0].Budget);
        Assert.Equal(0, decoder.StaticCalls);
        Assert.Equal((byte)42, coordinator.State.Image.Pixels.Span[2]);
    }

    [Fact]
    public async Task PageNavigationDoesNotChangeFolderSelectionOrOpenGeneration()
    {
        Session session = new(ImageSequenceKind.Pages);
        ImageBrowseSession browse = new();
        using ImageOpenCoordinator coordinator = new(new Picker(), new Decoder(session), browse);
        string[] files = [Path.GetFullPath("before.png"), Path.GetFullPath("pages.tiff"), Path.GetFullPath("after.png")];
        Task<bool> open = coordinator.OpenCandidatesAsync(files[1..].Concat(files[..1]).ToArray(), selection: true, cancellationToken: TestContext.Current.CancellationToken);
        session.Requests[0].Complete(11);
        Assert.True(await open);
        string[] original = browse.Items.ToArray();
        int folderIndex = browse.CurrentIndex;
        long generation = coordinator.RequestVersion;
        Task<bool> seek = coordinator.PresentFrameAsync(2, TestContext.Current.CancellationToken);
        session.Requests[^1].Complete(24);
        Assert.True(await seek);
        Assert.Equal(original, browse.Items);
        Assert.Equal(folderIndex, browse.CurrentIndex);
        Assert.Equal(files[1], browse.CurrentPath);
        Assert.Equal(generation, coordinator.RequestVersion);
        Assert.Equal(2, coordinator.State.FrameIndex);
        Assert.Equal((byte)24, coordinator.State.Image!.Pixels.Span[2]);
    }

    [Fact]
    public async Task InvalidFrameOutputIsReleasedAndCannotReplaceCurrentPixels()
    {
        Session session = new();
        using ImageOpenCoordinator coordinator = new(new Picker(), new Decoder(session));
        await OpenFirst(coordinator, session);
        PixelBuffer current = coordinator.State.Image!;
        Task<bool> seek = coordinator.PresentFrameAsync(1, TestContext.Current.CancellationToken);
        using PixelBuffer invalid = new(new(2, 1), 8, new byte[8], sourceSize: new(1, 1));
        session.Requests[^1].Completion.SetResult(invalid);
        Assert.False(await seek);
        AssertReleased(invalid);
        Assert.Same(current, coordinator.State.Image);
        Assert.Equal(0, coordinator.State.FrameIndex);
        Assert.Equal(ImageOpenError.ImageTooLarge, coordinator.State.RefinementError);
        Assert.Equal((byte)11, current.Pixels.Span[2]);
    }

    [Fact]
    public async Task InvalidatedFrameSessionWithOldSequenceNeverFallsBackToStaticFirstPage()
    {
        Session session = new(ImageSequenceKind.Pages, new(800, 400));
        Decoder decoder = new(session);
        using ImageOpenCoordinator coordinator = new(new Picker(), decoder);
        coordinator.SetPreviewTarget(new(80, 40));
        await OpenFirst(coordinator, session);
        Task<bool> seek = coordinator.PresentFrameAsync(2, TestContext.Current.CancellationToken);
        session.Requests[^1].Complete(42);
        Assert.True(await seek);
        PixelBuffer current = coordinator.State.Image!;
        coordinator.CancelPendingOpen();
        Assert.False(coordinator.HasFrameSession);
        Assert.NotNull(coordinator.State.Sequence);
        Assert.False(await coordinator.UpgradePreviewAsync(new(160, 80), cancellationToken: TestContext.Current.CancellationToken));
        Assert.False(await coordinator.RefineAsync(TestContext.Current.CancellationToken));
        Assert.False(await coordinator.RequestRegionAsync(new(0, 0, 8, 8), TestContext.Current.CancellationToken));
        Assert.Equal(0, decoder.StaticCalls);
        Assert.Equal(0, decoder.PreviewCalls);
        Assert.Equal(0, decoder.DetailCalls);
        Assert.Equal(0, decoder.RegionCalls);
        Assert.Same(current, coordinator.State.Image);
        Assert.Equal(2, coordinator.State.FrameIndex);
        Assert.Equal((byte)42, current.Pixels.Span[2]);
    }

    [Fact]
    public async Task RotatedAnimationPreviewUpgradeDoesNotLoseResolutionOnNextFrame()
    {
        Session session = new(ImageSequenceKind.Animation, new(800, 1600));
        using ImageOpenCoordinator coordinator = new(new Picker(), new Decoder(session));
        coordinator.SetPreviewTarget(new(160, 80));
        await OpenFirst(coordinator, session);
        Task<bool> upgrade = coordinator.UpgradePreviewAsync(new(400, 200), default(ViewOrientation).RotateRight(), TestContext.Current.CancellationToken);
        session.Requests[^1].Complete(22);
        Assert.True(await upgrade);
        PixelSize upgraded = coordinator.State.Image!.Size;
        Task<bool> next = coordinator.PresentFrameAsync(1, TestContext.Current.CancellationToken);
        session.Requests[^1].Complete(42);
        Assert.True(await next);
        Assert.Equal(upgraded, coordinator.State.Image!.Size);
        Assert.Equal(new PixelSize(200, 400), upgraded);
        Assert.Equal((byte)42, coordinator.State.Image.Pixels.Span[2]);
    }

    [Theory]
    [InlineData(1, 1_000_000, 4)]
    [InlineData(1_000_000, 1, 4)]
    [InlineData(1, 1_000_000, 64)]
    [InlineData(1_000_000, 1, 64)]
    public void ExtremeAspectRatiosRemainWithinPixelBudget(int width, int height, long budget)
    {
        PixelSize result = ImageFrameLimits.Fit(new(width, height), new(width, height), budget);
        Assert.InRange(result.Width, 1, width);
        Assert.InRange(result.Height, 1, height);
        Assert.InRange(result.PixelCount * 4, 4, budget);
    }

    [Fact]
    public async Task PageRegionRefinesSelectedPageAndLateRegionCannotSurvivePageSwitch()
    {
        Session session = new(ImageSequenceKind.Pages, new(800, 400));
        Decoder decoder = new(session);
        using ImageOpenCoordinator coordinator = new(new Picker(), decoder);
        coordinator.SetPreviewTarget(new(80, 40));
        await OpenFirst(coordinator, session);
        Task<bool> seek = coordinator.PresentFrameAsync(2, TestContext.Current.CancellationToken);
        session.Requests[^1].Complete(42);
        Assert.True(await seek);
        PixelRect bounds = new(0, 0, 8, 8);
        Task<bool> detail = coordinator.RequestRegionAsync(bounds, TestContext.Current.CancellationToken);
        RegionRequest region = Assert.Single(session.Regions);
        Assert.Equal(2, region.Index);
        PixelBuffer committed = region.Complete(61);
        Assert.True(await detail);
        Assert.Equal((byte)61, coordinator.State.Region!.Image.Pixels.Span[2]);
        Task<bool> lateDetail = coordinator.RequestRegionAsync(new(8, 8, 8, 8), TestContext.Current.CancellationToken);
        RegionRequest pending = session.Regions[1];
        Task<bool> next = coordinator.PresentFrameAsync(1, TestContext.Current.CancellationToken);
        session.Requests[^1].Complete(52);
        Assert.True(await next);
        AssertReleased(committed);
        PixelBuffer late = pending.Complete(99);
        Assert.False(await lateDetail);
        AssertReleased(late);
        Assert.Null(coordinator.State.Region);
        Assert.False(coordinator.State.IsRegionLoading);
        Assert.Equal(1, coordinator.State.FrameIndex);
        Assert.Equal((byte)52, coordinator.State.Image!.Pixels.Span[2]);
        Assert.Equal(0, decoder.RegionCalls);
    }

    private static async Task OpenFirst(ImageOpenCoordinator coordinator, Session session)
    {
        Task<bool> open = coordinator.OpenAsync("frames.gif", TestContext.Current.CancellationToken);
        session.Requests[0].Complete(11);
        Assert.True(await open);
    }

    [Fact]
    public async Task FullPageSameIndexAndRefinementReusePixelsWithoutAnotherDecode()
    {
        Session session = new(ImageSequenceKind.Pages);
        using ImageOpenCoordinator coordinator = new(new Picker(), new Decoder(session));
        await OpenFirst(coordinator, session);
        Assert.False(coordinator.State.IsPreview);
        PixelBuffer pixels = coordinator.State.Image!;
        int changes = 0;
        coordinator.PropertyChanged += (_, _) => changes++;
        Task<bool> same = coordinator.PresentFrameAsync(0, TestContext.Current.CancellationToken);
        Assert.True(same.IsCompletedSuccessfully);
        Assert.True(await same);
        Task<bool> refine = coordinator.RefineAsync(TestContext.Current.CancellationToken);
        Assert.True(refine.IsCompletedSuccessfully);
        Assert.True(await refine);
        Assert.Single(session.Requests);
        Assert.Same(pixels, coordinator.State.Image);
        Assert.Equal(0, changes);
        Assert.Equal((byte)11, pixels.Pixels.Span[2]);
    }

    [Fact]
    public async Task ReturningToCachedRegionACancelsBAndReleasesItsLatePixels()
    {
        Session session = new(ImageSequenceKind.Pages, new(800, 400));
        using ImageOpenCoordinator coordinator = new(new Picker(), new Decoder(session));
        coordinator.SetPreviewTarget(new(80, 40));
        await OpenFirst(coordinator, session);
        PixelRect a = new(0, 0, 8, 8);
        Task<bool> first = coordinator.RequestRegionAsync(a, TestContext.Current.CancellationToken);
        PixelBuffer aPixels = session.Regions[0].Complete(61);
        Assert.True(await first);
        DecodedImageRegion cached = coordinator.State.Region!;
        Task<bool> pendingB = coordinator.RequestRegionAsync(new(8, 8, 8, 8), TestContext.Current.CancellationToken);
        Assert.True(coordinator.State.IsRegionLoading);
        Task<bool> returnA = coordinator.RequestRegionAsync(a, TestContext.Current.CancellationToken);
        Assert.True(returnA.IsCompletedSuccessfully);
        Assert.True(await returnA);
        Assert.Equal(2, session.Regions.Count);
        Assert.Same(cached, coordinator.State.Region);
        Assert.False(coordinator.State.IsRegionLoading);
        PixelBuffer bPixels = session.Regions[1].Complete(99);
        Assert.False(await pendingB);
        AssertReleased(bPixels);
        Assert.Same(cached, coordinator.State.Region);
        Assert.Equal((byte)61, aPixels.Pixels.Span[2]);
        Assert.False(coordinator.State.IsRegionLoading);
    }

    [Fact]
    public async Task PlaybackCannotInterruptPreviewUpgradeAndNextFrameKeepsUpgradedSize()
    {
        Session session = new(ImageSequenceKind.Animation, new(800, 400));
        using ImageOpenCoordinator coordinator = new(new Picker(), new Decoder(session));
        coordinator.SetPreviewTarget(new(80, 40));
        await OpenFirst(coordinator, session);
        Task<bool> upgrade = coordinator.UpgradePreviewAsync(new(400, 200), cancellationToken: TestContext.Current.CancellationToken);
        Request highResolution = session.Requests[1];
        Assert.False(await coordinator.PresentPlaybackFrameAsync(1, TestContext.Current.CancellationToken));
        Assert.False(await coordinator.PresentPlaybackFrameAsync(2, TestContext.Current.CancellationToken));
        Assert.False(highResolution.Token.IsCancellationRequested);
        Assert.Equal(2, session.Requests.Count);
        highResolution.Complete(61);
        Assert.True(await upgrade);
        Assert.Equal(new PixelSize(400, 200), coordinator.State.Image!.Size);
        Task<bool> next = coordinator.PresentPlaybackFrameAsync(2, TestContext.Current.CancellationToken);
        session.Requests[^1].Complete(42);
        Assert.True(await next);
        Assert.Equal(new PixelSize(400, 200), coordinator.State.Image!.Size);
        Assert.Equal(2, coordinator.State.FrameIndex);
        Assert.Equal((byte)42, coordinator.State.Image.Pixels.Span[2]);
    }

    [Fact]
    public async Task ManualSeekInterruptsPreviewUpgradeAndLateUpgradeCannotReplaceIt()
    {
        Session session = new(ImageSequenceKind.Animation, new(800, 400));
        using ImageOpenCoordinator coordinator = new(new Picker(), new Decoder(session));
        coordinator.SetPreviewTarget(new(80, 40));
        await OpenFirst(coordinator, session);
        Task<bool> upgrade = coordinator.UpgradePreviewAsync(new(400, 200), cancellationToken: TestContext.Current.CancellationToken);
        Task<bool> seek = coordinator.PresentFrameAsync(2, TestContext.Current.CancellationToken);
        Assert.True(session.Requests[1].Token.IsCancellationRequested);
        PixelBuffer latest = session.Requests[2].Complete(42);
        Assert.True(await seek);
        PixelBuffer stale = session.Requests[1].Complete(99);
        Assert.False(await upgrade);
        AssertReleased(stale);
        Assert.Same(latest, coordinator.State.Image);
        Assert.Equal(2, coordinator.State.FrameIndex);
        Assert.Equal((byte)42, latest.Pixels.Span[2]);
    }

    private static void AssertReleased(PixelBuffer pixels) => Assert.Throws<ObjectDisposedException>(() => pixels.Pixels);

    private sealed class Picker : IImageFilePicker
    {
        public Task<string?> PickImageAsync(CancellationToken cancellationToken) => Task.FromResult<string?>(null);
    }

    private sealed class Decoder(params IImageFrameSession?[] sessions) : IImageDecoder, IImageFrameDecoder, IPreviewImageDecoder, IRegionImageDecoder
    {
        private readonly Queue<IImageFrameSession?> _sessions = new(sessions);
        public TaskCompletionSource<IImageFrameSession?>? PendingOpen { get; set; }
        public int StaticCalls { get; private set; }
        public int PreviewCalls { get; private set; }
        public int DetailCalls { get; private set; }
        public int RegionCalls { get; private set; }
        public Task<IImageFrameSession?> TryOpenFrameSessionAsync(string path, CancellationToken cancellationToken)
        {
            if (PendingOpen is { } pending) { PendingOpen = null; return pending.Task; }
            return Task.FromResult(_sessions.Dequeue());
        }
        public Task<PixelBuffer> DecodeAsync(string path, CancellationToken cancellationToken)
        {
            StaticCalls++;
            return Task.FromResult(new PixelBuffer(new(1, 1), 4, [0, 0, 77, 255]));
        }
        public Task<PixelBuffer> DecodePreviewAsync(string path, PixelSize maximumSize, CancellationToken cancellationToken)
        {
            PreviewCalls++;
            return DecodeAsync(path, cancellationToken);
        }
        public Task<PixelBuffer> DecodeDetailAsync(string path, long maximumDecodedBytes, CancellationToken cancellationToken)
        {
            DetailCalls++;
            return DecodeAsync(path, cancellationToken);
        }
        public Task<DecodedImageRegion> DecodeRegionAsync(string path, PixelRect bounds, PixelSize expectedSourceSize,
            long maximumDecodedBytes, CancellationToken cancellationToken)
        {
            RegionCalls++;
            return Task.FromException<DecodedImageRegion>(new InvalidOperationException("Static region fallback must not run for frames."));
        }
    }

    private sealed class Session : IImageFrameSession
    {
        private byte[]? _retained = new byte[256];
        public Session(ImageSequenceKind kind = ImageSequenceKind.Animation, PixelSize? size = null)
        {
            PixelSize source = size ?? new(8, 4);
            Info = new(kind, Enumerable.Range(0, 3).Select(index => new ImageFrameInfo(index, source,
                new(0, 0, source.Width, source.Height), DurationMilliseconds: 100)).ToArray());
        }
        public ImageSequenceInfo Info { get; }
        public ImageFileStamp FileStamp { get; } = new(123, DateTime.UnixEpoch);
        public long RetainedPixelBytes => _retained?.LongLength ?? 0;
        public bool Disposed { get; private set; }
        public List<Request> Requests { get; } = [];
        public List<RegionRequest> Regions { get; } = [];
        public TaskCompletionSource<Request> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<PixelBuffer> DecodeFrameAsync(int index, PixelSize maximumSize, long maximumDecodedBytes, CancellationToken cancellationToken)
        {
            Request request = new(index, Info.Frames[index].CanvasSize, maximumSize, maximumDecodedBytes, FileStamp, cancellationToken);
            Requests.Add(request);
            Started.TrySetResult(request);
            return request.Completion.Task;
        }
        public Task<DecodedImageRegion> DecodeRegionAsync(int index, PixelRect bounds, PixelSize expectedSourceSize,
            long maximumDecodedBytes, CancellationToken cancellationToken)
        {
            RegionRequest request = new(index, bounds, expectedSourceSize, maximumDecodedBytes, FileStamp);
            Regions.Add(request);
            return request.Completion.Task;
        }
        public void Dispose() { Disposed = true; _retained = null; }
    }

    // Ignore cancellation deliberately: native work can return after its owner has moved on.
    private sealed class Request(int index, PixelSize source, PixelSize target, long budget, ImageFileStamp stamp, CancellationToken token)
    {
        public int Index => index;
        public long Budget => budget;
        public CancellationToken Token => token;
        public long OutputBytes { get; private set; }
        public TaskCompletionSource<PixelBuffer> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public PixelBuffer Complete(byte red)
        {
            PixelSize size = ImageFrameLimits.Fit(source, target, budget);
            byte[] bytes = new byte[checked(size.Width * size.Height * 4)];
            for (int offset = 0; offset < bytes.Length; offset += 4) { bytes[offset + 2] = red; bytes[offset + 3] = 255; }
            OutputBytes = bytes.LongLength;
            PixelBuffer pixels = new(size, size.Width * 4, bytes, sourceSize: source, sourceFileStamp: stamp);
            Completion.SetResult(pixels);
            return pixels;
        }
    }

    private sealed class RegionRequest(int index, PixelRect bounds, PixelSize source, long budget, ImageFileStamp stamp)
    {
        public int Index => index;
        public TaskCompletionSource<DecodedImageRegion> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public PixelBuffer Complete(byte red)
        {
            byte[] bytes = new byte[checked(bounds.Width * bounds.Height * 4)];
            Assert.InRange(bytes.LongLength, 4, budget);
            for (int offset = 0; offset < bytes.Length; offset += 4) { bytes[offset + 2] = red; bytes[offset + 3] = 255; }
            PixelBuffer pixels = new(bounds.Size, bounds.Width * 4, bytes, sourceSize: source, sourceFileStamp: stamp);
            Completion.SetResult(new(pixels, bounds));
            return pixels;
        }
    }
}
