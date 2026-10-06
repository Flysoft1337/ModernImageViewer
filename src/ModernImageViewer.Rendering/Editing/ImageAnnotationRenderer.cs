using System.Text;

using ModernImageViewer.Imaging;

using SkiaSharp;

namespace ModernImageViewer.Rendering.Editing;

/// <summary>Draws on an output-coordinate canvas. Privacy sampling finishes before writing the destination.</summary>
public static class ImageAnnotationRenderer
{
    public const long LocalCopyByteLimit = 4L * 1024 * 1024;
    private const int LocalCopyMaximumEdge = 1024;

    public static void Draw(SKCanvas canvas, ImageEditRecipe recipe, SKBitmap? composedPixels = null)
    {
        ArgumentNullException.ThrowIfNull(canvas);
        ArgumentNullException.ThrowIfNull(recipe);
        ImageAnnotation.ValidateCollection(recipe.Annotations);
        if (recipe.Annotations.IsEmpty) { return; }
        if (recipe.Annotations.Any(annotation => annotation.Kind is ImageAnnotationKind.Mosaic or ImageAnnotationKind.RegionBlur)
            && (composedPixels is null || composedPixels.Width != recipe.OutputSize.Width
                || composedPixels.Height != recipe.OutputSize.Height || composedPixels.GetPixels() == IntPtr.Zero))
        {
            throw new ArgumentException("Privacy tools require composed output pixels.", nameof(composedPixels));
        }
        using SKPath crop = RectanglePath(recipe.Crop.X, recipe.Crop.Y, recipe.Crop.Right, recipe.Crop.Bottom, recipe);
        canvas.Save();
        try
        {
            canvas.ClipRect(new(0, 0, recipe.OutputSize.Width, recipe.OutputSize.Height));
            canvas.ClipPath(crop, SKClipOperation.Intersect, antialias: false);
            var matrix = recipe.GetMatrix();
            double scale = Math.Sqrt(Math.Abs((matrix.M11 * matrix.M22) - (matrix.M12 * matrix.M21)));
            foreach (ImageAnnotation annotation in recipe.Annotations)
            {
                if (annotation.Kind is ImageAnnotationKind.Mosaic or ImageAnnotationKind.RegionBlur)
                {
                    canvas.Flush();
                    DrawPrivacy(canvas, recipe, annotation, composedPixels!, scale);
                    continue;
                }
                using SKPaint paint = new()
                {
                    Color = new SKColor(annotation.Color),
                    IsAntialias = true,
                    Style = SKPaintStyle.Stroke,
                    StrokeWidth = (float)(annotation.StrokeWidth * scale),
                    StrokeCap = SKStrokeCap.Round,
                    StrokeJoin = SKStrokeJoin.Round,
                };
                if (annotation.Kind == ImageAnnotationKind.Text) { DrawText(canvas, recipe, annotation, paint, scale); }
                else
                {
                    using SKPath path = CreatePath(recipe, annotation);
                    canvas.DrawPath(path, paint);
                }
            }
        }
        finally { canvas.Restore(); }
    }

    private static SKPath CreatePath(ImageEditRecipe recipe, ImageAnnotation annotation)
    {
        using SKPathBuilder builder = new();
        ImageEditPoint first = annotation.Points[0];
        ImageEditPoint last = annotation.Points[^1];
        if (annotation.Kind == ImageAnnotationKind.Ellipse)
        {
            using SKPathBuilder ovalBuilder = new();
            ovalBuilder.AddOval(new((float)Math.Min(first.X, last.X), (float)Math.Min(first.Y, last.Y),
                (float)Math.Max(first.X, last.X), (float)Math.Max(first.Y, last.Y)));
            var m = recipe.GetMatrix();
            SKMatrix transform = new((float)m.M11, (float)m.M21, (float)m.OffsetX,
                (float)m.M12, (float)m.M22, (float)m.OffsetY, 0, 0, 1);
            using SKPath oval = ovalBuilder.Detach();
            builder.AddPath(oval, transform);
        }
        else if (annotation.Kind == ImageAnnotationKind.Rectangle)
        {
            return RectanglePath(first.X, first.Y, last.X, last.Y, recipe);
        }
        else
        {
            builder.MoveTo(Map(recipe, first.X, first.Y));
            foreach (ImageEditPoint point in annotation.Points.AsSpan()[1..]) { builder.LineTo(Map(recipe, point.X, point.Y)); }
            if (annotation.Kind == ImageAnnotationKind.Arrow)
            {
                double dx = last.X - first.X;
                double dy = last.Y - first.Y;
                double length = Math.Sqrt((dx * dx) + (dy * dy));
                if (length > 0)
                {
                    double head = Math.Min(length * .45, annotation.StrokeWidth * 4);
                    double ux = dx / length;
                    double uy = dy / length;
                    builder.MoveTo(Map(recipe, last.X - (head * ux) - (head * .55 * uy), last.Y - (head * uy) + (head * .55 * ux)));
                    builder.LineTo(Map(recipe, last.X, last.Y));
                    builder.LineTo(Map(recipe, last.X - (head * ux) + (head * .55 * uy), last.Y - (head * uy) - (head * .55 * ux)));
                }
            }
        }
        return builder.Detach();
    }

    private static SKPoint Map(ImageEditRecipe recipe, double x, double y)
    {
        var point = recipe.ToOutput(x, y);
        return new((float)point.X, (float)point.Y);
    }

    private static SKPath RectanglePath(double x1, double y1, double x2, double y2, ImageEditRecipe recipe)
    {
        using SKPathBuilder builder = new();
        builder.MoveTo(Map(recipe, x1, y1));
        builder.LineTo(Map(recipe, x2, y1));
        builder.LineTo(Map(recipe, x2, y2));
        builder.LineTo(Map(recipe, x1, y2));
        builder.Close();
        return builder.Detach();
    }

