using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

using ModernImageViewer.Application.Images;
using ModernImageViewer.Application.Observations;
using ModernImageViewer.Imaging;
using ModernImageViewer.UI.Observations;
using ModernImageViewer.UI.Rendering;

namespace ModernImageViewer.UI;

[SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable",
    Justification = "DisposeAnimationObservation is called after frame controls and viewport disposal in the owning window's close handler.")]
public partial class MainWindow
{
    private const int MaximumAnimationSamples = 4096;
    private AnimationObservationOptions? _animationObservationOptions;
    private CancellationTokenSource? _animationObservationCancellation;
    private Stopwatch? _animationObservationClock;
    private List<AnimationObservationSample>? _animationObservationSamples;
    private HashSet<string>? _animationObservationCoverage;
    private Func<AnimationResourceSnapshot>? _animationResourceReader;
    private ImageOpenState? _animationPaintedState;
    private bool _animationDriverCompleted;
    private string? _animationObservationFailure;
    private int _animationCompletedLoops;
    private int _animationDroppedSamples;

    // Call once after InitializeComponent. No observation state exists during ordinary runs.
    private void InitializeAnimationObservation(Func<AnimationResourceSnapshot>? resourceSnapshot = null)
    {
        if ((AnimationObservationOptions.Current ?? AnimationObservationOptions.FromEnvironment()) is not { } options) { return; }
        _animationObservationOptions = options;
        PixelBuffer.ObserveLifetime = true;
        _animationResourceReader = resourceSnapshot;
        _animationObservationCancellation = new();
        _animationObservationClock = Stopwatch.StartNew();
        _animationObservationSamples = [];
        _animationObservationCoverage = [];
        ContentRendered += OnAnimationObservedWindowVisible;
        Viewport.FramePresented += OnAnimationObservedFramePresented;
    }

    private void OnAnimationObservedWindowVisible(object? sender, EventArgs e)
    {
        ContentRendered -= OnAnimationObservedWindowVisible;
        ObserveAnimation("WindowVisible");
        _ = RunAnimationObservationAsync(_animationObservationCancellation!.Token);
    }

    private void OnAnimationObservedFramePresented(object? sender, ImageOpenState painted)
    {
        ImageOpenState current = _viewModel.Presentation;
        if (current.RequestId == painted.RequestId && current.Source?.Identity == painted.Source?.Identity
            && current.FrameIndex == painted.FrameIndex && ReferenceEquals(current.Image, painted.Image))
        {
            _animationPaintedState = painted;
        }
    }

    private bool IsAnimationCurrentFramePainted()
    {
        ImageOpenState state = _viewModel.Presentation;
        return state.Status == ImageOpenStatus.Loaded && state.Image is not null
            && _animationPaintedState is { } painted && state.RequestId == painted.RequestId
            && state.FrameIndex == painted.FrameIndex && ReferenceEquals(state.Image, painted.Image);
    }

