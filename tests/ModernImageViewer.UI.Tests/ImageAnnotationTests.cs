using System.Collections.Immutable;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

using ModernImageViewer.Application.Editing;
using ModernImageViewer.Imaging;
using ModernImageViewer.Rendering.Editing;
using ModernImageViewer.UI.Controls;

using SkiaSharp;

namespace ModernImageViewer.UI.Tests;

public sealed class ImageAnnotationTests
{
    [Fact]
    public void ImmutableModelRejectsInvalidGeometryNumbersAndUnboundedHistory()
    {
        ImmutableArray<ImageEditPoint> line = [new(10, 10), new(40, 30)];
        Assert.Throws<ArgumentException>(() => new ImageAnnotation(ImageAnnotationKind.Arrow, default));
        Assert.Throws<ArgumentException>(() => new ImageAnnotation(ImageAnnotationKind.Arrow, [new(1, 2)]));
        Assert.Throws<ArgumentException>(() => new ImageAnnotation(ImageAnnotationKind.Arrow, [new(1, 2), new(1, 2)]));
        Assert.Throws<ArgumentException>(() => new ImageAnnotation(ImageAnnotationKind.Mosaic, [new(1, 2), new(1, 20)]));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ImageAnnotation(ImageAnnotationKind.Pen, [new(double.NaN, 0), new(1, 2)]));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ImageAnnotation(ImageAnnotationKind.Pen, line, StrokeWidth: double.PositiveInfinity));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ImageAnnotation(ImageAnnotationKind.Pen, line, StrokeWidth: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ImageAnnotation(ImageAnnotationKind.Pen, line, FontSize: 32769));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ImageAnnotation(ImageAnnotationKind.Mosaic, line, Strength: 0));
        Assert.Throws<ArgumentException>(() => new ImageAnnotation(ImageAnnotationKind.Text, [new(1, 2)], Text: " "));
        Assert.Throws<ArgumentException>(() => new ImageAnnotation(ImageAnnotationKind.Text, [new(1, 2)], Text: new('x', 513)));
        Assert.Throws<ArgumentException>(() => new ImageAnnotation(ImageAnnotationKind.Pen,
            Enumerable.Range(0, ImageAnnotation.MaximumPoints + 1).Select(i => new ImageEditPoint(i, i)).ToImmutableArray()));
        ImageAnnotation small = new(ImageAnnotationKind.Arrow, line);
        Assert.Throws<ArgumentException>(() => ImageAnnotation.ValidateCollection(
            Enumerable.Repeat(small, ImageAnnotation.MaximumAnnotations + 1).ToImmutableArray()));
        ImageAnnotation pen = new(ImageAnnotationKind.Pen,
            Enumerable.Range(0, ImageAnnotation.MaximumPoints).Select(i => new ImageEditPoint(i, i)).ToImmutableArray());
        Assert.Throws<ArgumentException>(() => ImageAnnotation.ValidateCollection(Enumerable.Repeat(pen, 9).ToImmutableArray()));
        ImageAnnotation text = new(ImageAnnotationKind.Text, [new(1, 2)], Text: new('x', 512));
        Assert.Throws<ArgumentException>(() => ImageAnnotation.ValidateCollection(Enumerable.Repeat(text, 17).ToImmutableArray()));
    }

    [Fact]
    public void AnnotationChangesUndoRedoAndResetWithoutPixelStorage()
    {
        ImageEditSession session = new(new(100, 80));
        ImageAnnotation arrow = new(ImageAnnotationKind.Arrow, [new(10, 10), new(40, 30)]);
        ImageEditRecipe initial = session.Current;
        session.Apply(initial.WithAnnotations([arrow]));
        Assert.Single(session.Current.Annotations);
        Assert.True(session.HasUnexportedChanges);
        session.Undo();
        Assert.Equal(initial, session.Current);
        session.Redo();
        Assert.Same(arrow, Assert.Single(session.Current.Annotations));
        Assert.Single(session.Current.RotateRight().WithCrop(new(0, 0, 50, 40)).WithSize(new(80, 80)).Annotations);
        session.Reset();
        Assert.Empty(session.Current.Annotations);
    }

    [Theory]
    [InlineData(ImageAnnotationKind.Arrow)]
    [InlineData(ImageAnnotationKind.Rectangle)]
    [InlineData(ImageAnnotationKind.Ellipse)]
    [InlineData(ImageAnnotationKind.Pen)]
    public void VectorAnnotationsFollowCropRotationFlipAndScale(ImageAnnotationKind kind)
    {
        ImageEditRecipe recipe = ImageEditRecipe.Create(new(100, 80)).WithCrop(new(10, 10, 60, 40))
            .RotateRight().FlipHorizontal().WithRotation(27).WithSize(new(80, 120));
        ImageAnnotation annotation = new(kind, [new(20, 20), new(50, 40)], StrokeWidth: 3);
        recipe = recipe.WithAnnotations([annotation]);
        using SKBitmap output = new(new SKImageInfo(80, 120, SKColorType.Bgra8888, SKAlphaType.Premul));
        using SKCanvas canvas = new(output);
        canvas.Clear(SKColors.Transparent);
        ImageAnnotationRenderer.Draw(canvas, recipe);
        canvas.Flush();
        Assert.Contains(output.Pixels, color => color.Alpha > 0 && color.Red > 200);
        var point = recipe.ToOutput(20, kind == ImageAnnotationKind.Ellipse ? 30 : 20);
        Assert.True(HasInkNear(output, point.X, point.Y, 4));
        Assert.Equal((byte)0, output.GetPixel(0, 0).Alpha);
    }

    [Theory]
    [InlineData(ImageAnnotationKind.Mosaic)]
    [InlineData(ImageAnnotationKind.RegionBlur)]
    public void PrivacyToolsReplaceOnlyTransformedRegionOnSameComposedBitmap(ImageAnnotationKind kind)
    {
        ImageEditRecipe recipe = ImageEditRecipe.Create(new(80, 64)).WithCrop(new(8, 8, 64, 48))
            .RotateRight().FlipHorizontal().WithRotation(27).WithSize(new(96, 128));
        recipe = recipe.WithAnnotations([new(kind, [new(20, 20), new(50, 40)], Strength: 12)]);
        using SKBitmap output = CreateChecker(96, 128);
        SKColor[] before = output.Pixels;
        using SKCanvas canvas = new(output);
        ImageAnnotationRenderer.Draw(canvas, recipe, output);
        canvas.Flush();
        var center = recipe.ToOutput(35, 30);
        SKColor blurred = output.GetPixel((int)center.X, (int)center.Y);
        Assert.InRange(blurred.Red, (byte)35, (byte)220);
        Assert.Equal((byte)255, blurred.Alpha);
        Assert.Equal(before[0], output.GetPixel(0, 0));
        Assert.Equal(before[^1], output.GetPixel(95, 127));
        Assert.Throws<ArgumentException>(() => ImageAnnotationRenderer.Draw(canvas, recipe));
    }

    [Theory]
    [InlineData(ImageAnnotationKind.Mosaic)]
    [InlineData(ImageAnnotationKind.RegionBlur)]
    public void LargePrivacyRegionDownsamplesAndKeepsSurroundingPixels(ImageAnnotationKind kind)
    {
        ImageEditRecipe recipe = ImageEditRecipe.Create(new(2048, 1536)).WithAnnotations(
            [new(kind, [new(128, 128), new(1920, 1408)], Strength: 24)]);
        using SKBitmap output = new(new SKImageInfo(2048, 1536, SKColorType.Bgra8888, SKAlphaType.Premul));
        using SKCanvas canvas = new(output);
        canvas.Clear(SKColors.White);
        using SKPaint paint = new() { Color = SKColors.Black };
        canvas.DrawRect(new SKRect(900, 128, 1100, 1408), paint);
        ImageAnnotationRenderer.Draw(canvas, recipe, output);
        canvas.Flush();
        Assert.Equal(SKColors.White, output.GetPixel(0, 0));
        Assert.Equal(SKColors.White, output.GetPixel(2047, 1535));
        Assert.Equal((byte)255, output.GetPixel(1000, 700).Alpha);
        Assert.Equal(4L * 1024 * 1024, ImageAnnotationRenderer.LocalCopyByteLimit);
    }

    [Fact]
    public void EnglishAndChineseTextRenderWithFontFallbackAndRotation()
    {
        ImageEditRecipe recipe = ImageEditRecipe.Create(new(256, 128)).RotateRight().WithAnnotations(
            [new(ImageAnnotationKind.Text, [new(10, 10)], Text: "A\n\u4e2d\u6587", FontSize: 28)]);
        using SKBitmap output = new(new SKImageInfo(128, 256, SKColorType.Bgra8888, SKAlphaType.Premul));
        using SKCanvas canvas = new(output);
        canvas.Clear(SKColors.Transparent);
        ImageAnnotationRenderer.Draw(canvas, recipe);
        canvas.Flush();
        Assert.Contains(output.Pixels, color => color.Alpha > 0 && color.Red > 200);
        // The second line occupies a different source Y range; rotation maps it into output X.
        Assert.True(HasInkInSourceArea(output, recipe, 10, 10, 35, 40));
        Assert.True(HasInkInSourceArea(output, recipe, 10, 45, 75, 75));
    }

    [Fact]
    public void SourceScaledWidthAndFontRemainVisibleOnLargeImagePreview()
    {
        double sourcePerDip = 8000d / 300;
        ImageEditRecipe recipe = ImageEditRecipe.Create(new(8000, 4000)).WithSize(new(300, 150)).WithAnnotations(
        [
            new(ImageAnnotationKind.Pen, [new(2000, 2000), new(6000, 2000)], StrokeWidth: 3 * sourcePerDip),
            new(ImageAnnotationKind.Text, [new(2000, 500)], Text: "A", FontSize: 24 * sourcePerDip),
        ]);
        using SKBitmap output = new(new SKImageInfo(300, 150, SKColorType.Bgra8888, SKAlphaType.Premul));
        using SKCanvas canvas = new(output);
        canvas.Clear(SKColors.Transparent);
        ImageAnnotationRenderer.Draw(canvas, recipe);
        canvas.Flush();
        Assert.Equal((byte)255, output.GetPixel(150, 75).Alpha);
        Assert.Equal((byte)0, output.GetPixel(150, 78).Alpha);
        int textRows = Enumerable.Range(18, 35).Count(y => Enumerable.Range(75, 30).Any(x => output.GetPixel(x, y).Alpha > 0));
        Assert.InRange(textRows, 12, 26);
    }

    internal static void VerifyEditorGestures(EditWindow editor)
    {
        ImageViewport preview = (ImageViewport)editor.FindName("Preview");
        StackPanel fields = (StackPanel)editor.FindName("AnnotationFields");
        Canvas overlay = (Canvas)editor.FindName("AnnotationOverlay");
        Canvas crop = (Canvas)editor.FindName("CropOverlay");
        ImageEditSession session = ReadEditorField<ImageEditSession>(editor, "_session");
        List<ImageEditPoint> gesture = ReadEditorField<List<ImageEditPoint>>(editor, "_annotationPoints");
        var modes = ((Grid)((ScrollViewer)editor.FindName("EditorFieldsScroll")).Parent).Children
            .OfType<System.Windows.Controls.Primitives.UniformGrid>().Single().Children.OfType<RadioButton>().ToArray();
        RadioButton originalMode = modes.Single(mode => mode.IsChecked == true);
        ImageAnnotationKind? originalTool = ReadEditorField<ImageAnnotationKind?>(editor, "_annotationTool");
        bool originalPointer = ReadEditorField<bool>(editor, "_annotationSelectMode");
        int originalSelection = ReadEditorField<int>(editor, "_selectedAnnotationIndex");
        InvokeEditor(editor, "CommitAdjustments");
        ImageEditRecipe originalRecipe = editor.Recipe;
        List<ImageEditRecipe> undo = (List<ImageEditRecipe>)typeof(ImageEditSession)
            .GetField("_undo", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(session)!;
        Stack<ImageEditRecipe> redo = (Stack<ImageEditRecipe>)typeof(ImageEditSession)
            .GetField("_redo", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(session)!;
        ImageEditRecipe[] originalUndo = undo.ToArray();
        ImageEditRecipe[] originalRedo = redo.ToArray();
        Slider width = ReadEditorField<Slider>(editor, "_annotationWidth");
        Slider fontSize = ReadEditorField<Slider>(editor, "_annotationFontSize");
        Slider strength = ReadEditorField<Slider>(editor, "_annotationStrength");
        TextBox text = ReadEditorField<TextBox>(editor, "_annotationText");
        var colors = ReadEditorField<Dictionary<uint, RadioButton>>(editor, "_annotationColors");
        var originalValues = (width.Value, fontSize.Value, strength.Value, text.Text,
            ReadEditorField<uint>(editor, "_annotationColor"));
        try
        {
            modes.Single(mode => Equals(mode.Tag, "Annotate")).IsChecked = true;
            editor.Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
            editor.UpdateLayout();
            preview.Fit();
            Assert.True(preview.ActualWidth > 0 && preview.ActualHeight > 0);
            Assert.True(fields.IsVisible);
            InvokeEditor(editor, "SelectAnnotationTool", ImageAnnotationKind.Arrow);
            width.Value = 3;
            fontSize.Value = 24;
            strength.Value = 12;
            Assert.Equal(Visibility.Visible, overlay.Visibility);
            Assert.Equal(Visibility.Collapsed, crop.Visibility);

            PixelRect bounds = originalRecipe.Crop;
            ImageEditPoint first = new(bounds.X + (bounds.Width * .25), bounds.Y + (bounds.Height * .25));
            ImageEditPoint last = new(bounds.X + (bounds.Width * .75), bounds.Y + (bounds.Height * .75));
            ImageEditPoint center = new((first.X + last.X) / 2, (first.Y + last.Y) / 2);
            var matrix = originalRecipe.GetMatrix();
            double fitScale = Math.Min(preview.ActualWidth / originalRecipe.OutputSize.Width,
                preview.ActualHeight / originalRecipe.OutputSize.Height);
            double density = 1 / (fitScale * Math.Sqrt(Math.Abs((matrix.M11 * matrix.M22) - (matrix.M12 * matrix.M21))));
            gesture.AddRange([first, last]);
            Assert.Equal(originalRecipe, session.Current);
            InvokeEditor(editor, "CommitAnnotation");
            ImageEditRecipe committed = editor.Recipe;
            int arrowIndex = originalRecipe.Annotations.Length;
            Assert.Equal(arrowIndex + 1, committed.Annotations.Length);
            ImageAnnotation arrow = committed.Annotations[arrowIndex];
            Assert.Equal(ImageAnnotationKind.Arrow, arrow.Kind);
            Assert.Equal(Math.Clamp(3 * density, ImageAnnotation.MinimumScaledValue, ImageAnnotation.MaximumScaledValue), arrow.StrokeWidth, 6);
            Assert.Equal(Math.Clamp(24 * density, ImageAnnotation.MinimumScaledValue, ImageAnnotation.MaximumScaledValue), arrow.FontSize, 6);
            Assert.Equal(Math.Clamp(12 * density, ImageAnnotation.MinimumScaledValue, ImageAnnotation.MaximumScaledValue), arrow.Strength, 6);
            Assert.Empty(gesture);
            Assert.Equal(originalUndo.Length + 1, undo.Count);
            InvokeEditor(editor, "OnUndoClick", editor, new RoutedEventArgs());
            Assert.Equal(originalRecipe, editor.Recipe);
            Assert.Equal(originalUndo.Length, undo.Count);
            InvokeEditor(editor, "OnRedoClick", editor, new RoutedEventArgs());
            Assert.Equal(committed, editor.Recipe);

            ReadEditorField<RadioButton>(editor, "_annotationSelect").IsChecked = true;
            Assert.Equal(arrowIndex, InvokeEditor(editor, "HitTestAnnotation", center));
            InvokeEditor(editor, "BeginAnnotationMove", center);
            Assert.Equal(arrowIndex, ReadEditorField<int>(editor, "_selectedAnnotationIndex"));
            Assert.Equal(Visibility.Visible, ReadEditorField<System.Windows.Shapes.Polygon>(editor, "_annotationSelection").Visibility);
            ImageEditPoint movedCenter = new(center.X + (bounds.Width * .05), center.Y + (bounds.Height * .05));
            InvokeEditor(editor, "UpdateAnnotationMove", movedCenter);
            Assert.True(IsAnnotationDrawing(editor));
            Assert.Equal(committed, editor.Recipe);
            Assert.NotEqual(committed.Annotations[arrowIndex].Points, preview.EditRecipe!.Annotations[arrowIndex].Points);
            InvokeEditor(editor, "CommitAnnotationMove");
            ImageEditRecipe moved = editor.Recipe;
            Assert.Equal(first.X + (bounds.Width * .05), moved.Annotations[arrowIndex].Points[0].X, 6);
            Assert.Equal(first.Y + (bounds.Height * .05), moved.Annotations[arrowIndex].Points[0].Y, 6);
            Assert.Equal(originalUndo.Length + 2, undo.Count);
            InvokeEditor(editor, "OnUndoClick", editor, new RoutedEventArgs());
            Assert.Equal(committed, editor.Recipe);
            InvokeEditor(editor, "OnRedoClick", editor, new RoutedEventArgs());
            Assert.Equal(moved, editor.Recipe);

            InvokeEditor(editor, "BeginAnnotationMove", movedCenter);
            InvokeEditor(editor, "UpdateAnnotationMove", center);
            Assert.True(IsAnnotationDrawing(editor));
            KeyEventArgs escape = new(Keyboard.PrimaryDevice, PresentationSource.FromVisual(editor)!, Environment.TickCount, Key.Escape)
            { RoutedEvent = Keyboard.PreviewKeyDownEvent };
            InvokeEditor(editor, "OnPreviewKeyDown", editor, escape);
            Assert.True(escape.Handled);
            Assert.False(IsAnnotationDrawing(editor));
            Assert.Equal(moved, editor.Recipe);
            Assert.Equal(moved, preview.EditRecipe);
            Assert.Equal(originalUndo.Length + 2, undo.Count);

            InvokeEditor(editor, "SelectAnnotationTool", ImageAnnotationKind.Text);
            text.Text = "Draft";
            ImageEditPoint textAnchor = new(bounds.X + (bounds.Width * .1), bounds.Y + (bounds.Height * .1));
            gesture.Add(textAnchor);
            InvokeEditor(editor, "CommitAnnotation");
            ReadEditorField<RadioButton>(editor, "_annotationSelect").IsChecked = true;
            int textIndex = editor.Recipe.Annotations.Length - 1;
            Assert.Equal(textIndex, InvokeEditor(editor, "HitTestAnnotation", textAnchor));
            InvokeEditor(editor, "SelectAnnotationIndex", textIndex);
            ImageEditRecipe beforeTextChange = editor.Recipe;
            int historyBeforeTextChange = undo.Count;
            text.Text = "Ch";
            text.Text = "Changed";
            Assert.Equal(beforeTextChange, editor.Recipe);
            Assert.Equal("Changed", preview.EditRecipe!.Annotations[textIndex].Text);
            Assert.True(IsAnnotationDrawing(editor));
            Assert.Equal(historyBeforeTextChange, undo.Count);
            text.RaiseEvent(new KeyboardFocusChangedEventArgs(Keyboard.PrimaryDevice, Environment.TickCount, text, preview)
            { RoutedEvent = Keyboard.LostKeyboardFocusEvent });
            Assert.Equal("Changed", editor.Recipe.Annotations[textIndex].Text);
            Assert.Equal(historyBeforeTextChange + 1, undo.Count);
            InvokeEditor(editor, "OnUndoClick", editor, new RoutedEventArgs());
            Assert.Equal(beforeTextChange, editor.Recipe);
            InvokeEditor(editor, "OnRedoClick", editor, new RoutedEventArgs());
            Assert.Equal("Changed", editor.Recipe.Annotations[textIndex].Text);
            uint newColor = editor.Recipe.Annotations[textIndex].Color == 0xFF429BEE ? 0xFF32B57B : 0xFF429BEE;
            colors[newColor].IsChecked = true;
            Assert.Equal(newColor, editor.Recipe.Annotations[textIndex].Color);
            ImageEditRecipe beforeFontChange = editor.Recipe;
            int historyBeforeFontChange = undo.Count;
            double newFontSize = fontSize.Value < 100 ? fontSize.Value + 6 : fontSize.Value - 6;
            fontSize.Value = (newFontSize + fontSize.Value) / 2;
            fontSize.Value = newFontSize;
            Assert.Equal(beforeFontChange, editor.Recipe);
            Assert.Equal(historyBeforeFontChange, undo.Count);
            Assert.True(IsAnnotationDrawing(editor));
            fontSize.RaiseEvent(new System.Windows.Controls.Primitives.DragCompletedEventArgs(0, 0, false)
            { RoutedEvent = System.Windows.Controls.Primitives.Thumb.DragCompletedEvent });
            Assert.Equal(Math.Clamp(newFontSize * density, ImageAnnotation.MinimumScaledValue, ImageAnnotation.MaximumScaledValue),
                editor.Recipe.Annotations[textIndex].FontSize, 6);
            Assert.Equal(historyBeforeFontChange + 1, undo.Count);
            ImageEditRecipe beforeInvalidText = editor.Recipe;
            int historyBeforeInvalidText = undo.Count;
            text.Text = "";
            Assert.False((bool)InvokeEditor(editor, "CommitAnnotationProperties")!);
            Assert.True(IsAnnotationDrawing(editor));
            Assert.Equal(beforeInvalidText, editor.Recipe);
            InvokeEditor(editor, "CancelAnnotationGesture");
            Assert.Equal("Changed", text.Text);
            Assert.Equal(beforeInvalidText, preview.EditRecipe);
            Assert.Equal(historyBeforeInvalidText, undo.Count);
            fontSize.Value = newFontSize < 100 ? newFontSize + 3 : newFontSize - 3;
            Assert.True(IsAnnotationDrawing(editor));
            KeyEventArgs cancelProperties = new(Keyboard.PrimaryDevice, PresentationSource.FromVisual(editor)!, Environment.TickCount, Key.Escape)
            { RoutedEvent = Keyboard.PreviewKeyDownEvent };
            InvokeEditor(editor, "OnPreviewKeyDown", editor, cancelProperties);
            Assert.True(cancelProperties.Handled);
            Assert.False(IsAnnotationDrawing(editor));
            Assert.Equal(beforeInvalidText, editor.Recipe);
            Assert.Equal(beforeInvalidText, preview.EditRecipe);
            Assert.Equal(historyBeforeInvalidText, undo.Count);
            ImageEditRecipe beforeDelete = editor.Recipe;
            InvokeEditor(editor, "DeleteSelectedAnnotation");
            Assert.Equal(beforeDelete.Annotations.Length - 1, editor.Recipe.Annotations.Length);
            Assert.Equal(moved.Annotations[arrowIndex], editor.Recipe.Annotations[arrowIndex]);
            InvokeEditor(editor, "OnUndoClick", editor, new RoutedEventArgs());
            Assert.Equal(beforeDelete, editor.Recipe);

            int historyBeforeCancellation = undo.Count;
            gesture.AddRange([first, last]);
            InvokeEditor(editor, "OnSelectCropClick", editor, new RoutedEventArgs());
            Assert.Empty(gesture);
            Assert.True(ReadEditorField<bool>(editor, "_selectingCrop"));
            Assert.False(ReadEditorField<bool>(editor, "_annotationSelectMode"));
            Assert.Null(ReadEditorField<ImageAnnotationKind?>(editor, "_annotationTool"));
            Assert.Equal(Visibility.Visible, crop.Visibility);
            Assert.Equal(Visibility.Collapsed, overlay.Visibility);
            InvokeEditor(editor, "SelectAnnotationTool", ImageAnnotationKind.Pen);
            Assert.False(ReadEditorField<bool>(editor, "_selectingCrop"));
            Assert.Equal(Visibility.Collapsed, crop.Visibility);
            Assert.Equal(Visibility.Visible, overlay.Visibility);
            gesture.AddRange([first, last]);
            modes.Single(mode => Equals(mode.Tag, "Geometry")).IsChecked = true;
            InvokeEditor(editor, "RefreshEditor");
            Assert.Empty(gesture);
            Assert.False(fields.IsVisible);
            Assert.Equal(Visibility.Collapsed, overlay.Visibility);
            Assert.Equal(beforeDelete, editor.Recipe);
            Assert.Equal(historyBeforeCancellation, undo.Count);
        }
        finally
        {
            InvokeEditor(editor, "CancelAnnotationGesture");
            InvokeEditor(editor, "SelectAnnotationTool", [null]);
            session.Apply(originalRecipe);
            undo.Clear();
            undo.AddRange(originalUndo);
            redo.Clear();
            foreach (ImageEditRecipe recipe in originalRedo.Reverse()) { redo.Push(recipe); }
            width.Value = originalValues.Item1;
            fontSize.Value = originalValues.Item2;
            strength.Value = originalValues.Item3;
            text.Text = originalValues.Item4;
            if (colors.TryGetValue(originalValues.Item5, out RadioButton? color)) { color.IsChecked = true; }
            InvokeEditor(editor, "RefreshEditor");
            originalMode.IsChecked = true;
            if (originalPointer)
            {
                InvokeEditor(editor, "SelectAnnotationPointer");
                InvokeEditor(editor, "SelectAnnotationIndex", originalSelection);
            }
            else if (originalTool is { } tool) { InvokeEditor(editor, "SelectAnnotationTool", tool); }
            editor.UpdateLayout();
        }
    }

    private static bool IsAnnotationDrawing(EditWindow editor) =>
        (bool)typeof(EditWindow).GetProperty("AnnotationIsDrawing", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(editor)!;

    private static T ReadEditorField<T>(EditWindow editor, string name) =>
        (T)typeof(EditWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(editor)!;

    private static object? InvokeEditor(EditWindow editor, string name, params object?[] arguments) =>
        typeof(EditWindow).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)!.Invoke(editor, arguments);

    [Fact]
    public void RendererPreservesCanvasTransformAndClip()
    {
        ImageEditRecipe recipe = ImageEditRecipe.Create(new(32, 32)).WithAnnotations(
            [new(ImageAnnotationKind.Rectangle, [new(8, 8), new(24, 24)])]);
        using SKBitmap output = new(new SKImageInfo(64, 64, SKColorType.Bgra8888, SKAlphaType.Premul));
        using SKCanvas canvas = new(output);
        canvas.Translate(2, 3);
        canvas.ClipRect(new(4, 4, 30, 30));
        SKMatrix matrix = canvas.TotalMatrix;
        SKRectI clip = canvas.DeviceClipBounds;
        ImageAnnotationRenderer.Draw(canvas, recipe);
        Assert.Equal(matrix, canvas.TotalMatrix);
        Assert.Equal(clip, canvas.DeviceClipBounds);
    }

    private static SKBitmap CreateChecker(int width, int height)
    {
        SKBitmap bitmap = new(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul));
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++) { bitmap.SetPixel(x, y, ((x + y) & 1) == 0 ? SKColors.Black : SKColors.White); }
        }
        return bitmap;
    }

    private static bool HasInkNear(SKBitmap bitmap, double x, double y, int radius)
    {
        for (int row = Math.Max(0, (int)y - radius); row <= Math.Min(bitmap.Height - 1, (int)y + radius); row++)
        {
            for (int column = Math.Max(0, (int)x - radius); column <= Math.Min(bitmap.Width - 1, (int)x + radius); column++)
            {
                if (bitmap.GetPixel(column, row).Alpha > 0) { return true; }
            }
        }
        return false;
    }

    private static bool HasInkInSourceArea(SKBitmap bitmap, ImageEditRecipe recipe, int x1, int y1, int x2, int y2)
    {
        for (int y = y1; y < y2; y++)
        {
            for (int x = x1; x < x2; x++)
            {
                var output = recipe.ToOutput(x, y);
                if (HasInkNear(bitmap, output.X, output.Y, 0)) { return true; }
            }
        }
        return false;
    }
}
