using System.Buffers.Binary;
using System.IO;
using System.Runtime.InteropServices;

using Microsoft.Win32.SafeHandles;

using ModernImageViewer.Imaging;

namespace ModernImageViewer.Codecs.Wic;

// A short-lived WIC session avoids WPF's source-sized memory-pressure registrations
// for ordinary PNG previews. All other inputs retain the existing WPF pipeline.
internal static unsafe partial class WicPngPreview
{
    private static readonly Guid FactoryClass = new("cacaf262-9370-4615-a13b-9f5539da4c0a");
    private static readonly Guid FactoryInterface = new("ec5ec8a9-c395-4314-9c77-54d7a935ff70");
    private static readonly Guid MicrosoftVendor = new("f0e749ca-edef-4589-a73a-ee0e626a2a2b");
    private static readonly Guid PngContainer = new("1b7cfaf4-713f-473c-bbcd-6137425faeaf");
    private static readonly Guid Bgr24 = new("6fddc324-4e03-4bfe-b185-3d77768dc90c");
    private static readonly Guid Bgra32 = new("6fddc324-4e03-4bfe-b185-3d77768dc90f");
    private static readonly Guid Pbgra32 = new("6fddc324-4e03-4bfe-b185-3d77768dc910");
    private static int s_activeHandles;
    private static ReadOnlySpan<byte> Signature => [137, 80, 78, 71, 13, 10, 26, 10];

    internal static bool ObserveLifetime { get; set; }
    internal static int ObservedActiveHandles => Volatile.Read(ref s_activeHandles);

    internal static PixelBuffer? TryDecode(FileStream input, PixelSize maximumSize, long? maximumDecodedBytes,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        Span<byte> header = stackalloc byte[29];
        int read = input.ReadAtLeast(header, header.Length, throwOnEndOfStream: false);
        input.Position = 0;
        if (read != header.Length || !header[..8].SequenceEqual(Signature)
            || !header[12..16].SequenceEqual("IHDR"u8) || header[24] != 8
            || header[25] is not (2 or 6) || header[28] != 0)
        {
            return null;
        }
        uint width = BinaryPrimitives.ReadUInt32BigEndian(header[16..20]);
        uint height = BinaryPrimitives.ReadUInt32BigEndian(header[20..24]);
        if (width is 0 or > int.MaxValue || height is 0 or > int.MaxValue) { return null; }
        PixelSize original = new((int)width, (int)height);
        ImageDecodeLimits.Default.ValidateAndGetStride(original);
        if (!HasNoExif(input, token)) { return null; }
        try
        {
            using Apartment apartment = new();
            Check(CoCreateInstance(in FactoryClass, 0, 1, in FactoryInterface, out nint factoryPointer));
            using ComHandle factory = new(factoryPointer);
            // WIC borrows the exact file already locked/stamped by the caller, never reopens its path.
            using FileHandleLease file = new(input.SafeFileHandle);
            nint decoderPointer = 0;
            Guid vendor = MicrosoftVendor;
            Check(((delegate* unmanaged[Stdcall]<nint, nint, Guid*, uint, nint*, int>)Slots(factoryPointer)[5])(
                factoryPointer, file.Pointer, &vendor, 0, &decoderPointer));
            using ComHandle decoder = new(decoderPointer);
            Guid container = default;
            Check(((delegate* unmanaged[Stdcall]<nint, Guid*, int>)Slots(decoderPointer)[5])(decoderPointer, &container));
            if (container != PngContainer) { return null; }
            nint framePointer = 0;
            Check(((delegate* unmanaged[Stdcall]<nint, uint, nint*, int>)Slots(decoderPointer)[13])(
                decoderPointer, 0, &framePointer));
            using ComHandle frame = new(framePointer);
            uint actualWidth = 0, actualHeight = 0, contexts = 0;
            Check(((delegate* unmanaged[Stdcall]<nint, uint*, uint*, int>)Slots(framePointer)[3])(
                framePointer, &actualWidth, &actualHeight));
            if (actualWidth != width || actualHeight != height) { return null; }
            Guid format = default;
            Check(((delegate* unmanaged[Stdcall]<nint, Guid*, int>)Slots(framePointer)[4])(framePointer, &format));
            Check(((delegate* unmanaged[Stdcall]<nint, uint, nint, uint*, int>)Slots(framePointer)[9])(
                framePointer, 0, 0, &contexts));
            if (contexts != 0 || (format != Bgr24 && format != Bgra32)) { return null; }
            token.ThrowIfCancellationRequested();
            double scale = Math.Min(1, Math.Min((double)maximumSize.Width / width, (double)maximumSize.Height / height));
            PixelSize output = new(Math.Max(1, (int)Math.Floor(width * scale)), Math.Max(1, (int)Math.Floor(height * scale)));
            if (output.Width > maximumSize.Width || output.Height > maximumSize.Height) { return null; }
            int stride = ImageDecodeLimits.Default.ValidateAndGetStride(output);
            int bytes = checked(stride * output.Height);
            if (maximumDecodedBytes is { } budget && bytes > budget) { throw new ImageSizeLimitExceededException(); }
            nint scalerPointer = 0;
            Check(((delegate* unmanaged[Stdcall]<nint, nint*, int>)Slots(factoryPointer)[11])(factoryPointer, &scalerPointer));
            using ComHandle scaler = new(scalerPointer);
            nint source = framePointer;
            if (output != original)
            {
                // Fant is the same WIC interpolation used by WPF's BitmapImage/TransformedBitmap.
                Check(((delegate* unmanaged[Stdcall]<nint, nint, uint, uint, uint, int>)Slots(scalerPointer)[8])(
                    scalerPointer, framePointer, (uint)output.Width, (uint)output.Height, 3));
                source = scalerPointer;
            }
            nint converterPointer = 0;
            Check(((delegate* unmanaged[Stdcall]<nint, nint*, int>)Slots(factoryPointer)[10])(
                factoryPointer, &converterPointer));
            using ComHandle converter = new(converterPointer);
            Guid targetFormat = Pbgra32;
            Check(((delegate* unmanaged[Stdcall]<nint, nint, Guid*, uint, nint, double, uint, int>)Slots(converterPointer)[8])(
                converterPointer, source, &targetFormat, 0, 0, 0, 0));
            byte[] pixels = GC.AllocateUninitializedArray<byte>(bytes, pinned: true);
            token.ThrowIfCancellationRequested();
            fixed (byte* destination = pixels)
            {
                Check(((delegate* unmanaged[Stdcall]<nint, nint, uint, uint, byte*, int>)Slots(converterPointer)[7])(
                    converterPointer, 0, (uint)stride, (uint)bytes, destination));
            }
            // A running native CopyPixels cannot be interrupted; reject the late result on return.
            token.ThrowIfCancellationRequested();
            return new PixelBuffer(output, stride, pixels, sourceSize: original);
        }
        catch (COMException)
        {
            // Preserve WPF's existing fallback and error classification for codec/platform failures.
            return null;
        }
        finally
        {
            // Exposing SafeFileHandle flushes FileStream's read buffer. Restore its logical position
            // before either the ordinary WPF fallback or the caller's file-stamp validation.
            input.Position = 0;
        }
    }

