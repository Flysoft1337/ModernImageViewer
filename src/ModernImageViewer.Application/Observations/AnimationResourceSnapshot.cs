namespace ModernImageViewer.Application.Observations;

/// <summary>Application-owned frame resources. Null counters mean instrumentation is unavailable.</summary>
public sealed record AnimationResourceSnapshot(
    long RetainedFrameBytes,
    int PinnedBitmapCount,
    int? ActiveTimerCount = null,
    int? ActiveBitmapCount = null,
    int? ActiveNativeSessionCount = null,
    long? RetainedSnapshotBytes = null,
    int ActivePixelCount = 0,
    long ActivePixelBytes = 0,
    int? ActiveNativeDecoderCount = null);
