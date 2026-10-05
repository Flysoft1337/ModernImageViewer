using ModernImageViewer.Application.Images;

namespace ModernImageViewer.Application.Browsing;

public sealed partial class ImageBrowseSession
{
    private readonly Func<string?, string?, CancellationToken, string[]> _enumerate;
    private readonly Func<string, FileSortProperties?> _readProperties;
    private string[] _items = [];
    private string? _directory;
    private BrowseSnapshot? _prepared;
    private long _revision;
    private long _prepareGeneration;
    private bool _hasCompleteIndex;

    public ImageBrowseSession() : this(Enumerate) { }

    internal ImageBrowseSession(Func<string?, string?, CancellationToken, string[]> enumerate,
        Func<string, FileSortProperties?>? readProperties = null)
    {
        _enumerate = enumerate;
        _readProperties = readProperties ?? ReadProperties;
    }

    public string? CurrentPath { get; private set; }
    public int CurrentIndex { get; private set; } = -1;
    public int Count => _items.Length;
    public IReadOnlyList<string> Items => _items;
    public bool IsSelection { get; private set; }
    public bool IsIndexing { get; private set; }
    public bool CanMovePrevious => !IsIndexing && CurrentIndex > 0;
    public bool CanMoveNext => !IsIndexing && CurrentIndex >= 0 && CurrentIndex < _items.Length - 1;
    public static bool IsSupported(string path) => SupportedImageFormats.IsSupported(path);
    public string? GetPreviousPath() => CanMovePrevious ? _items[CurrentIndex - 1] : null;
    public string? GetNextPath() => CanMoveNext ? _items[CurrentIndex + 1] : null;

    public async Task PrepareAsync(string path, CancellationToken cancellationToken)
    {
        long preparation = ++_prepareGeneration;
        long revision = _revision;
        long generation = _sortGeneration;
        BrowseSnapshot snapshot = await PrepareSnapshotAsync(path, cancellationToken);
        if (preparation == _prepareGeneration && revision == _revision && generation == _sortGeneration)
        {
            _prepared = snapshot;
        }
    }

