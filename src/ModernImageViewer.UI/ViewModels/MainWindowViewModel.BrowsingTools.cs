using System.IO;

using ModernImageViewer.Application.Browsing;
using ModernImageViewer.Application.Integration;
using ModernImageViewer.UI.Localization;

namespace ModernImageViewer.UI.ViewModels;

public sealed partial class MainWindowViewModel
{
    private readonly Func<IFileRevealService>? _fileReveal;
    private CancellationTokenSource? _sortCancellation;
    private CancellationTokenSource? _revealCancellation;

    internal ILocalizationService Localization => _localization;
    public string SortLabel => Text("Sort_Title");
    public string SortNameLabel => Text("Sort_Name");
    public string SortModifiedLabel => Text("Sort_ModifiedTime");
    public string SortSizeLabel => Text("Sort_Size");
    public string SortAscendingLabel => Text("Sort_Ascending");
    public string SortDescendingLabel => Text("Sort_Descending");
    public string SortHint => Text(_browseSession.IsSelection ? "Sort_SelectionHint" : "Sort_Description");
    public string FileActionsLabel => Text("File_Actions");
    public string RevealLabel => Text("Command_RevealInExplorer");
    public string HelpLabel => Text("Command_ShortcutHelp");
    public BrowseSortMode SortMode => _browseSession.SortMode;
    public bool SortDescending => _browseSession.SortDescending;
    public bool IsSortByName => SortMode == BrowseSortMode.Name;
    public bool IsSortByModifiedTime => SortMode == BrowseSortMode.ModifiedTime;
    public bool IsSortBySize => SortMode == BrowseSortMode.Size;
    public bool IsSortAscending => !SortDescending;
    public bool IsSorting => _browseSession.IsSorting;
    public bool CanSort => HasImage && !IsLoading && _browseSession.CanSort;
    public bool CanReveal => HasImage && !IsLoading && CurrentFilePath.Length > 0 && _revealCancellation is null;

    public async Task<bool> ChangeSortAsync(BrowseSortMode mode, bool descending)
    {
        if (_disposed || !CanSort)
        {
            return false;
        }
        IsSlideshowPlaying = false;
        _sortCancellation?.Cancel();
        using CancellationTokenSource cancellation = new();
        _sortCancellation = cancellation;
        try
        {
            Task<bool> sorting = _browseSession.ChangeSortAsync(mode, descending, cancellation.Token);
            NotifyAll();
            bool changed = await sorting;
            if (changed && !_disposed && !cancellation.IsCancellationRequested)
            {
                _coordinator.RefreshNeighborPrefetch();
                UpdateBrowseItems();
                _messageKey = null;
            }
            return changed;
        }
        catch (OperationCanceledException) { return false; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            if (!_disposed && !cancellation.IsCancellationRequested)
            {
                ShowMessage("Sort_Failed");
            }
            return false;
        }
        finally
        {
            if (ReferenceEquals(_sortCancellation, cancellation))
            {
                _sortCancellation = null;
            }
            if (!_disposed)
            {
                NotifyAll();
            }
        }
    }

    public async Task RevealCurrentFileAsync()
    {
        if (_disposed || !CanReveal)
        {
            return;
        }
        IsSlideshowPlaying = false;
        string path = CurrentFilePath;
        using CancellationTokenSource cancellation = new();
        _revealCancellation = cancellation;
        NotifyAll();
        try
        {
            FileRevealResult result = _fileReveal is null ? FileRevealResult.Unavailable
                : await _fileReveal().RevealAsync(path, cancellation.Token);
            if (_disposed || cancellation.IsCancellationRequested
                || !string.Equals(path, CurrentFilePath, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
            switch (result)
            {
                case FileRevealResult.Success: ShowMessage("File_Revealed"); break;
                case FileRevealResult.MissingFile: ShowMessage("File_RevealMissing"); break;
                case FileRevealResult.Unavailable: ShowMessage("File_RevealFailed"); break;
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            if (ReferenceEquals(_revealCancellation, cancellation))
            {
                _revealCancellation = null;
            }
            if (!_disposed)
            {
                NotifyAll();
            }
        }
    }

    private void CancelBrowsingTools()
    {
        _sortCancellation?.Cancel();
        _browseSession.CancelSorting();
        _revealCancellation?.Cancel();
    }
}