    private async Task RunAnimationObservationAsync(CancellationToken token)
    {
        try
        {
            AnimationObservationOptions options = _animationObservationOptions!;
            RequireAnimationObservation(options.Inputs.Count >= 4, "InsufficientInputs");
            string? animation = null;
            string? staticImage = null;
            int animationInputs = 0;
            for (int loop = 0; loop < options.Loops; loop++)
            {
                foreach (string input in options.Inputs)
                {
                    await OpenAnimationObservedInputAsync(input, token);
                    ImageOpenState state = _viewModel.Presentation;
                    ImageSequenceKind kind = state.Sequence?.Kind ?? ImageSequenceKind.Static;
                    if (kind == ImageSequenceKind.Animation)
                    {
                        animation = input;
                        if (loop == 0) { animationInputs++; }
                        ObserveAnimation(Path.GetExtension(input).Equals(".gif", StringComparison.OrdinalIgnoreCase)
                            ? "AnimationGif" : "AnimationWebp");
                        await ExerciseObservedAnimationAsync(token);
                    }
                    else if (kind == ImageSequenceKind.Pages)
                    {
                        RequireAnimationObservation(_viewModel.HasFrameSequence && !_viewModel.IsAnimation, "PageClassification");
                        int last = state.Sequence!.Count - 1;
                        await SeekObservedFrameAsync(last, token);
                        Viewport.ActualSize();
                        await ObserveAnimationDelayAsync(400, token);
                        await WaitForAnimationConditionAsync(IsAnimationCurrentFramePainted, token);
                        RequireAnimationObservation(_viewModel.Presentation.FrameIndex == last, "PageDetailReturnedToFirst");
                        await SeekObservedFrameAsync(0, token);
                        ObserveAnimation("PagesSeekAndDetail");
                    }
                    else
                    {
                        staticImage = input;
                        RequireAnimationObservation(!_viewModel.HasFrameSequence && !_viewModel.IsAnimation
                            && !_viewModel.IsAnimationPlaying, "StaticClassification");
                        await WaitForAnimationConditionAsync(() => ReadAnimationResources().RetainedFrameBytes == 0, token);
                        ObserveAnimation("StaticResourcesReleased");
                    }
                    Viewport.Fit();
                    Viewport.ActualSize();
                    Viewport.ZoomIn();
                    Viewport.ZoomOut();
                    await ObserveAnimationDelayAsync(400, token);
                    ToggleFullScreen();
                    await ObserveAnimationDelayAsync(100, token);
                    ToggleFullScreen();
                    Viewport.Fit();
                    ObserveAnimation("ViewportAndFullscreen");
                    long previousRequest = _viewModel.Presentation.RequestId;
                    await _viewModel.RefreshFolderAsync().WaitAsync(token);
                    await WaitForAnimationConditionAsync(() => _viewModel.Presentation.RequestId > previousRequest
                        && IsAnimationCurrentFramePainted(), token);
                    ObserveAnimation("RefreshF5");
                }
                _animationCompletedLoops++;
            }
            RequireAnimationObservation(animationInputs >= 2 && animation is not null && staticImage is not null
                && _animationObservationCoverage!.Contains("PagesSeekAndDetail")
                && _animationObservationCoverage.Contains("AnimationGif")
                && _animationObservationCoverage.Contains("AnimationWebp"), "MissingAnimationStaticOrPages");
            // Overlap real requests to exercise cancellation and rejection of late frame work.
            List<Task> pending = [];
            for (int index = 0; index < 4; index++)
            {
                pending.Add(_viewModel.OpenPathAsync(index % 2 == 0 ? animation! : staticImage!));
                await Task.Delay(10, token);
            }
            await Task.WhenAll(pending).WaitAsync(TimeSpan.FromSeconds(60), token);
            await WaitForAnimationConditionAsync(IsAnimationCurrentFramePainted, token);
            RequireAnimationObservation(!_viewModel.IsAnimation && !_viewModel.HasFrameSequence, "LateAnimationCommit");
            ObserveAnimation("RapidSwitch");
            await OpenAnimationObservedInputAsync(animation!, token);
            _viewModel.SetFramePresentationAvailable(true);
            await _viewModel.RestartAnimationAsync().WaitAsync(token);
            await ObserveAnimationDelayAsync(options.LongSeconds * 1000, token, pulse: true, restartEnded: true);
            ObserveAnimation("LongPlaybackCompleted");
            await OpenAnimationObservedInputAsync(staticImage!, token);
            await WaitForAnimationConditionAsync(() => ReadAnimationResources().RetainedFrameBytes == 0, token);
            await ObserveAnimationDelayAsync(500, token);
            ObserveAnimation("StaticDrain");
            // Release through the real coordinator and await native completion before window shutdown.
            await OpenAnimationObservedInputAsync(animation!, token);
            ObserveAnimation("BeforeRelease");
            await _viewModel.ReleaseFrameObservationResourcesAsync().WaitAsync(token);
            await WaitForAnimationConditionAsync(() => AreAnimationResourcesReleased(ReadAnimationResources()), token);
            ObserveAnimation("ImageResourcesDrained");
            await ObserveAnimationDelayAsync(500, token);
            RequireAnimationObservation(AreAnimationResourcesReleased(ReadAnimationResources()), "IdleResourcesRetained");
            ObserveAnimation("ReleasedIdle");
            ObserveAnimation("BeforeClose");
            _animationDriverCompleted = true;
            Close();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception exception)
        {
            // Neither codec exception messages nor input paths belong in observation reports.
            _animationObservationFailure ??= exception.GetType().Name;
            Close();
        }
    }

