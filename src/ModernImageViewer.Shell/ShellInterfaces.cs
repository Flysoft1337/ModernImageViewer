using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace ModernImageViewer.Shell;

[GeneratedComInterface, Guid("00000001-0000-0000-C000-000000000046")]
internal partial interface IClassFactory
{
    [PreserveSig] int CreateInstance(nint outer, in Guid iid, out nint instance);
    [PreserveSig] int LockServer(int locked);
}

[GeneratedComInterface, Guid("B824B49D-22AC-4161-AC8A-9916E8FA3F7F")]
internal partial interface IInitializeWithStream
{
    [PreserveSig] int Initialize(nint stream, uint mode);
}

[GeneratedComInterface, Guid("E357FCCD-A995-4576-B01F-234630154E96")]
internal partial interface IThumbnailProvider
{
    [PreserveSig] int GetThumbnail(uint size, out nint bitmap, out int alphaType);
}

[GeneratedComInterface, Guid("8895B1C6-B41F-4C1C-A562-0D564250836F")]
internal partial interface IPreviewHandler
{
    [PreserveSig] int SetWindow(nint parent, in Rect bounds);
    [PreserveSig] int SetRect(in Rect bounds);
    [PreserveSig] int DoPreview();
    [PreserveSig] int Unload();
    [PreserveSig] int SetFocus();
    [PreserveSig] int QueryFocus(out nint window);
    [PreserveSig] int TranslateAccelerator(nint message);
}

[GeneratedComInterface, Guid("FC4801A3-2BA9-11CF-A229-00AA003D7352")]
internal partial interface IObjectWithSite
{
    [PreserveSig] int SetSite(nint site);
    [PreserveSig] int GetSite(in Guid iid, out nint site);
}

[GeneratedComInterface, Guid("00000114-0000-0000-C000-000000000046")]
internal partial interface IOleWindow
{
    [PreserveSig] int GetWindow(out nint window);
    [PreserveSig] int ContextSensitiveHelp(int enterMode);
}

[StructLayout(LayoutKind.Sequential)]
internal struct Rect
{
    internal int Left, Top, Right, Bottom;
    internal readonly int Width => Right - Left;
    internal readonly int Height => Bottom - Top;
}
