using ModernImageViewer.Imaging;

namespace ModernImageViewer.Application.Images;

public sealed class NeighborPreviewCache : IDisposable
{
    public const long MaximumCachedBytes = 4 * 1024 * 1024;

    public static PixelSize MaximumPreviewSize { get; } = new(1280, 800);

    private readonly object _gate = new();
    private readonly IPrefetchImageDecoder _decoder;
    private CancellationTokenSource? _pendingCancellation;
    private Task _pendingTask = Task.CompletedTask;
    private Entry? _entry;
    private long _version;
    private bool _disposed;

    public long RetainedBytes
    {
        get { lock (_gate) { return _entry?.Buffer.Pixels.Length ?? 0; } }
    }

    public NeighborPreviewCache(IPrefetchImageDecoder decoder)
    {
        ArgumentNullException.ThrowIfNull(decoder);
        _decoder = decoder;
    }

    public void Schedule(string path)
    {
        CancellationTokenSource? previous;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            previous = _pendingCancellation;
            CancellationTokenSource cancellation = new();
            _pendingCancellation = cancellation;
            long version = ++_version;
            _pendingTask = Task.Run(() => PrefetchAsync(path, version, cancellation));
        }

        Cancel(previous);
    }

    // Foreground work cancels speculative decoding but can still consume a completed preview.
    public void CancelPending()
    {
        CancellationTokenSource? cancellation;
        lock (_gate)
        {
            ++_version;
            cancellation = _pendingCancellation;
            _pendingCancellation = null;
        }

        Cancel(cancellation);
    }

    public async Task<PixelBuffer?> TryTakeAsync(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Entry? entry;
        lock (_gate)
        {
            entry = _disposed ? null : _entry;
        }

        if (entry is null)
        {
            return null;
        }

        FileStamp stamp;
        try
        {
            if (!string.Equals(entry.Stamp.Path, Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }
            stamp = await Task.Run(() => ReadStamp(path), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            Remove(entry);
            return null;
        }

        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (_disposed || !ReferenceEquals(_entry, entry))
            {
                return null;
            }

            if (!string.Equals(entry.Stamp.Path, stamp.Path, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            _entry = null;
            if (entry.Stamp.Length == stamp.Length && entry.Stamp.LastWriteTimeUtc == stamp.LastWriteTimeUtc)
            {
                // Removing the entry transfers the only ownership of this buffer to the caller.
                return entry.Buffer;
            }
        }

        entry.Buffer.Dispose();
        return null;
    }

    public Task WaitForPendingAsync()
    {
        lock (_gate)
        {
            return _pendingTask;
        }
    }

    public void Clear()
    {
        Entry? entry;
        CancellationTokenSource? cancellation;
        lock (_gate)
        {
            ++_version;
            entry = _entry;
            _entry = null;
            cancellation = _pendingCancellation;
            _pendingCancellation = null;
        }

        Cancel(cancellation);
        entry?.Buffer.Dispose();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
        }

        Clear();
    }

    private async Task PrefetchAsync(string path, long version, CancellationTokenSource cancellation)
    {
        PixelBuffer? buffer = null;
        try
        {
            CancellationToken token = cancellation.Token;
            await Task.Delay(TimeSpan.FromMilliseconds(250), token).ConfigureAwait(false);
            FileStamp stamp = ReadStamp(path);
            buffer = await _decoder.TryDecodePreviewAsync(stamp.Path, MaximumPreviewSize, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (buffer is null || buffer.Size.Width > MaximumPreviewSize.Width || buffer.Size.Height > MaximumPreviewSize.Height
                || buffer.Pixels.Length > MaximumCachedBytes || stamp != ReadStamp(stamp.Path))
            {
                return;
            }

            Entry? previous;
            lock (_gate)
            {
                if (_disposed || version != _version || token.IsCancellationRequested)
                {
                    return;
                }

                previous = _entry;
                _entry = new Entry(stamp, buffer);
                buffer = null;
            }

            previous?.Buffer.Dispose();
        }
        catch (Exception)
        {
            // Speculative I/O never interrupts the current image or retries a busy decoder.
        }
        finally
        {
            buffer?.Dispose();
            lock (_gate)
            {
                if (ReferenceEquals(_pendingCancellation, cancellation))
                {
                    _pendingCancellation = null;
                }
            }

            cancellation.Dispose();
        }
    }

    private void Remove(Entry entry)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(_entry, entry))
            {
                return;
            }

            _entry = null;
        }

        entry.Buffer.Dispose();
    }

    private static void Cancel(CancellationTokenSource? cancellation)
    {
        try
        {
            cancellation?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The background operation may have completed after cancellation was detached.
        }
    }

    private static FileStamp ReadStamp(string path)
    {
        FileInfo file = new(Path.GetFullPath(path));
        return new FileStamp(file.FullName, file.Length, file.LastWriteTimeUtc);
    }

    private sealed record Entry(FileStamp Stamp, PixelBuffer Buffer);

    private readonly record struct FileStamp(string Path, long Length, DateTime LastWriteTimeUtc);
}