    private static nint* Slots(nint instance) => *(nint**)instance;
    private static void Check(int result) => Marshal.ThrowExceptionForHR(result);

    private static bool HasNoExif(FileStream input, CancellationToken token)
    {
        try
        {
            input.Position = 8;
            Span<byte> chunk = stackalloc byte[8];
            // Skip payloads; never copy or expand the encoded image to inspect metadata.
            // An excessive/malformed chunk table falls back to the existing WPF parser.
            for (int count = 0; count < 4096; count++)
            {
                token.ThrowIfCancellationRequested();
                if (input.ReadAtLeast(chunk, chunk.Length, throwOnEndOfStream: false) != chunk.Length) { return false; }
                uint length = BinaryPrimitives.ReadUInt32BigEndian(chunk);
                if (chunk[4..].SequenceEqual("eXIf"u8) || length > input.Length - input.Position - 4) { return false; }
                if (chunk[4..].SequenceEqual("IEND"u8)) { return length == 0; }
                input.Position += (long)length + 4;
            }
            return false;
        }
        finally { input.Position = 0; }
    }

    private sealed class ComHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        private readonly bool _observed = ObserveLifetime;

        internal ComHandle(nint pointer) : base(ownsHandle: true)
        {
            SetHandle(pointer);
            if (_observed && !IsInvalid) { Interlocked.Increment(ref s_activeHandles); }
        }

        protected override bool ReleaseHandle()
        {
            _ = ((delegate* unmanaged[Stdcall]<nint, uint>)Slots(handle)[2])(handle);
            if (_observed) { Interlocked.Decrement(ref s_activeHandles); }
            return true;
        }
    }

    private readonly struct Apartment : IDisposable
    {
        private readonly bool _initialized;

        public Apartment()
        {
            int result = CoInitializeEx(0, 0);
            if (result != unchecked((int)0x80010106)) { Check(result); }
            _initialized = result >= 0;
        }

        public void Dispose() { if (_initialized) { CoUninitialize(); } }
    }

    private readonly struct FileHandleLease : IDisposable
    {
        private readonly SafeFileHandle _handle;

        internal FileHandleLease(SafeFileHandle handle)
        {
            bool held = false;
            handle.DangerousAddRef(ref held);
            _handle = handle;
        }

        internal nint Pointer => _handle.DangerousGetHandle();
        public void Dispose() => _handle.DangerousRelease();
    }

    [LibraryImport("ole32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int CoInitializeEx(nint reserved, uint mode);

    [LibraryImport("ole32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial void CoUninitialize();

    [LibraryImport("ole32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int CoCreateInstance(in Guid clsid, nint outer, uint context, in Guid iid, out nint instance);
}
