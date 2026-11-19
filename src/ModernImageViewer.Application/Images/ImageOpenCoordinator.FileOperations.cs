using ModernImageViewer.Imaging;

namespace ModernImageViewer.Application.Images;

public sealed partial class ImageOpenCoordinator
{
    public async Task PrepareCurrentFileOperationAsync()
    {
        _neighborCache?.CancelPending();
        CancelPendingRefinement();
        CancelPendingPreviewUpgrade();
        CancelPendingRegion();
        Task release = _frameSession?.ReleaseCompletion ?? Task.CompletedTask;
        StopFrameSession();
        await release;
    }

    public async Task CompleteCurrentFileRenameAsync(string newPath)
    {
        _currentSession?.RenameCurrentFile(newPath);
        await UpdateCurrentFilePathAsync(newPath);
    }

    private async Task UpdateCurrentFilePathAsync(string newPath, CancellationToken cancellationToken = default)
    {
        _neighborCache?.Clear();
        State = State with { Status = ImageOpenStatus.Loaded, FilePath = newPath, Error = ImageOpenError.None, PendingPath = null };
        await RestoreCurrentFileFramesAsync(cancellationToken);
        ScheduleNeighborPreview();
    }

    public async Task<bool> CompleteCurrentFileRecycleAsync()
    {
        string[] candidates = _currentSession?.RemoveCurrentFile() ?? [];
        await CloseImageAsync();
        return candidates.Length == 0 || await OpenCandidatesAsync(candidates, _currentSession);
    }

    public async Task RestoreCurrentFileFramesAsync(CancellationToken cancellationToken = default)
    {
        if (State is not { Sequence: { } sequence, FilePath: { } path } current || decoder is not IImageFrameDecoder frameDecoder)
        {
            return;
        }
        IImageFrameSession? session = null;
        try
        {
            session = await frameDecoder.TryOpenFrameSessionAsync(path, cancellationToken);
            if (_disposed || cancellationToken.IsCancellationRequested) { return; }
            if (session is not null && session.Info.Kind == sequence.Kind && session.Info.Count == sequence.Count)
            {
                _frameSession = session;
                session = null;
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ImageDecodeException
            or ImageSizeLimitExceededException)
        {
            // Retain the current pixels if the original frame session cannot be reopened.
        }
        finally
        {
            session?.Dispose();
        }
        if (_disposed) { return; }
        State = _frameSession is null
            ? current with { Sequence = null, FrameIndex = 0, IsSequenceUnavailable = true }
            : current with { Sequence = _frameSession.Info };
    }
}
