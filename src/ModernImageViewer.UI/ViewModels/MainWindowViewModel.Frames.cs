using ModernImageViewer.Application.Images;
using ModernImageViewer.Imaging;

namespace ModernImageViewer.UI.ViewModels;

public sealed partial class MainWindowViewModel
{
    private FramePlaybackController? _framePlayback;
    private Guid? _frameSourceIdentity;
    private bool _framePresentationAvailable = true;
    private bool _framePulsePending;
    private int? _frameNavigationTarget;

    public bool HasFrameSequence => Presentation.Sequence is { Count: > 1 };
    public bool IsAnimation => Presentation.Sequence?.Kind == ImageSequenceKind.Animation;
    public bool IsAnimationPlaying => _framePlayback?.IsPlaying == true;
    public bool IsAnimationEnded => _framePlayback?.IsEnded == true;
    public bool CanEditStatic => HasImage && !HasFrameSequence && !Presentation.IsSequenceUnavailable;
    public bool CanMovePreviousFrame => HasFrameSequence && (IsAnimation || FrameNavigationIndex > 0);
    public bool CanMoveNextFrame => HasFrameSequence && (IsAnimation || FrameNavigationIndex < Presentation.Sequence!.Count - 1);
    public string AnimationPlayLabel => Text(IsAnimationPlaying ? "Frames_Pause" : "Frames_Play");
    public string AnimationRestartLabel => Text("Frames_Restart");
    public string FramePreviousLabel => Text("Frames_Previous");
    public string FrameNextLabel => Text("Frames_Next");
    public string FramePositionLabel => Text(IsAnimation ? "Frames_Frame" : "Frames_Page");
    public string FrameIndexHint => Text("Frames_IndexHint");
    public string FrameIndexText => (Presentation.FrameIndex + 1).ToString(_localization.CurrentCulture);
    public string FrameCountText => $"/ {Presentation.Sequence?.Count.ToString(_localization.CurrentCulture)}";
    public string FrameStatusText => HasFrameSequence ? string.Format(_localization.CurrentCulture,
        Text(IsAnimation ? "Frames_FramePosition" : "Frames_PagePosition"), Presentation.FrameIndex + 1, Presentation.Sequence!.Count) : string.Empty;
    public string FrameDurationText => Presentation.Sequence is { Kind: ImageSequenceKind.Animation } info
        ? TimeSpan.FromMilliseconds(info.DurationMilliseconds).ToString(@"m\:ss\.ff", _localization.CurrentCulture) : string.Empty;
    public string FrameLoopText => Presentation.Sequence?.TotalPlays is { } plays
        ? string.Format(_localization.CurrentCulture, Text("Frames_FiniteLoop"), plays) : Text("Frames_InfiniteLoop");
    public string FrameDurationLabel => Text("Frames_Duration");
    public string FrameLoopLabel => Text("Frames_Loop");
    internal long RetainedFrameBytes => _coordinator.RetainedFrameBytes;
    internal int ActiveFrameControllers => _framePlayback is null ? 0 : 1;
    internal long NextFrameDueMilliseconds => _framePlayback?.NextDueMilliseconds ?? 100;
    private int FrameNavigationIndex => _frameNavigationTarget ?? Presentation.FrameIndex;

    public void ToggleAnimation() => _ = ToggleAnimationAsync();

    public async Task ToggleAnimationAsync()
    {
        if (_disposed || _framePlayback is not { } playback || !_coordinator.HasFrameSession) { return; }
        if (playback.IsPlaying)
        {
            playback.Pause();
            _coordinator.CancelPendingRegion();
        }
        else if (playback.IsEnded) { playback.Restart(); }
        else { playback.Play(); }
        NotifyFrameProperties();
        if (playback.Index != Presentation.FrameIndex) { await _coordinator.PresentFrameAsync(playback.Index); }
    }

    public void RestartAnimation() => _ = RestartAnimationAsync();

    public async Task RestartAnimationAsync()
    {
        if (_disposed || _framePlayback is not { } playback || !_coordinator.HasFrameSession) { return; }
        playback.Restart();
        NotifyFrameProperties();
        await _coordinator.PresentFrameAsync(0);
    }

