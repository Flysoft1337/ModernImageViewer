using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

using ModernImageViewer.Application.Images;
using ModernImageViewer.Imaging;
using ModernImageViewer.UI.Rendering;

using SkiaSharp;
using SkiaSharp.Views.Desktop;
using SkiaSharp.Views.WPF;

namespace ModernImageViewer.UI.Controls;

public partial class ImageViewport : UserControl, IDisposable
{
    public event EventHandler<double>? ScaleChanged;
    public event EventHandler? DetailRequested;
    public event EventHandler<PixelRect>? RegionDetailRequested;
    public event EventHandler? OrientationChanged;
    public ViewOrientation Orientation { get; private set; }
    public ImageEditRecipe? EditRecipe { get; private set; }

    public void SetEditRecipe(ImageEditRecipe? recipe, bool fit = true)
    {
        if (recipe is not null && Image?.SourceSize != recipe.SourceSize)
        {
            throw new ArgumentException("The recipe must match the displayed source.", nameof(recipe));
        }
        PixelSize? previousSize = EditRecipe?.OutputSize;
        if (EditRecipe != recipe) { ClearEditorPreview(); }
        EditRecipe = recipe;
        _lastRequestedRegion = null;
        if (fit || previousSize != recipe?.OutputSize) { Fit(); }
        else { _surface?.InvalidateVisual(); }
    }

    private PixelSize DisplaySize => EditRecipe?.OutputSize ?? Orientation.GetDisplaySize(Image!.SourceSize);

    public (double X, double Y) ToSourcePoint(Point position)
    {
        double x = (position.X - _transform.OffsetX) / _transform.Scale;
        double y = (position.Y - _transform.OffsetY) / _transform.Scale;
        return EditRecipe?.ToSource(x, y) ?? Orientation.ToSourcePoint(Image!.SourceSize, x, y);
    }

    public Point ToCanvasPoint(double x, double y)
    {
        var point = EditRecipe?.ToOutput(x, y) ?? Orientation.ToDisplayPoint(Image!.SourceSize, x, y);
        return new Point((point.X * _transform.Scale) + _transform.OffsetX, (point.Y * _transform.Scale) + _transform.OffsetY);
    }
    public PixelRect? VisibleDetailRegion => CalculateVisibleDetailRegion();

    public static readonly DependencyProperty PresentationProperty = DependencyProperty.Register(
        nameof(Presentation), typeof(ImageOpenState), typeof(ImageViewport),
        new PropertyMetadata(null, OnPresentationChanged));

    public ImageOpenState? Presentation
    {
        get => (ImageOpenState?)GetValue(PresentationProperty);
        set => SetValue(PresentationProperty, value);
    }

    public static readonly DependencyProperty ImageProperty = DependencyProperty.Register(
        nameof(Image),
        typeof(PixelBuffer),
        typeof(ImageViewport),
        new PropertyMetadata(null, OnImageChanged));

    public static readonly DependencyProperty CanvasBackgroundProperty = DependencyProperty.Register(
        nameof(CanvasBackground), typeof(Brush), typeof(ImageViewport),
        new PropertyMetadata(Brushes.Transparent, (sender, _) => ((ImageViewport)sender)._surface?.InvalidateVisual()));

    public Brush CanvasBackground
    {
        get => (Brush)GetValue(CanvasBackgroundProperty);
        set => SetValue(CanvasBackgroundProperty, value);
    }

    private SKElement? _surface;
    private SKBitmap? _checkerTile;
    private SKPaint? _checkerPaint;
    private SKColor _checkerDark;
    private SKColor _checkerLight;
    private readonly ViewportTransform _transform = new();
    private SKBitmap? _bitmap;
    private Point? _lastPointer;
    private bool _preserveTransform;
    private bool _detailRequested;
    private readonly DispatcherTimer _regionTimer = new() { Interval = TimeSpan.FromMilliseconds(160) };
    private SKBitmap? _regionBitmap;
    private DecodedImageRegion? _displayedRegion;
    private PixelRect? _lastRequestedRegion;

    public ImageViewport()
    {
        InitializeComponent();
        _regionTimer.Tick += OnRegionTimer;
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

        _transform.Fit(DisplaySize, Canvas.ActualWidth, Canvas.ActualHeight);
        NotifyTransformChanged();
    }

    public void ActualSize()
    {
        if (Image is null)
        {
            return;
        }

        _transform.ActualSize(DisplaySize, Canvas.ActualWidth, Canvas.ActualHeight, 1 / VisualTreeHelper.GetDpi(Canvas).DpiScaleX);
        NotifyTransformChanged();
    }

    public void RotateLeft() => ChangeOrientation(Orientation.RotateLeft());
    public void RotateRight() => ChangeOrientation(Orientation.RotateRight());
    public void FlipHorizontal() => ChangeOrientation(Orientation.FlipHorizontal());
    public void FlipVertical() => ChangeOrientation(Orientation.FlipVertical());
    public void ResetOrientation() => ChangeOrientation(default);

