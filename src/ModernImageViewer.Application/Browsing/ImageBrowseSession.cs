namespace ModernImageViewer.Application.Browsing;

public sealed class ImageBrowseSession
{
    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg",
        ".jpeg",
        ".png",
    };

    private string[] _items = [];

    public string? CurrentPath { get; private set; }

    public int CurrentIndex { get; private set; } = -1;

    public int Count => _items.Length;

    public bool CanMovePrevious => CurrentIndex > 0;

    public bool CanMoveNext => CurrentIndex >= 0 && CurrentIndex < _items.Length - 1;

    public static bool IsSupported(string path) => SupportedExtensions.Contains(Path.GetExtension(path));

    public string? GetPreviousPath() => CanMovePrevious ? _items[CurrentIndex - 1] : null;

    public string? GetNextPath() => CanMoveNext ? _items[CurrentIndex + 1] : null;

    public void Commit(string path)
    {
        string fullPath = Path.GetFullPath(path);
        string? directory = Path.GetDirectoryName(fullPath);

        try
        {
            _items = directory is null
                ? [fullPath]
                : Directory.EnumerateFiles(directory)
                    .Where(IsSupported)
                    .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
        }
        catch (IOException)
        {
            _items = [fullPath];
        }
        catch (UnauthorizedAccessException)
        {
            _items = [fullPath];
        }

        int index = FindIndex(fullPath);
        if (index < 0)
        {
            _items = [fullPath];
            index = 0;
        }

        CurrentPath = fullPath;
        CurrentIndex = index;
    }

    private int FindIndex(string path)
    {
        for (int index = 0; index < _items.Length; index++)
        {
            if (StringComparer.OrdinalIgnoreCase.Equals(_items[index], path))
            {
                return index;
            }
        }

        return -1;
    }
}
