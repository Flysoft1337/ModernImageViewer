using System.Diagnostics;

using ModernImageViewer.Imaging;

namespace ModernImageViewer.Application.Images;

/// <summary>
/// Caller-driven playback state. Use on one thread; GetTargetFrame and commands refresh
/// the state snapshot. No timer, decoder, or presentation work is owned here.
/// </summary>
public sealed class FramePlaybackController
{
    private readonly Func<long> _clock;
    private readonly long[] _frameEnds;
    private readonly long _duration;
    private readonly long? _totalDuration;
    private Int128 _elapsed;
    private long? _lastClock;

    public FramePlaybackController(ImageSequenceInfo info, Func<long>? clock = null)
    {
        ArgumentNullException.ThrowIfNull(info);
        if (info.Kind is not (ImageSequenceKind.Animation or ImageSequenceKind.Pages))
        {
            throw new ArgumentException("Only animation and page sequences need a controller.", nameof(info));
        }
        if (info.Frames is null || info.Count == 0)
        {
            throw new ArgumentException("The sequence must contain frames.", nameof(info));
        }
        if (info.Kind == ImageSequenceKind.Animation && info.TotalPlays is <= 0)
        {
            throw new ArgumentException("Total plays must be positive or null for infinite playback.", nameof(info));
        }

        Info = info;
        _clock = clock ?? GetMonotonicMilliseconds;
        _frameEnds = new long[info.Count];
        long duration = 0;
        for (int index = 0; index < info.Count; index++)
        {
            ImageFrameInfo frame = info.Frames[index];
            if (frame is null || frame.Index != index)
            {
                throw new ArgumentException("Frame indices must be contiguous and zero-based.", nameof(info));
            }
            if (info.Kind == ImageSequenceKind.Animation)
            {
                if (frame.DurationMilliseconds < 1)
                {
                    throw new ArgumentException("Effective animation delays must be at least one millisecond.", nameof(info));
                }
                duration = checked(duration + frame.DurationMilliseconds);
                _frameEnds[index] = duration;
            }
        }
        _duration = duration;
        if (info.Kind == ImageSequenceKind.Animation && info.TotalPlays is int totalPlays)
        {
            _totalDuration = checked(duration * totalPlays);
        }
    }

    public ImageSequenceInfo Info { get; }
    public int Index { get; private set; }

    /// <summary>User playback intent; suspension freezes time without clearing it.</summary>
    public bool IsPlaying { get; private set; }
    public bool IsEnded { get; private set; }
    public bool IsSuspended { get; private set; }

    /// <summary>Effective time at the last refresh, saturating at long.MaxValue.</summary>
    public long ElapsedMilliseconds => (long)Int128.Min(_elapsed, long.MaxValue);

    /// <summary>Remaining delay at the last refresh, or null when time is frozen.</summary>
    public long? NextDueMilliseconds => IsPlaying && !IsSuspended
        ? _frameEnds[Index] - (long)(_elapsed % _duration) : null;

    /// <summary>Raised synchronously only when frame or playback state changes.</summary>
    public event EventHandler? Changed;

    public void Play()
    {
        var previous = CaptureState();
        Advance();
        if (Info.Kind == ImageSequenceKind.Animation && !IsPlaying && !IsEnded)
        {
            if (!IsSuspended)
            {
                ReadClock();
            }
            IsPlaying = true;
        }
        NotifyChanged(previous);
    }

    public void Pause()
    {
        var previous = CaptureState();
        Advance();
        IsPlaying = false;
        NotifyChanged(previous);
    }

    /// <summary>Starts animation again at frame zero, preserving suspension.</summary>
    public void Restart()
    {
        var previous = CaptureState();
        if (Info.Kind == ImageSequenceKind.Animation && !IsSuspended)
        {
            ReadClock();
        }
        _elapsed = 0;
        Index = 0;
        IsEnded = false;
        IsPlaying = Info.Kind == ImageSequenceKind.Animation;
        NotifyChanged(previous);
    }

    /// <summary>Pauses at the selected frame's start in the first play.</summary>
    public void Seek(int index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, _frameEnds.Length);
        var previous = CaptureState();
        _elapsed = index == 0 ? 0 : _frameEnds[index - 1];
        Index = index;
        IsPlaying = false;
        IsEnded = false;
        NotifyChanged(previous);
    }

    public void SetSuspended(bool suspended)
    {
        if (IsSuspended == suspended)
        {
            return;
        }
        var previous = CaptureState();
        Advance();
        if (!suspended && IsPlaying)
        {
            ReadClock();
        }
        IsSuspended = suspended;
        NotifyChanged(previous);
    }

    public int GetTargetFrame()
    {
        var previous = CaptureState();
        Advance();
        NotifyChanged(previous);
        return Index;
    }

    private void Advance()
    {
        if (!IsPlaying || IsSuspended)
        {
            return;
        }

        long previousClock = _lastClock!.Value;
        long now = ReadClock();
        // Widen before subtraction and accumulation to preserve the infinite-loop
        // phase even when a signed-long clock jump exceeds long.MaxValue.
        _elapsed += (Int128)now - previousClock;
        if (_totalDuration is long totalDuration && _elapsed >= totalDuration)
        {
            _elapsed = totalDuration;
            Index = _frameEnds.Length - 1;
            IsPlaying = false;
            IsEnded = true;
            return;
        }

        long position = (long)(_elapsed % _duration);
        int index = Array.BinarySearch(_frameEnds, position);
        Index = index < 0 ? ~index : index + 1;
    }

    private long ReadClock()
    {
        long now = _clock();
        if (_lastClock is long previous && now < previous)
        {
            throw new InvalidOperationException("The playback clock must be monotonic.");
        }
        _lastClock = now;
        return now;
    }

    private (int Index, bool Playing, bool Ended, bool Suspended) CaptureState() =>
        (Index, IsPlaying, IsEnded, IsSuspended);

    private void NotifyChanged((int Index, bool Playing, bool Ended, bool Suspended) previous)
    {
        if (previous != CaptureState())
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    private static long GetMonotonicMilliseconds() =>
        Stopwatch.GetElapsedTime(0, Stopwatch.GetTimestamp()).Ticks / TimeSpan.TicksPerMillisecond;
}
