using System.Diagnostics;

namespace ModernImageViewer.Application.Observations;

public enum BrowsingPhase
{
    WindowVisible,
    OpenRequested,
    LoadingPublished,
    PreviewPublished,
    FirstRecognizablePainted,
    NavigationAvailable,
    RequiredDetailRequested,
    DetailBudgetLimited,
    RequiredDetailPainted,
    NeighborSwitchCompleted,
}

public sealed record BrowsingPhaseSample(long RequestId, BrowsingPhase Phase, double SinceStartMs, double? SinceRequestMs);

/// <summary>Opt-in, bounded, path-free samples. Call on the owning UI thread; retain no pixel buffers.</summary>
public sealed class BrowsingObservation
{
    public const int MaximumRequests = 1024;
    public const int MaximumSamples = (MaximumRequests * 10) + 1;
    private readonly Func<double> _elapsedMilliseconds;
    private readonly List<BrowsingPhaseSample> _samples = [];
    private readonly HashSet<BrowsingPhase> _currentPhases = [];
    private long _requestId;
    private double _requestedAt;
    private bool _neighbor;
    private bool _windowVisible;
    private double? _pendingReceivedAt;
    private int _requestCount;

    public BrowsingObservation(Func<double>? elapsedMilliseconds = null)
    {
        Stopwatch watch = Stopwatch.StartNew();
        _elapsedMilliseconds = elapsedMilliseconds ?? (() => watch.Elapsed.TotalMilliseconds);
    }

    public long CurrentRequestId => _requestId;
    public int DroppedRequests { get; private set; }
    public IReadOnlyList<BrowsingPhaseSample> Samples => _samples.AsReadOnly();

    public void RequestReceived() => _pendingReceivedAt = _elapsedMilliseconds();

    public void WindowVisible()
    {
        if (_windowVisible) { return; }
        _windowVisible = true;
        _samples.Add(new(0, BrowsingPhase.WindowVisible, _elapsedMilliseconds(), null));
    }

    public void OpenRequested(long requestId, bool neighborSwitch)
    {
        if (requestId <= _requestId) { return; }
        _requestId = requestId;
        _requestedAt = _pendingReceivedAt ?? _elapsedMilliseconds();
        _pendingReceivedAt = null;
        _neighbor = neighborSwitch;
        _currentPhases.Clear();
        _requestCount++;
        if (_requestCount > MaximumRequests) { DroppedRequests++; }
        _currentPhases.Add(BrowsingPhase.OpenRequested);
        if (_requestCount <= MaximumRequests && _samples.Count < MaximumSamples)
        {
            _samples.Add(new(requestId, BrowsingPhase.OpenRequested, _requestedAt, 0));
        }
        Record(requestId, BrowsingPhase.LoadingPublished);
    }

    public void PreviewPublished(long requestId) => Record(requestId, BrowsingPhase.PreviewPublished);

    public void NavigationAvailable(long requestId) => Record(requestId, BrowsingPhase.NavigationAvailable);

    public void DetailDemandRequested(long requestId)
    {
        if (requestId != _requestId || requestId == 0) { return; }
        _currentPhases.Remove(BrowsingPhase.RequiredDetailPainted);
        _currentPhases.Remove(BrowsingPhase.DetailBudgetLimited);
        _currentPhases.Remove(BrowsingPhase.RequiredDetailRequested);
        Record(requestId, BrowsingPhase.RequiredDetailRequested);
    }

    public void DetailBudgetLimited(long requestId) => Record(requestId, BrowsingPhase.DetailBudgetLimited);

    public void FramePainted(long requestId, bool requiredDetail)
    {
        if (requestId != _requestId || requestId == 0) { return; }
        Record(requestId, BrowsingPhase.FirstRecognizablePainted);
        if (_neighbor) { Record(requestId, BrowsingPhase.NeighborSwitchCompleted); }
        if (requiredDetail) { Record(requestId, BrowsingPhase.RequiredDetailPainted); }
    }

    public bool HasPhase(BrowsingPhase phase) => _currentPhases.Contains(phase);

    private void Record(long requestId, BrowsingPhase phase)
    {
        if (requestId == 0 || requestId != _requestId || !_currentPhases.Add(phase)) { return; }
        if (_requestCount <= MaximumRequests && _samples.Count < MaximumSamples)
        {
            double elapsed = _elapsedMilliseconds();
            _samples.Add(new(requestId, phase, elapsed, elapsed - _requestedAt));
        }
    }
}
