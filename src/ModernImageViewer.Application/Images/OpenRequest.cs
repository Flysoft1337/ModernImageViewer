namespace ModernImageViewer.Application.Images;

/// <summary>A bounded list of local file-system paths, never shell commands or URI activations.</summary>
public sealed record OpenRequest(IReadOnlyList<string> Paths, int RejectedCount)
{
    public const int MaximumPaths = 128;
    public const int MaximumPathLength = 32768;

    public static OpenRequest Create(IEnumerable<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        string[] supplied = paths.Take(MaximumPaths + 1).ToArray();
        if (supplied.Length > MaximumPaths)
        {
            throw new ArgumentException($"At most {MaximumPaths} paths can be opened at once.", nameof(paths));
        }

        List<string> normalized = [];
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        int rejected = 0;
        foreach (string path in supplied)
        {
            if (string.IsNullOrWhiteSpace(path) || path.Length > MaximumPathLength || IsUri(path))
            {
                rejected++;
                continue;
            }
            try
            {
                string fullPath = Path.GetFullPath(path);
                if (fullPath.Length > MaximumPathLength)
                {
                    rejected++;
                }
                else if (seen.Add(fullPath))
                {
                    normalized.Add(fullPath);
                }
            }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException or IOException)
            {
                rejected++;
            }
        }
        return new OpenRequest(normalized.ToArray(), rejected);
    }

    private static bool IsUri(string path)
    {
        int colon = path.IndexOf(':');
        // A Windows drive prefix is a file-system path; URI schemes are not.
        return colon >= 0 && !(colon == 1 && char.IsAsciiLetter(path[0]));
    }
}
