using ModernImageViewer.Application.Browsing;

namespace ModernImageViewer.Application.Images;

public sealed partial class ImageOpenCoordinator
{
    public void TrackEmptyDirectory(ImageBrowseSession session, string directory)
    {
        session.CommitEmptyDirectory(directory);
        _currentSession = session;
    }

    public async Task<bool> ReconcileDirectoryAsync(string directory, IReadOnlyList<BrowseFileRename> renames,
        Func<bool, bool> tryBeginCommit, CancellationToken cancellationToken)
    {
        if (_currentSession is not { } session || !StringComparer.OrdinalIgnoreCase.Equals(directory, session.ActiveDirectory))
        {
            return true;
        }

        long request = RequestVersion;
        long revision = session.Revision;
        ImageBrowseSession.BrowseSnapshot snapshot = await session.ReadDirectorySnapshotAsync(directory, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (_disposed || !ReferenceEquals(session, _currentSession) || request != RequestVersion || revision != session.Revision
            || !session.CanApplyDirectorySnapshot(snapshot))
        {
            return false;
        }

        bool changed = !session.Items.SequenceEqual(snapshot.Items, StringComparer.Ordinal);
        if (!tryBeginCommit(changed)) { return false; }

        string? current = State.FilePath;
        string? retained = current is null ? null : FindRetainedPath(snapshot.Items, current, renames);
        if (retained is not null)
        {
            if (!StringComparer.Ordinal.Equals(current, retained))
            {
                await PrepareCurrentFileOperationAsync();
                if (_disposed || cancellationToken.IsCancellationRequested) { return true; }
                session.CommitSnapshot(snapshot, retained);
                await UpdateCurrentFilePathAsync(retained, cancellationToken);
            }
            else
            {
                session.CommitSnapshot(snapshot, retained);
                if (changed)
                {
                    _neighborCache?.Clear();
                    ScheduleNeighborPreview();
                }
            }
            return true;
        }

        int index = Math.Max(0, session.CurrentIndex);
        session.CommitSnapshot(snapshot, path: null);
        if (State.Image is not null)
        {
            await CloseImageAsync();
        }
        if (!_disposed && snapshot.Items.Length > 0)
        {
            string[] candidates = ImageBrowseSession.GetNearbyCandidates(snapshot.Items, index);
            await OpenCandidatesAsync(candidates, session, cancellationToken: cancellationToken);
        }
        return true;
    }

    private static string? FindRetainedPath(string[] items, string current, IReadOnlyList<BrowseFileRename> renames)
    {
        // Prefer a name that still exists, even if a temporary file was renamed over it.
        if (items.Contains(current, StringComparer.Ordinal)) { return current; }
        string path = current;
        foreach (BrowseFileRename rename in renames)
        {
            if (StringComparer.OrdinalIgnoreCase.Equals(path, rename.OldPath)) { path = rename.NewPath; }
        }
        return items.FirstOrDefault(item => StringComparer.OrdinalIgnoreCase.Equals(item, path));
    }
}
