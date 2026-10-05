using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

using Microsoft.Win32;

using ModernImageViewer.Application.Editing;
using ModernImageViewer.Application.Images;
using ModernImageViewer.Imaging;
using ModernImageViewer.UI.Localization;

namespace ModernImageViewer.UI;

public partial class EditWindow : Window
{
    private readonly ILocalizationService _localization;
    private readonly IImageExportService _exporter;
    private readonly ImageEditSession _session;
    private readonly string? _sourcePath;
    private readonly long? _sourceLength;
    private readonly DateTime? _sourceModified;
    private readonly Guid? _memorySourceIdentity;
    private ImageExportPixels? _sourcePixels;
    private bool _refreshing;
    private bool _sizeDirty;
    private bool _selectingCrop;
    private (double X, double Y)? _cropStart;
    private PixelSize? _cropRatio;
    private CancellationTokenSource? _exportCancellation;
    private bool _closeAfterExport;

    public EditWindow(ImageOpenState presentation, ViewOrientation orientation, ILocalizationService localization,
        IImageExportService exporter, long? sourceLength = null, DateTime? sourceModifiedUtc = null,
        ImageExportPixels? sourcePixels = null)
    {
        ArgumentNullException.ThrowIfNull(presentation);
        if (presentation.Image is null) { throw new ArgumentException("An image is required.", nameof(presentation)); }
        _localization = localization;
        _exporter = exporter;
        _sourcePath = presentation.FilePath;
        if (presentation.IsMemorySource)
        {
            if (_sourcePath is not null || presentation.Source!.Identity == Guid.Empty || sourcePixels is null
                || sourcePixels.Size != presentation.Image.SourceSize || sourcePixels.SourceFileStamp is not null
                || sourcePixels.Stride < (long)sourcePixels.Size.Width * 4
                || sourcePixels.Pixels.Length < (long)sourcePixels.Stride * sourcePixels.Size.Height)
            {
                throw new ArgumentException("A memory source requires an identity and complete pixels.", nameof(presentation));
            }
            if ((long)sourcePixels.Stride * sourcePixels.Size.Height > 64L * 1024 * 1024
                || sourcePixels.Pixels.Length > 64L * 1024 * 1024)
            {
                throw new ImageExportException(ImageExportError.BudgetExceeded);
            }
            _memorySourceIdentity = presentation.Source.Identity;
            _sourcePixels = sourcePixels;
        }
        else if (string.IsNullOrWhiteSpace(_sourcePath) || sourceLength is null || sourceModifiedUtc is null)
        {
            throw new ArgumentException("A file source and its version are required.", nameof(presentation));
        }
        _session = new(presentation.Image!.SourceSize, orientation);
        _sourceLength = _memorySourceIdentity is null ? sourceLength : null;
        _sourceModified = _memorySourceIdentity is null ? sourceModifiedUtc : null;
        if (_memorySourceIdentity is null && presentation.Image.Size == presentation.Image.SourceSize
            && presentation.Image.SourceFileStamp == new ImageFileStamp(sourceLength!.Value, sourceModifiedUtc!.Value))
        {
            _sourcePixels = new(presentation.Image.Size, presentation.Image.Stride, presentation.Image.Pixels, presentation.Image.SourceFileStamp);
        }
        DataContext = new EditorStrings(localization);
        InitializeComponent();
        if (presentation.IsMemorySource)
        {
            PreviewNotice.Text = Text("Edit_ClipboardNotice");
        }
        else if (presentation.Image.Metadata.IsEmbeddedPreview)
        {
            PreviewNotice.Text = Text("Edit_EmbeddedPreviewNotice");
        }
        // Own a read-only wrapper, sharing the array without depending on the browsing owner.
        if (!MemoryMarshal.TryGetArray(presentation.Image.Pixels, out ArraySegment<byte> previewPixels)
            || previewPixels.Array is null || previewPixels.Offset != 0)
        {
            throw new ArgumentException("Array-backed preview pixels are required.", nameof(presentation));
        }
        PixelBuffer previewSnapshot = new(presentation.Image.Size, presentation.Image.Stride, previewPixels.Array,
            presentation.Image.Metadata, presentation.Image.SourceSize, presentation.Image.SourceFileStamp);
        Preview.Presentation = presentation with { Image = previewSnapshot, Region = null, IsRefining = false, IsRegionLoading = false };
        RefreshEditor();
        UpdateFormatHint();
        Loaded += (_, _) => Preview.Focus();
        Closing += OnClosing;
        Closed += (_, _) =>
        {
            Preview.Dispose();
            Preview.Presentation = null;
            previewSnapshot.Dispose();
            _sourcePixels = null;
        };
    }

