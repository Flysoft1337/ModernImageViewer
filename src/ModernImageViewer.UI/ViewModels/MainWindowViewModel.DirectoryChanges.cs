using System.IO;

using ModernImageViewer.Application.Browsing;

namespace ModernImageViewer.UI.ViewModels;

public sealed partial class MainWindowViewModel
{
    internal string? WatchedDirectory => _browseSession.ActiveDirectory;
    public bool CanRefreshFolder => !Presentation.IsMemorySource && (HasImage || WatchedDirectory is not null);

    internal async Task<bool> RefreshWatchedDirectoryAsync(string directory, IReadOnlyList<BrowseFileRename> renames,
        Func<bool> batchIsCurrent, CancellationToken cancellationToken)
    {
        if (_disposed || !StringComparer.OrdinalIgnoreCase.Equals(directory, WatchedDirectory)) { return true; }
        if (!CanApplyWatchedChanges()) { return false; }

        long input = _inputVersion;
        TaskCompletionSource? commit = null;
        bool IsCurrent() => !_disposed && !cancellationToken.IsCancellationRequested && input == _inputVersion
            && StringComparer.OrdinalIgnoreCase.Equals(directory, WatchedDirectory);

        try
        {
            return await _coordinator.ReconcileDirectoryAsync(directory, renames, changed =>
            {
                if (!IsCurrent() || !CanApplyWatchedChanges() || !batchIsCurrent()) { return false; }
                commit = new(TaskCreationOptions.RunContinuationsAsynchronously);
                _fileOperation = commit;
                if (_messageKey is "Error_FolderReadFailed" or "Error_FolderAccessDenied" or "FolderWatch_Unavailable")
                {
                    _messageKey = null;
                }
                if (changed) { InvalidateBrowsingCache(); }
                return true;
            }, cancellationToken);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            if (IsCurrent())
            {
                ShowMessage(exception is IOException ? "Error_FolderReadFailed" : "Error_FolderAccessDenied");
            }
            return true;
        }
        finally
        {
            if (commit is not null)
            {
                _fileOperation = null;
                commit.SetResult();
                if (!_disposed)
                {
                    UpdateBrowseItems();
                    _isSlideshowPlaying = _isSlideshowPlaying && CanPlaySlideshow;
                    NotifyAll();
                }
            }
        }
    }

    private bool CanApplyWatchedChanges() => !IsLoading && !_browseSession.IsIndexing && !IsSorting
        && _folderCancellation is null && _refreshCancellation is null;
}
