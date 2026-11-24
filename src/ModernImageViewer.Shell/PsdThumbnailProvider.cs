using System.Runtime.InteropServices.Marshalling;

namespace ModernImageViewer.Shell;

[GeneratedComClass]
internal sealed partial class PsdThumbnailProvider : IInitializeWithStream, IThumbnailProvider, IDisposable
{
    private ComReadStream? _stream;
    private bool _initialized;

    public int Initialize(nint stream, uint mode)
    {
        if (stream == 0) { return ComServer.InvalidArgument; }
        if (_initialized) { return ComServer.AlreadyInitialized; }
        _stream = new ComReadStream(stream);
        _initialized = true;
        return ComServer.Ok;
    }

    public int GetThumbnail(uint size, out nint bitmap, out int alphaType)
    {
        bitmap = 0;
        alphaType = 0;
        if (size == 0) { return ComServer.InvalidArgument; }
        if (_stream is null) { return ComServer.Fail; }
        using ComReadStream stream = _stream;
        _stream = null;
        try
        {
            (bitmap, _, _) = ShellPsdBitmap.Decode(stream, (int)Math.Min(size, 1024));
            alphaType = 2; // WTSAT_ARGB
            return ComServer.Ok;
        }
        catch (Exception error) { return error.HResult; }
    }

    public void Dispose()
    {
        _stream?.Dispose();
        _stream = null;
    }
}
