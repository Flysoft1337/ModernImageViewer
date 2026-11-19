using System.Runtime.InteropServices;

namespace ModernImageViewer.Shell;

internal static unsafe partial class ShellNative
{
    [LibraryImport("kernel32", EntryPoint = "GetModuleHandleExW")]
    internal static partial int GetModuleHandleEx(uint flags, nint address, out nint module);
    [LibraryImport("kernel32", EntryPoint = "GetModuleFileNameW")]
    internal static partial uint GetModuleFileName(nint module, char* fileName, uint capacity);
    [LibraryImport("user32", EntryPoint = "CreateWindowExW", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial nint CreateWindowEx(uint extendedStyle, string className, string? name, uint style,
        int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);
    [LibraryImport("user32")]
    internal static partial int DestroyWindow(nint window);
    [LibraryImport("user32")]
    internal static partial int MoveWindow(nint window, int x, int y, int width, int height, int repaint);
    [LibraryImport("user32")]
    internal static partial nint SetFocus(nint window);
    [LibraryImport("user32")]
    internal static partial nint GetFocus();
    [LibraryImport("user32")]
    internal static partial nint BeginPaint(nint window, out PaintStruct paint);
    [LibraryImport("user32")]
    internal static partial int EndPaint(nint window, in PaintStruct paint);
    [LibraryImport("user32")]
    internal static partial int GetClientRect(nint window, out Rect bounds);
    [LibraryImport("user32")]
    internal static partial int FillRect(nint dc, in Rect bounds, nint brush);
    [LibraryImport("user32")]
    internal static partial nint GetSysColorBrush(int index);
    [LibraryImport("comctl32")]
    internal static partial int SetWindowSubclass(nint window, delegate* unmanaged[Stdcall]<nint, uint, nuint, nint, nuint, nuint, nint> callback, nuint id, nuint data);
    [LibraryImport("comctl32")]
    internal static partial int RemoveWindowSubclass(nint window, delegate* unmanaged[Stdcall]<nint, uint, nuint, nint, nuint, nuint, nint> callback, nuint id);
    [LibraryImport("comctl32")]
    internal static partial nint DefSubclassProc(nint window, uint message, nuint wParam, nint lParam);
    [LibraryImport("gdi32")]
    internal static partial nint CreateDIBSection(nint dc, in BitmapHeader bitmap, uint usage, out nint bits, nint section, uint offset);
    [LibraryImport("gdi32")]
    internal static partial int DeleteObject(nint bitmap);
    [LibraryImport("gdi32")]
    internal static partial nint CreateCompatibleDC(nint dc);
    [LibraryImport("gdi32")]
    internal static partial int DeleteDC(nint dc);
    [LibraryImport("gdi32")]
    internal static partial nint SelectObject(nint dc, nint bitmap);
    [LibraryImport("msimg32")]
    internal static partial int AlphaBlend(nint destination, int x, int y, int width, int height,
        nint source, int sourceX, int sourceY, int sourceWidth, int sourceHeight, uint blend);
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct PaintStruct
{
    internal nint DC;
    internal int Erase;
    internal Rect Update;
    internal int Restore, IncUpdate;
    internal fixed byte Reserved[32];
}

[StructLayout(LayoutKind.Sequential)]
internal struct BitmapHeader
{
    internal uint HeaderSize;
    internal int Width, Height;
    internal ushort Planes, BitsPerPixel;
    internal uint Compression, ImageSize;
    internal int PixelsPerMetreX, PixelsPerMetreY;
    internal uint ColorsUsed, ColorsImportant;
}