    internal async Task<BrowseSnapshot> PrepareSnapshotAsync(string path, CancellationToken cancellationToken, bool resetSelection = false)
    {
        string fullPath = Path.GetFullPath(path);
        string? directory = Path.GetDirectoryName(fullPath);
        if (!IsIndexing && _hasCompleteIndex && (!resetSelection || !IsSelection) && FindIndex(fullPath) >= 0
            && (IsSelection || StringComparer.OrdinalIgnoreCase.Equals(directory, _directory)))
        {
            return CurrentSnapshot();
        }
        if (_prepared is { IsSelection: false } prepared && MatchesSort(prepared)
            && StringComparer.OrdinalIgnoreCase.Equals(directory, prepared.Directory))
        {
            return prepared;
        }

        BrowseSortMode mode = SortMode;
        bool descending = SortDescending;
        BrowseSnapshot snapshot = await Task.Run(() => EnumerateSnapshot(directory, fullPath, mode, descending, cancellationToken), cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return snapshot;
    }

    public async Task<string?> FindFirstAsync(string directory, CancellationToken cancellationToken)
    {
        string fullDirectory = Path.GetFullPath(directory);
        long preparation = ++_prepareGeneration;
        long revision = _revision;
        long generation = _sortGeneration;
        BrowseSortMode mode = SortMode;
        bool descending = SortDescending;
        BrowseSnapshot snapshot = await Task.Run(() => EnumerateSnapshot(fullDirectory, null, mode, descending, cancellationToken), cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (preparation != _prepareGeneration || revision != _revision || generation != _sortGeneration)
        {
            return null;
        }
        _prepared = snapshot.Items.Length == 0 ? null : snapshot;
        return snapshot.Items.FirstOrDefault();
    }

    internal bool TryCommitCached(string path, bool resetSelection)
    {
        string fullPath = Path.GetFullPath(path);
        string? directory = Path.GetDirectoryName(fullPath);
        if (_prepared is { IsSelection: false } prepared && MatchesSort(prepared)
            && StringComparer.OrdinalIgnoreCase.Equals(directory, prepared.Directory))
        {
            CommitSnapshot(prepared, fullPath);
            return true;
        }
        if (!IsIndexing && _hasCompleteIndex && (!resetSelection || !IsSelection) && FindIndex(fullPath) >= 0
            && (IsSelection || StringComparer.OrdinalIgnoreCase.Equals(directory, _directory)))
        {
            CommitSnapshot(CurrentSnapshot(), fullPath);
            return true;
        }
        return false;
    }

    internal long BeginIndexing(string path)
    {
        string fullPath = Path.GetFullPath(path);
        CommitSnapshot(new BrowseSnapshot(Path.GetDirectoryName(fullPath), [fullPath], false, SortMode, SortDescending), fullPath);
        IsIndexing = true;
        _hasCompleteIndex = false;
        return _revision;
    }

    internal bool CompleteIndexing(long revision, BrowseSnapshot? snapshot, string path)
    {
        if (revision != _revision)
        {
            return false;
        }
        if (snapshot is not null)
        {
            CommitSnapshot(snapshot, path);
        }
        else
        {
            IsIndexing = false;
            _revision++;
        }
        return true;
    }

    internal void CancelIndexing()
    {
        if (IsIndexing)
        {
            IsIndexing = false;
            _revision++;
        }
    }

    public void Commit(string path, bool refresh = false)
    {
        string fullPath = Path.GetFullPath(path);
        string? directory = Path.GetDirectoryName(fullPath);
        if (!refresh && FindIndex(fullPath) >= 0
            && (IsSelection || StringComparer.OrdinalIgnoreCase.Equals(directory, _directory))
            && _prepared is null)
        {
            CommitSnapshot(CurrentSnapshot(), fullPath);
            return;
        }
        BrowseSnapshot snapshot = !refresh && _prepared is { } prepared
            && MatchesSort(prepared)
            && (prepared.IsSelection || StringComparer.OrdinalIgnoreCase.Equals(directory, prepared.Directory))
            ? prepared : Task.Run(() => EnumerateSnapshot(directory, fullPath, SortMode, SortDescending, CancellationToken.None)).GetAwaiter().GetResult();
        CommitSnapshot(snapshot, fullPath);
    }

    internal void CommitSelection(IReadOnlyList<string> paths, string currentPath) =>
        CommitSnapshot(new BrowseSnapshot(null, paths.ToArray(), true, SortMode, SortDescending), currentPath);

    internal void CommitSnapshot(BrowseSnapshot snapshot, string path)
    {
        string fullPath = Path.GetFullPath(path);
        InvalidateSorting();
        _items = snapshot.Items;
        _sortEntries = snapshot.Entries;
        int index = FindIndex(fullPath);
        if (index < 0)
        {
            // Keep a deleted-but-displayed entry without adding unrelated files to a selection.
            if (snapshot.IsSelection)
            {
                _items = _items.Append(fullPath).ToArray();
            }
            else
            {
                // A deleted displayed file has no readable attributes; retain it at the end of attribute sorts.
                FileSortEntry[] entries = (_sortEntries ?? _items.Select(item => new FileSortEntry(item, null)).ToArray())
                    .Append(new FileSortEntry(fullPath, null)).ToArray();
                SortEntries(entries, snapshot.SortMode, snapshot.SortDescending, CancellationToken.None);
                _sortEntries = entries;
                _items = entries.Select(entry => entry.Path).ToArray();
            }
            index = FindIndex(fullPath);
        }
        CurrentPath = fullPath;
        CurrentIndex = index;
        _directory = snapshot.Directory;
        IsSelection = snapshot.IsSelection;
        IsIndexing = false;
        _hasCompleteIndex = true;
        _prepared = null;
        _revision++;
    }

    internal void Clear()
    {
        InvalidateSorting();
        _revision++;
        _prepareGeneration++;
        _items = [];
        _sortEntries = null;
        _prepared = null;
        _directory = null;
        _hasCompleteIndex = false;
        CurrentPath = null;
        CurrentIndex = -1;
        IsSelection = false;
        IsIndexing = false;
    }

    public async Task RefreshAsync(CancellationToken cancellationToken)
    {
        if (CurrentPath is not { } path)
        {
            return;
        }
        InvalidateSorting();
        long revision = ++_revision;
        IsIndexing = true;
        string? directory = _directory;
        bool isSelection = IsSelection;
        string[] selected = _items;
        BrowseSortMode mode = SortMode;
        bool descending = SortDescending;
        try
        {
            BrowseSnapshot snapshot = await Task.Run(() => isSelection
                ? new BrowseSnapshot(directory, selected.Where(item =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    return StringComparer.OrdinalIgnoreCase.Equals(item, path) || File.Exists(item);
                }).ToArray(), true, mode, descending)
                : EnumerateSnapshot(directory, path, mode, descending, cancellationToken), cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (revision == _revision)
            {
                CommitSnapshot(snapshot, path);
            }
        }
        finally
        {
            if (revision == _revision)
            {
                IsIndexing = false;
                _revision++;
            }
        }
    }

    private static string[] Enumerate(string? directory, string? fallback, CancellationToken token)
    {
        try
        {
            if (directory is not null)
            {
                List<string> items = [];
                foreach (string path in Directory.EnumerateFiles(directory))
                {
                    token.ThrowIfCancellationRequested();
                    if (IsSupported(path))
                    {
                        items.Add(path);
                    }
                }
                token.ThrowIfCancellationRequested();
                return items.ToArray();
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return fallback is null ? [] : [fallback];
    }

    private int FindIndex(string path) => Array.FindIndex(_items, item => StringComparer.OrdinalIgnoreCase.Equals(item, path));
    internal sealed record BrowseSnapshot(string? Directory, string[] Items, bool IsSelection,
        BrowseSortMode SortMode = BrowseSortMode.Name, bool SortDescending = false, FileSortEntry[]? Entries = null);
}
