using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;

using ModernImageViewer.Imaging;

namespace ModernImageViewer.UI;

public partial class EditWindow
{
    private readonly Dictionary<string, Slider> _adjustmentSliders = [];
    private readonly DispatcherTimer _adjustmentTimer = new() { Interval = TimeSpan.FromMilliseconds(80) };
    private string _editorMode = "Geometry";
    private bool _restoringEditorMode;

    private void InitializeAdjustments()
    {
        AddAdjustment(RotationFields, "Rotation", -180, 180, 0, 0.1);
        AddAdjustment(AdjustmentFields, "Exposure", -5, 5, 0, 0.1);
        AddAdjustment(AdjustmentFields, "Brightness", -100, 100, 0, 1);
        AddAdjustment(AdjustmentFields, "Contrast", -100, 100, 0, 1);
        AddAdjustment(AdjustmentFields, "Gamma", 0.1, 5, 1, 0.05);
        AddAdjustment(AdjustmentFields, "Saturation", -100, 100, 0, 1);
        AddAdjustment(AdjustmentFields, "Temperature", -100, 100, 0, 1);
        AddAdjustment(AdjustmentFields, "Sharpen", 0, 100, 0, 1);
        AddAdjustment(AdjustmentFields, "Blur", 0, 20, 0, 0.1);
        Button reset = new() { Content = Text("Edit_ResetAdjustments"), Margin = new(0, 6, 0, 0) };
        reset.SetResourceReference(StyleProperty, "ToolbarButtonStyle");
        reset.Click += (_, _) => { _session.Apply(Recipe.WithAdjustments(new())); RefreshEditor(); };
        AdjustmentFields.Children.Add(reset);
        _adjustmentTimer.Tick += (_, _) =>
        {
            _adjustmentTimer.Stop();
            if (IsExporting || _refreshing || !HasAdjustmentDraft) { return; }
            Preview.SetEditRecipe(ReadAdjustmentRecipe(), fit: false);
        };
    }

    private void AddAdjustment(StackPanel container, string name, double minimum, double maximum, double value, double step)
    {
        Grid header = new() { Margin = new(0, 0, 0, 5) };
        header.ColumnDefinitions.Add(new());
        header.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        TextBlock label = new() { Text = Text("Edit_" + name), FontSize = 12 };
        TextBlock amount = new() { FontSize = 12, Text = value.ToString("0.##", CultureInfo.InvariantCulture) };
        amount.SetResourceReference(ForegroundProperty, "SecondaryTextBrush");
        Grid.SetColumn(amount, 1);
        header.Children.Add(label);
        header.Children.Add(amount);
        Slider slider = new()
        {
            Minimum = minimum,
            Maximum = maximum,
            Value = value,
            SmallChange = step,
            LargeChange = step * 10,
            TickFrequency = step,
            IsSnapToTickEnabled = true,
            Margin = new(0, 0, 0, 16),
            Height = 24,
            ToolTip = label.Text,
        };
        slider.SetResourceReference(StyleProperty, "EditorSlider");
        System.Windows.Automation.AutomationProperties.SetName(slider, label.Text);
        slider.ValueChanged += (_, _) =>
        {
            amount.Text = slider.Value.ToString("0.##", CultureInfo.InvariantCulture);
            if (_refreshing) { return; }
            if (!CommitAnnotationProperties()) { return; }
            if (_selectingCrop) { CropOverlay.Visibility = Visibility.Collapsed; _selectingCrop = false; }
            CancelAnnotationGesture();
            _adjustmentTimer.Stop();
            _adjustmentTimer.Start();
        };
        slider.AddHandler(Thumb.DragCompletedEvent, new DragCompletedEventHandler((_, _) => CommitAdjustments()));
        slider.AddHandler(Mouse.PreviewMouseUpEvent, new MouseButtonEventHandler((_, _) =>
            _ = Dispatcher.BeginInvoke(new Action(CommitAdjustments))));
        slider.KeyUp += (_, e) =>
        {
            if (e.Key is Key.Left or Key.Right or Key.Up or Key.Down or Key.Home or Key.End or Key.PageUp or Key.PageDown)
            { CommitAdjustments(); }
        };
        slider.LostKeyboardFocus += (_, _) => { if (!_refreshing) { CommitAdjustments(); } };
        _adjustmentSliders[name] = slider;
        container.Children.Add(header);
        container.Children.Add(slider);
    }

