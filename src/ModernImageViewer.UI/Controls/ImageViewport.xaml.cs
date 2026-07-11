using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

using ModernImageViewer.Imaging;

using SkiaSharp;
using SkiaSharp.Views.Desktop;

namespace ModernImageViewer.UI.Controls;

public partial class ImageViewport : UserControl, IDisposable
{
    public static readonly DependencyProperty ImageProperty = DependencyProperty.Register(
        nameof(Image),
        typeof(PixelBuffer),
        typeof(ImageViewport),
        new PropertyMetadata(null, OnImageChanged));

    private readonly ViewportTransform _transform = new();
    private SKBitmap? _bitmap;
    private Point? _lastPointer;

    public ImageViewport()
    {
        InitializeComponent();
        Unloaded += (_, _) => DisposeBitmap();
    }

    public PixelBuffer? Image
    {
        get => (PixelBuffer?)GetValue(ImageProperty);
        set => SetValue(ImageProperty, value);
    }

    public void Fit()
    {
        if (Image is null)
        {
            return;
        }

        _transform.Fit(Image.Size, Canvas.ActualWidth, Canvas.ActualHeight);
        Canvas.InvalidateVisual();
    }

    public void ActualSize()
    {
        if (Image is null)
        {
            return;
        }

        _transform.ActualSize(Image.Size, Canvas.ActualWidth, Canvas.ActualHeight);
        Canvas.InvalidateVisual();
    }

    private static void OnImageChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs e)
    {
        ImageViewport viewport = (ImageViewport)dependencyObject;
        viewport.RebuildBitmap((PixelBuffer?)e.NewValue);
    }

    private void RebuildBitmap(PixelBuffer? image)
    {
        DisposeBitmap();
        if (image is null)
        {
            Canvas.InvalidateVisual();
            return;
        }

        SKImageInfo info = new(image.Size.Width, image.Size.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        _bitmap = new SKBitmap(info);
        byte[] pixels = image.Pixels.ToArray();
        System.Runtime.InteropServices.Marshal.Copy(pixels, 0, _bitmap.GetPixels(), pixels.Length);
        Fit();
    }

    private void OnPaintSurface(object? sender, SKPaintSurfaceEventArgs e)
    {
        SKCanvas canvas = e.Surface.Canvas;
        canvas.Clear(new SKColor(17, 19, 24));
        if (_bitmap is null)
        {
            return;
        }

        double dpiScale = VisualTreeHelper.GetDpi(Canvas).DpiScaleX;
        canvas.Save();
        canvas.Scale((float)dpiScale);
        canvas.Translate((float)_transform.OffsetX, (float)_transform.OffsetY);
        canvas.Scale((float)_transform.Scale);
        canvas.DrawBitmap(_bitmap, 0, 0, new SKSamplingOptions(SKFilterMode.Linear));
        canvas.Restore();
    }

    private void OnMouseWheel(object sender, MouseWheelEventArgs e)
    {
        Point point = e.GetPosition(Canvas);
        _transform.ZoomAt(e.Delta > 0 ? 1.15 : 1 / 1.15, point.X, point.Y);
        Canvas.InvalidateVisual();
    }

    private void OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            ToggleFitAndActualSize();
            return;
        }

        _lastPointer = e.GetPosition(Canvas);
        Canvas.CaptureMouse();
    }

    private void OnMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        _lastPointer = null;
        Canvas.ReleaseMouseCapture();
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (_lastPointer is null || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        Point point = e.GetPosition(Canvas);
        _transform.Pan(point.X - _lastPointer.Value.X, point.Y - _lastPointer.Value.Y);
        _lastPointer = point;
        Canvas.InvalidateVisual();
    }

    private void ToggleFitAndActualSize()
    {
        if (_transform.Mode == ViewportMode.Fit)
        {
            ActualSize();
        }
        else
        {
            Fit();
        }
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_transform.Mode == ViewportMode.Fit)
        {
            Fit();
        }
    }

    public void Dispose()
    {
        DisposeBitmap();
        GC.SuppressFinalize(this);
    }

    private void DisposeBitmap()
    {
        _bitmap?.Dispose();
        _bitmap = null;
    }
}
