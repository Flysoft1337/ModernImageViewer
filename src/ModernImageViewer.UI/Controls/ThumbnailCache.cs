using System.Diagnostics.CodeAnalysis;

namespace ModernImageViewer.UI.Controls;

internal readonly record struct ThumbnailCacheKey(string Path, long LastWriteTimeUtcTicks, long FileLength);

internal sealed class ThumbnailCache<T>(int entryLimit, long byteLimit) where T : class
{
    private readonly Dictionary<ThumbnailCacheKey, LinkedListNode<Entry>> _entries = new(KeyComparer.Instance);
    private readonly LinkedList<Entry> _recency = new();
    private readonly object _gate = new();
    private long _pixelBytes;
    private long _generation;

    public long Generation
    {
        get { lock (_gate) { return _generation; } }
    }

    public bool TryGet(ThumbnailCacheKey key, [NotNullWhen(true)] out T? value)
    {
        lock (_gate)
        {
            if (!_entries.TryGetValue(key, out LinkedListNode<Entry>? node))
            {
                value = null;
                return false;
            }

            _recency.Remove(node);
            _recency.AddFirst(node);
            value = node.Value.Value;
            return true;
        }
    }

    public bool Store(ThumbnailCacheKey key, T value, long pixelBytes, long generation)
    {
        ArgumentNullException.ThrowIfNull(value);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pixelBytes);
        lock (_gate)
        {
            if (generation != _generation || pixelBytes > byteLimit)
            {
                return false;
            }

            if (_entries.TryGetValue(key, out LinkedListNode<Entry>? previous))
            {
                Remove(previous);
            }

            var node = _recency.AddFirst(new Entry(key, value, pixelBytes));
            _entries.Add(key, node);
            _pixelBytes += pixelBytes;
            while (_entries.Count > entryLimit || _pixelBytes > byteLimit)
            {
                Remove(_recency.Last!);
            }

            return true;
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _generation++;
            _entries.Clear();
            _recency.Clear();
            _pixelBytes = 0;
        }
    }

    private void Remove(LinkedListNode<Entry> node)
    {
        _entries.Remove(node.Value.Key);
        _recency.Remove(node);
        _pixelBytes -= node.Value.PixelBytes;
    }

    private sealed record Entry(ThumbnailCacheKey Key, T Value, long PixelBytes);

    private sealed class KeyComparer : IEqualityComparer<ThumbnailCacheKey>
    {
        public static KeyComparer Instance { get; } = new();

        public bool Equals(ThumbnailCacheKey x, ThumbnailCacheKey y) =>
            StringComparer.OrdinalIgnoreCase.Equals(x.Path, y.Path)
            && x.LastWriteTimeUtcTicks == y.LastWriteTimeUtcTicks && x.FileLength == y.FileLength;

        public int GetHashCode(ThumbnailCacheKey obj) => HashCode.Combine(
            StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Path), obj.LastWriteTimeUtcTicks, obj.FileLength);
    }
}
