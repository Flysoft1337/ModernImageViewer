using ModernImageViewer.Imaging;
using ModernImageViewer.Rendering.Editing;
using ModernImageViewer.UI.Rendering;

using SkiaSharp;

namespace ModernImageViewer.UI.Controls;

public partial class ImageViewport
{
    public const long EditorPreviewByteLimit = 8L * 1024 * 1024;
    private SKBitmap? _editorPreview;
    private byte[]? _editSourceProfile;

    public void SetEditSourceColorProfile(byte[]? profile)
    {
        _editSourceProfile = profile;
        if (Image is { } image)
        {
            ClearEditorPreview();
            _bitmap?.Dispose();
            using SKColorSpace space = profile is null ? SKColorSpace.CreateSrgb() : SKColorSpace.CreateIcc(profile)
                ?? throw new ArgumentException("Invalid RGB color profile.", nameof(profile));
            _bitmap = SharedPixelBitmap.Create(image, space);
            _surface?.InvalidateVisual();
        }
    }

    private void ClearEditorPreview()
    {
        _editorPreview?.Dispose();
        _editorPreview = null;
    }

    private SKBitmap GetEditorPreview(ImageEditRecipe recipe)
    {
        if (_editorPreview is not null) { return _editorPreview; }
        PixelSize output = recipe.OutputSize;
        double scale = Math.Min(1, Math.Min(2048.0 / Math.Max(output.Width, output.Height),
            Math.Sqrt((double)EditorPreviewByteLimit / (output.PixelCount * 4))));
        PixelSize size = new(Math.Max(1, (int)Math.Floor(output.Width * scale)),
            Math.Max(1, (int)Math.Floor(output.Height * scale)));
        using SKColorSpace srgb = SKColorSpace.CreateSrgb();
        SKBitmap preview = new(new SKImageInfo(size.Width, size.Height, SKColorType.Bgra8888, SKAlphaType.Premul, srgb));
        try
        {
            if (preview.GetPixels() == IntPtr.Zero) { throw new InvalidOperationException("Unable to allocate editor preview."); }
            using SKCanvas canvas = new(preview);
            canvas.Clear(SKColors.Transparent);
            ImageEditRecipe scaled = recipe.WithSize(size);
            if (!recipe.Adjustments.IsIdentity)
            {
                using SKPaint paint = ImageEditEffects.CreatePaint(recipe.Adjustments, scale);
                canvas.SaveLayer(new SKRect(0, 0, size.Width, size.Height), paint);
                DrawImagePixels(canvas, scaled);
                canvas.Restore();
            }
            else { DrawImagePixels(canvas, scaled); }
            ImageAnnotationRenderer.Draw(canvas, scaled, preview);
            _editorPreview = preview;
            return preview;
        }
        catch { preview.Dispose(); throw; }
    }
}
