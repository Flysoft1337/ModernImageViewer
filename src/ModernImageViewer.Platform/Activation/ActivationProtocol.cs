using System.Buffers.Binary;
using System.IO;
using System.Text;

namespace ModernImageViewer.Platform.Activation;

internal static class ActivationProtocol
{
    internal const int MaximumPathCount = 128;
    internal const int MaximumPathLength = 32_768;
    internal const int MaximumMessageLength = 1_048_576;
    private static readonly UTF8Encoding Encoding = new(false, true);

    // A frame is: payload byte length, path count, then (UTF-8 byte length, path) pairs.
    // All lengths are little endian. Check bounds before allocating either frame or paths.
    internal static byte[] Encode(string[] paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        if (paths.Length > MaximumPathCount)
        {
            throw new ArgumentException("Too many activation paths.", nameof(paths));
        }

        int length = sizeof(int) * 2;
        foreach (string path in paths)
        {
            ValidatePath(path);
            length += sizeof(int) + Encoding.GetByteCount(path);
            if (length > MaximumMessageLength)
            {
                throw new ArgumentException("Activation message is too large.", nameof(paths));
            }
        }

        byte[] frame = new byte[length];
        BinaryPrimitives.WriteInt32LittleEndian(frame, length - sizeof(int));
        BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(sizeof(int)), paths.Length);
        int offset = sizeof(int) * 2;
        foreach (string path in paths)
        {
            int byteLength = Encoding.GetByteCount(path);
            BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(offset), byteLength);
            offset += sizeof(int);
            Encoding.GetBytes(path, frame.AsSpan(offset, byteLength));
            offset += byteLength;
        }

        return frame;
    }

    internal static async Task<string[]> ReadAsync(Stream stream, CancellationToken cancellationToken)
    {
        byte[] prefix = new byte[sizeof(int)];
        await stream.ReadExactlyAsync(prefix, cancellationToken).ConfigureAwait(false);
        int length = BinaryPrimitives.ReadInt32LittleEndian(prefix);
        if (length < sizeof(int) || length > MaximumMessageLength - sizeof(int))
        {
            throw new InvalidDataException("Invalid activation frame length.");
        }

        byte[] payload = new byte[length];
        await stream.ReadExactlyAsync(payload, cancellationToken).ConfigureAwait(false);
        return Decode(payload);
    }

    private static string[] Decode(byte[] payload)
    {
        int count = BinaryPrimitives.ReadInt32LittleEndian(payload);
        if (count < 0 || count > MaximumPathCount)
        {
            throw new InvalidDataException("Invalid activation path count.");
        }

        string[] paths = new string[count];
        int offset = sizeof(int);
        for (int index = 0; index < count; index++)
        {
            if (offset > payload.Length - sizeof(int))
            {
                throw new InvalidDataException("Truncated activation path.");
            }

            int byteLength = BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(offset));
            offset += sizeof(int);
            if (byteLength < 0 || byteLength > MaximumPathLength * 3 || byteLength > payload.Length - offset)
            {
                throw new InvalidDataException("Invalid activation path length.");
            }

            string path = Encoding.GetString(payload, offset, byteLength);
            ValidatePath(path);
            paths[index] = path;
            offset += byteLength;
        }

        if (offset != payload.Length)
        {
            throw new InvalidDataException("Unexpected trailing activation data.");
        }

        return paths;
    }

    private static void ValidatePath(string path)
    {
        if (path is null || path.Length > MaximumPathLength)
        {
            throw new ArgumentException("Activation paths must be non-null and within the length limit.", nameof(path));
        }
    }
}