    private static void DrawText(SKCanvas canvas, ImageEditRecipe recipe, ImageAnnotation annotation, SKPaint paint, double scale)
    {
        var m = recipe.GetMatrix();
        SKPoint anchor = Map(recipe, annotation.Points[0].X, annotation.Points[0].Y);
        canvas.Save();
        try
        {
            // Normalize the basis so font size is scaled exactly once, including rotation and flips.
            canvas.Concat(new SKMatrix((float)(m.M11 / scale), (float)(m.M21 / scale), anchor.X,
                (float)(m.M12 / scale), (float)(m.M22 / scale), anchor.Y, 0, 0, 1));
            paint.Style = SKPaintStyle.Fill;
            using SKTypeface typeface = SKTypeface.FromFamilyName("Segoe UI");
            using SKFont font = new(typeface, (float)(annotation.FontSize * scale));
            float x = 0;
            float y = -font.Metrics.Ascent;
            foreach (Rune rune in annotation.Text.EnumerateRunes())
            {
                if (rune.Value == '\r') { continue; }
                if (rune.Value == '\n') { x = 0; y += font.Size * 1.25f; continue; }
                string glyph = rune.Value == '\t' ? "    " : rune.ToString();
                if (font.ContainsGlyph(rune.Value))
                {
                    canvas.DrawText(glyph, x, y, SKTextAlign.Left, font, paint);
                    x += font.MeasureText(glyph, paint);
                }
                else
                {
                    using SKTypeface? fallback = SKFontManager.Default.MatchCharacter("Segoe UI", rune.Value);
                    using SKFont fallbackFont = new(fallback ?? typeface, font.Size);
                    canvas.DrawText(glyph, x, y, SKTextAlign.Left, fallbackFont, paint);
                    x += fallbackFont.MeasureText(glyph, paint);
                }
            }
        }
        finally { canvas.Restore(); }
    }

    private static void DrawPrivacy(SKCanvas canvas, ImageEditRecipe recipe, ImageAnnotation annotation, SKBitmap composed, double scale)
    {
        ImageEditPoint first = annotation.Points[0];
        ImageEditPoint last = annotation.Points[1];
        using SKPath path = RectanglePath(first.X, first.Y, last.X, last.Y, recipe);
        SKRect bounds = path.Bounds;
        bounds = new(Math.Max(0, (float)Math.Floor(bounds.Left)), Math.Max(0, (float)Math.Floor(bounds.Top)),
            Math.Min(composed.Width, (float)Math.Ceiling(bounds.Right)), Math.Min(composed.Height, (float)Math.Ceiling(bounds.Bottom)));
        if (bounds.Width <= 0 || bounds.Height <= 0) { return; }
        bool mosaic = annotation.Kind == ImageAnnotationKind.Mosaic;
        int maximumEdge = mosaic ? LocalCopyMaximumEdge : 768;
        double samplingScale = Math.Min(1, maximumEdge / Math.Max(bounds.Width, bounds.Height));
        if (mosaic) { samplingScale = Math.Min(samplingScale, 1 / Math.Max(2, annotation.Strength * scale)); }
        else { samplingScale = Math.Min(samplingScale, 32 / Math.Max(.5, annotation.Strength * scale)); }
        int width = Math.Clamp((int)Math.Ceiling(bounds.Width * samplingScale), 1, LocalCopyMaximumEdge);
        int height = Math.Clamp((int)Math.Ceiling(bounds.Height * samplingScale), 1, LocalCopyMaximumEdge);
        using SKBitmap sample = new(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul));
        if (sample.GetPixels() == IntPtr.Zero || sample.ByteCount > LocalCopyByteLimit) { throw new InvalidOperationException("Unable to allocate bounded privacy pixels."); }
        using (SKCanvas sampling = new(sample))
        {
            sampling.Clear(SKColors.Transparent);
            sampling.DrawBitmap(composed, bounds, new(0, 0, width, height), new SKSamplingOptions(SKFilterMode.Linear));
            sampling.Flush();
        }
        sample.SetImmutable();
        canvas.Save();
        try
        {
            canvas.ClipPath(path, SKClipOperation.Intersect, antialias: false);
            if (mosaic)
            {
                using SKPaint replace = new() { BlendMode = SKBlendMode.Src };
                canvas.DrawBitmap(sample, bounds, new SKSamplingOptions(SKFilterMode.Nearest), replace);
            }
            else
            {
                // The blur filter runs on the bounded local image, never on the full output surface.
                using SKBitmap blurred = new(sample.Info);
                if (blurred.GetPixels() == IntPtr.Zero || blurred.ByteCount > LocalCopyByteLimit) { throw new InvalidOperationException("Unable to allocate bounded blur pixels."); }
                using SKImageFilter filter = SKImageFilter.CreateBlur(
                    (float)Math.Clamp(annotation.Strength * scale * width / bounds.Width, .5, 32),
                    (float)Math.Clamp(annotation.Strength * scale * height / bounds.Height, .5, 32), SKShaderTileMode.Clamp);
                using (SKCanvas blurCanvas = new(blurred))
                {
                    using SKPaint blurPaint = new() { ImageFilter = filter, BlendMode = SKBlendMode.Src };
                    blurCanvas.Clear(SKColors.Transparent);
                    blurCanvas.DrawBitmap(sample, 0, 0, new SKSamplingOptions(SKFilterMode.Linear), blurPaint);
                    blurCanvas.Flush();
                }
                using SKPaint replace = new() { BlendMode = SKBlendMode.Src };
                canvas.DrawBitmap(blurred, bounds, new SKSamplingOptions(SKFilterMode.Linear), replace);
            }
        }
        finally { canvas.Restore(); }
    }
}
