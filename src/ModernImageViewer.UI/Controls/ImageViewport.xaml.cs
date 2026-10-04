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
    public event EventHandler<double>? ScaleChanged;

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
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        LostMouseCapture += (_, _) => _lastPointer = null;
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
        NotifyTransformChanged();
    }

    public void ActualSize()
    {
        if (Image is null)
        {
            return;
        }

        _transform.ActualSize(Image.Size, Canvas.ActualWidth, Canvas.ActualHeight);
        NotifyTransformChanged();
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
        int rowLength = checked(image.Size.Width * 4);
        for (int row = 0; row < image.Size.Height; row++)
        {
            IntPtr destination = IntPtr.Add(_bitmap.GetPixels(), checked(row * _bitmap.RowBytes));
            System.Runtime.InteropServices.Marshal.Copy(pixels, checked(row * image.Stride), destination, rowLength);
        }
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

    public void ZoomIn() => ZoomAtCenter(1.15);

    public void ZoomOut() => ZoomAtCenter(1 / 1.15);

    private void OnMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (Image is null)
        {
            return;
        }

        Point point = e.GetPosition(Canvas);
        _transform.ZoomAt(e.Delta > 0 ? 1.15 : 1 / 1.15, point.X, point.Y);
        NotifyTransformChanged();
    }

    private void OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (Image is null)
        {
            return;
        }

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

    private void ZoomAtCenter(double factor)
    {
        if (Image is null)
        {
            return;
        }

        _transform.ZoomAt(factor, Canvas.ActualWidth / 2, Canvas.ActualHeight / 2);
        NotifyTransformChanged();
    }

    private void NotifyTransformChanged()
    {
        Canvas.InvalidateVisual();
        ScaleChanged?.Invoke(this, _transform.Scale);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (Image is not null && _bitmap is null)
        {
            RebuildBitmap(Image);
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e) => DisposeBitmap();

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
