namespace ModernImageViewer.Application.Browsing;

public sealed partial class ImageBrowseSession
{
    private FileSortEntry[]? _sortEntries;
    private CancellationTokenSource? _sortingCancellation;
    private long _sortGeneration;

    public BrowseSortMode SortMode { get; private set; }
    public bool SortDescending { get; private set; }
    public bool IsSorting { get; private set; }
    public bool CanSort => !IsSelection && !IsIndexing && _hasCompleteIndex && Count > 0;

    public void InitializeSortPreference(BrowseSortMode mode, bool descending)
    {
        if (!Enum.IsDefined(mode))
        {
            throw new ArgumentOutOfRangeException(nameof(mode));
        }
        if (Count != 0 || IsIndexing || IsSorting)
        {
            throw new InvalidOperationException("Sorting preferences must be initialized before opening a session.");
        }
        SortMode = mode;
        SortDescending = descending;
    }

    public async Task<bool> ChangeSortAsync(BrowseSortMode mode, bool descending, CancellationToken cancellationToken)
    {
        if (!Enum.IsDefined(mode))
        {
            throw new ArgumentOutOfRangeException(nameof(mode));
        }
        if (!CanSort || CurrentPath is not { } path || cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        InvalidateSorting();
        if (SortMode == mode && SortDescending == descending)
        {
            return true;
        }

        long generation = _sortGeneration;
        long revision = _revision;
        string? directory = _directory;
        string[] items = _items;
        using CancellationTokenSource cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _sortingCancellation = cancellation;
        IsSorting = true;
        try
        {
            BrowseSnapshot snapshot = await Task.Run(() => SortSnapshot(directory, items, mode, descending, cancellation.Token), cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            if (generation != _sortGeneration || revision != _revision || IsSelection || IsIndexing)
            {
                return false;
            }
            SortMode = mode;
            SortDescending = descending;
            CommitSnapshot(snapshot, path);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        finally
        {
            if (generation == _sortGeneration)
            {
                _sortingCancellation = null;
                IsSorting = false;
            }
        }
    }

    public void CancelSorting() => InvalidateSorting();

    private void InvalidateSorting()
    {
        _sortGeneration++;
        _sortingCancellation?.Cancel();
        _sortingCancellation = null;
        IsSorting = false;
    }

    private bool MatchesSort(BrowseSnapshot snapshot) => snapshot.SortMode == SortMode && snapshot.SortDescending == SortDescending;

    private BrowseSnapshot CurrentSnapshot() => new(_directory, _items, IsSelection, SortMode, SortDescending, _sortEntries);

    private BrowseSnapshot EnumerateSnapshot(string? directory, string? fallback, BrowseSortMode mode, bool descending, CancellationToken token) =>
        SortSnapshot(directory, _enumerate(directory, fallback, token), mode, descending, token);

    private BrowseSnapshot SortSnapshot(string? directory, string[] items, BrowseSortMode mode, bool descending, CancellationToken token)
    {
        FileSortEntry[] entries = new FileSortEntry[items.Length];
        for (int i = 0; i < items.Length; i++)
        {
            token.ThrowIfCancellationRequested();
            FileSortProperties? properties = null;
            if (mode != BrowseSortMode.Name)
            {
                try
                {
                    properties = _readProperties(items[i]);
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
                catch (System.Security.SecurityException) { }
            }
            entries[i] = new FileSortEntry(items[i], properties);
        }
        SortEntries(entries, mode, descending, token);
        return new BrowseSnapshot(directory, entries.Select(entry => entry.Path).ToArray(), false, mode, descending, entries);
    }

    private static void SortEntries(FileSortEntry[] entries, BrowseSortMode mode, bool descending, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        Array.Sort(entries, (left, right) =>
        {
            int result;
            if (mode == BrowseSortMode.Name)
            {
                result = NaturalFileNameComparer.Instance.Compare(left.Name, right.Name);
            }
            else
            {
                // Unreadable attributes always follow readable files, including descending order.
                if (left.Properties is null || right.Properties is null)
                {
                    result = (left.Properties is null).CompareTo(right.Properties is null);
                    if (result != 0)
                    {
                        return result;
                    }
                    return CompareNamesAndPaths(left, right);
                }
                result = mode == BrowseSortMode.Size
                    ? left.Properties.Size.CompareTo(right.Properties.Size)
                    : left.Properties.ModifiedTimeUtc.CompareTo(right.Properties.ModifiedTimeUtc);
            }
            if (result != 0)
            {
                return descending ? -Math.Sign(result) : result;
            }
            return CompareNamesAndPaths(left, right);
        });
        token.ThrowIfCancellationRequested();
    }

    private static int CompareNamesAndPaths(FileSortEntry left, FileSortEntry right)
    {
        int name = NaturalFileNameComparer.Instance.Compare(left.Name, right.Name);
        if (name != 0)
        {
            return name;
        }
        int path = StringComparer.OrdinalIgnoreCase.Compare(left.Path, right.Path);
        return path != 0 ? path : StringComparer.Ordinal.Compare(left.Path, right.Path);
    }

    private static FileSortProperties? ReadProperties(string path)
    {
        FileInfo info = new(path);
        info.Refresh();
        return info.Exists ? new FileSortProperties(info.Length, info.LastWriteTimeUtc) : null;
    }

    internal sealed record FileSortProperties(long Size, DateTime ModifiedTimeUtc);
    internal sealed record FileSortEntry(string Path, FileSortProperties? Properties)
    {
        public string Name { get; } = System.IO.Path.GetFileName(Path);
    }
}
