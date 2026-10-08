using System.Windows.Media;

using ModernImageViewer.Application.Images;
using ModernImageViewer.Imaging;

namespace ModernImageViewer.UI.Controls;

public partial class ImageViewport
{
    private bool _disposed;
    private bool _isUnloaded;
    private bool _fitOnLoad;
    private PixelSize? _lastPreviewTarget;
    private bool _updatingAnimationFrame;

    public event EventHandler<PixelSize>? PreviewTargetChanged;
    public event EventHandler<ImageOpenState>? FramePresented;

    internal bool IsAutomaticDetailRequired => NeedsAutomaticDetail(
        _transform.Scale * VisualTreeHelper.GetDpi(Canvas).DpiScaleX);

    private bool NeedsAutomaticDetail(double pixelScale) => !_disposed && !_isUnloaded
        && (_transform.Mode != ViewportMode.Fit || EditRecipe is not null)
        && Image is { } image && image.Size != image.SourceSize
        && pixelScale > Math.Min((double)image.Size.Width / image.SourceSize.Width,
            (double)image.Size.Height / image.SourceSize.Height);

    // The canvas measures DIP; decoder targets are physical pixels, bounded by the shared policy.
    public PixelSize CurrentPreviewTarget
    {
        get
        {
            var dpi = VisualTreeHelper.GetDpi(Canvas);
            return PreviewDecodePolicy.CalculateTarget(Canvas.ActualWidth, Canvas.ActualHeight,
                dpi.DpiScaleX, dpi.DpiScaleY);
        }
    }

    private void NotifyPreviewTargetChanged(System.Windows.DpiScale? changedDpi = null)
    {
        if (_disposed || _isUnloaded) { return; }
        PixelSize target = changedDpi is { } dpi
            ? PreviewDecodePolicy.CalculateTarget(Canvas.ActualWidth, Canvas.ActualHeight, dpi.DpiScaleX, dpi.DpiScaleY)
            : CurrentPreviewTarget;
        if (_lastPreviewTarget == target) { return; }
        _lastPreviewTarget = target;
        PreviewTargetChanged?.Invoke(this, target);
    }

    private static bool IsSameSource(ImageOpenState? previous, ImageOpenState? current)
    {
        if (previous?.Image is null || current?.Image is null
            || previous.Image.SourceSize != current.Image.SourceSize)
        {
            return false;
        }
        if (current.Sequence?.Kind == ImageSequenceKind.Pages && previous.FrameIndex != current.FrameIndex)
        {
            return false;
        }
        if (previous.Source is not null || current.Source is not null)
        {
            return previous.Source is not null && current.Source is not null
                && previous.Source.Identity == current.Source.Identity;
        }
        // Older/editor callers may supply a path without the browsing source model.
        return previous.FilePath is not null && current.FilePath is not null
            && string.Equals(previous.FilePath, current.FilePath, StringComparison.OrdinalIgnoreCase);
    }

    private void NotifyFramePresented()
    {
        if (FramePresented is null || _disposed || _isUnloaded || _bitmap is null || Image is null
            || Presentation is not { } state || !ReferenceEquals(state.Image, Image)
            || !ReferenceEquals(state.Region, _displayedRegion)) { return; }
        try
        {
            _ = Image.Pixels;
            _ = state.Region?.Image.Pixels;
        }
        catch (ObjectDisposedException) { return; }
        FramePresented?.Invoke(this, state);
    }
}
