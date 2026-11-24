using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace ModernImageViewer.Shell;

[GeneratedComClass]
internal sealed unsafe partial class PsdPreviewHandler : IInitializeWithStream, IPreviewHandler, IObjectWithSite, IOleWindow, IDisposable
{
    private static readonly Guid PreviewFrame = new("FEC87AAF-35F9-447A-ADB7-20234491401A");
    private ComReadStream? _stream;
    private nint _parent, _window, _site, _bitmap;
    private Rect _bounds;
    private int _width, _height;

    public int Initialize(nint stream, uint mode)
    {
        if (stream == 0) { return ComServer.InvalidArgument; }
        if (_stream is not null || _window != 0) { return ComServer.AlreadyInitialized; }
        _stream = new ComReadStream(stream);
        return ComServer.Ok;
    }

    public int SetWindow(nint parent, in Rect bounds)
    {
        if (parent == 0 || (_window != 0 && parent != _parent)) { return ComServer.InvalidArgument; }
        _parent = parent;
        return SetRect(bounds);
    }

    public int SetRect(in Rect bounds)
    {
        _bounds = bounds;
        if (_window != 0 && ShellNative.MoveWindow(_window, bounds.Left, bounds.Top,
            Math.Max(0, bounds.Width), Math.Max(0, bounds.Height), 1) == 0) { return ComServer.Fail; }
        return ComServer.Ok;
    }

    public int DoPreview()
    {
        if (_parent == 0 || _stream is null || _window != 0) { return ComServer.Fail; }
        try
        {
            using ComReadStream stream = _stream;
            _stream = null;
            (_bitmap, _width, _height) = ShellPsdBitmap.Decode(stream, 1536);
            const uint child = 0x40000000;
            const uint visible = 0x10000000;
            _window = ShellNative.CreateWindowEx(0, "STATIC", null, child | visible, _bounds.Left, _bounds.Top,
                Math.Max(0, _bounds.Width), Math.Max(0, _bounds.Height), _parent, 0, 0, 0);
            if (_window == 0) { throw new InvalidOperationException(); }
            GCHandle state = GCHandle.Alloc(this);
            if (ShellNative.SetWindowSubclass(_window, &PreviewWindowProc, 1, (nuint)GCHandle.ToIntPtr(state)) == 0)
            {
                state.Free();
                throw new InvalidOperationException();
            }
            return ComServer.Ok;
        }
        catch (Exception error)
        {
            Unload();
            return error.HResult;
        }
    }

    public int Unload()
    {
        _stream?.Dispose();
        _stream = null;
        if (_window != 0 && ShellNative.DestroyWindow(_window) == 0) { return ComServer.Fail; }
        _window = 0;
        ReleaseBitmap();
        return ComServer.Ok;
    }

    private void ReleaseBitmap()
    {
        if (_bitmap != 0) { _ = ShellNative.DeleteObject(_bitmap); }
        _bitmap = 0;
    }

    public int GetWindow(out nint window)
    {
        window = _window != 0 ? _window : _parent;
        return window != 0 ? ComServer.Ok : ComServer.Fail;
    }

    public int ContextSensitiveHelp(int enterMode) => unchecked((int)0x80004001);
    public int SetFocus()
    {
        if (_window == 0) { return ComServer.False; }
        ShellNative.SetFocus(_window);
        return ComServer.Ok;
    }

    public int QueryFocus(out nint window)
    {
        window = ShellNative.GetFocus();
        return window != 0 ? ComServer.Ok : ComServer.False;
    }

    public int TranslateAccelerator(nint message)
    {
        if (message == 0) { return ComServer.InvalidArgument; }
        if (GetSite(PreviewFrame, out nint frame) < 0) { return ComServer.False; }
        try
        {
            var translate = (delegate* unmanaged[Stdcall]<nint, nint, int>)(*(nint**)frame)[4];
            return translate(frame, message);
        }
        finally { Marshal.Release(frame); }
    }

    public int SetSite(nint site)
    {
        if (site != 0) { Marshal.AddRef(site); }
        if (_site != 0) { Marshal.Release(_site); }
        _site = site;
        return ComServer.Ok;
    }

    public int GetSite(in Guid iid, out nint site)
    {
        site = 0;
        if (_site == 0) { return ComServer.Fail; }
        Guid id = iid;
        return Marshal.QueryInterface(_site, in id, out site);
    }

    public void Dispose()
    {
        Unload();
        SetSite(0);
        GC.SuppressFinalize(this);
    }

    ~PsdPreviewHandler()
    {
        if (_site != 0) { Marshal.Release(_site); }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static nint PreviewWindowProc(nint window, uint message, nuint wParam, nint lParam, nuint id, nuint state)
    {
        var handle = GCHandle.FromIntPtr((nint)state);
        var handler = (PsdPreviewHandler)handle.Target!;
        if (message == 0x000F) // WM_PAINT
        {
            handler.Paint(window);
            return 0;
        }
        if (message == 0x0082) // WM_NCDESTROY
        {
            _ = ShellNative.RemoveWindowSubclass(window, &PreviewWindowProc, id);
            handler._window = 0;
            handler.ReleaseBitmap();
            handle.Free();
        }
        return ShellNative.DefSubclassProc(window, message, wParam, lParam);
    }

    private void Paint(nint window)
    {
        nint dc = ShellNative.BeginPaint(window, out PaintStruct paint);
        try
        {
            if (dc == 0 || ShellNative.GetClientRect(window, out Rect area) == 0) { return; }
            ShellNative.FillRect(dc, area, ShellNative.GetSysColorBrush(5)); // COLOR_WINDOW
            if (_bitmap == 0 || area.Width <= 0 || area.Height <= 0) { return; }
            double scale = Math.Min((double)area.Width / _width, (double)area.Height / _height);
            int width = Math.Max(1, (int)(_width * scale));
            int height = Math.Max(1, (int)(_height * scale));
            nint source = ShellNative.CreateCompatibleDC(dc);
            if (source == 0) { return; }
            nint previous = ShellNative.SelectObject(source, _bitmap);
            try
            {
                const uint blendWithSourceAlpha = 0x01FF0000;
                _ = ShellNative.AlphaBlend(dc, (area.Width - width) / 2, (area.Height - height) / 2, width, height,
                    source, 0, 0, _width, _height, blendWithSourceAlpha);
            }
            finally
            {
                ShellNative.SelectObject(source, previous);
                _ = ShellNative.DeleteDC(source);
            }
        }
        finally { ShellNative.EndPaint(window, paint); }
    }
}
