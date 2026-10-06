using System.Collections.Concurrent;
using System.IO;

using ModernImageViewer.Application.Images;
using ModernImageViewer.Codecs.Wic;
using ModernImageViewer.Imaging;

namespace ModernImageViewer.Codecs.Frames;

internal sealed class TiffImageFrameSession : IImageFrameSession
{
    private const long RegionBytes = 16L * 1024 * 1024;
    private readonly object _gate = new();
    private readonly BlockingCollection<Action> _queue = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private TiffWicContext? _context;
    private (PixelSize Size, ushort Orientation)[] _pages = [];
    private bool _disposed;
    private long _retainedPixelBytes;
    private static int _activeSessions;
    private Exception? _workerError;

    private TiffImageFrameSession()
    {
        Thread thread = new(Work) { IsBackground = true, Name = "TIFF page decoder" };
        thread.SetApartmentState(ApartmentState.MTA);
        Interlocked.Increment(ref _activeSessions);
        try { thread.Start(); }
        catch
        {
            Interlocked.Decrement(ref _activeSessions);
            _queue.Dispose();
            _lifetime.Dispose();
            throw;
        }
    }

    public ImageSequenceInfo Info { get; private set; } = null!;
    public ImageFileStamp FileStamp { get; private set; } = null!;
    public long RetainedPixelBytes => Interlocked.Read(ref _retainedPixelBytes);
    internal Task Completion => _closed.Task;
    public Task ReleaseCompletion => _closed.Task;
    internal static int ActiveSessionCount => Volatile.Read(ref _activeSessions);
    internal static int ActiveContextCount => TiffWicContext.ActiveContextCount;

