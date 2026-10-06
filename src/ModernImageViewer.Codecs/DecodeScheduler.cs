namespace ModernImageViewer.Codecs;

internal enum DecodePriority
{
    Foreground,
    Detail,
    Thumbnail,
}

internal sealed class DecodeScheduler(bool serializeThumbnails = false)
{
    private readonly object _gate = new();
    private readonly LinkedList<Request>[] _queues = [new(), new(), new()];
    private bool _mainActive;
    private int _thumbnailActive;

    internal async Task<IDisposable> AcquireAsync(DecodePriority priority, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Request request = new(priority, cancellationToken);
        lock (_gate)
        {
            request.Node = _queues[(int)priority].AddLast(request);
            Drain();
        }
        using CancellationTokenRegistration registration = cancellationToken.UnsafeRegister(
            _ => Cancel(request), null);
        return await request.Completion.Task.ConfigureAwait(false);
    }

    internal IDisposable? TryAcquirePrefetch(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (_mainActive || _thumbnailActive != 0 || _queues[0].Count != 0 || _queues[1].Count != 0)
            {
                return null;
            }
            _mainActive = true;
            return new Lease(this, thumbnail: false);
        }
    }

    internal async Task<T> RunAsync<T>(DecodePriority priority, Func<T> work, CancellationToken cancellationToken)
        where T : IDisposable
    {
        using IDisposable lease = await AcquireAsync(priority, cancellationToken).ConfigureAwait(false);
        return await RunAcquiredAsync(work, cancellationToken).ConfigureAwait(false);
    }

    internal async Task<T?> TryRunPrefetchAsync<T>(Func<T> work, CancellationToken cancellationToken)
        where T : class, IDisposable
    {
        using IDisposable? lease = TryAcquirePrefetch(cancellationToken);
        return lease is null ? null : await RunAcquiredAsync(work, cancellationToken).ConfigureAwait(false);
    }

    internal async Task<T> RunTaskAsync<T>(DecodePriority priority, Func<Task<T>> work, CancellationToken cancellationToken)
        where T : IDisposable
    {
        using IDisposable lease = await AcquireAsync(priority, cancellationToken).ConfigureAwait(false);
        return await Task.Run(async () =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            T result = await work().ConfigureAwait(false);
            return Complete(result, cancellationToken);
        }, cancellationToken).ConfigureAwait(false);
    }

    private static Task<T> RunAcquiredAsync<T>(Func<T> work, CancellationToken cancellationToken) where T : IDisposable =>
        Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Complete(work(), cancellationToken);
        }, cancellationToken);

    private static T Complete<T>(T result, CancellationToken cancellationToken) where T : IDisposable
    {
        // Cancellation never releases a running slot early. Dispose its late result before
        // signalling cancellation, even when the native codec or async reader ignored the token.
        if (cancellationToken.IsCancellationRequested)
        {
            result.Dispose();
            cancellationToken.ThrowIfCancellationRequested();
        }
        return result;
    }

    // Keep the existing WIC/metadata gate contract, using the same single main-image slot.
    internal async Task WaitAsync(CancellationToken cancellationToken) =>
        _ = await AcquireAsync(DecodePriority.Foreground, cancellationToken).ConfigureAwait(false);

    internal void Wait(CancellationToken cancellationToken) => WaitAsync(cancellationToken).GetAwaiter().GetResult();

    internal Task<bool> WaitAsync(int millisecondsTimeout, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNotEqual(millisecondsTimeout, 0);
        return Task.FromResult(TryAcquirePrefetch(cancellationToken) is not null);
    }

    internal void Release() => Release(thumbnail: false);

    private void Cancel(Request request)
    {
        lock (_gate)
        {
            if (request.Node is null) { return; }
            _queues[(int)request.Priority].Remove(request.Node);
            request.Node = null;
            request.Completion.TrySetCanceled(request.Token);
            Drain();
        }
    }

    private void Release(bool thumbnail)
    {
        lock (_gate)
        {
            if (thumbnail)
            {
                if (_thumbnailActive == 0) { throw new SemaphoreFullException(); }
                _thumbnailActive--;
            }
            else
            {
                if (!_mainActive) { throw new SemaphoreFullException(); }
                _mainActive = false;
            }
            Drain();
        }
    }

    private void Drain()
    {
        if (!_mainActive)
        {
            Request? main = Take(DecodePriority.Foreground) ?? Take(DecodePriority.Detail)
                ?? (serializeThumbnails ? Take(DecodePriority.Thumbnail) : null);
            if (main is not null)
            {
                _mainActive = true;
                main.Completion.SetResult(new Lease(this, thumbnail: false));
            }
        }
        if (serializeThumbnails || _mainActive || _queues[0].Count != 0 || _queues[1].Count != 0) { return; }
        while (_thumbnailActive < 2 && Take(DecodePriority.Thumbnail) is { } thumbnail)
        {
            _thumbnailActive++;
            thumbnail.Completion.SetResult(new Lease(this, thumbnail: true));
        }
    }

    private Request? Take(DecodePriority priority)
    {
        LinkedList<Request> queue = _queues[(int)priority];
        while (queue.First is { } node)
        {
            queue.RemoveFirst();
            Request request = node.Value;
            request.Node = null;
            if (request.Token.IsCancellationRequested)
            {
                request.Completion.TrySetCanceled(request.Token);
                continue;
            }
            return request;
        }
        return null;
    }

    private sealed class Request(DecodePriority priority, CancellationToken token)
    {
        internal DecodePriority Priority { get; } = priority;
        internal CancellationToken Token { get; } = token;
        internal LinkedListNode<Request>? Node { get; set; }
        internal TaskCompletionSource<IDisposable> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class Lease(DecodeScheduler scheduler, bool thumbnail) : IDisposable
    {
        private DecodeScheduler? _scheduler = scheduler;

        public void Dispose() => Interlocked.Exchange(ref _scheduler, null)?.Release(thumbnail);
    }
}