    public ImageEditRecipe Recipe => _session.Current;
    public bool IsExporting => _exportCancellation is not null;

    private void RefreshEditor()
    {
        _refreshing = true;
        try
        {
            ImageEditRecipe recipe = Recipe;
            CropX.Text = recipe.Crop.X.ToString(CultureInfo.InvariantCulture);
            CropY.Text = recipe.Crop.Y.ToString(CultureInfo.InvariantCulture);
            CropWidth.Text = recipe.Crop.Width.ToString(CultureInfo.InvariantCulture);
            CropHeight.Text = recipe.Crop.Height.ToString(CultureInfo.InvariantCulture);
            OutputWidth.Text = recipe.OutputSize.Width.ToString(CultureInfo.InvariantCulture);
            OutputHeight.Text = recipe.OutputSize.Height.ToString(CultureInfo.InvariantCulture);
            UndoButton.IsEnabled = _session.CanUndo;
            RedoButton.IsEnabled = _session.CanRedo;
            _sizeDirty = false;
            _selectingCrop = false;
            CropOverlay.Visibility = Visibility.Collapsed;
            SelectionRectangle.Visibility = Visibility.Collapsed;
            Preview.SetEditRecipe(recipe);
            OutputSummary.Text = string.Format(_localization.CurrentCulture, Text("Edit_OutputSummary"),
                recipe.OutputSize.Width, recipe.OutputSize.Height);
            StatusText.Text = Text("Edit_Ready");
        }
        finally { _refreshing = false; }
    }

    private void OnUndoClick(object sender, RoutedEventArgs e) { _session.Undo(); RefreshEditor(); }
    private void OnRedoClick(object sender, RoutedEventArgs e) { _session.Redo(); RefreshEditor(); }
    private void OnResetClick(object sender, RoutedEventArgs e) { _session.Reset(); RefreshEditor(); }

