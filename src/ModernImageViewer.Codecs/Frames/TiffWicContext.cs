using System.IO;
using System.Runtime.InteropServices;

using ModernImageViewer.Application.Images;
using ModernImageViewer.Imaging;

namespace ModernImageViewer.Codecs.Frames;

// Raw COM pointers keep creation, use and final Release on the session's owning thread.
internal sealed class TiffWicContext : IDisposable
{
    internal static readonly Guid TiffContainer = new("163BCC30-E2E9-4F0B-961D-A3E9FDB788A3");
    private static readonly Guid Pbgra32 = new("6FDDC324-4E03-4BFE-B185-3D77768DC910");
    private readonly FileStream _file;
    private static int _activeContexts;
    private bool _counted;
    private nint _factory, _stream, _decoder;
    private readonly string _path;

    internal TiffWicContext(string path)
    {
        _path = path;
        _file = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            Stamp = ReadStamp();
            if (Stamp.Length > ImageFrameLimits.MaximumInputBytes) { throw new ImageSizeLimitExceededException(); }
            Guid clsid = new("CACAF262-9370-4615-A13B-9F5539DA4C0A");
            Guid iid = new("EC5EC8A9-C395-4314-9C77-54D7A935FF70");
            Check(CoCreateInstance(ref clsid, 0, 1, ref iid, out _factory));
            Check(SHCreateStreamOnFileEx(path, 0x20, 0, false, 0, out _stream));
            Check(Call<CreateDecoder>(_factory, 4)(_factory, _stream, 0, 0, out _decoder));
            Interlocked.Increment(ref _activeContexts);
            _counted = true;
            Check(Call<GetGuid>(_decoder, 5)(_decoder, out Guid container));
            IsTiff = container == TiffContainer;
            Check(Call<GetCount>(_decoder, 12)(_decoder, out uint count));
            Count = checked((int)count);
            ValidateStamp();
        }
        catch { Dispose(); throw; }
    }

    internal ImageFileStamp Stamp { get; } = null!;
    internal bool IsTiff { get; }
    internal int Count { get; }
    internal static int ActiveContextCount => Volatile.Read(ref _activeContexts);
    private ImageFileStamp ReadStamp() => new(_file.Length, File.GetLastWriteTimeUtc(_path));
    internal void ValidateStamp()
    {
        if (ReadStamp() != Stamp) { throw new IOException("The source image changed during decoding."); }
    }

    internal (PixelSize Size, ushort Orientation) Describe(int index)
    {
        nint frame = GetFrame(index);
        nint reader = 0;
        try
        {
            PixelSize size = SizeOf(frame);
            ImageDecodeLimits.Default.ValidateAndGetStride(size);
            ushort orientation = 1;
            int hr = Call<GetObject>(frame, 8)(frame, out reader);
            if (hr >= 0)
            {
                PropVariant value = default;
                try
                {
                    hr = Call<GetMetadata>(reader, 5)(reader, "/ifd/{ushort=274}", out value);
                    if (hr >= 0 && value.Type == 18 && value.Number is >= 1 and <= 8) { orientation = value.Number; }
                }
                finally { Check(PropVariantClear(ref value)); }
            }
            return (size, orientation);
        }
        finally { Release(ref reader); Release(ref frame); }
    }

    internal void Copy(int index, PixelSize? scaledSize, PixelRect bounds, byte[] pixels)
    {
        nint frame = GetFrame(index), scaler = 0, converter = 0;
        try
        {
            nint source = frame;
            if (scaledSize is { } size && size != SizeOf(frame))
            {
                Check(Call<GetObject>(_factory, 11)(_factory, out scaler));
                Check(Call<InitializeScaler>(scaler, 8)(scaler, source, (uint)size.Width, (uint)size.Height, 2));
                source = scaler;
            }
            Check(Call<GetObject>(_factory, 10)(_factory, out converter));
            Guid format = Pbgra32;
            Check(Call<InitializeConverter>(converter, 8)(converter, source, ref format, 0, 0, 0, 0));
            WicRect rect = new() { X = bounds.X, Y = bounds.Y, Width = bounds.Width, Height = bounds.Height };
            GCHandle pin = GCHandle.Alloc(pixels, GCHandleType.Pinned);
            try
            {
                Check(Call<CopyPixels>(converter, 7)(converter, ref rect, (uint)(bounds.Width * 4),
                    (uint)pixels.Length, pin.AddrOfPinnedObject()));
            }
            finally { pin.Free(); }
        }
        finally { Release(ref converter); Release(ref scaler); Release(ref frame); }
    }

    private nint GetFrame(int index)
    {
        Check(Call<ReadFrame>(_decoder, 13)(_decoder, (uint)index, out nint frame));
        return frame;
    }

    private static PixelSize SizeOf(nint source)
    {
        Check(Call<GetSize>(source, 3)(source, out uint width, out uint height));
        if (width == 0 || height == 0 || width > int.MaxValue || height > int.MaxValue)
        { throw new ImageSizeLimitExceededException(); }
        return new((int)width, (int)height);
    }

    public void Dispose()
    {
        Release(ref _decoder);
        Release(ref _stream);
        Release(ref _factory);
        _file.Dispose();
        if (_counted)
        {
            _counted = false;
            Interlocked.Decrement(ref _activeContexts);
        }
    }

    internal static void InitializeThread() => Check(CoInitializeEx(0, 0));
    internal static void UninitializeThread() => CoUninitialize();
    private static void Release(ref nint pointer)
    {
        if (pointer != 0) { Marshal.Release(pointer); pointer = 0; }
    }
    private static T Call<T>(nint instance, int slot) where T : Delegate =>
        Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(instance), slot * nint.Size));
    private static void Check(int hr)
    {
        if (hr < 0) { throw new ImageDecodeException(ImageOpenError.CorruptFile, Marshal.GetExceptionForHR(hr)); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WicRect { internal int X, Y, Width, Height; }
    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct PropVariant
    {
        [FieldOffset(0)] internal ushort Type;
        [FieldOffset(8)] internal ushort Number;
    }
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int CreateDecoder(nint self, nint stream, nint vendor, uint options, out nint decoder);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetGuid(nint self, out Guid value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetCount(nint self, out uint value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetObject(nint self, out nint value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int ReadFrame(nint self, uint index, out nint frame);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetSize(nint self, out uint width, out uint height);
    [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Unicode)]
    private delegate int GetMetadata(nint self, string query, out PropVariant value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int InitializeScaler(nint self, nint source, uint width, uint height, uint interpolation);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int InitializeConverter(nint self, nint source, ref Guid format, uint dither, nint palette, double alpha, uint paletteType);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int CopyPixels(nint self, ref WicRect rect, uint stride, uint length, nint pixels);
    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern int CoCreateInstance(ref Guid clsid, nint outer, uint context, ref Guid iid, out nint instance);
    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern int CoInitializeEx(nint reserved, uint flags);
    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern void CoUninitialize();
    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern int PropVariantClear(ref PropVariant value);
    [DllImport("shlwapi.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern int SHCreateStreamOnFileEx(string path, uint mode, uint attributes,
        [MarshalAs(UnmanagedType.Bool)] bool create, nint template, out nint stream);
}
