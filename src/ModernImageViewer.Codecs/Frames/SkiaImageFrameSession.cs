using System.IO;
using System.Runtime.InteropServices;

using ModernImageViewer.Application.Images;
using ModernImageViewer.Codecs.Wic;
using ModernImageViewer.Imaging;

using SkiaSharp;

namespace ModernImageViewer.Codecs.Frames;

internal sealed class SkiaImageFrameSession : IImageFrameSession
{
    private static int _activeSessions;
    private static int _activeDecoders;
    // Admission bound for a source-sized workspace, not a cap on all native allocations.
    // Oversized animations signal ImageTooLarge; the coordinator keeps a static preview with feedback.
    internal const long MaximumSourceWorkspaceBytes = 92L * 1024 * 1024;
    private readonly object _lifetime = new();
    private readonly SemaphoreSlim _serial = new(1, 1);
    private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly string _path;
    private readonly FileStream _file;
    private readonly SKManagedStream _stream;
    private readonly SKCodec _codec;
    private readonly PixelSize _source;
    private readonly ushort _orientation;
    private readonly bool[] _completeFrames;
    private byte[]? _reference;
    private PixelSize _referenceSize;
    private int _referenceIndex = -1;
    private long _retainedBytes;
    private int _requests;
    private bool _disposed;
    private bool _releaseQueued;

    private SkiaImageFrameSession(string path, FileStream file, SKManagedStream stream, SKCodec codec,
        PixelSize source, ushort orientation, bool[] completeFrames, ImageSequenceInfo info, ImageFileStamp fileStamp)
    {
        _path = path;
        _file = file;
        _stream = stream;
        _codec = codec;
        _source = source;
        _orientation = orientation;
        _completeFrames = completeFrames;
        Info = info;
        FileStamp = fileStamp;
        Interlocked.Increment(ref _activeSessions);
    }

    public ImageSequenceInfo Info { get; }
    public ImageFileStamp FileStamp { get; }
    public long RetainedPixelBytes => Interlocked.Read(ref _retainedBytes);
    public Task ReleaseCompletion => _closed.Task;
    internal Task Completion => ReleaseCompletion;
    internal static int ActiveSessionCount => Volatile.Read(ref _activeSessions);
    internal static int ActiveDecoderCount => Volatile.Read(ref _activeDecoders);

    internal static async Task<IImageFrameSession?> TryOpenAsync(string path, CancellationToken cancellationToken)
    {
        using IDisposable lease = await WicImageDecoder.DecodeSlot.AcquireAsync(
            DecodePriority.Foreground, cancellationToken).ConfigureAwait(false);
        SkiaImageFrameSession? session = await Task.Run(() => Open(path, cancellationToken), cancellationToken).ConfigureAwait(false);
        if (cancellationToken.IsCancellationRequested)
        {
            session?.Dispose();
            cancellationToken.ThrowIfCancellationRequested();
        }
        return session;
    }