    private void OnDirectionClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string action }) { return; }
        _session.Apply(action switch
        {
            "Left" => Recipe.RotateLeft(),
            "Right" => Recipe.RotateRight(),
            "Horizontal" => Recipe.FlipHorizontal(),
            "Vertical" => Recipe.FlipVertical(),
            _ => Recipe,
        });
        RefreshEditor();
    }

    private void OnSelectCropClick(object sender, RoutedEventArgs e)
    {
        _selectingCrop = !_selectingCrop;
        Preview.SetEditRecipe(_selectingCrop ? ImageEditRecipe.Create(Recipe.SourceSize, Recipe.Orientation) : Recipe);
        CropOverlay.Visibility = _selectingCrop ? Visibility.Visible : Visibility.Collapsed;
        SelectionRectangle.Visibility = Visibility.Collapsed;
        StatusText.Text = Text(_selectingCrop ? "Edit_DragCrop" : "Edit_Ready");
        Preview.Focus();
    }

    private (double X, double Y) SourcePoint(Point pointer)
    {
        var point = Preview.ToSourcePoint(pointer);
        return (Math.Clamp(point.X, 0, Recipe.SourceSize.Width), Math.Clamp(point.Y, 0, Recipe.SourceSize.Height));
    }

    private void OnCropDown(object sender, MouseButtonEventArgs e)
    {
        _cropStart = SourcePoint(e.GetPosition(Preview));
        CropOverlay.CaptureMouse();
        e.Handled = true;
    }

    private void OnCropMove(object sender, MouseEventArgs e)
    {
        if (_cropStart is not { } start || e.LeftButton != MouseButtonState.Pressed) { return; }
        var end = SourcePoint(e.GetPosition(Preview));
        if (ImageCropGeometry.Drag(Recipe.SourceSize, Recipe.Orientation, start, end, CropRatioOriginal.IsChecked == true
            ? Recipe.Orientation.GetDisplaySize(Recipe.SourceSize) : _cropRatio) is not { } crop)
        {
            SelectionRectangle.Visibility = Visibility.Collapsed;
            return;
        }
        WriteCropValues(crop);
        ShowCropRectangle(crop);
        e.Handled = true;
    }

    private void WriteCropValues(PixelRect crop)
    {
        CropX.Text = crop.X.ToString(CultureInfo.InvariantCulture);
        CropY.Text = crop.Y.ToString(CultureInfo.InvariantCulture);
        CropWidth.Text = crop.Width.ToString(CultureInfo.InvariantCulture);
        CropHeight.Text = crop.Height.ToString(CultureInfo.InvariantCulture);
    }

    private void ShowCropRectangle(PixelRect crop)
    {
        Point first = Preview.ToCanvasPoint(crop.X, crop.Y);
        Point last = Preview.ToCanvasPoint(crop.Right, crop.Bottom);
        Canvas.SetLeft(SelectionRectangle, Math.Min(first.X, last.X));
        Canvas.SetTop(SelectionRectangle, Math.Min(first.Y, last.Y));
        SelectionRectangle.Width = Math.Abs(last.X - first.X);
        SelectionRectangle.Height = Math.Abs(last.Y - first.Y);
        SelectionRectangle.Visibility = Visibility.Visible;
    }

    private void OnCropRatioChanged(object sender, RoutedEventArgs e)
    {
        if (_refreshing || CropX is null || sender is not RadioButton { Tag: string tag }) { return; }
        _cropRatio = tag switch
        {
            "Original" => Recipe.Orientation.GetDisplaySize(Recipe.SourceSize),
            "1:1" => new(1, 1),
            "4:3" => new(4, 3),
            "3:2" => new(3, 2),
            "16:9" => new(16, 9),
            "9:16" => new(9, 16),
            _ => null,
        };
        if (_cropRatio is not { } ratio) { return; }
        PixelRect crop = ImageCropGeometry.Fit(Recipe.Crop, Recipe.Orientation, ratio);
        WriteCropValues(crop);
        if (_selectingCrop) { ShowCropRectangle(crop); }
        StatusText.Text = Text("Edit_ApplyCropHint");
    }

    private void OnCropUp(object sender, MouseButtonEventArgs e)
    {
        _cropStart = null;
        CropOverlay.ReleaseMouseCapture();
        e.Handled = true;
    }

    private void OnCropCaptureLost(object sender, MouseEventArgs e) => _cropStart = null;

    private bool TryReadCrop(out PixelRect crop)
    {
        crop = Recipe.Crop;
        if (!ReadInteger(CropX, 0, Recipe.SourceSize.Width - 1, out int x)
            || !ReadInteger(CropY, 0, Recipe.SourceSize.Height - 1, out int y)
            || !ReadInteger(CropWidth, 1, Recipe.SourceSize.Width - x, out int width)
            || !ReadInteger(CropHeight, 1, Recipe.SourceSize.Height - y, out int height)) { return false; }
        crop = new(x, y, width, height);
        return true;
    }

    private bool TryReadSize(out PixelSize size)
    {
        size = Recipe.OutputSize;
        if (!ReadInteger(OutputWidth, 1, 32768, out int width) || !ReadInteger(OutputHeight, 1, 32768, out int height)) { return false; }
        size = new(width, height);
        if (size.PixelCount * 4 > 64L * 1024 * 1024)
        {
            StatusText.Text = Text("Edit_Error_BudgetExceeded");
            return false;
        }
        return true;
    }

    private bool ReadInteger(TextBox input, int minimum, int maximum, out int value)
    {
        if (int.TryParse(input.Text, NumberStyles.None, CultureInfo.InvariantCulture, out value) && value >= minimum && value <= maximum) { return true; }
        StatusText.Text = Text("Edit_InvalidValues");
        input.Focus();
        input.SelectAll();
        return false;
    }

    private void OnApplyCropClick(object sender, RoutedEventArgs e)
    {
        if (!TryReadCrop(out PixelRect crop)) { return; }
        _session.Apply(Recipe.WithCrop(crop));
        RefreshEditor();
    }

    private void OnOutputSizeChanged(object sender, TextChangedEventArgs e)
    {
        if (_refreshing || OutputWidth is null || OutputHeight is null) { return; }
        _sizeDirty = true;
        if (LockAspectRatio?.IsChecked != true || sender is not TextBox input
            || !int.TryParse(input.Text, NumberStyles.None, CultureInfo.InvariantCulture, out int value) || value <= 0) { return; }
        double ratio = (double)Recipe.OutputSize.Width / Recipe.OutputSize.Height;
        _refreshing = true;
        try
        {
            if (ReferenceEquals(input, OutputWidth)) { OutputHeight.Text = Math.Max(1, Math.Round(value / ratio)).ToString(CultureInfo.InvariantCulture); }
            else { OutputWidth.Text = Math.Max(1, Math.Round(value * ratio)).ToString(CultureInfo.InvariantCulture); }
        }
        finally { _refreshing = false; }
    }

    private void OnApplySizeClick(object sender, RoutedEventArgs e)
    {
        if (!TryReadSize(out PixelSize size)) { return; }
        _session.Apply(Recipe.WithSize(size));
        RefreshEditor();
    }

    private void OnFormatChanged(object sender, RoutedEventArgs e) => UpdateFormatHint();
    private void UpdateFormatHint()
    {
        if (FormatHint is null || QualityPanel is null) { return; }
        bool jpeg = JpegFormat?.IsChecked == true;
        bool webp = WebpFormat?.IsChecked == true;
        FormatHint.Text = Text(webp ? "Edit_WebpHint" : jpeg ? "Edit_JpegHint" : "Edit_PngHint");
        QualityPanel.Visibility = jpeg || webp ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void OnSaveClick(object sender, RoutedEventArgs e)
    {
        if (IsExporting || !TryReadCrop(out PixelRect crop)) { return; }
        ImageEditRecipe next = crop == Recipe.Crop ? Recipe : Recipe.WithCrop(crop);
        if (_sizeDirty)
        {
            if (!TryReadSize(out PixelSize size)) { return; }
            next = next.WithSize(size);
        }
        _session.Apply(next);
        RefreshEditor();
        ImageExportFormat format = WebpFormat.IsChecked == true ? ImageExportFormat.Webp
            : JpegFormat.IsChecked == true ? ImageExportFormat.Jpeg : ImageExportFormat.Png;
        int quality = 90;
        if (format != ImageExportFormat.Png && !ReadInteger(JpegQuality, 1, 100, out quality)) { return; }
        string extension = format switch { ImageExportFormat.Png => ".png", ImageExportFormat.Webp => ".webp", _ => ".jpg" };
        SaveFileDialog dialog = new()
        {
            Title = Text("Edit_SaveAs"),
            AddExtension = true,
            DefaultExt = extension,
            Filter = format switch
            {
                ImageExportFormat.Png => "PNG (*.png)|*.png",
                ImageExportFormat.Webp => "WebP (*.webp)|*.webp",
                _ => "JPEG (*.jpg;*.jpeg)|*.jpg;*.jpeg",
            },
            FileName = (_sourcePath is null ? "clipboard" : Path.GetFileNameWithoutExtension(_sourcePath)) + "-edited" + extension,
            OverwritePrompt = false,
        };
        if (dialog.ShowDialog(this) != true) { return; }
        if (File.Exists(dialog.FileName))
        {
            StatusText.Text = Text("Edit_Error_DestinationExists");
            return;
        }
        CancellationTokenSource cancellation = new();
        _exportCancellation = cancellation;
        SetExporting(true);
        StatusText.Text = Text("Edit_Saving");
        try
        {
            await _exporter.ExportAsync(new(_sourcePath, dialog.FileName, Recipe, format,
                quality, _sourceLength, _sourceModified, _sourcePixels, _memorySourceIdentity), cancellation.Token);
            StatusText.Text = string.Format(_localization.CurrentCulture, Text("Edit_Saved"), Path.GetFileName(dialog.FileName));
        }
        catch (OperationCanceledException) { StatusText.Text = Text("Edit_Cancelled"); }
        catch (ImageExportException exception) { StatusText.Text = Text($"Edit_Error_{exception.Error}"); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            StatusText.Text = Text("Edit_Error_WriteFailed");
        }
        finally
        {
            _exportCancellation = null;
            cancellation.Dispose();
            SetExporting(false);
            if (_closeAfterExport) { Close(); }
        }
    }

    private void SetExporting(bool exporting)
    {
        EditToolbar.IsEnabled = !exporting;
        EditFields.IsEnabled = !exporting;
        SaveButton.IsEnabled = !exporting;
        CropOverlay.IsEnabled = !exporting;
        CancelExportButton.Visibility = exporting ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnCancelExportClick(object sender, RoutedEventArgs e) => _exportCancellation?.Cancel();
    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (!IsExporting) { return; }
        e.Cancel = true;
        _closeAfterExport = true;
        _exportCancellation!.Cancel();
        StatusText.Text = Text("Edit_Cancelling");
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            if (_selectingCrop) { OnSelectCropClick(this, e); }
            else if (IsExporting) { _exportCancellation!.Cancel(); }
            else { Close(); }
            e.Handled = true;
            return;
        }
        if (IsExporting) { return; }
        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.S)
        {
            OnSaveClick(this, e);
            e.Handled = true;
            return;
        }
        if (Keyboard.FocusedElement is TextBoxBase) { return; }
        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.Z) { OnUndoClick(this, e); }
        else if ((Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift) && e.Key == Key.Z)
            || (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.Y)) { OnRedoClick(this, e); }
        else { return; }
        e.Handled = true;
    }

    private string Text(string key) => _localization.GetString(key);

    private sealed class EditorStrings(ILocalizationService localization)
    {
        public string this[string key] => localization.GetString(key);
    }
}
