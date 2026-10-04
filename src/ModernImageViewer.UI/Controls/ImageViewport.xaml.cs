using System.Runtime.InteropServices;
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

    public static readonly DependencyProperty CanvasBackgroundProperty = DependencyProperty.Register(
        nameof(CanvasBackground), typeof(Brush), typeof(ImageViewport),
        new PropertyMetadata(Brushes.Transparent, (sender, _) => ((ImageViewport)sender).Canvas.InvalidateVisual()));

    public Brush CanvasBackground
    {
        get => (Brush)GetValue(CanvasBackgroundProperty);
        set => SetValue(CanvasBackgroundProperty, value);
    }

    private SKBitmap? _checkerTile;
    private SKPaint? _checkerPaint;
    private SKColor _checkerDark;
    private SKColor _checkerLight;
    private readonly ViewportTransform _transform = new();
    private SKBitmap? _bitmap;
    private Point? _lastPointer;

    public ImageViewport()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        LostMouseCapture += (_, _) => { _lastPointer = null; Cursor = Image is null ? Cursors.Arrow : Cursors.Hand; };
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

        _transform.ActualSize(Image.Size, Canvas.ActualWidth, Canvas.ActualHeight, 1 / VisualTreeHelper.GetDpi(Canvas).DpiScaleX);
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
        Cursor = image is null ? Cursors.Arrow : Cursors.Hand;
        if (image is null)
        {
            Canvas.InvalidateVisual();
            return;
        }

        SKImageInfo info = new(image.Size.Width, image.Size.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        _bitmap = new SKBitmap(info);
        ArraySegment<byte> segment = MemoryMarshal.TryGetArray(image.Pixels, out ArraySegment<byte> storage)
            ? storage : new ArraySegment<byte>(image.Pixels.ToArray());
        byte[] pixels = segment.Array!;
        int rowLength = checked(image.Size.Width * 4);
        for (int row = 0; row < image.Size.Height; row++)
        {
            IntPtr destination = IntPtr.Add(_bitmap.GetPixels(), checked(row * _bitmap.RowBytes));
            Marshal.Copy(pixels, checked(segment.Offset + (row * image.Stride)), destination, rowLength);
        }
        Fit();
    }

    private void OnPaintSurface(object? sender, SKPaintSurfaceEventArgs e)
    {
        SKCanvas canvas = e.Surface.Canvas;
        canvas.Clear(ToSkColor(CanvasBackground));
        if (_bitmap is null)
        {
            return;
        }

        double dpiScale = VisualTreeHelper.GetDpi(Canvas).DpiScaleX;
        canvas.Save();
        canvas.Scale((float)dpiScale);
        EnsureCheckerPaint();
        canvas.DrawRect(new SKRect((float)_transform.OffsetX, (float)_transform.OffsetY,
            (float)(_transform.OffsetX + (_bitmap.Width * _transform.Scale)),
            (float)(_transform.OffsetY + (_bitmap.Height * _transform.Scale))), _checkerPaint!);
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
        _transform.ZoomAt(Math.Pow(1.15, e.Delta / 120.0), point.X, point.Y);
        e.Handled = true;
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
        Cursor = Cursors.SizeAll;
        e.Handled = true;
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
        NotifyTransformChanged();
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
        else if (_transform.Mode == ViewportMode.ActualSize)
        {
            ActualSize();
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
        ScaleChanged?.Invoke(this, _transform.Scale * VisualTreeHelper.GetDpi(Canvas).DpiScaleX);
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        if (_transform.Mode == ViewportMode.ActualSize)
        {
            ActualSize();
        }
        else
        {
            NotifyTransformChanged();
        }
    }

    private static SKColor ToSkColor(Brush brush)
    {
        Color color = brush is SolidColorBrush solid ? solid.Color : Colors.Transparent;
        return new SKColor(color.R, color.G, color.B, color.A);
    }

    private void EnsureCheckerPaint()
    {
        SKColor dark = ToSkColor((Brush)FindResource("CheckerDarkBrush"));
        SKColor light = ToSkColor((Brush)FindResource("CheckerLightBrush"));
        if (_checkerPaint is not null && dark == _checkerDark && light == _checkerLight)
        {
            return;
        }

        DisposeChecker();
        _checkerDark = dark;
        _checkerLight = light;
        _checkerTile = new SKBitmap(16, 16);
        using SKCanvas tileCanvas = new(_checkerTile);
        tileCanvas.Clear(dark);
        using SKPaint squares = new() { Color = light };
        tileCanvas.DrawRect(0, 0, 8, 8, squares);
        tileCanvas.DrawRect(8, 8, 8, 8, squares);
        using SKShader shader = SKShader.CreateBitmap(_checkerTile, SKShaderTileMode.Repeat, SKShaderTileMode.Repeat);
        _checkerPaint = new SKPaint { Shader = shader };
    }

    private void DisposeChecker()
    {
        _checkerPaint?.Dispose();
        _checkerPaint = null;
        _checkerTile?.Dispose();
        _checkerTile = null;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (Image is not null && _bitmap is null)
        {
            RebuildBitmap(Image);
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        DisposeBitmap();
        DisposeChecker();
    }

    public void Dispose()
    {
        DisposeBitmap();
        DisposeChecker();
        GC.SuppressFinalize(this);
    }

    private void DisposeBitmap()
    {
        _bitmap?.Dispose();
        _bitmap = null;
    }
}