    internal static async Task<IImageFrameSession?> TryOpenAsync(string path, CancellationToken cancellationToken)
    {
        using IDisposable slot = await WicImageDecoder.DecodeSlot.AcquireAsync(DecodePriority.Foreground, cancellationToken)
            .ConfigureAwait(false);
        bool multipage = await Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            using (FileStream file = new(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                Span<byte> signature = stackalloc byte[4];
                if (file.Read(signature) != 4 || !(signature.SequenceEqual("II\x2a\0"u8)
                    || signature.SequenceEqual("MM\0\x2a"u8))) { return false; }
            }
            TiffWicContext.InitializeThread();
            try
            {
                using TiffWicContext probe = new(path);
                cancellationToken.ThrowIfCancellationRequested();
                return probe.IsTiff && probe.Count > 1;
            }
            finally { TiffWicContext.UninitializeThread(); }
        }, cancellationToken).ConfigureAwait(false);
        if (!multipage) { return null; }
        TiffImageFrameSession session = new();
        try
        {
            // The caller owns the foreground lease throughout probe and initialization.
            await session.Enqueue(token =>
            {
                session._context = new(path);
                TiffWicContext context = session._context;
                if (!context.IsTiff || context.Count <= 1) { throw new ImageDecodeException(ImageOpenError.CorruptFile); }
                if (context.Count > ImageFrameLimits.MaximumFrames
                    || (long)context.Count * 128 > ImageFrameLimits.MaximumMetadataBytes)
                { throw new ImageSizeLimitExceededException(); }
                session._pages = new (PixelSize, ushort)[context.Count];
                ImageFrameInfo[] frames = new ImageFrameInfo[context.Count];
                for (int index = 0; index < frames.Length; index++)
                {
                    token.ThrowIfCancellationRequested();
                    var page = context.Describe(index);
                    session._pages[index] = page;
                    PixelSize size = Oriented(page.Size, page.Orientation);
                    frames[index] = new(index, size, new(0, 0, size.Width, size.Height));
                }
                context.ValidateStamp();
                session.FileStamp = context.Stamp;
                session.Info = new(ImageSequenceKind.Pages, Array.AsReadOnly(frames), 1, true);
                return true;
            }, cancellationToken, acquireDetail: false).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return session;
        }
        catch
        {
            session.Dispose();
            await session.Completion.ConfigureAwait(false);
            throw;
        }
    }

    public Task<PixelBuffer> DecodeFrameAsync(int index, PixelSize maximumSize, long maximumDecodedBytes,
        CancellationToken cancellationToken) => Enqueue(token =>
        {
            var page = GetPage(index);
            if (maximumSize.Width <= 0 || maximumSize.Height <= 0 || maximumDecodedBytes < 4)
            { throw new ImageSizeLimitExceededException(); }
            PixelSize sourceSize = Oriented(page.Size, page.Orientation);
            long budget = Math.Min(maximumDecodedBytes, ImageDecodeLimits.Default.MaximumDecodedBytes);
            PixelSize target = ImageFrameLimits.Fit(sourceSize, maximumSize, budget);
            // A very thin page can round its minor dimension up to one pixel after Fit.
            long maximumPixels = budget / 4;
            if (target.PixelCount > maximumPixels)
            {
                target = target.Width >= target.Height
                    ? new((int)(maximumPixels / target.Height), target.Height)
                    : new(target.Width, (int)(maximumPixels / target.Width));
            }
            PixelSize rawTarget = Oriented(target, page.Orientation);
            byte[] pixels = Allocate(rawTarget);
            _context!.Copy(index, rawTarget, new(0, 0, rawTarget.Width, rawTarget.Height), pixels);
            _context.ValidateStamp();
            token.ThrowIfCancellationRequested();
            PixelSize size = PixelOrientation.ApplyInPlace(pixels, rawTarget, page.Orientation, token);
            return new PixelBuffer(size, size.Width * 4, pixels,
                new ImageMetadata { Orientation = page.Orientation }, sourceSize, FileStamp);
        }, cancellationToken);

    public Task<DecodedImageRegion> DecodeRegionAsync(int index, PixelRect bounds, PixelSize expectedSourceSize,
        long maximumDecodedBytes, CancellationToken cancellationToken) => Enqueue(token =>
        {
            var page = GetPage(index);
            if (bounds.Width <= 0 || bounds.Height <= 0 || bounds.Width > 2048 || bounds.Height > 2048
                || maximumDecodedBytes < 4 || (long)bounds.Width * bounds.Height * 4 > Math.Min(RegionBytes, maximumDecodedBytes))
            { throw new ImageSizeLimitExceededException(); }
            PixelSize sourceSize = Oriented(page.Size, page.Orientation);
            if (sourceSize != expectedSourceSize || bounds.Right > sourceSize.Width || bounds.Bottom > sourceSize.Height)
            { throw new ImageDecodeException(ImageOpenError.CorruptFile); }
            PixelRect rawBounds = RegionOrientation.ToRawBounds(bounds, page.Size, page.Orientation);
            byte[] pixels = Allocate(rawBounds.Size);
            _context!.Copy(index, null, rawBounds, pixels);
            _context.ValidateStamp();
            token.ThrowIfCancellationRequested();
            PixelSize size = PixelOrientation.ApplyInPlace(pixels, rawBounds.Size, page.Orientation, token);
            return new DecodedImageRegion(new PixelBuffer(size, size.Width * 4, pixels,
                new ImageMetadata { Orientation = page.Orientation }, sourceSize, FileStamp), bounds);
        }, cancellationToken);

    private (PixelSize Size, ushort Orientation) GetPage(int index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, _pages.Length);
        _context!.ValidateStamp();
        return _pages[index];
    }

    private byte[] Allocate(PixelSize size)
    {
        int stride = ImageDecodeLimits.Default.ValidateAndGetStride(size);
        byte[] pixels = GC.AllocateUninitializedArray<byte>(checked(stride * size.Height));
        Interlocked.Exchange(ref _retainedPixelBytes, pixels.LongLength);
        return pixels;
    }

    private static PixelSize Oriented(PixelSize size, ushort orientation) =>
        orientation >= 5 ? new(size.Height, size.Width) : size;

    private Task<T> Enqueue<T>(Func<CancellationToken, T> work, CancellationToken cancellationToken, bool acquireDetail = true)
    {
        lock (_gate)
        {
            if (_disposed) { return Task.FromException<T>(new ObjectDisposedException(nameof(TiffImageFrameSession))); }
            CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
            TaskCompletionSource<T> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _queue.Add(() =>
            {
                CancellationToken token = linked.Token;
                T result = default!;
                Exception? failure = null;
                bool canceled = false;
                try
                {
                    token.ThrowIfCancellationRequested();
                    if (_workerError is { } error) { throw new ImageDecodeException(ImageOpenError.DecodeFailed, error); }
                    using IDisposable? slot = acquireDetail
                        ? WicImageDecoder.DecodeSlot.AcquireAsync(DecodePriority.Detail, token).GetAwaiter().GetResult() : null;
                    token.ThrowIfCancellationRequested();
                    result = work(token);
                    if (token.IsCancellationRequested)
                    {
                        if (result is IDisposable disposable) { disposable.Dispose(); }
                        canceled = true;
                    }
                }
                catch (OperationCanceledException) { canceled = true; }
                catch (Exception exception) { failure = exception; }
                finally
                {
                    Interlocked.Exchange(ref _retainedPixelBytes, 0);
                    linked.Dispose();
                }
                // Native work and its lease have ended before any cancellation is observable.
                if (canceled) { completion.TrySetCanceled(token); }
                else if (failure is not null) { completion.TrySetException(failure); }
                else { completion.TrySetResult(result); }
            });
            return completion.Task;
        }
    }

    private void Work()
    {
        bool initialized = false;
        try
        {
            try
            {
                TiffWicContext.InitializeThread();
                initialized = true;
            }
            catch (Exception exception) { _workerError = exception; }
            foreach (Action action in _queue.GetConsumingEnumerable()) { action(); }
        }
        finally
        {
            _context?.Dispose();
            if (initialized) { TiffWicContext.UninitializeThread(); }
            _queue.Dispose();
            _lifetime.Dispose();
            Interlocked.Decrement(ref _activeSessions);
            _closed.TrySetResult();
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) { return; }
            _disposed = true;
            _lifetime.Cancel();
            _queue.CompleteAdding();
        }
    }
}
