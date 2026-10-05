using ModernImageViewer.Imaging;

namespace ModernImageViewer.UI.ViewModels;

public sealed partial class MainWindowViewModel
{
    private ViewOrientation _viewOrientation;

    public string ViewOrientationLabel => HasViewOrientation
        ? $"{Text("Orientation_Title")} · {_viewOrientation.RotationDegrees}°" : Text("Orientation_Title");
    public string RotateLeftLabel => Text("Orientation_RotateLeft");
    public string RotateRightLabel => Text("Orientation_RotateRight");
    public string FlipHorizontalLabel => Text("Orientation_FlipHorizontal");
    public string FlipVerticalLabel => Text("Orientation_FlipVertical");
    public string ResetOrientationLabel => Text("Orientation_Reset");
    public string BrowsingPreferencesLabel => Text("Preferences_Browsing");
    public bool HasViewOrientation => _viewOrientation != default;
    public bool IsFlippedHorizontally => _viewOrientation.IsFlippedHorizontally;
    public bool IsFlippedVertically => _viewOrientation.IsFlippedVertically;

    public void UpdateViewOrientation(ViewOrientation orientation)
    {
        _viewOrientation = orientation;
        NotifyAll();
    }
}
