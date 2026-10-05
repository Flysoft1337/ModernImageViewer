using System.IO;
using System.Runtime.InteropServices;

using ModernImageViewer.Application.Images;

namespace ModernImageViewer.Codecs.Raw;

internal sealed class RawNativeApi
{
    internal static readonly Lazy<RawNativeApi> Instance = new(() => new RawNativeApi());

    [StructLayout(LayoutKind.Sequential)]
    internal struct PreviewInfo
    {
        public uint Width, Height, Channels, Bits, Format, Bytes, Orientation;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate int ProgressCallback(nint state, int stage, int iteration, int expected);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate nint CreateContext(ProgressCallback callback);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
    internal delegate int OpenContext(nint context, [MarshalAs(UnmanagedType.LPWStr)] string path);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate int UnpackContext(nint context);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate int GetPreview(nint context, out PreviewInfo info, out nint data);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate nint GetText(nint context);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate void CloseContext(nint context);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int GetVersion();

    internal CreateContext Create { get; }
    internal OpenContext Open { get; }
    internal UnpackContext Unpack { get; }
    internal GetPreview Preview { get; }
    internal GetText Make { get; }
    internal GetText Model { get; }
    internal CloseContext Close { get; }

    private RawNativeApi()
    {
        if (!OperatingSystem.IsWindows() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
        {
            throw new ImageDecodeException(ImageOpenError.UnsupportedFormat);
        }
        string path = Path.Combine(AppContext.BaseDirectory, "ModernImageViewer.RawBridge.dll");
        nint library;
        try
        {
            library = NativeLibrary.Load(path, typeof(RawNativeApi).Assembly,
                DllImportSearchPath.UseDllDirectoryForDependencies | DllImportSearchPath.System32);
        }
        catch (Exception exception) when (exception is DllNotFoundException or BadImageFormatException)
        {
            throw new ImageDecodeException(ImageOpenError.UnsupportedFormat, exception);
        }
        T Bind<T>(string name) where T : Delegate =>
            Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(library, name));
        if (Bind<GetVersion>("miv_raw_api_version")() != 1 || Bind<GetVersion>("miv_raw_lib_version")() != 0x1602)
        {
            NativeLibrary.Free(library);
            throw new ImageDecodeException(ImageOpenError.UnsupportedFormat);
        }
        Create = Bind<CreateContext>("miv_raw_create");
        Open = Bind<OpenContext>("miv_raw_open");
        Unpack = Bind<UnpackContext>("miv_raw_unpack_preview");
        Preview = Bind<GetPreview>("miv_raw_get_preview");
        Make = Bind<GetText>("miv_raw_make");
        Model = Bind<GetText>("miv_raw_model");
        Close = Bind<CloseContext>("miv_raw_close");
        // One process-lifetime module; individual contexts own all image allocations.
    }
}
