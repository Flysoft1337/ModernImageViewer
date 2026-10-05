using System.IO;
using System.Runtime.InteropServices;

namespace ModernImageViewer.Codecs.Raw;

// Borrows compressed preview bytes only while the owning LibRaw context remains alive.
internal sealed class NativePreviewStream(nint data, int length) : Stream
{
    private long _position;
    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override long Length => length;
    public override long Position
    {
        get => _position;
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            _position = value;
        }
    }
    public override int Read(byte[] buffer, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        if (offset > buffer.Length - count) { throw new ArgumentException("The read range is invalid."); }
        int available = (int)Math.Min(count, Math.Max(0, length - _position));
        if (available != 0)
        {
            Marshal.Copy(nint.Add(data, checked((int)_position)), buffer, offset, available);
            _position += available;
        }
        return available;
    }
    public override long Seek(long offset, SeekOrigin origin)
    {
        Position = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => checked(_position + offset),
            SeekOrigin.End => checked(length + offset),
            _ => throw new ArgumentOutOfRangeException(nameof(origin)),
        };
        return _position;
    }
    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