    internal void StopFramePresentation()
    {
        StopFramePlayback();
        if (!_disposed) { _coordinator.CancelPendingOpen(); }
    }

    private void NotifyFramePosition()
    {
        OnPropertyChanged(nameof(FrameIndexText));
        OnPropertyChanged(nameof(FrameStatusText));
        OnPropertyChanged(nameof(CanMovePreviousFrame));
        OnPropertyChanged(nameof(CanMoveNextFrame));
    }

    public void SetFramePresentationAvailable(bool available)
    {
        _framePresentationAvailable = available;
        _framePlayback?.SetSuspended(!available);
        NotifyFrameProperties();
    }

    public Task ReleaseFrameObservationResourcesAsync()
    {
        StopFramePlayback();
        return _coordinator.CloseImageAsync();
    }

    public async Task PulseAnimationAsync()
    {
        if (_disposed || _framePulsePending || _framePlayback is not { } playback
            || !playback.IsPlaying || playback.IsSuspended || IsLoading || !_coordinator.HasFrameSession) { return; }
        _framePulsePending = true;
        bool wasPlaying = playback.IsPlaying;
        try
        {
            int target = playback.GetTargetFrame();
            if (target != Presentation.FrameIndex)
            {
                bool presented = await _coordinator.PresentPlaybackFrameAsync(target);
                if (!presented && ReferenceEquals(playback, _framePlayback) && Presentation.RefinementError != ImageOpenError.None)
                {
                    playback.Pause();
                }
            }
        }
        finally
        {
            _framePulsePending = false;
            if (wasPlaying != playback.IsPlaying) { NotifyFrameProperties(); }
        }
    }

    public Task<bool> MoveFrameAsync(int delta)
    {
        if (!HasFrameSequence) { return Task.FromResult(false); }
        int index = FrameNavigationIndex + delta;
        int count = Presentation.Sequence!.Count;
        if (IsAnimation) { index = (index % count + count) % count; }
        else { index = Math.Clamp(index, 0, count - 1); }
        return SeekFrameAsync(index);
    }

    public async Task<bool> SeekFrameAsync(int index)
    {
        if (_disposed || !HasFrameSequence || !_coordinator.HasFrameSession || index < 0 || index >= Presentation.Sequence!.Count)
        {
            return false;
        }
        _framePlayback?.Seek(index);
        _frameNavigationTarget = index;
        NotifyFrameProperties();
        try { return await _coordinator.PresentFrameAsync(index); }
        finally
        {
            if (_frameNavigationTarget == index) { _frameNavigationTarget = null; }
            NotifyFrameProperties();
        }
    }

    private void UpdateFramePlayback(ImageOpenState state)
    {
        if (state.Status == ImageOpenStatus.Loading || !_coordinator.HasFrameSession)
        {
            StopFramePlayback();
            return;
        }
        if (_frameSourceIdentity == state.Source?.Identity) { return; }
        StopFramePlayback();
        _frameSourceIdentity = state.Source?.Identity;
        if (state.Sequence is { Kind: ImageSequenceKind.Animation, Count: > 1 } info)
        {
            _framePlayback = new FramePlaybackController(info);
            _framePlayback.SetSuspended(!_framePresentationAvailable);
            _framePlayback.Play();
        }
    }

    private void StopFramePlayback()
    {
        _framePlayback?.Pause();
        _framePlayback = null;
        _frameSourceIdentity = null;
        _frameNavigationTarget = null;
    }

    private void NotifyFrameProperties()
    {
        if (_disposed) { return; }
        string[] properties = [nameof(HasFrameSequence), nameof(IsAnimation), nameof(IsAnimationPlaying),
            nameof(IsAnimationEnded), nameof(AnimationPlayLabel), nameof(AnimationRestartLabel), nameof(FrameStatusText),
            nameof(FrameIndexText), nameof(FrameCountText), nameof(FramePositionLabel), nameof(FrameDurationText),
            nameof(FrameLoopText), nameof(FrameDurationLabel), nameof(FrameLoopLabel), nameof(FramePreviousLabel),
            nameof(FrameNextLabel), nameof(CanMovePreviousFrame), nameof(CanMoveNextFrame), nameof(CanEditStatic)];
        foreach (string property in properties) { OnPropertyChanged(property); }
    }
}