    private static SkiaImageFrameSession? Open(string path, CancellationToken cancellationToken)
    {
        FileStream? file = null;
        SKManagedStream? stream = null;
        SKCodec? codec = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            Span<byte> signature = stackalloc byte[12];
            int length = file.ReadAtLeast(signature, signature.Length, throwOnEndOfStream: false);
            bool gif = length >= 6 && (signature[..6].SequenceEqual("GIF87a"u8) || signature[..6].SequenceEqual("GIF89a"u8));
            bool webp = length == 12 && signature[..4].SequenceEqual("RIFF"u8) && signature[8..].SequenceEqual("WEBP"u8);
            if (!gif && !webp) { return null; }
            if (file.Length > ImageFrameLimits.MaximumInputBytes) { throw new ImageSizeLimitExceededException(); }
            file.Position = 0;
            ImageFileStamp stamp = ImageDecoder.ReadFileStamp(path, file);
            stream = new SKManagedStream(file, disposeManagedStream: false);
            codec = SKCodec.Create(stream) ?? throw new ImageDecodeException(ImageOpenError.CorruptFile);
            Interlocked.Increment(ref _activeDecoders);
            if (codec.EncodedFormat != (gif ? SKEncodedImageFormat.Gif : SKEncodedImageFormat.Webp))
            {
                throw new ImageDecodeException(ImageOpenError.UnsupportedFormat);
            }
            PixelSize source = new(codec.Info.Width, codec.Info.Height);
            ImageDecodeLimits.Default.ValidateAndGetStride(source);
            int count = codec.FrameCount;
            cancellationToken.ThrowIfCancellationRequested();
            if (count <= 1) { return null; }
            if (count > ImageFrameLimits.MaximumFrames || (long)count * 128 > ImageFrameLimits.MaximumMetadataBytes)
            {
                throw new ImageSizeLimitExceededException();
            }
            if (source.PixelCount * 4 > MaximumSourceWorkspaceBytes) { throw new ImageSizeLimitExceededException(); }
            ushort orientation = (ushort)codec.EncodedOrigin;
            if (orientation is < 1 or > 8) { orientation = 1; }
            PixelSize canvas = orientation >= 5 ? new(source.Height, source.Width) : source;
            ImageFrameInfo[] frames = new ImageFrameInfo[count];
            bool[] completeFrames = new bool[count];
            for (int i = 0; i < count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                bool validInfo = codec.GetFrameInfo(i, out SKCodecFrameInfo frame)
                    && frame.RequiredFrame >= -1 && frame.RequiredFrame < i && frame.Duration >= 0
                    && frame.FrameRect.Left >= 0 && frame.FrameRect.Top >= 0 && !frame.FrameRect.IsEmpty
                    && frame.FrameRect.Right <= source.Width && frame.FrameRect.Bottom <= source.Height;
                completeFrames[i] = validInfo && frame.FullyRecieved;
                if (!completeFrames[i] && i == 0)
                {
                    throw new ImageDecodeException(ImageOpenError.CorruptFile);
                }
                if (!validInfo)
                {
                    // Preserve earlier valid frames. This placeholder is never decoded/published.
                    frames[i] = new ImageFrameInfo(i, canvas, new PixelRect(0, 0, canvas.Width, canvas.Height),
                        DurationMilliseconds: 100);
                    continue;
                }
                PixelRect bounds = OrientBounds(frame.FrameRect, source, orientation);
                int duration = frame.Duration < 20 ? 100 : frame.Duration;
                frames[i] = new ImageFrameInfo(i, canvas, bounds, frame.Duration, duration,
                    frame.Blend == SKCodecAnimationBlend.Src ? ImageFrameBlend.Source : ImageFrameBlend.Over,
                    frame.DisposalMethod switch
                    {
                        SKCodecAnimationDisposalMethod.RestoreBackgroundColor => ImageFrameDisposal.Background,
                        SKCodecAnimationDisposalMethod.RestorePrevious => ImageFrameDisposal.Previous,
                        _ => ImageFrameDisposal.Keep,
                    }, frame.RequiredFrame, frame.RequiredFrame >= 0 || bounds.Size != canvas);
            }
            int repeat = codec.RepetitionCount;
            int? totalPlays = repeat < 0 ? null : (int)Math.Min(int.MaxValue, (long)repeat + 1);
            cancellationToken.ThrowIfCancellationRequested();
            ImageDecoder.ValidateFileStamp(path, file, stamp);
            SkiaImageFrameSession session = new(path, file, stream, codec, source, orientation, completeFrames,
                new ImageSequenceInfo(ImageSequenceKind.Animation, Array.AsReadOnly(frames), totalPlays), stamp);
            file = null;
            stream = null;
            codec = null;
            return session;
        }
        finally
        {
            try
            {
                if (codec is not null)
                {
                    try { codec.Dispose(); }
                    finally { Interlocked.Decrement(ref _activeDecoders); }
                }
            }
            finally
            {
                try { stream?.Dispose(); }
                finally { file?.Dispose(); }
            }
        }
    }

    public Task<PixelBuffer> DecodeFrameAsync(int index, PixelSize maximumSize, long maximumDecodedBytes,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, Info.Count);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumDecodedBytes);
        if (maximumDecodedBytes < 4) { throw new ImageSizeLimitExceededException(); }
        lock (_lifetime)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _requests++;
        }
        return DecodeRequestAsync(index, maximumSize, maximumDecodedBytes, cancellationToken);
    }

    private async Task<PixelBuffer> DecodeRequestAsync(int index, PixelSize maximumSize, long maximumDecodedBytes,
        CancellationToken cancellationToken)
    {
        bool acquired = false;
        try
        {
            await _serial.WaitAsync(cancellationToken).ConfigureAwait(false);
            acquired = true;
            ThrowIfClosed(cancellationToken);
            PixelSize fitted = FitTarget(maximumSize, maximumDecodedBytes);
            List<int> plan = [];
            int current = index;
            while (true)
            {
                ThrowIfClosed(cancellationToken);
                if (!_completeFrames[current]) { throw new ImageDecodeException(ImageOpenError.CorruptFile); }
                plan.Add(current);
                int required = Info.Frames[current].RequiredFrame;
                if (required < 0 || (_reference is not null && _referenceSize == fitted
                    && _referenceIndex >= required && _referenceIndex < current)) { break; }
                if (Info.Frames[required].Disposal == ImageFrameDisposal.Previous)
                {
                    throw new ImageDecodeException(ImageOpenError.CorruptFile);
                }
                current = required;
            }
            // Decode dependencies iteratively, releasing Detail between frames for foreground work.
            // GetPixels never recursively walks an unbounded native dependency chain here.
            for (int i = plan.Count - 1; i >= 0; i--)
            {
                int frameIndex = plan[i];
                PixelBuffer decoded = await WicImageDecoder.DecodeSlot.RunAsync(DecodePriority.Detail,
                    () => Decode(frameIndex, maximumSize, maximumDecodedBytes, cancellationToken), cancellationToken).ConfigureAwait(false);
                if (i == 0) { return decoded; }
                decoded.Dispose();
            }
            throw new ImageDecodeException(ImageOpenError.CorruptFile);
        }
        catch (OperationCanceledException) when (acquired)
        {
            _reference = null;
            _referenceIndex = -1;
            Interlocked.Exchange(ref _retainedBytes, 0);
            throw;
        }
        finally
        {
            if (acquired) { _serial.Release(); }
            lock (_lifetime)
            {
                _requests--;
                QueueReleaseIfIdle();
            }
        }
    }

    private PixelBuffer Decode(int index, PixelSize maximumSize, long maximumDecodedBytes, CancellationToken cancellationToken)
    {
        ThrowIfClosed(cancellationToken);
        ImageDecoder.ValidateFileStamp(_path, _file, FileStamp);
        if (!_completeFrames[index]) { throw new ImageDecodeException(ImageOpenError.CorruptFile); }
        PixelSize fitted = FitTarget(maximumSize, maximumDecodedBytes);
        float scale = Math.Min((float)fitted.Width / _source.Width, (float)fitted.Height / _source.Height);
        SKSizeI dimensions = _codec.GetScaledDimensions(scale);
        PixelSize size = new(dimensions.Width, dimensions.Height);
        int stride = ImageDecodeLimits.Default.ValidateAndGetStride(size);
        long bytes = (long)stride * size.Height;
        if (size.Width > fitted.Width || size.Height > fitted.Height
            || bytes > Math.Min(maximumDecodedBytes, ImageFrameLimits.MaximumFrameBytes))
        {
            throw new ImageDecodeException(ImageOpenError.UnsupportedFormat);
        }
        byte[] pixels = GC.AllocateUninitializedArray<byte>(checked((int)bytes), pinned: true);
        int required = Info.Frames[index].RequiredFrame;
        int prior = -1;
        if (required >= 0 && _reference is not null && _referenceSize == size
            && _referenceIndex >= required && _referenceIndex < index)
        {
            _reference.CopyTo(pixels, 0);
            prior = _referenceIndex;
        }
        if (required >= 0 && prior < 0) { throw new ImageDecodeException(ImageOpenError.CorruptFile); }
        using SKColorSpace srgb = SKColorSpace.CreateSrgb();
        SKImageInfo info = new(size.Width, size.Height, SKColorType.Bgra8888, SKAlphaType.Premul, srgb);
        SKCodecOptions options = new(index, prior);
        ThrowIfClosed(cancellationToken);
        GCHandle pin = GCHandle.Alloc(pixels, GCHandleType.Pinned);
        SKCodecResult result;
        try { result = _codec.GetPixels(info, pin.AddrOfPinnedObject(), stride, options); }
        finally { pin.Free(); }
        // Native calls finish before cancellation/closure can release any owned resources.
        ThrowIfClosed(cancellationToken);
        if (result != SKCodecResult.Success) { throw new ImageDecodeException(ImageOpenError.CorruptFile); }
        ImageDecoder.ValidateFileStamp(_path, _file, FileStamp);
        byte[] output = _orientation == 1 ? pixels : (byte[])pixels.Clone();
        PixelSize outputSize = PixelOrientation.ApplyInPlace(output, size, _orientation, cancellationToken);
        lock (_lifetime)
        {
            ThrowIfClosed(cancellationToken);
            if (Info.Frames[index].Disposal != ImageFrameDisposal.Previous)
            {
                _reference = pixels;
                _referenceSize = size;
                _referenceIndex = index;
                Interlocked.Exchange(ref _retainedBytes, bytes);
            }
            else if (_referenceSize != size)
            {
                _reference = null;
                _referenceIndex = -1;
                Interlocked.Exchange(ref _retainedBytes, 0);
            }
            return new PixelBuffer(outputSize, checked(outputSize.Width * 4), output,
                ImageMetadata.Empty with { Orientation = _orientation }, Info.Frames[index].CanvasSize, FileStamp);
        }
    }

    private void ThrowIfClosed(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_lifetime) { ObjectDisposedException.ThrowIf(_disposed, this); }
    }

    private PixelSize FitTarget(PixelSize maximumSize, long maximumDecodedBytes)
    {
        PixelSize maximum = _orientation >= 5 ? new(maximumSize.Height, maximumSize.Width) : maximumSize;
        return ImageFrameLimits.Fit(_source, maximum, Math.Min(maximumDecodedBytes, ImageFrameLimits.MaximumFrameBytes));
    }

    public void Dispose()
    {
        lock (_lifetime)
        {
            _disposed = true;
            QueueReleaseIfIdle();
        }
    }

    private void QueueReleaseIfIdle()
    {
        if (!_disposed || _requests != 0 || _releaseQueued) { return; }
        _releaseQueued = true;
        _ = Task.Run(() =>
        {
            _reference = null;
            Interlocked.Exchange(ref _retainedBytes, 0);
            try
            {
                try
                {
                    try { _codec.Dispose(); }
                    finally { Interlocked.Decrement(ref _activeDecoders); }
                }
                finally
                {
                    try { _stream.Dispose(); }
                    finally
                    {
                        try { _file.Dispose(); }
                        finally { _serial.Dispose(); Interlocked.Decrement(ref _activeSessions); }
                    }
                }
            }
            catch (Exception exception) { _closed.TrySetException(exception); }
            finally { _closed.TrySetResult(); }
        });
    }

    private static PixelRect OrientBounds(SKRectI bounds, PixelSize source, ushort orientation) => orientation switch
    {
        2 => new(source.Width - bounds.Right, bounds.Top, bounds.Width, bounds.Height),
        3 => new(source.Width - bounds.Right, source.Height - bounds.Bottom, bounds.Width, bounds.Height),
        4 => new(bounds.Left, source.Height - bounds.Bottom, bounds.Width, bounds.Height),
        5 => new(bounds.Top, bounds.Left, bounds.Height, bounds.Width),
        6 => new(source.Height - bounds.Bottom, bounds.Left, bounds.Height, bounds.Width),
        7 => new(source.Height - bounds.Bottom, source.Width - bounds.Right, bounds.Height, bounds.Width),
        8 => new(bounds.Top, source.Width - bounds.Right, bounds.Height, bounds.Width),
        _ => new(bounds.Left, bounds.Top, bounds.Width, bounds.Height),
    };
}