    private void ChangeOrientation(ViewOrientation orientation)
    {
        if (Image is not { } image || orientation == Orientation)
        {
            return;
        }
        _transform.Reorient(image.SourceSize, Orientation, orientation, Canvas.ActualWidth, Canvas.ActualHeight,
            1 / VisualTreeHelper.GetDpi(Canvas).DpiScaleX);
        Orientation = orientation;
        _lastRequestedRegion = null;
        OrientationChanged?.Invoke(this, EventArgs.Empty);
        NotifyTransformChanged();
    }

    private static void OnPresentationChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs e)
    {
        ImageViewport viewport = (ImageViewport)dependencyObject;
        ImageOpenState? previous = (ImageOpenState?)e.OldValue;
        ImageOpenState? current = (ImageOpenState?)e.NewValue;
        // A single binding carries path and pixels together, so a late refinement cannot reset
        // zoom or accidentally preserve the position when switching to another same-size image.
        viewport._preserveTransform = previous?.Image is not null && current?.Image is not null
            && (previous.IsMemorySource || current.IsMemorySource
                ? previous.Source?.Identity == current.Source?.Identity
                : string.Equals(previous.FilePath, current.FilePath, StringComparison.OrdinalIgnoreCase))
            && previous.Image.SourceSize == current.Image.SourceSize;
        if (!viewport._preserveTransform)
        {
            viewport.EditRecipe = null;
            viewport.ClearOrientation();
        }
        viewport.Image = current?.Image;
        viewport._preserveTransform = false;
        viewport.RebuildRegionBitmap(current?.Region);
    }

    private static void OnImageChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs e)
    {
        ImageViewport viewport = (ImageViewport)dependencyObject;
        viewport.RebuildBitmap((PixelBuffer?)e.NewValue);
    }

    private void RebuildBitmap(PixelBuffer? image)
    {
        if (!_preserveTransform)
        {
            ClearOrientation();
        }
        DisposeBitmap();
        Cursor = image is null ? Cursors.Arrow : Cursors.Hand;
        if (image is null)
        {
            _surface?.InvalidateVisual();
            return;
        }

        if (_surface is null)
        {
            _surface = new SKElement();
            _surface.PaintSurface += OnPaintSurface;
            Canvas.Children.Add(_surface);
        }
        if (_editSourceProfile is { } profile)
        {
            using SKColorSpace colorSpace = SKColorSpace.CreateIcc(profile);
            _bitmap = SharedPixelBitmap.Create(image, colorSpace);
        }
        else { _bitmap = SharedPixelBitmap.Create(image); }
        _detailRequested = false;
        _lastRequestedRegion = null;
        if (_preserveTransform)
        {
            NotifyTransformChanged();
        }
        else
        {
            Fit();
        }
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
        PixelSize sourceSize = Image!.SourceSize;
        PixelSize displaySize = DisplaySize;
        canvas.DrawRect(new SKRect((float)_transform.OffsetX, (float)_transform.OffsetY,
            (float)(_transform.OffsetX + (displaySize.Width * _transform.Scale)),
            (float)(_transform.OffsetY + (displaySize.Height * _transform.Scale))), _checkerPaint!);
        canvas.Translate((float)_transform.OffsetX, (float)_transform.OffsetY);
        canvas.Scale((float)_transform.Scale);
        if (EditRecipe is { } edited && (_editSourceProfile is not null || !edited.Adjustments.IsIdentity || !edited.Annotations.IsEmpty))
        {
            SKBitmap preview = GetEditorPreview(edited);
            canvas.DrawBitmap(preview, new SKRect(0, 0, displaySize.Width, displaySize.Height),
                new SKSamplingOptions(SKFilterMode.Linear));
        }
        else { DrawImagePixels(canvas, EditRecipe); }
        canvas.Restore();
    }

    private void DrawImagePixels(SKCanvas canvas, ImageEditRecipe? recipe)
    {
        PixelSize sourceSize = Image!.SourceSize;
        canvas.Save();
        var orientation = recipe?.GetMatrix() ?? Orientation.GetMatrix(sourceSize);
        canvas.Concat(new SKMatrix((float)orientation.M11, (float)orientation.M21, (float)orientation.OffsetX,
            (float)orientation.M12, (float)orientation.M22, (float)orientation.OffsetY, 0, 0, 1));
        if (recipe is not null)
        {
            canvas.ClipRect(ToSkRect(recipe.Crop));
        }
        // Exclude the detailed rectangle from the preview, so translucent pixels are composited once.
        canvas.Save();
        if (_regionBitmap is not null && _displayedRegion is { } detailed)
        {
            canvas.ClipRect(ToSkRect(detailed.Bounds), SKClipOperation.Difference);
        }
        if (recipe is not null && Image.Size == sourceSize)
        {
            using SKBitmap cropped = new();
            PixelRect bounds = recipe.Crop;
            if (_bitmap!.ExtractSubset(cropped, new SKRectI(bounds.X, bounds.Y, bounds.Right, bounds.Bottom)))
            { canvas.DrawBitmap(cropped, ToSkRect(bounds), new SKSamplingOptions(SKFilterMode.Linear)); }
        }
        else
        {
            canvas.DrawBitmap(_bitmap, new SKRect(0, 0, sourceSize.Width, sourceSize.Height),
                new SKSamplingOptions(SKFilterMode.Linear));
        }
        canvas.Restore();
        if (_regionBitmap is not null && _displayedRegion is { } region)
        {
            canvas.DrawBitmap(_regionBitmap, ToSkRect(region.Bounds), new SKSamplingOptions(SKFilterMode.Linear));
        }
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

        Focus();
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
        else
        {
            NotifyTransformChanged();
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
        _surface?.InvalidateVisual();
        double pixelScale = _transform.Scale * VisualTreeHelper.GetDpi(Canvas).DpiScaleX;
        ScaleChanged?.Invoke(this, pixelScale);
        _regionTimer.Stop();
        if (NeedsRegionDetail(pixelScale))
        {
            _regionTimer.Start();
        }
        if (!_detailRequested && Image is { } image && image.Size != image.SourceSize
            && pixelScale > Math.Min((double)image.Size.Width / image.SourceSize.Width,
                (double)image.Size.Height / image.SourceSize.Height))
        {
            _detailRequested = true;
            DetailRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    private bool NeedsRegionDetail(double pixelScale) => Presentation is { Status: ImageOpenStatus.Loaded, IsPreview: true } state
        && state.RefinementError != ImageOpenError.UnsupportedFormat && Image is { } image
        && image.SourceSize.PixelCount > ImageOpenCoordinator.FullResolutionOutputLimit / 4
        && pixelScale > Math.Min((double)image.Size.Width / image.SourceSize.Width,
            (double)image.Size.Height / image.SourceSize.Height);

    private PixelRect? CalculateVisibleDetailRegion()
    {
        if (EditRecipe is { } recipe)
        {
            return recipe.GetVisibleSourceRegion(_transform, Canvas.ActualWidth, Canvas.ActualHeight);
        }
        return Image is { } image
            ? _transform.GetVisibleSourceRegion(image.SourceSize, Orientation, Canvas.ActualWidth, Canvas.ActualHeight)
            : null;
    }

    private void ClearOrientation()
    {
        if (Orientation == default)
        {
            return;
        }
        Orientation = default;
        _lastRequestedRegion = null;
        OrientationChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnRegionTimer(object? sender, EventArgs e)
    {
        _regionTimer.Stop();
        double scale = _transform.Scale * VisualTreeHelper.GetDpi(Canvas).DpiScaleX;
        if (!NeedsRegionDetail(scale) || VisibleDetailRegion is not { } bounds || _lastRequestedRegion == bounds
            || (Presentation?.Region?.Bounds == bounds && !Presentation.IsRegionLoading))
        {
            return;
        }
        _lastRequestedRegion = bounds;
        RegionDetailRequested?.Invoke(this, bounds);
    }

    private void RebuildRegionBitmap(DecodedImageRegion? region)
    {
        if (ReferenceEquals(_displayedRegion, region) && (region is null || _regionBitmap is not null))
        {
            return;
        }
        ClearEditorPreview();
        _regionBitmap?.Dispose();
        _regionBitmap = region is null ? null : SharedPixelBitmap.Create(region.Image);
        _displayedRegion = region;
        _surface?.InvalidateVisual();
    }

    private static SKRect ToSkRect(PixelRect bounds) => new(bounds.X, bounds.Y, bounds.Right, bounds.Bottom);

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
            _preserveTransform = true;
            RebuildBitmap(Image);
            _preserveTransform = false;
            RebuildRegionBitmap(Presentation?.Region);
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _regionTimer.Stop();
        DisposeBitmap();
        DisposeChecker();
        RemoveSurface();
    }

    private void RemoveSurface()
    {
        if (_surface is not null)
        {
            _surface.PaintSurface -= OnPaintSurface;
            Canvas.Children.Remove(_surface);
            _surface = null;
        }
    }

    public void Dispose()
    {
        _regionTimer.Stop();
        _regionTimer.Tick -= OnRegionTimer;
        DisposeBitmap();
        DisposeChecker();
        RemoveSurface();
        GC.SuppressFinalize(this);
    }

    private void DisposeBitmap()
    {
        ClearEditorPreview();
        _regionBitmap?.Dispose();
        _regionBitmap = null;
        _displayedRegion = null;
        _bitmap?.Dispose();
        _bitmap = null;
    }
}