    private double Value(string name) => _adjustmentSliders[name].Value;
    private ImageEditAdjustments ReadAdjustments() => new(
        Exposure: Value("Exposure"), Brightness: Value("Brightness"), Contrast: Value("Contrast"),
        Gamma: Value("Gamma"), Saturation: Value("Saturation"), Temperature: Value("Temperature"),
        Sharpen: Value("Sharpen"), Blur: Value("Blur"));

    private bool HasAdjustmentDraft => _adjustmentSliders.Count != 0 &&
        (Math.Abs(Value("Rotation") - Recipe.RotationDegrees) > 0.00001 || ReadAdjustments() != Recipe.Adjustments);

    private ImageEditRecipe ReadAdjustmentRecipe()
    {
        ImageEditRecipe recipe = Recipe;
        if (Math.Abs(Value("Rotation") - recipe.RotationDegrees) > 0.00001)
        { recipe = recipe.WithRotation(Value("Rotation")); }
        return recipe.WithAdjustments(ReadAdjustments());
    }

    private void CommitAdjustments()
    {
        _adjustmentTimer.Stop();
        if (_refreshing || IsExporting || !HasAdjustmentDraft) { return; }
        if (!CommitAnnotationProperties()) { return; }
        if (!TryReadCrop(out PixelRect crop)) { return; }
        ImageEditRecipe next = ReadAdjustmentRecipe();
        if (crop != Recipe.Crop) { next = next.WithCrop(crop); }
        if (_sizeDirty)
        {
            if (!TryReadSize(out PixelSize size)) { return; }
            next = next.WithSize(size);
        }
        _session.Apply(next);
        RefreshEditor();
    }

    private void RefreshAdjustments()
    {
        _adjustmentTimer.Stop();
        if (_adjustmentSliders.Count == 0) { return; }
        ImageEditAdjustments a = Recipe.Adjustments;
        foreach ((string name, double value) in new (string, double)[]
        {
            ("Rotation", Recipe.RotationDegrees), ("Exposure", a.Exposure), ("Brightness", a.Brightness),
            ("Contrast", a.Contrast), ("Gamma", a.Gamma), ("Saturation", a.Saturation),
            ("Temperature", a.Temperature), ("Sharpen", a.Sharpen), ("Blur", a.Blur),
        })
        { _adjustmentSliders[name].Value = value; }
    }

    private void OnEditorModeChanged(object sender, RoutedEventArgs e)
    {
        if (_restoringEditorMode || GeometryFields is null || sender is not RadioButton { Tag: string mode }) { return; }
        if (!CommitAnnotationProperties())
        {
            _restoringEditorMode = true;
            try
            {
                Grid sidebar = (Grid)EditorFieldsScroll.Parent;
                var choices = sidebar.Children.OfType<UniformGrid>().Single();
                foreach (RadioButton choice in choices.Children.OfType<RadioButton>())
                { choice.IsChecked = Equals(choice.Tag, _editorMode); }
            }
            finally { _restoringEditorMode = false; }
            return;
        }
        _editorMode = mode;
        CommitAdjustments();
        CancelAnnotationGesture();
        _selectingCrop = false;
        CropOverlay.Visibility = Visibility.Collapsed;
        AnnotationOverlay.Visibility = Visibility.Collapsed;
        SelectionRectangle.Visibility = Visibility.Collapsed;
        GeometryFields.Visibility = mode == "Geometry" ? Visibility.Visible : Visibility.Collapsed;
        AdjustmentFields.Visibility = mode == "Adjust" ? Visibility.Visible : Visibility.Collapsed;
        AnnotationFields.Visibility = mode == "Annotate" ? Visibility.Visible : Visibility.Collapsed;
        ExportFields.Visibility = mode == "Export" ? Visibility.Visible : Visibility.Collapsed;
        EditorFieldsScroll.ScrollToHome();
        Preview.SetEditRecipe(Recipe, fit: false);
    }
}
