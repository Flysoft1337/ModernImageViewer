using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

using ModernImageViewer.Application.Settings;

namespace ModernImageViewer.UI;

public partial class MainWindow
{
    private bool _changingFullScreen;

    private void InitializeWindowPreferences()
    {
        if (_viewModel.WindowPlacement is not null)
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
        }
        SourceInitialized += (_, _) => RestoreWindowPreferences();
        Loaded += (_, _) => SaveWindowPreferences();
        LocationChanged += (_, _) => SaveWindowPreferences();
        SizeChanged += (_, _) => SaveWindowPreferences();
        StateChanged += (_, _) => SaveWindowPreferences();
        Closing += (_, _) =>
        {
            SaveWindowPreferences();
            _viewModel.FlushPreferences();
        };
    }

    private void RestoreWindowPreferences()
    {
        if (_viewModel.WindowPlacement is not { } saved)
        {
            return;
        }
        NativeRect rectangle = new()
        {
            Left = (int)Math.Round(saved.Left * saved.DpiScale),
            Top = (int)Math.Round(saved.Top * saved.DpiScale),
            Right = (int)Math.Round((saved.Left + saved.Width) * saved.DpiScale),
            Bottom = (int)Math.Round((saved.Top + saved.Height) * saved.DpiScale),
        };
        IntPtr monitor = MonitorFromRect(ref rectangle, 2);
        MonitorInfo info = new() { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref info))
        {
            return;
        }
        double scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        if (GetDpiForMonitor(monitor, 0, out uint dpiX, out _) == 0 && dpiX > 0)
        {
            scale = dpiX / 96d;
        }
        Rect work = new(info.Work.Left / scale, info.Work.Top / scale,
            (info.Work.Right - info.Work.Left) / scale, (info.Work.Bottom - info.Work.Top) / scale);
        Rect bounds = WindowPlacementPolicy.Restore(saved, work, scale);
        MinWidth = Math.Min(720, work.Width);
        MinHeight = Math.Min(480, work.Height);
        // The new HWND may still have the primary monitor's DPI. Position it in physical
        // screen coordinates; WM_DPICHANGED then updates WPF's DIP coordinates on that monitor.
        SetWindowPos(new WindowInteropHelper(this).Handle, IntPtr.Zero,
            (int)Math.Round(bounds.Left * scale), (int)Math.Round(bounds.Top * scale),
            (int)Math.Round(bounds.Width * scale), (int)Math.Round(bounds.Height * scale), 0x0014);
        if (saved.IsMaximized)
        {
            WindowState = WindowState.Maximized;
        }
    }

    private void SaveWindowPreferences()
    {
        if (!IsLoaded || _changingFullScreen || _viewModel.IsFullScreen || WindowState == WindowState.Minimized)
        {
            return;
        }
        Rect bounds = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
        if (bounds.IsEmpty || !double.IsFinite(bounds.Width) || bounds.Width <= 0)
        {
            return;
        }
        double scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        WindowPlacementData placement = new(bounds.Left, bounds.Top, bounds.Width, bounds.Height,
            scale, WindowState == WindowState.Maximized);
        _viewModel.SaveWindowPlacement(placement);
    }

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern IntPtr MonitorFromRect(ref NativeRect rectangle, uint flags);

    [DllImport("shcore.dll", ExactSpelling = true)]
    private static extern int GetDpiForMonitor(IntPtr monitor, int dpiType, out uint dpiX, out uint dpiY);

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
}