    private async Task OpenAnimationObservedInputAsync(string input, CancellationToken token)
    {
        long request = _viewModel.Presentation.RequestId;
        await _viewModel.OpenPathAsync(input).WaitAsync(token);
        await WaitForAnimationConditionAsync(() => _viewModel.Presentation.RequestId > request
            && IsAnimationCurrentFramePainted(), token);
        ObserveAnimation("InputPainted");
    }

    private async Task ExerciseObservedAnimationAsync(CancellationToken token)
    {
        RequireAnimationObservation(_viewModel.HasFrameSequence && _viewModel.IsAnimation, "AnimationClassification");
        _viewModel.SetFramePresentationAvailable(true);
        await _viewModel.RestartAnimationAsync().WaitAsync(token);
        await WaitForAnimationAdvanceAsync(token);
        ObserveAnimation("PlaybackAdvanced");
        if (_viewModel.IsAnimationPlaying) { await _viewModel.ToggleAnimationAsync().WaitAsync(token); }
        RequireAnimationObservation(!_viewModel.IsAnimationPlaying, "PauseFailed");
        int paused = _viewModel.Presentation.FrameIndex;
        await ObserveAnimationDelayAsync(250, token, pulse: true);
        RequireAnimationObservation(_viewModel.Presentation.FrameIndex == paused, "PausedFrameAdvanced");
        ObserveAnimation("UserPause");
        await _viewModel.ToggleAnimationAsync().WaitAsync(token);
        await WaitForAnimationAdvanceAsync(token);
        ObserveAnimation("UserResume");
        await SeekObservedFrameAsync(_viewModel.Presentation.Sequence!.Count - 1, token);
        RequireAnimationObservation(!_viewModel.IsAnimationPlaying, "SeekDidNotPause");
        ObserveAnimation("AnimationSeek");
        await _viewModel.RestartAnimationAsync().WaitAsync(token);
        await WaitForAnimationAdvanceAsync(token);
        WindowState saved = WindowState;
        WindowState = WindowState.Minimized;
        _viewModel.SetFramePresentationAvailable(false);
        await ObserveAnimationDelayAsync(100, token, pulse: true);
        int hidden = _viewModel.Presentation.FrameIndex;
        await ObserveAnimationDelayAsync(300, token, pulse: true);
        RequireAnimationObservation(_viewModel.Presentation.FrameIndex == hidden, "UnavailableFrameAdvanced");
        ObserveAnimation("BackgroundPause");
        WindowState = saved;
        _viewModel.SetFramePresentationAvailable(true);
        await WaitForAnimationAdvanceAsync(token);
        ObserveAnimation("VisibleResume");
        if (_viewModel.IsAnimationPlaying) { await _viewModel.ToggleAnimationAsync().WaitAsync(token); }
        paused = _viewModel.Presentation.FrameIndex;
        _viewModel.SetFramePresentationAvailable(false);
        await ObserveAnimationDelayAsync(100, token, pulse: true);
        _viewModel.SetFramePresentationAvailable(true);
        await ObserveAnimationDelayAsync(150, token, pulse: true);
        RequireAnimationObservation(!_viewModel.IsAnimationPlaying && _viewModel.Presentation.FrameIndex == paused,
            "VisibilityResumedUserPause");
        ObserveAnimation("UserPausePreserved");
        await _viewModel.RestartAnimationAsync().WaitAsync(token);
        await WaitForAnimationAdvanceAsync(token);
        ObserveAnimation("Restart");
    }

    private async Task SeekObservedFrameAsync(int index, CancellationToken token)
    {
        long request = _viewModel.Presentation.RequestId;
        RequireAnimationObservation(await _viewModel.SeekFrameAsync(index).WaitAsync(token), "SeekRejected");
        await WaitForAnimationConditionAsync(() => _viewModel.Presentation.FrameIndex == index
            && IsAnimationCurrentFramePainted(), token);
        RequireAnimationObservation(_viewModel.Presentation.RequestId == request, "SeekChangedOpenGeneration");
    }

    private async Task WaitForAnimationAdvanceAsync(CancellationToken token)
    {
        int frame = _viewModel.Presentation.FrameIndex;
        await WaitForAnimationConditionAsync(() => _viewModel.Presentation.FrameIndex != frame
            && IsAnimationCurrentFramePainted(), token, pulse: true);
    }

