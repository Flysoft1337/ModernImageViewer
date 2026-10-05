using System.Windows;

using ModernImageViewer.Application.Settings;

namespace ModernImageViewer.UI;

internal static class WindowPlacementPolicy
{
    public static Rect Restore(WindowPlacementData saved, Rect workArea, double dpiScale)
    {
        double width = Math.Min(Math.Max(720, saved.Width), workArea.Width);
        double height = Math.Min(Math.Max(480, saved.Height), workArea.Height);
        double left = Math.Clamp(saved.Left * saved.DpiScale / dpiScale, workArea.Left, workArea.Right - width);
        double top = Math.Clamp(saved.Top * saved.DpiScale / dpiScale, workArea.Top, workArea.Bottom - height);
        return new Rect(left, top, width, height);
    }
}
