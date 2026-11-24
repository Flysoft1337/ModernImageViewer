using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace ModernImageViewer.Shell;

internal static class ComServer
{
    internal const int Ok = 0;
    internal const int False = 1;
    internal const int Fail = unchecked((int)0x80004005);
    internal const int InvalidArgument = unchecked((int)0x80070057);
    internal const int AlreadyInitialized = unchecked((int)0x800704DF);
    private static readonly Guid ThumbnailClass = new("8ECCFC5B-9E25-4DF5-8E2C-E6C9FF60F9E4");
    private static readonly Guid PreviewClass = new("CBD0D211-0458-404C-AACF-DB57871FCEFF");
    private static readonly StrategyBasedComWrappers Wrappers = new();

    [UnmanagedCallersOnly(EntryPoint = "DllGetClassObject")]
    private static unsafe int GetClassObject(Guid* classId, Guid* iid, nint* factory)
    {
        if (classId == null || iid == null || factory == null) { return InvalidArgument; }
        *factory = 0;
        if (*classId != ThumbnailClass && *classId != PreviewClass) { return unchecked((int)0x80040111); }
        try { return Query(new ClassFactory(*classId == PreviewClass), *iid, out *factory); }
        catch (Exception error) { return error.HResult; }
    }

    // NativeAOT libraries stay loaded for the lifetime of their isolated COM host.
    [UnmanagedCallersOnly(EntryPoint = "DllCanUnloadNow")]
    private static int CanUnloadNow() => False;

    internal static int Query(object value, in Guid iid, out nint instance)
    {
        nint unknown = Wrappers.GetOrCreateComInterfaceForObject(value, CreateComInterfaceFlags.None);
        try
        {
            Guid id = iid;
            return Marshal.QueryInterface(unknown, in id, out instance);
        }
        finally { Marshal.Release(unknown); }
    }
}

[GeneratedComClass]
internal sealed partial class ClassFactory(bool preview) : IClassFactory
{
    public int CreateInstance(nint outer, in Guid iid, out nint instance)
    {
        instance = 0;
        if (outer != 0) { return unchecked((int)0x80040110); }
        try { return ComServer.Query(preview ? new PsdPreviewHandler() : new PsdThumbnailProvider(), iid, out instance); }
        catch (Exception error) { return error.HResult; }
    }

    public int LockServer(int locked) => ComServer.Ok;
}