    private async Task WaitForAnimationConditionAsync(Func<bool> condition, CancellationToken token, bool pulse = false)
    {
        Stopwatch deadline = Stopwatch.StartNew();
        while (!condition())
        {
            token.ThrowIfCancellationRequested();
            RequireAnimationObservation(_viewModel.Presentation.Status != ImageOpenStatus.Error, "OpenFailed");
            if (deadline.Elapsed > TimeSpan.FromSeconds(60)) { throw new TimeoutException(); }
            if (pulse) { await _viewModel.PulseAnimationAsync().WaitAsync(token); }
            await Task.Delay(25, token);
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background, token);
        }
    }

    private async Task ObserveAnimationDelayAsync(int milliseconds, CancellationToken token, bool pulse = false, bool restartEnded = false)
    {
        Stopwatch duration = Stopwatch.StartNew();
        while (duration.ElapsedMilliseconds < milliseconds)
        {
            if (restartEnded && _viewModel.IsAnimation && !_viewModel.IsAnimationPlaying)
            {
                await _viewModel.RestartAnimationAsync().WaitAsync(token);
                ObserveAnimation("PressureRestart");
            }
            if (pulse) { await _viewModel.PulseAnimationAsync().WaitAsync(token); }
            ObserveAnimation("ResourceSample");
            await Task.Delay(100, token);
        }
    }

    private AnimationResourceSnapshot ReadAnimationResources()
    {
        AnimationResourceSnapshot resources = _animationResourceReader?.Invoke() ?? new(0, 0);
        return resources with
        {
            RetainedFrameBytes = _viewModel.RetainedFrameBytes,
            PinnedBitmapCount = SharedPixelBitmap.ActivePinCount,
            ActiveTimerCount = _frameTimer is null ? 0 : 1,
            ActiveBitmapCount = SharedPixelBitmap.ActivePinCount,
            RetainedSnapshotBytes = 0,
            ActivePixelCount = PixelBuffer.ObservedActiveCount,
            ActivePixelBytes = PixelBuffer.ObservedActiveBytes,
        };
    }

    private void RequireAnimationObservation(bool condition, string failure)
    {
        if (condition) { return; }
        _animationObservationFailure = failure;
        throw new InvalidOperationException();
    }

    private void ObserveAnimation(string stage)
    {
        if (_animationObservationOptions is null) { return; }
        if (stage != "ResourceSample") { _animationObservationCoverage!.Add(stage); }
        ImageOpenState state = _viewModel.Presentation;
        AnimationResourceSnapshot resources = ReadAnimationResources();
        RequireAnimationObservation(resources.RetainedFrameBytes is >= 0 and <= ImageFrameLimits.MaximumPixelBytes,
            "FramePixelBudgetExceeded");
        if (state.Sequence?.Kind == ImageSequenceKind.Animation)
        {
            RequireAnimationObservation(resources.RetainedFrameBytes + (state.Image?.Pixels.Length ?? 0)
                + (state.Region?.Image.Pixels.Length ?? 0) <= ImageFrameLimits.MaximumPixelBytes, "FrameAndPresentationBudgetExceeded");
        }
        if (_animationObservationSamples!.Count >= MaximumAnimationSamples) { _animationDroppedSamples++; return; }
        DpiScale dpi = VisualTreeHelper.GetDpi(Viewport);
        _animationObservationSamples.Add(new(stage, _animationObservationClock!.Elapsed.TotalMilliseconds,
            state.RequestId, state.Sequence?.Kind ?? ImageSequenceKind.Static, state.FrameIndex,
            state.Sequence?.Count ?? 1, _viewModel.IsAnimationPlaying, IsAnimationCurrentFramePainted(),
            state.Image?.Pixels.Length ?? 0, state.Region?.Image.Pixels.Length ?? 0, resources,
            new(dpi.DpiScaleX, dpi.DpiScaleY, Viewport.ActualWidth, Viewport.ActualHeight,
                Viewport.ActualWidth * dpi.DpiScaleX, Viewport.ActualHeight * dpi.DpiScaleY)));
    }

    private static bool AreAnimationResourcesReleased(AnimationResourceSnapshot resources) =>
        resources.RetainedFrameBytes == 0 && resources.PinnedBitmapCount == 0
        && resources.ActivePixelCount == 0 && resources.ActivePixelBytes == 0
        && resources.ActiveTimerCount is null or 0 && resources.ActiveBitmapCount is null or 0
        && resources.ActiveNativeSessionCount is null or 0 && resources.ActiveNativeDecoderCount is null or 0
        && resources.RetainedSnapshotBytes is null or 0;

    // Call at the END of the Closed handler, after DisposeFrameControls and Viewport.Dispose.
    // Counters must remain readable after disposal; no async dispatcher work can survive app shutdown.
    private void DisposeAnimationObservation()
    {
        if (_animationObservationOptions is not { } options) { return; }
        ContentRendered -= OnAnimationObservedWindowVisible;
        Viewport.FramePresented -= OnAnimationObservedFramePresented;
        _animationObservationCancellation!.Cancel();
        _animationObservationCancellation.Dispose();
        _animationObservationCancellation = null;
        try
        {
            AnimationResourceSnapshot closed = ReadAnimationResources();
            bool released = AreAnimationResourcesReleased(closed);
            string output = JsonSerializer.Serialize(new
            {
                SchemaVersion = 2,
                Success = _animationDriverCompleted && _animationObservationFailure is null && released,
                Failure = _animationObservationFailure ?? (!_animationDriverCompleted ? "ClosedBeforeCompletion" : !released ? "ResourcesRetainedAfterClose" : null),
                CompletedLoops = _animationCompletedLoops,
                Coverage = _animationObservationCoverage!.Order(StringComparer.Ordinal),
                Samples = _animationObservationSamples,
                DroppedSamples = _animationDroppedSamples,
                ResourcesAfterClose = closed,
                InstrumentationComplete = closed.ActiveTimerCount is not null && closed.ActiveBitmapCount is not null
                    && closed.ActiveNativeSessionCount is not null && closed.ActiveNativeDecoderCount is not null,
                PixelObservationScope = "Live PixelBuffer wrappers created while ObserveLifetime is enabled. Shared-array wrappers may be counted repeatedly; these bytes are not managed heap size or pinned-array lifetime.",
                BitmapObservationScope = "Provider counter for pixel-backed SKBitmaps; SharedPixelBitmap pins are sampled independently. Checkerboard/render surfaces are outside this scope unless the provider counts them.",
                TimerObservationScope = "Animation frame timers only; ordinary UI/message/slideshow timers are outside this counter.",
                SnapshotObservationScope = "GIF previous-disposal is handled by SKCodec; retained reference-frame pixels are included in RetainedFrameBytes. WebP uses a bounded composite and temporary local-frame decode; the local work array and native scratch are not retained-byte counters.",
                Clock = "Monotonic milliseconds since opt-in initialization on the UI dispatcher; process samples use a separate process-launch stopwatch.",
                Bounds = new { FramePixelBytes = ImageFrameLimits.MaximumPixelBytes, Samples = MaximumAnimationSamples },
                Limitations = "Opt-in VM/viewport driver. Background availability is explicitly controlled on this window. Paint means current Skia callback, not scanout. Driver awaits ReleaseFrameObservationResourcesAsync and samples released idle before Close; final snapshot follows frame-controls and viewport disposal. DI/VM disposal occurs later in App.OnExit. Null counters are uninstrumented. No forced GC; no paths or file names.",
            }, BrowsingObservationJson.Options);
            string destination = Path.GetFullPath(options.OutputPath);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.WriteAllText(destination + ".pending", output);
            File.Move(destination + ".pending", destination, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            // Missing output is a script failure; observation I/O must not interrupt closing.
        }
        finally
        {
            PixelBuffer.ObserveLifetime = false;
            _animationObservationOptions = null;
            _animationResourceReader = null;
            _animationPaintedState = null;
            _animationObservationClock = null;
            _animationObservationSamples = null;
            _animationObservationCoverage = null;
        }
    }
}

internal sealed record AnimationObservationSample(string Stage, double ElapsedMs, long RequestId,
    ImageSequenceKind Kind, int FrameIndex, int FrameCount, bool Playing, bool CurrentFramePainted,
    long MainPixelBytes, long RegionPixelBytes, AnimationResourceSnapshot Resources,
    AnimationViewportSnapshot Viewport);

internal sealed record AnimationViewportSnapshot(double DpiScaleX, double DpiScaleY,
    double WidthDip, double HeightDip, double WidthPhysicalPixels, double HeightPhysicalPixels);
