using System.Runtime.InteropServices;

namespace ModernImageViewer.Shell;

internal sealed unsafe class ComReadStream : Stream
{
    private nint _stream;

    internal ComReadStream(nint stream)
    {
        Marshal.AddRef(stream);
        _stream = stream;
    }

    ~ComReadStream() => Release();

    public override bool CanRead => _stream != 0;
    public override bool CanSeek => _stream != 0;
    public override bool CanWrite => false;
    public override long Length
    {
        get
        {
            long position = Position;
            try { return Seek(0, SeekOrigin.End); }
            finally { Position = position; }
        }
    }

    public override long Position
    {
        get => Seek(0, SeekOrigin.Current);
        set => Seek(value, SeekOrigin.Begin);
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        ValidateBufferArguments(buffer, offset, count);
        return Read(buffer.AsSpan(offset, count));
    }

    public override int Read(Span<byte> buffer)
    {
        ObjectDisposedException.ThrowIf(_stream == 0, this);
        if (buffer.IsEmpty) { return 0; }
        var read = (delegate* unmanaged[Stdcall]<nint, byte*, uint, uint*, int>)(*(nint**)_stream)[3];
        fixed (byte* destination = buffer)
        {
            uint count = 0;
            Marshal.ThrowExceptionForHR(read(_stream, destination, (uint)buffer.Length, &count));
            return checked((int)count);
        }
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        ObjectDisposedException.ThrowIf(_stream == 0, this);
        var seek = (delegate* unmanaged[Stdcall]<nint, long, uint, ulong*, int>)(*(nint**)_stream)[5];
        ulong position = 0;
        Marshal.ThrowExceptionForHR(seek(_stream, offset, (uint)origin, &position));
        return checked((long)position);
    }

    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        Release();
        base.Dispose(disposing);
    }

    private void Release()
    {
        nint stream = Interlocked.Exchange(ref _stream, 0);
        if (stream != 0) { Marshal.Release(stream); }
    }
}
