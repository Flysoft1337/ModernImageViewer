using ModernImageViewer.Application.Images;

namespace ModernImageViewer.Application.Browsing;

public sealed class ImageBrowseSession
{
    private readonly Func<string?, string?, CancellationToken, string[]> _enumerate;
    private string[] _items = [];
    private string? _directory;
    private BrowseSnapshot? _prepared;
    private long _revision;
    private bool _hasCompleteIndex;

    public ImageBrowseSession() : this(Enumerate) { }

    internal ImageBrowseSession(Func<string?, string?, CancellationToken, string[]> enumerate) => _enumerate = enumerate;

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
        _prepared = await PrepareSnapshotAsync(path, cancellationToken);
    }

    internal async Task<BrowseSnapshot> PrepareSnapshotAsync(string path, CancellationToken cancellationToken, bool resetSelection = false)
    {
        string fullPath = Path.GetFullPath(path);
        string? directory = Path.GetDirectoryName(fullPath);
        if (!IsIndexing && _hasCompleteIndex && (!resetSelection || !IsSelection) && FindIndex(fullPath) >= 0
            && (IsSelection || StringComparer.OrdinalIgnoreCase.Equals(directory, _directory)))
        {
            return new BrowseSnapshot(_directory, _items, IsSelection);
        }
        if (_prepared is { IsSelection: false } prepared && StringComparer.OrdinalIgnoreCase.Equals(directory, prepared.Directory))
        {
            return prepared;
        }

        string[] items = await Task.Run(() => _enumerate(directory, fullPath, cancellationToken), cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return new BrowseSnapshot(directory, items, false);
    }

    public async Task<string?> FindFirstAsync(string directory, CancellationToken cancellationToken)
    {
        string fullDirectory = Path.GetFullPath(directory);
        string[] items = await Task.Run(() => _enumerate(fullDirectory, null, cancellationToken), cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        _prepared = items.Length == 0 ? null : new BrowseSnapshot(fullDirectory, items, false);
        return items.FirstOrDefault();
    }

    internal bool TryCommitCached(string path, bool resetSelection)
    {
        string fullPath = Path.GetFullPath(path);
        string? directory = Path.GetDirectoryName(fullPath);
        if (_prepared is { IsSelection: false } prepared && StringComparer.OrdinalIgnoreCase.Equals(directory, prepared.Directory))
        {
            CommitSnapshot(prepared, fullPath);
            return true;
        }
        if (!IsIndexing && _hasCompleteIndex && (!resetSelection || !IsSelection) && FindIndex(fullPath) >= 0
            && (IsSelection || StringComparer.OrdinalIgnoreCase.Equals(directory, _directory)))
        {
            CommitSnapshot(new BrowseSnapshot(_directory, _items, IsSelection), fullPath);
            return true;
        }
        return false;
    }

    internal long BeginIndexing(string path)
    {
        string fullPath = Path.GetFullPath(path);
        CommitSnapshot(new BrowseSnapshot(Path.GetDirectoryName(fullPath), [fullPath], false), fullPath);
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
            CommitSnapshot(new BrowseSnapshot(_directory, _items, IsSelection), fullPath);
            return;
        }
        BrowseSnapshot snapshot = !refresh && _prepared is { } prepared
            && (prepared.IsSelection || StringComparer.OrdinalIgnoreCase.Equals(directory, prepared.Directory))
            ? prepared : new BrowseSnapshot(directory, _enumerate(directory, fullPath, CancellationToken.None), false);
        CommitSnapshot(snapshot, fullPath);
    }

    internal void CommitSelection(IReadOnlyList<string> paths, string currentPath) =>
        CommitSnapshot(new BrowseSnapshot(null, paths.ToArray(), true), currentPath);

    internal void CommitSnapshot(BrowseSnapshot snapshot, string path)
    {
        string fullPath = Path.GetFullPath(path);
        _items = snapshot.Items;
        int index = FindIndex(fullPath);
        if (index < 0)
        {
            // Keep a deleted-but-displayed entry without adding unrelated files to a selection.
            _items = snapshot.IsSelection ? _items.Append(fullPath).ToArray()
                : _items.Append(fullPath).OrderBy(Path.GetFileName, NaturalFileNameComparer.Instance).ToArray();
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

    public async Task RefreshAsync(CancellationToken cancellationToken)
    {
        if (CurrentPath is not { } path)
        {
            return;
        }
        long revision = ++_revision;
        IsIndexing = true;
        string? directory = _directory;
        bool isSelection = IsSelection;
        string[] selected = _items;
        try
        {
            string[] items = await Task.Run(() => isSelection
                ? selected.Where(item =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    return StringComparer.OrdinalIgnoreCase.Equals(item, path) || File.Exists(item);
                }).ToArray()
                : _enumerate(directory, path, cancellationToken), cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (revision == _revision)
            {
                CommitSnapshot(new BrowseSnapshot(directory, items, isSelection), path);
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
                string[] sorted = items.OrderBy(Path.GetFileName, NaturalFileNameComparer.Instance).ToArray();
                token.ThrowIfCancellationRequested();
                return sorted;
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return fallback is null ? [] : [fallback];
    }

    private int FindIndex(string path) => Array.FindIndex(_items, item => StringComparer.OrdinalIgnoreCase.Equals(item, path));
    internal sealed record BrowseSnapshot(string? Directory, string[] Items, bool IsSelection);
}
