using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Media;
using System.Windows.Threading;

using ModernImageViewer.Application.Images;
using ModernImageViewer.Application.Observations;
using ModernImageViewer.UI.Controls;
using ModernImageViewer.UI.Observations;
using ModernImageViewer.UI.Rendering;

namespace ModernImageViewer.UI;

[SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable",
    Justification = "The WPF Closed handler calls DisposeBrowsingObservation on the owning UI thread.")]
public partial class MainWindow
{
    private BrowsingObservation? _browsingObservation;
    private BrowsingObservationOptions? _browsingObservationOptions;
    private CancellationTokenSource? _browsingDriverCancellation;
    private long _observedRequestId;
    private bool _observingNeighborSwitch;
    private bool _observationWritten;
    private double _observedPixelScale;
    private List<BrowsingResourceObservation>? _observedResources;
    private bool _observingActualDetail;

    private void InitializeBrowsingObservation()
    {
        if (BrowsingObservationOptions.Current is not { } options) { return; }
        _browsingObservationOptions = options;
        _browsingObservation = new();
        _observedResources = [];
        _browsingDriverCancellation = new();
        ContentRendered += OnObservedWindowVisible;
        _viewModel.PropertyChanged += OnBrowsingObservationStateChanged;
        _viewModel.BrowsingOpenRequested += OnBrowsingOpenRequested;
        Viewport.FramePresented += OnBrowsingFramePresented;
        Viewport.ScaleChanged += OnBrowsingObservationScaleChanged;
    }

    private void OnObservedWindowVisible(object? sender, EventArgs e)
    {
        ContentRendered -= OnObservedWindowVisible;
        _browsingObservation!.WindowVisible();
        _ = RunBrowsingObservationDriverAsync(_browsingDriverCancellation!.Token);
    }

    private void OnBrowsingObservationStateChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_browsingObservation is not { } observation) { return; }
        ImageOpenState state = _viewModel.Presentation;
        if (state.Status == ImageOpenStatus.Loading && state.RequestId > _observedRequestId)
        {
            _observedRequestId = state.RequestId;
            observation.OpenRequested(state.RequestId, _observingNeighborSwitch);
        }
        if (state.Status == ImageOpenStatus.Loaded)
        {
            observation.PreviewPublished(state.RequestId);
        }
        if (state.Status == ImageOpenStatus.Loaded && _viewModel.IsNavigationReady)
        {
            observation.NavigationAvailable(_observedRequestId);
        }
        if (state.RequestId == _observedRequestId && state.RefinementError == ImageOpenError.ImageTooLarge)
        {
            observation.DetailBudgetLimited(_observedRequestId);
        }
    }

    private void OnBrowsingOpenRequested(object? sender, EventArgs e) => _browsingObservation?.RequestReceived();

    // Wire from ImageViewport.FramePresented. The callback describes pixels actually painted,
    // not merely decoded or assigned; the identity and reference checks reject a previous frame.
    private void OnBrowsingObservationScaleChanged(object? sender, double scale) => _observedPixelScale = scale;

    private void OnBrowsingFramePresented(object? sender, ImageOpenState painted)
    {
        ImageOpenState state = _viewModel.Presentation;
        if (_browsingObservation is { } observation && BrowsingFrameObservation.IsCurrent(state, painted, _observedRequestId))
        {
            bool detail = BrowsingFrameObservation.HasRequiredDetail(state, _observedPixelScale, Viewport.VisibleDetailRegion);
            observation.FramePainted(_observedRequestId, detail);
            if (!detail && !_observingActualDetail && !state.IsRefining && !state.IsRegionLoading && state.Image is { } image)
            {
                double needed = Math.Min(1, _observedPixelScale);
                double width = image.SourceSize.Width * needed;
                double height = image.SourceSize.Height * needed;
                if (width > PreviewDecodePolicy.MaximumEdge || height > PreviewDecodePolicy.MaximumEdge
                    || width * height * 4 > PreviewDecodePolicy.MaximumBytes)
                {
                    observation.DetailBudgetLimited(_observedRequestId);
                }
            }
        }
    }

    private async Task RunBrowsingObservationDriverAsync(CancellationToken token)
    {
        int completed = 0;
        try
        {
            await WaitForObservedRequestAsync(token);
            bool forward = true;
            for (int index = 0; index < _browsingObservationOptions!.NeighborSwitches; index++)
            {
                if (forward && !_viewModel.CanMoveNext) { forward = false; }
                if (!forward && !_viewModel.CanMovePrevious) { forward = true; }
                if (!(_viewModel.CanMoveNext || _viewModel.CanMovePrevious))
                {
                    WriteBrowsingObservation(false, completed, "NavigationUnavailable");
                    return;
                }
                _observingNeighborSwitch = true;
                long previousRequest = _observedRequestId;
                try
                {
                    await (forward ? _viewModel.NextCommand : _viewModel.PreviousCommand).ExecuteAsync().WaitAsync(token);
                    if (_observedRequestId == previousRequest) { throw new InvalidOperationException("No new browsing request."); }
                    await WaitForObservedRequestAsync(token);
                    completed++;
                }
                finally { _observingNeighborSwitch = false; }
                if (_browsingObservationOptions.DetailEvery > 0 && completed % _browsingObservationOptions.DetailEvery == 0)
                {
                    _browsingObservation!.DetailDemandRequested(_observedRequestId);
                    _observingActualDetail = true;
                    try
                    {
                        Viewport.ActualSize();
                        await WaitForObservedRequestAsync(token);
                    }
                    finally
                    {
                        _observingActualDetail = false;
                        Viewport.Fit();
                    }
                }
                if (_browsingObservationOptions.RefreshEvery > 0 && completed % _browsingObservationOptions.RefreshEvery == 0)
                {
                    await _viewModel.RefreshFolderAsync().WaitAsync(token);
                    await WaitForObservedRequestAsync(token);
                }
            }
            if (_browsingObservationOptions!.RapidBurst > 0)
            {
                // Use ordinary open APIs on distinct nearby files; no global keyboard/mouse injection.
                string[] paths = _viewModel.BrowseItems.Select(item => item.FilePath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                if (paths.Length < 2) { throw new InvalidOperationException("Insufficient rapid-switch inputs."); }
                List<Task> pending = [];
                for (int index = 0; index < _browsingObservationOptions.RapidBurst; index++)
                {
                    pending.Add(_viewModel.OpenPathAsync(paths[index % paths.Length]));
                    await Task.Delay(10, token);
                }
                await Task.WhenAll(pending).WaitAsync(TimeSpan.FromSeconds(60), token);
                await WaitForObservedRequestAsync(token);
            }
            WriteBrowsingObservation(true, completed, null);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception exception) when (exception is TimeoutException or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            // Exception messages can contain full user paths. Export only a stable category.
            WriteBrowsingObservation(false, completed, exception.GetType().Name);
        }
    }

    private async Task WaitForObservedRequestAsync(CancellationToken token)
    {
        Stopwatch timeout = Stopwatch.StartNew();
        while (timeout.Elapsed < TimeSpan.FromSeconds(60))
        {
            token.ThrowIfCancellationRequested();
            if (_viewModel.Presentation.Status == ImageOpenStatus.Error) { throw new InvalidOperationException("Open failed."); }
            if (_browsingObservation!.HasPhase(BrowsingPhase.FirstRecognizablePainted)
                && _browsingObservation.HasPhase(BrowsingPhase.NavigationAvailable)
                && (_browsingObservation.HasPhase(BrowsingPhase.RequiredDetailPainted)
                    || _browsingObservation.HasPhase(BrowsingPhase.DetailBudgetLimited)))
            {
                if (_observedResources!.Count < BrowsingObservation.MaximumRequests)
                {
                    _observedResources.Add(ReadBrowsingResourceObservation());
                }
                return;
            }
            await Task.Delay(20, token);
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background, token);
        }
        throw new TimeoutException("Browsing observation exceeded its request deadline.");
    }

    private BrowsingResourceObservation ReadBrowsingResourceObservation()
    {
        ImageOpenState state = _viewModel.Presentation;
        var thumbnail = ThumbnailImage.Retained;
        long neighborBytes = _viewModel.CachedNeighborBytes;
        return new(state.RequestId, state.Image?.Pixels.Length ?? 0, state.Region?.Image.Pixels.Length ?? 0,
            SharedPixelBitmap.ActivePinCount, new(neighborBytes > 0 ? 1 : 0, neighborBytes),
            new(thumbnail.Count, thumbnail.Bytes));
    }

    private void WriteBrowsingObservation(bool success, int completedSwitches, string? failure)
    {
        if (_observationWritten || _browsingObservationOptions is not { } options || _browsingObservation is not { } observation) { return; }
        _observationWritten = true;
        var dpi = VisualTreeHelper.GetDpi(Viewport);
        string output = JsonSerializer.Serialize(new
        {
            SchemaVersion = 2,
            Success = success,
            AllRequiredDetailsPainted = success && !observation.Samples.Any(sample => sample.Phase == BrowsingPhase.DetailBudgetLimited),
            DetailBudgetLimitedCount = observation.Samples.Count(sample => sample.Phase == BrowsingPhase.DetailBudgetLimited),
            CompletedNeighborSwitches = completedSwitches,
            Failure = failure,
            observation.DroppedRequests,
            Phases = observation.Samples,
            Resources = _observedResources,
            RetainedAtCompletion = ReadBrowsingResourceObservation(),
            Viewport = new
            {
                WidthDip = Viewport.ActualWidth,
                HeightDip = Viewport.ActualHeight,
                dpi.DpiScaleX,
                dpi.DpiScaleY,
                WidthPhysical = Viewport.ActualWidth * dpi.DpiScaleX,
                HeightPhysical = Viewport.ActualHeight * dpi.DpiScaleY,
                PreviewWidth = Viewport.CurrentPreviewTarget.Width,
                PreviewHeight = Viewport.CurrentPreviewTarget.Height,
                MeasurementThreadId = Environment.CurrentManagedThreadId,
                Definition = "UI dispatcher VisualTreeHelper.GetDpi; DIP dimensions and bounded physical preview target.",
            },
            Bounds = new
            {
                PreviewBytes = PreviewDecodePolicy.MaximumBytes,
                PreviewEdge = PreviewDecodePolicy.MaximumEdge,
                NeighborBytes = NeighborPreviewCache.MaximumCachedBytes,
                NeighborEntries = 1,
                ThumbnailBytes = 2L * 1024 * 1024,
                ThumbnailEntries = 24,
                MainPixelBytes = ImageOpenCoordinator.MainPixelBudgetBytes,
                FullResolutionOutputBytes = ImageOpenCoordinator.FullResolutionOutputLimit,
                RegionBytes = ImageOpenCoordinator.MaximumRegionBytes,
                ObservationRequests = BrowsingObservation.MaximumRequests,
                Note = "Pixel/cache budgets, not a process/native working-set hard cap. Pin count is observed, not a process-global hard cap.",
            },
            Clock = "Milliseconds since observation initialization; not process creation. All phases share a monotonic clock.",
            OpenDefinition = "VM entry receives the open request; retained monotonic timestamp is mapped to the coordinator Loading generation. Picker/coordinator-only requests fall back to Loading publication. Not raw OS activation receipt.",
            LoadingDefinition = "Coordinator Loading generation observed on the UI thread; the request-to-Loading interval includes input filtering and notification delivery.",
            PreviewDefinition = "Current Loaded pixels first observed through VM notification, before paint. Loading-to-preview includes decoding, scheduling and UI notification; not native-only codec time.",
            PaintDefinition = "Skia draw callback completed for current identity/pixel reference; not GPU scanout or proof of human recognition.",
            DetailDefinition = "Current viewport demand satisfied; may be sufficient preview, full pixels or current region. Not necessarily full-source decode.",
            BudgetDefinition = "DetailBudgetLimited is a terminal budget outcome, not painted detail. Driver success means its sequence completed; AllRequiredDetailsPainted reports quality completion separately.",
        }, BrowsingObservationJson.Options);
        try
        {
            string destination = Path.GetFullPath(options.OutputPath);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            string temporary = destination + ".pending";
            File.WriteAllText(temporary, output);
            File.Move(temporary, destination, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            // A failed optional observation must not disrupt the viewer. The script detects no report.
        }
    }

    private void DisposeBrowsingObservation()
    {
        ContentRendered -= OnObservedWindowVisible;
        _viewModel.PropertyChanged -= OnBrowsingObservationStateChanged;
        _viewModel.BrowsingOpenRequested -= OnBrowsingOpenRequested;
        Viewport.FramePresented -= OnBrowsingFramePresented;
        Viewport.ScaleChanged -= OnBrowsingObservationScaleChanged;
        _browsingDriverCancellation?.Cancel();
        _browsingDriverCancellation?.Dispose();
        _browsingDriverCancellation = null;
        _observedResources = null;
        _browsingObservation = null;
        if (_browsingObservationOptions is not null)
        {
            _browsingObservationOptions = null;
        }
    }
}

internal sealed record BrowsingResourceObservation(long RequestId, long RetainedMainBytes, long RegionBytes,
    int PinnedBitmapCount, BrowsingCacheRetained NeighborCache, BrowsingCacheRetained ThumbnailCache);

internal sealed record BrowsingCacheRetained(int Count, long Bytes);

internal static class BrowsingObservationJson
{
    internal static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };
}
