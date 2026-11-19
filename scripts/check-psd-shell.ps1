#Requires -Version 7.0
param(
    [Parameter(Mandatory)][string]$ShellPath,
    [switch]$CheckRegistration
)

$ErrorActionPreference = 'Stop'
$shell = (Resolve-Path -LiteralPath $ShellPath).Path
if (-not ('PsdShellCheck' -as [type])) {
    Add-Type -CompilerOptions '/unsafe' -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Threading;

public static unsafe class PsdShellCheck
{
    private static readonly Guid ClassFactory = new("00000001-0000-0000-C000-000000000046");
    private static readonly Guid InitializeWithStream = new("B824B49D-22AC-4161-AC8A-9916E8FA3F7F");
    private static readonly Guid ThumbnailClass = new("8ECCFC5B-9E25-4DF5-8E2C-E6C9FF60F9E4");
    private static readonly Guid ThumbnailProvider = new("E357FCCD-A995-4576-B01F-234630154E96");
    private static readonly Guid PreviewClass = new("CBD0D211-0458-404C-AACF-DB57871FCEFF");
    private static readonly Guid PreviewHandler = new("8895B1C6-B41F-4C1C-A562-0D564250836F");

    public static void Run(string path, bool checkRegistration)
    {
        Exception failure = null;
        var thread = new Thread(() =>
        {
            try { Check(path, checkRegistration); }
            catch (Exception error) { failure = error; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure != null) { throw new InvalidOperationException(failure.Message, failure); }
    }

    private static void Check(string path, bool checkRegistration)
    {
        int initialized = CoInitializeEx(0, 2);
        if (initialized < 0) { CheckResult(initialized, "CoInitializeEx"); }
        try
        {
            nint export = NativeLibrary.GetExport(NativeLibrary.Load(path), "DllGetClassObject");
            if (checkRegistration)
            {
                AssertAssociation(ThumbnailProvider, ThumbnailClass);
                AssertAssociation(PreviewHandler, PreviewClass);
            }
            nint thumbnail = Create(export, ThumbnailClass, ThumbnailProvider, checkRegistration);
            try
            {
                Initialize(thumbnail);
                nint bitmap = 0;
                int alpha = 0;
                var getThumbnail = (delegate* unmanaged[Stdcall]<nint, uint, nint*, int*, int>)(*(nint**)thumbnail)[3];
                CheckResult(getThumbnail(thumbnail, 12, &bitmap, &alpha), "GetThumbnail");
                try
                {
                    if (GetObjectW(bitmap, sizeof(Bitmap), out Bitmap data) != sizeof(Bitmap) ||
                        data.Width != 12 || data.Height != 6 || data.BitsPerPixel != 32 || alpha != 2 ||
                        data.Pixels == 0 || *(uint*)data.Pixels != 0xFFCC3311)
                    {
                        throw new InvalidOperationException("Unexpected thumbnail dimensions, alpha or first pixel.");
                    }
                }
                finally { DeleteObject(bitmap); }
            }
            finally { Marshal.Release(thumbnail); }

            nint preview = Create(export, PreviewClass, PreviewHandler, checkRegistration);
            nint parent = CreateWindowExW(0, "STATIC", null, 0x80000000, 0, 0, 100, 100, 0, 0, 0, 0);
            if (parent == 0) { Marshal.ThrowExceptionForHR(Marshal.GetHRForLastWin32Error()); }
            try
            {
                Initialize(preview);
                nint* table = *(nint**)preview;
                int* rect = stackalloc int[] { 2, 3, 98, 97 };
                CheckResult(((delegate* unmanaged[Stdcall]<nint, nint, int*, int>)table[3])(preview, parent, rect), "SetWindow");
                CheckResult(((delegate* unmanaged[Stdcall]<nint, int>)table[5])(preview), "DoPreview");
                nint child = GetWindow(parent, 5);
                if (child == 0) { throw new InvalidOperationException("Preview child window was not created."); }
                rect[2] = 76;
                CheckResult(((delegate* unmanaged[Stdcall]<nint, int*, int>)table[4])(preview, rect), "SetRect");
                CheckResult(((delegate* unmanaged[Stdcall]<nint, int>)table[6])(preview), "Unload");
                if (IsWindow(child)) { throw new InvalidOperationException("Preview child window survived Unload."); }
            }
            finally { DestroyWindow(parent); Marshal.Release(preview); }
        }
        finally { CoUninitialize(); }
    }

    private static nint Create(nint export, Guid clsid, Guid iid, bool registered)
    {
        nint instance = 0;
        if (registered)
        {
            CheckResult(CoCreateInstance(in clsid, 0, 1, in iid, out instance), "CoCreateInstance");
            return instance;
        }
        nint factory = 0;
        Guid factoryId = ClassFactory;
        CheckResult(((delegate* unmanaged[Stdcall]<Guid*, Guid*, nint*, int>)export)(&clsid, &factoryId, &factory), "DllGetClassObject");
        try
        {
            var create = (delegate* unmanaged[Stdcall]<nint, nint, Guid*, nint*, int>)(*(nint**)factory)[3];
            CheckResult(create(factory, 0, &iid, &instance), "IClassFactory.CreateInstance");
            return instance;
        }
        finally { Marshal.Release(factory); }
    }

    private static void Initialize(nint instance)
    {
        // A 16 x 8 flattened, uncompressed RGB PSD with uniform RGB CC3311.
        byte[] header = Convert.FromHexString("38425053000100000000000000030000000800000010000800030000000000000000000000000000");
        byte[] psd = new byte[header.Length + 384];
        header.CopyTo(psd, 0);
        psd.AsSpan(header.Length, 128).Fill(0xCC);
        psd.AsSpan(header.Length + 128, 128).Fill(0x33);
        psd.AsSpan(header.Length + 256, 128).Fill(0x11);
        Guid initId = InitializeWithStream;
        CheckResult(Marshal.QueryInterface(instance, in initId, out nint init), "QueryInterface(IInitializeWithStream)");
        nint stream = SHCreateMemStream(psd, (uint)psd.Length);
        try
        {
            if (stream == 0) { throw new InvalidOperationException("Could not create the PSD test stream."); }
            var initialize = (delegate* unmanaged[Stdcall]<nint, nint, uint, int>)(*(nint**)init)[3];
            CheckResult(initialize(init, stream, 0), "IInitializeWithStream.Initialize");
        }
        finally
        {
            if (stream != 0) { Marshal.Release(stream); }
            Marshal.Release(init);
        }
    }

    private static void AssertAssociation(Guid iid, Guid clsid)
    {
        char* value = stackalloc char[64];
        uint length = 64;
        CheckResult(AssocQueryStringW(0, 16, ".psd", iid.ToString("B"), value, ref length), "Resolve PSD Shell handler");
        if (!Guid.TryParse(new string(value), out Guid actual) || actual != clsid)
        {
            throw new InvalidOperationException("Windows resolved a different PSD handler.");
        }
    }

    private static void CheckResult(int result, string operation)
    {
        if (result != 0) { throw new COMException(operation + " returned 0x" + result.ToString("X8"), result); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Bitmap
    {
        public int Type, Width, Height, WidthBytes;
        public ushort Planes, BitsPerPixel;
        public nint Pixels;
    }

    [DllImport("ole32.dll")] private static extern int CoInitializeEx(nint reserved, uint flags);
    [DllImport("ole32.dll")] private static extern void CoUninitialize();
    [DllImport("ole32.dll")] private static extern int CoCreateInstance(in Guid clsid, nint outer, uint context, in Guid iid, out nint instance);
    [DllImport("shlwapi.dll")] private static extern nint SHCreateMemStream(byte[] bytes, uint count);
    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)] private static extern int AssocQueryStringW(uint flags, uint kind, string extension, string extra, char* output, ref uint length);
    [DllImport("gdi32.dll")] private static extern int GetObjectW(nint handle, int size, out Bitmap bitmap);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint handle);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)] private static extern nint CreateWindowExW(uint exStyle, string cls, string caption, uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint param);
    [DllImport("user32.dll")] private static extern nint GetWindow(nint window, uint command);
    [DllImport("user32.dll")] private static extern bool IsWindow(nint window);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(nint window);
}
'@
}

[PsdShellCheck]::Run($shell, $CheckRegistration.IsPresent)
Write-Output 'PSD thumbnail pixels and preview window lifecycle verified.'
