using System.IO;

using ModernImageViewer.Application.Integration;

namespace ModernImageViewer.UI.ViewModels;

public sealed partial class MainWindowViewModel
{
    private readonly IImageFileOperations? _fileOperations;
    private TaskCompletionSource? _fileOperation;

    public string RenameFileLabel => Text("File_RenameMenu");
    public string RecycleFileLabel => Text("File_RecycleMenu");
    public bool CanChangeFile => !_disposed && _fileOperations is not null && HasImage && !IsLoading
        && !_browseSession.IsIndexing && !IsSorting && _folderCancellation is null && _refreshCancellation is null
        && CurrentFilePath.Length > 0;
    private bool IsChangingFile => _fileOperation is not null;

    public Task<string?> RenameCurrentFileAsync(string path, string name)
    {
        if (!CanChangeFile || !StringComparer.Ordinal.Equals(path, CurrentFilePath))
        {
            return Task.FromResult<string?>("File_OperationUnavailable");
        }

        if (string.IsNullOrWhiteSpace(name) || name.EndsWith(' ') || name.EndsWith('.')
            || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            return Task.FromResult<string?>("File_InvalidName");
        }

        string newPath = Path.Combine(Path.GetDirectoryName(path)!, name + Path.GetExtension(path));
        return StringComparer.Ordinal.Equals(path, newPath)
            ? Task.FromResult<string?>(null)
            : ChangeCurrentFileAsync(path, newPath);
    }

    public Task<string?> RecycleCurrentFileAsync(string path) => !CanChangeFile || !StringComparer.Ordinal.Equals(path, CurrentFilePath)
        ? Task.FromResult<string?>("File_OperationUnavailable")
        : ChangeCurrentFileAsync(path, newPath: null);

    private async Task<string?> ChangeCurrentFileAsync(string path, string? newPath)
    {
        TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _fileOperation = completion;
        IsSlideshowPlaying = false;
        NotifyAll();
        try
        {
            await _coordinator.PrepareCurrentFileOperationAsync();
            if (_disposed) { return "File_OperationUnavailable"; }
            ImageFileOperationResult result = newPath is null
                ? await _fileOperations!.RecycleAsync(path)
                : await _fileOperations!.RenameAsync(path, newPath);
            if (_disposed) { return null; }
            if (result != ImageFileOperationResult.Success)
            {
                await _coordinator.RestoreCurrentFileFramesAsync();
                return result switch
                {
                    ImageFileOperationResult.MissingFile => "File_OperationMissing",
                    ImageFileOperationResult.NameConflict => "File_NameConflict",
                    ImageFileOperationResult.AccessDenied => "File_OperationAccessDenied",
                    ImageFileOperationResult.FileInUse => "File_OperationInUse",
                    _ => newPath is null ? "File_RecycleFailed" : "File_RenameFailed",
                };
            }

            _navigationTarget = null;
            InvalidateBrowsingCache();
            if (newPath is not null)
            {
                await _coordinator.CompleteCurrentFileRenameAsync(newPath);
                if (!_disposed) { ShowMessage("File_RenameSucceeded"); }
            }
            else
            {
                bool openedNeighborOrEmpty = await _coordinator.CompleteCurrentFileRecycleAsync();
                if (!_disposed)
                {
                    ShowMessage(openedNeighborOrEmpty ? "File_RecycleSucceeded" : "File_RecycleNoReadableNeighbor");
                }
            }
            return null;
        }
        finally
        {
            _fileOperation = null;
            completion.SetResult();
            if (!_disposed) { NotifyAll(); }
        }
    }

    private async Task WaitForFileOperationAsync()
    {
        while (_fileOperation is { } active)
        {
            await active.Task;
        }
    }
}
