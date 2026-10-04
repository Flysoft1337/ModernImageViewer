namespace ModernImageViewer.Application.Browsing;

public sealed class ImageBrowseSession
{
    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase) { ".jpg", ".jpeg", ".png" };
    private string[] _items = [];
    private string? _directory;
    private DirectorySnapshot? _prepared;

    public string? CurrentPath { get; private set; }
    public int CurrentIndex { get; private set; } = -1;
    public int Count => _items.Length;
    public IReadOnlyList<string> Items => _items;
    public bool CanMovePrevious => CurrentIndex > 0;
    public bool CanMoveNext => CurrentIndex >= 0 && CurrentIndex < _items.Length - 1;
    public static bool IsSupported(string path) => SupportedExtensions.Contains(Path.GetExtension(path));
    public string? GetPreviousPath() => CanMovePrevious ? _items[CurrentIndex - 1] : null;
    public string? GetNextPath() => CanMoveNext ? _items[CurrentIndex + 1] : null;

    public async Task PrepareAsync(string path, CancellationToken cancellationToken)
    {
        string fullPath = Path.GetFullPath(path);
        string? directory = Path.GetDirectoryName(fullPath);
        if (StringComparer.OrdinalIgnoreCase.Equals(directory, _directory) && FindIndex(fullPath) >= 0)
        {
            return;
        }
        if (_prepared is { } prepared && StringComparer.OrdinalIgnoreCase.Equals(directory, prepared.Directory))
        {
            return;
        }

        string[] items = await Task.Run(() => Enumerate(directory, fullPath, cancellationToken), cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        // Preparing a different directory must not move the visible browsing session.
        _prepared = new DirectorySnapshot(directory, items);
    }

    public async Task<string?> FindFirstAsync(string directory, CancellationToken cancellationToken)
    {
        string fullDirectory = Path.GetFullPath(directory);
        string[] items = await Task.Run(() => Enumerate(fullDirectory, null, cancellationToken), cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        _prepared = new DirectorySnapshot(fullDirectory, items);
        return items.FirstOrDefault();
    }

    public void Commit(string path, bool refresh = false)
    {
        string fullPath = Path.GetFullPath(path);
        string? directory = Path.GetDirectoryName(fullPath);
        if (!refresh && StringComparer.OrdinalIgnoreCase.Equals(directory, _directory)
            && (_prepared is null || !StringComparer.OrdinalIgnoreCase.Equals(directory, _prepared.Directory)))
        {
            int existingIndex = FindIndex(fullPath);
            if (existingIndex >= 0)
            {
                CurrentPath = fullPath;
                CurrentIndex = existingIndex;
                return;
            }
        }

        _items = !refresh && _prepared is { } prepared && StringComparer.OrdinalIgnoreCase.Equals(directory, prepared.Directory)
            ? prepared.Items : Enumerate(directory, fullPath, CancellationToken.None);
        int index = FindIndex(fullPath);
        if (index < 0)
        {
            // Keep a deleted-but-still-displayed image as the current entry while
            // retaining navigation to the other files in the refreshed directory.
            _items = _items.Append(fullPath).OrderBy(Path.GetFileName, NaturalFileNameComparer.Instance).ToArray();
            index = FindIndex(fullPath);
        }

        CurrentPath = fullPath;
        CurrentIndex = index;
        _directory = directory;
        _prepared = null;
    }

    public async Task RefreshAsync(CancellationToken cancellationToken)
    {
        if (CurrentPath is not { } path)
        {
            return;
        }
        string? directory = _directory;
        string[] items = await Task.Run(() => Enumerate(directory, path, cancellationToken), cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (!StringComparer.OrdinalIgnoreCase.Equals(path, CurrentPath))
        {
            return;
        }
        _prepared = new DirectorySnapshot(directory, items);
        _directory = null;
        Commit(path);
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
    private sealed record DirectorySnapshot(string? Directory, string[] Items);
}
