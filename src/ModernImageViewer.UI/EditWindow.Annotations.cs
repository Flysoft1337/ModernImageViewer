using System.Collections.Immutable;
using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

using ModernImageViewer.Imaging;

namespace ModernImageViewer.UI;

public partial class EditWindow
{
    private readonly List<ImageEditPoint> _annotationPoints = [];
    private readonly Dictionary<ImageAnnotationKind, RadioButton> _annotationTools = [];
    private readonly Dictionary<uint, RadioButton> _annotationColors = [];
    private ImageAnnotationKind? _annotationTool;
    private RadioButton? _annotationSelect;
    private bool _annotationSelectMode;
    private int _selectedAnnotationIndex = -1;
    private ImageEditPoint? _annotationDragStart;
    private ImageAnnotation? _annotationDragOriginal;
    private ImageAnnotation? _annotationDragDraft;
    private Polygon? _annotationSelection;
    private bool _annotationInvalidProperties;
    private ImageAnnotation? _annotationPropertiesOriginal;
    private ImageAnnotation? _annotationPropertiesDraft;
    private int _annotationPropertiesIndex = -1;
    private TextBox? _annotationText;
    private Slider? _annotationWidth;
    private Slider? _annotationFontSize;
    private Slider? _annotationStrength;
    private StackPanel? _annotationTextFields;
    private StackPanel? _annotationStrengthFields;
    private StackPanel? _annotationStrokeFields;
    private Button? _annotationDelete;
    private Button? _annotationClear;
    private uint _annotationColor = 0xFFE84C4C;
    private bool _annotationRefreshing;
    private long _annotationLastPreview;
    private int _annotationGesturePointLimit;

    internal bool HasAnnotationPropertiesDraft => _annotationPropertiesOriginal is not null;
    internal bool AnnotationIsDrawing => _annotationPoints.Count > 0 || _annotationDragStart is not null || HasAnnotationPropertiesDraft;

    private ImageAnnotation? SelectedAnnotation => _selectedAnnotationIndex >= 0 && _selectedAnnotationIndex < Recipe.Annotations.Length
        ? Recipe.Annotations[_selectedAnnotationIndex] : null;

    private string AnnotationLabel(string key, string english, string chinese)
    {
        string localized = Text(key);
        return localized != key ? localized : _localization.CurrentCulture.TwoLetterISOLanguageName == "zh" ? chinese : english;
    }

    private void InitializeAnnotations()
    {
        AnnotationFields.Children.Clear();
        TextBlock heading = new()
        {
            Text = AnnotationLabel("Edit_Annotations", "Annotations", "\u6807\u6ce8"),
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            Margin = new(0, 0, 0, 10),
        };
        AnnotationFields.Children.Add(heading);
        WrapPanel tools = new() { Margin = new(0, 0, 0, 8) };
        _annotationSelect = new()
        {
            GroupName = "AnnotationTool",
            Style = (Style)FindResource("EditorChoice"),
            Padding = new(6),
            Width = 36,
            Height = 36,
            Margin = new(0, 0, 5, 5),
            Content = new TextBlock { Text = "\uE8B0", FontFamily = new("Segoe MDL2 Assets"), FontSize = 17 },
            ToolTip = AnnotationLabel("Edit_AnnotationSelect", "Select annotation", "\u9009\u62e9\u6807\u6ce8"),
        };
        AutomationProperties.SetName(_annotationSelect, (string)_annotationSelect.ToolTip);
        _annotationSelect.Checked += (_, _) => { if (!_annotationRefreshing) { SelectAnnotationPointer(); } };
        tools.Children.Add(_annotationSelect);
        AddAnnotationTool(tools, ImageAnnotationKind.Arrow, "\uE72A", "Edit_AnnotationArrow", "Arrow", "\u7bad\u5934");
        AddAnnotationTool(tools, ImageAnnotationKind.Rectangle, "\uE91B", "Edit_AnnotationRectangle", "Rectangle", "\u77e9\u5f62");
        AddAnnotationTool(tools, ImageAnnotationKind.Ellipse, "\uE91F", "Edit_AnnotationEllipse", "Ellipse", "\u692d\u5706");
        AddAnnotationTool(tools, ImageAnnotationKind.Pen, "\uE70F", "Edit_AnnotationPen", "Pen", "\u753b\u7b14");
        AddAnnotationTool(tools, ImageAnnotationKind.Text, "\uE8D2", "Edit_AnnotationText", "Text", "\u6587\u5b57");
        AddAnnotationTool(tools, ImageAnnotationKind.Mosaic, "\uE80A", "Edit_AnnotationMosaic", "Mosaic", "\u9a6c\u8d5b\u514b");
        AddAnnotationTool(tools, ImageAnnotationKind.RegionBlur, "\uE7B3", "Edit_AnnotationBlur", "Region blur", "\u533a\u57df\u6a21\u7cca");
        AnnotationFields.Children.Add(tools);

        WrapPanel colors = new() { Margin = new(0, 0, 0, 8) };
        (uint Color, string Key, string English, string Chinese)[] swatches =
        [
            (0xFFE84C4C, "Edit_ColorRed", "Red", "\u7ea2\u8272"),
            (0xFFFFC83D, "Edit_ColorYellow", "Yellow", "\u9ec4\u8272"),
            (0xFF32B57B, "Edit_ColorGreen", "Green", "\u7eff\u8272"),
            (0xFF429BEE, "Edit_ColorBlue", "Blue", "\u84dd\u8272"),
            (0xFFFFFFFF, "Edit_ColorWhite", "White", "\u767d\u8272"),
            (0xFF17191C, "Edit_ColorBlack", "Black", "\u9ed1\u8272"),
        ];
        foreach (var swatch in swatches)
        {
            RadioButton color = new()
            {
                GroupName = "AnnotationColor",
                Style = (Style)FindResource("EditorChoice"),
                Padding = new(5),
                Margin = new(0, 0, 4, 4),
                Width = 32,
                Height = 32,
                ToolTip = AnnotationLabel(swatch.Key, swatch.English, swatch.Chinese),
                Content = new Ellipse { Width = 16, Height = 16, Fill = AnnotationBrush(swatch.Color), Stroke = Brushes.Gray, StrokeThickness = .5 },
                IsChecked = swatch.Color == _annotationColor,
            };
            AutomationProperties.SetName(color, (string)color.ToolTip);
            color.Checked += (_, _) =>
            {
                _annotationColor = swatch.Color;
                if (!_annotationRefreshing) { ChangeSelectedAnnotation("Color"); CommitAnnotationProperties(); }
            };
            _annotationColors[swatch.Color] = color;
            colors.Children.Add(color);
        }
        AnnotationFields.Children.Add(colors);
        _annotationStrokeFields = new();
        _annotationWidth = AddAnnotationSlider(_annotationStrokeFields, "Edit_AnnotationWidth", "Stroke width", "\u7ebf\u5bbd", 1, 128, 3);
        AnnotationFields.Children.Add(_annotationStrokeFields);
        _annotationTextFields = new() { Visibility = Visibility.Collapsed };
        _annotationText = new()
        {
            Style = (Style)FindResource("EditorInput"),
            MaxLength = ImageAnnotation.MaximumTextLength,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 58,
            MaxHeight = 140,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Margin = new(0, 0, 0, 8),
        };
        AutomationProperties.SetName(_annotationText, AnnotationLabel("Edit_AnnotationText", "Text", "\u6587\u5b57"));
        _annotationTextFields.Children.Add(_annotationText);
        _annotationFontSize = AddAnnotationSlider(_annotationTextFields, "Edit_AnnotationFontSize", "Font size", "\u5b57\u53f7", 6, 128, 24);
        AnnotationFields.Children.Add(_annotationTextFields);
        _annotationStrengthFields = new() { Visibility = Visibility.Collapsed };
        _annotationStrength = AddAnnotationSlider(_annotationStrengthFields, "Edit_AnnotationStrength", "Strength", "\u5f3a\u5ea6", 2, 128, 12);
        AnnotationFields.Children.Add(_annotationStrengthFields);
        BindAnnotationPropertySlider(_annotationWidth, "Width");
        BindAnnotationPropertySlider(_annotationFontSize, "FontSize");
        BindAnnotationPropertySlider(_annotationStrength, "Strength");
        _annotationText.TextChanged += (_, _) => ChangeSelectedAnnotation("Text");
        _annotationText.LostKeyboardFocus += (_, _) => { if (!_annotationRefreshing) { CommitAnnotationProperties(); } };
        _annotationText.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control)
            {
                CommitAnnotationProperties();
                e.Handled = true;
            }
        };

        WrapPanel actions = new() { Margin = new(0, 4, 0, 0) };
        Button cancel = AddAnnotationAction(actions, "\uE711", "Edit_AnnotationStop", "Stop drawing", "\u7ed3\u675f\u6807\u6ce8");
        cancel.Click += (_, _) => SelectAnnotationTool(null);
        _annotationDelete = AddAnnotationAction(actions, "\uE74D", "Edit_AnnotationDeleteLast", "Delete last annotation", "\u5220\u9664\u6700\u540e\u4e00\u4e2a\u6807\u6ce8");
        _annotationDelete.Click += (_, _) => DeleteSelectedAnnotation();
        _annotationClear = AddAnnotationAction(actions, "\uE894", "Edit_AnnotationClear", "Clear annotations", "\u6e05\u7a7a\u6807\u6ce8");
        _annotationClear.Click += (_, _) =>
        {
            CancelAnnotationGesture();
            _selectedAnnotationIndex = -1;
            _session.Apply(Recipe.WithAnnotations(ImmutableArray<ImageAnnotation>.Empty));
            RefreshEditor();
        };
        AnnotationFields.Children.Add(actions);
        AnnotationOverlay.Background = Brushes.Transparent;
        AnnotationOverlay.Cursor = Cursors.Cross;
        AnnotationOverlay.Visibility = Visibility.Collapsed;
        AnnotationOverlay.Focusable = true;
        _annotationSelection = new() { StrokeThickness = 1.5, StrokeDashArray = new([3d, 2d]), IsHitTestVisible = false, Visibility = Visibility.Collapsed };
        _annotationSelection.SetResourceReference(Shape.StrokeProperty, "AccentBrush");
        AnnotationOverlay.Children.Add(_annotationSelection);
        Preview.ScaleChanged += (_, _) =>
        {
            if (!HasAnnotationPropertiesDraft && _annotationDragStart is null && _annotationPoints.Count == 0 && SelectedAnnotation is not null)
            {
                bool refreshing = _annotationRefreshing;
                _annotationRefreshing = true;
                try { LoadSelectedAnnotationFields(); }
                finally { _annotationRefreshing = refreshing; }
            }
            RefreshAnnotationHighlight();
        };
        AnnotationOverlay.SizeChanged += (_, _) => RefreshAnnotationHighlight();
        PreviewKeyDown += (_, e) =>
        {
            if (!IsExporting && _annotationSelectMode && SelectedAnnotation is not null && e.Key == Key.Delete
                && Keyboard.FocusedElement is not TextBoxBase)
            {
                DeleteSelectedAnnotation();
                e.Handled = true;
            }
        };
        AnnotationOverlay.MouseLeftButtonDown += OnAnnotationDown;
        AnnotationOverlay.MouseMove += OnAnnotationMove;
        AnnotationOverlay.MouseLeftButtonUp += OnAnnotationUp;
        AnnotationOverlay.LostMouseCapture += (_, _) => CancelAnnotationGesture();
        AnnotationOverlay.IsEnabledChanged += (_, _) => { if (!AnnotationOverlay.IsEnabled) { CancelAnnotationGesture(); } };
        AnnotationOverlay.IsVisibleChanged += (_, _) =>
        {
            if (!AnnotationOverlay.IsVisible && (_annotationTool is not null || _annotationSelectMode))
            {
                _annotationTool = null;
                _annotationSelectMode = false;
                _selectedAnnotationIndex = -1;
                RefreshAnnotations();
            }
        };
        AnnotationFields.IsVisibleChanged += (_, _) =>
        {
            if (!AnnotationFields.IsVisible) { SelectAnnotationTool(null); }
        };
        Deactivated += (_, _) => CancelAnnotationGesture();
        RefreshAnnotations();
    }

    private static SolidColorBrush AnnotationBrush(uint color)
    {
        SolidColorBrush brush = new(Color.FromArgb((byte)(color >> 24), (byte)(color >> 16), (byte)(color >> 8), (byte)color));
        brush.Freeze();
        return brush;
    }

    private void AddAnnotationTool(WrapPanel tools, ImageAnnotationKind kind, string glyph, string key, string english, string chinese)
    {
        FrameworkElement icon;
        if (kind is ImageAnnotationKind.Rectangle or ImageAnnotationKind.Ellipse)
        {
            Shape shape = kind == ImageAnnotationKind.Rectangle ? new Rectangle() : new Ellipse();
            shape.Width = 17;
            shape.Height = 13;
            shape.StrokeThickness = 1.5;
            shape.SetResourceReference(Shape.StrokeProperty, "PrimaryTextBrush");
            icon = shape;
        }
        else { icon = new TextBlock { Text = glyph, FontFamily = new("Segoe MDL2 Assets"), FontSize = 17 }; }
        RadioButton button = new()
        {
            GroupName = "AnnotationTool",
            Style = (Style)FindResource("EditorChoice"),
            Padding = new(6),
            Width = 36,
            Height = 36,
            Margin = new(0, 0, 5, 5),
            ToolTip = AnnotationLabel(key, english, chinese),
            Content = icon,
        };
        AutomationProperties.SetName(button, (string)button.ToolTip);
        button.Checked += (_, _) => { if (!_annotationRefreshing) { SelectAnnotationTool(kind); } };
        _annotationTools[kind] = button;
        tools.Children.Add(button);
    }

    private Slider AddAnnotationSlider(StackPanel parent, string key, string english, string chinese, double minimum, double maximum, double value)
    {
        DockPanel row = new() { Margin = new(0, 0, 0, 5) };
        TextBlock number = new() { Text = value.ToString(CultureInfo.InvariantCulture), MinWidth = 32, TextAlignment = TextAlignment.Right };
        DockPanel.SetDock(number, Dock.Right);
        row.Children.Add(number);
        row.Children.Add(new TextBlock { Text = AnnotationLabel(key, english, chinese), FontSize = 12 });
        parent.Children.Add(row);
        Slider slider = new() { Minimum = minimum, Maximum = maximum, Value = value, TickFrequency = 1, IsSnapToTickEnabled = true, Margin = new(0, 0, 0, 10) };
        slider.SetResourceReference(FrameworkElement.StyleProperty, "EditorSlider");
        slider.SetResourceReference(Control.ForegroundProperty, "AccentBrush");
        AutomationProperties.SetName(slider, AnnotationLabel(key, english, chinese));
        slider.ValueChanged += (_, _) => number.Text = slider.Value.ToString("0", CultureInfo.InvariantCulture);
        parent.Children.Add(slider);
        return slider;
    }

    private Button AddAnnotationAction(WrapPanel parent, string glyph, string key, string english, string chinese)
    {
        Button button = new()
        {
            Style = (Style)FindResource("ToolbarButtonStyle"),
            Width = 36,
            Height = 36,
            Padding = new(6),
            Margin = new(0, 0, 5, 0),
            ToolTip = AnnotationLabel(key, english, chinese),
            Content = new TextBlock { Text = glyph, FontFamily = new("Segoe MDL2 Assets"), FontSize = 16 },
        };
        AutomationProperties.SetName(button, (string)button.ToolTip);
        parent.Children.Add(button);
        return button;
    }

    private void SelectAnnotationTool(ImageAnnotationKind? kind)
    {
        if (!CommitAnnotationProperties())
        {
            _annotationRefreshing = true;
            try
            {
                foreach (var (tool, button) in _annotationTools) { button.IsChecked = tool == _annotationTool; }
                _annotationSelect!.IsChecked = _annotationSelectMode;
            }
            finally { _annotationRefreshing = false; }
            return;
        }
        CancelAnnotationGesture();
        _annotationTool = kind;
        _annotationSelectMode = false;
        _selectedAnnotationIndex = -1;
        if (kind is not null)
        {
            _selectingCrop = false;
            _cropStart = null;
            CropOverlay.ReleaseMouseCapture();
            CropOverlay.Visibility = Visibility.Collapsed;
            SelectionRectangle.Visibility = Visibility.Collapsed;
            Preview.SetEditRecipe(Recipe, fit: false);
        }
        RefreshAnnotations();
    }

    private void SelectAnnotationPointer()
    {
        if (!CommitAnnotationProperties()) { return; }
        SelectAnnotationTool(null);
        _annotationSelectMode = true;
        _selectingCrop = false;
        _cropStart = null;
        CropOverlay.ReleaseMouseCapture();
        CropOverlay.Visibility = Visibility.Collapsed;
        SelectionRectangle.Visibility = Visibility.Collapsed;
        Preview.SetEditRecipe(Recipe, fit: false);
        RefreshAnnotations();
    }

    // Call when crop selection becomes active, before replacing the viewport recipe.
    internal void OnEditingModeChanged(bool selectingCrop)
    {
        if (selectingCrop) { SelectAnnotationTool(null); }
    }

    internal void RefreshAnnotations()
    {
        if (_annotationDelete is null) { return; }
        CancelAnnotationGesture();
        _annotationRefreshing = true;
        try
        {
            foreach (var (kind, button) in _annotationTools) { button.IsChecked = kind == _annotationTool; }
            _annotationSelect!.IsChecked = _annotationSelectMode;
            AnnotationOverlay.Cursor = _annotationSelectMode ? Cursors.Arrow : Cursors.Cross;
            AnnotationOverlay.Visibility = (!_annotationSelectMode && _annotationTool is null) || _selectingCrop || !AnnotationFields.IsVisible
                ? Visibility.Collapsed : Visibility.Visible;
            _annotationDelete.IsEnabled = !Recipe.Annotations.IsEmpty;
            _annotationClear!.IsEnabled = !Recipe.Annotations.IsEmpty;
            ImageAnnotationKind? activeKind = SelectedAnnotation?.Kind ?? _annotationTool;
            _annotationTextFields!.Visibility = activeKind == ImageAnnotationKind.Text ? Visibility.Visible : Visibility.Collapsed;
            bool privacy = activeKind is ImageAnnotationKind.Mosaic or ImageAnnotationKind.RegionBlur;
            _annotationStrengthFields!.Visibility = privacy ? Visibility.Visible : Visibility.Collapsed;
            _annotationStrokeFields!.Visibility = privacy || activeKind == ImageAnnotationKind.Text ? Visibility.Collapsed : Visibility.Visible;
            _annotationDelete.ToolTip = SelectedAnnotation is null
                ? AnnotationLabel("Edit_AnnotationDeleteLast", "Delete last annotation", "\u5220\u9664\u6700\u540e\u4e00\u4e2a\u6807\u6ce8")
                : AnnotationLabel("Edit_AnnotationDeleteSelected", "Delete selected annotation", "\u5220\u9664\u9009\u4e2d\u6807\u6ce8");
            AutomationProperties.SetName(_annotationDelete, (string)_annotationDelete.ToolTip);
            LoadSelectedAnnotationFields();
            RefreshAnnotationHighlight();
        }
        finally { _annotationRefreshing = false; }
    }

    internal void CancelAnnotationGesture()
    {
        bool hadDraft = AnnotationIsDrawing;
        bool hadProperties = HasAnnotationPropertiesDraft;
        _annotationPoints.Clear();
        _annotationDragStart = null;
        _annotationDragOriginal = null;
        _annotationDragDraft = null;
        _annotationInvalidProperties = false;
        _annotationPropertiesOriginal = null;
        _annotationPropertiesDraft = null;
        _annotationPropertiesIndex = -1;
        if (AnnotationOverlay.IsMouseCaptured) { AnnotationOverlay.ReleaseMouseCapture(); }
        if (hadDraft) { Preview.SetEditRecipe(Recipe, fit: false); }
        if (hadProperties)
        {
            bool refreshing = _annotationRefreshing;
            _annotationRefreshing = true;
            try { LoadSelectedAnnotationFields(); }
            finally { _annotationRefreshing = refreshing; }
        }
        RefreshAnnotationHighlight();
    }

    private void SelectAnnotationIndex(int index)
    {
        if (!CommitAnnotationProperties()) { return; }
        CancelAnnotationGesture();
        _selectedAnnotationIndex = index >= 0 && index < Recipe.Annotations.Length ? index : -1;
        RefreshAnnotations();
    }

    private void LoadSelectedAnnotationFields()
    {
        ImageAnnotation? annotation = _annotationPropertiesDraft ?? SelectedAnnotation;
        if (annotation is null) { return; }
        double density = AnnotationSourcePixelsPerDip();
        _annotationColor = annotation.Color;
        foreach (var (color, button) in _annotationColors) { button.IsChecked = color == annotation.Color; }
        _annotationWidth!.Value = Math.Clamp(annotation.StrokeWidth / density, _annotationWidth.Minimum, _annotationWidth.Maximum);
        _annotationFontSize!.Value = Math.Clamp(annotation.FontSize / density, _annotationFontSize.Minimum, _annotationFontSize.Maximum);
        _annotationStrength!.Value = Math.Clamp(annotation.Strength / density, _annotationStrength.Minimum, _annotationStrength.Maximum);
        _annotationText!.Text = annotation.Text;
    }

    private void ChangeSelectedAnnotation(string property)
    {
        if (_annotationRefreshing || IsExporting || !_annotationSelectMode || SelectedAnnotation is not { } original) { return; }
        uint color = _annotationColor;
        double width = _annotationWidth!.Value;
        double fontSize = _annotationFontSize!.Value;
        double strength = _annotationStrength!.Value;
        string text = _annotationText!.Text;
        if (_annotationDragStart is not null) { CancelAnnotationGesture(); }
        if (_annotationPropertiesOriginal is null)
        {
            _annotationPropertiesOriginal = original;
            _annotationPropertiesIndex = _selectedAnnotationIndex;
        }
        ImageAnnotation previous = _annotationPropertiesDraft ?? original;
        try
        {
            double density = AnnotationSourcePixelsPerDip();
            ImageAnnotation changed = new(previous.Kind, previous.Points,
                property == "Color" ? color : previous.Color,
                property == "Width" ? ScaleAnnotationValue(width, density) : previous.StrokeWidth,
                property == "Text" ? text : previous.Text,
                property == "FontSize" ? ScaleAnnotationValue(fontSize, density) : previous.FontSize,
                property == "Strength" ? ScaleAnnotationValue(strength, density) : previous.Strength);
            ImageEditRecipe draft = Recipe.WithAnnotations(Recipe.Annotations.SetItem(_selectedAnnotationIndex, changed));
            _annotationPropertiesDraft = changed;
            if (property == "Text" || !_annotationInvalidProperties) { _annotationInvalidProperties = false; }
            Preview.SetEditRecipe(draft, fit: false);
            RefreshAnnotationHighlight();
        }
        catch (ArgumentException)
        {
            _annotationInvalidProperties = true;
            StatusText.Text = AnnotationLabel("Edit_AnnotationInvalid", "Check the text or selection.", "\u8bf7\u68c0\u67e5\u6587\u5b57\u6216\u9009\u533a\u3002");
        }
    }

    private void BindAnnotationPropertySlider(Slider slider, string property)
    {
        slider.ValueChanged += (_, _) => ChangeSelectedAnnotation(property);
        slider.AddHandler(Thumb.DragCompletedEvent, new DragCompletedEventHandler((_, _) => CommitAnnotationProperties()));
        slider.AddHandler(Mouse.PreviewMouseUpEvent, new MouseButtonEventHandler((_, _) => CommitAnnotationProperties()));
        slider.KeyUp += (_, e) =>
        {
            if (e.Key is Key.Left or Key.Right or Key.Up or Key.Down or Key.Home or Key.End or Key.PageUp or Key.PageDown)
            {
                CommitAnnotationProperties();
            }
        };
        slider.LostKeyboardFocus += (_, _) => { if (!_annotationRefreshing) { CommitAnnotationProperties(); } };
    }

    internal bool CommitAnnotationProperties()
    {
        if (!HasAnnotationPropertiesDraft) { return true; }
        if (_annotationInvalidProperties) { return false; }
        int index = _annotationPropertiesIndex;
        ImageAnnotation? changed = _annotationPropertiesDraft;
        if (index < 0 || index >= Recipe.Annotations.Length || Recipe.Annotations[index] != _annotationPropertiesOriginal)
        {
            CancelAnnotationGesture();
            return false;
        }
        _annotationPropertiesOriginal = null;
        _annotationPropertiesDraft = null;
        _annotationPropertiesIndex = -1;
        if (changed is not null && changed != Recipe.Annotations[index])
        {
            _session.Apply(Recipe.WithAnnotations(Recipe.Annotations.SetItem(index, changed)));
        }
        Preview.SetEditRecipe(Recipe, fit: false);
        UndoButton.IsEnabled = _session.CanUndo;
        RedoButton.IsEnabled = _session.CanRedo;
        RefreshAnnotations();
        return true;
    }

    private void DeleteSelectedAnnotation()
    {
        if (IsExporting) { return; }
        CancelAnnotationGesture();
        if (Recipe.Annotations.IsEmpty) { return; }
        int index = SelectedAnnotation is null ? Recipe.Annotations.Length - 1 : _selectedAnnotationIndex;
        _selectedAnnotationIndex = -1;
        _session.Apply(Recipe.WithAnnotations(Recipe.Annotations.RemoveAt(index)));
        RefreshEditor();
    }

    private static Rect AnnotationSourceBounds(ImageAnnotation annotation)
    {
        if (annotation.Kind == ImageAnnotationKind.Text)
        {
            string[] lines = annotation.Text.Split('\n');
            return new(annotation.Points[0].X, annotation.Points[0].Y,
                Math.Max(1, lines.Max(line => line.Length)) * annotation.FontSize,
                lines.Length * annotation.FontSize * 1.25);
        }
        double left = annotation.Points.Min(point => point.X);
        double top = annotation.Points.Min(point => point.Y);
        double right = annotation.Points.Max(point => point.X);
        double bottom = annotation.Points.Max(point => point.Y);
        Rect bounds = new(left, top, right - left, bottom - top);
        if (annotation.Kind is not ImageAnnotationKind.Mosaic and not ImageAnnotationKind.RegionBlur)
        {
            bounds.Inflate(annotation.StrokeWidth / 2, annotation.StrokeWidth / 2);
        }
        return bounds;
    }

    private void RefreshAnnotationHighlight()
    {
        if (_annotationSelection is null) { return; }
        ImageAnnotation? annotation = _annotationDragDraft ?? _annotationPropertiesDraft ?? SelectedAnnotation;
        if (!_annotationSelectMode || !AnnotationFields.IsVisible || annotation is null)
        {
            _annotationSelection.Visibility = Visibility.Collapsed;
            return;
        }
        Rect bounds = AnnotationSourceBounds(annotation);
        bounds.Intersect(new Rect(Recipe.Crop.X, Recipe.Crop.Y, Recipe.Crop.Width, Recipe.Crop.Height));
        if (bounds.IsEmpty) { _annotationSelection.Visibility = Visibility.Collapsed; return; }
        _annotationSelection.Points = new([
            Preview.ToCanvasPoint(bounds.Left, bounds.Top), Preview.ToCanvasPoint(bounds.Right, bounds.Top),
            Preview.ToCanvasPoint(bounds.Right, bounds.Bottom), Preview.ToCanvasPoint(bounds.Left, bounds.Bottom),
        ]);
        _annotationSelection.Visibility = Visibility.Visible;
    }

    private int HitTestAnnotation(ImageEditPoint point)
    {
        double tolerance = 8 * AnnotationSourcePixelsPerDip();
        for (int index = Recipe.Annotations.Length - 1; index >= 0; index--)
        {
            ImageAnnotation annotation = Recipe.Annotations[index];
            if (annotation.Kind is ImageAnnotationKind.Arrow or ImageAnnotationKind.Pen)
            {
                for (int segment = 1; segment < annotation.Points.Length; segment++)
                {
                    ImageEditPoint a = annotation.Points[segment - 1];
                    ImageEditPoint b = annotation.Points[segment];
                    double dx = b.X - a.X;
                    double dy = b.Y - a.Y;
                    double lengthSquared = (dx * dx) + (dy * dy);
                    double position = lengthSquared == 0 ? 0
                        : Math.Clamp((((point.X - a.X) * dx) + ((point.Y - a.Y) * dy)) / lengthSquared, 0, 1);
                    double distanceX = point.X - a.X - (position * dx);
                    double distanceY = point.Y - a.Y - (position * dy);
                    double radius = tolerance + (annotation.StrokeWidth / 2);
                    if ((distanceX * distanceX) + (distanceY * distanceY) <= radius * radius) { return index; }
                }
            }
            else
            {
                Rect bounds = AnnotationSourceBounds(annotation);
                bounds.Inflate(tolerance, tolerance);
                if (bounds.Contains(point.X, point.Y)) { return index; }
            }
        }
        return -1;
    }

    private void BeginAnnotationMove(ImageEditPoint point)
    {
        if (!CommitAnnotationProperties()) { return; }
        SelectAnnotationIndex(HitTestAnnotation(point));
        if (SelectedAnnotation is not { } annotation) { return; }
        _annotationDragStart = point;
        _annotationDragOriginal = annotation;
        _annotationDragDraft = annotation;
        AnnotationOverlay.Focus();
        AnnotationOverlay.CaptureMouse();
    }

    private void UpdateAnnotationMove(ImageEditPoint point)
    {
        if (_annotationDragStart is not { } start || _annotationDragOriginal is not { } original) { return; }
        double left = original.Points.Min(value => value.X);
        double top = original.Points.Min(value => value.Y);
        double right = original.Points.Max(value => value.X);
        double bottom = original.Points.Max(value => value.Y);
        double dx = Math.Clamp(point.X - start.X, Math.Min(0, Recipe.Crop.X - left), Math.Max(0, Recipe.Crop.Right - right));
        double dy = Math.Clamp(point.Y - start.Y, Math.Min(0, Recipe.Crop.Y - top), Math.Max(0, Recipe.Crop.Bottom - bottom));
        _annotationDragDraft = dx == 0 && dy == 0 ? original : new(original.Kind,
            original.Points.Select(value => new ImageEditPoint(value.X + dx, value.Y + dy)).ToImmutableArray(),
            original.Color, original.StrokeWidth, original.Text, original.FontSize, original.Strength);
        Preview.SetEditRecipe(Recipe.WithAnnotations(Recipe.Annotations.SetItem(_selectedAnnotationIndex, _annotationDragDraft)), fit: false);
        RefreshAnnotationHighlight();
    }

    private void CommitAnnotationMove()
    {
        ImageAnnotation? draft = _annotationDragDraft;
        int index = _selectedAnnotationIndex;
        CancelAnnotationGesture();
        if (draft is null || index < 0 || index >= Recipe.Annotations.Length || draft == Recipe.Annotations[index]) { return; }
        _session.Apply(Recipe.WithAnnotations(Recipe.Annotations.SetItem(index, draft)));
        RefreshEditor();
    }

    private bool TryAnnotationPoint(Point pointer, bool clamp, out ImageEditPoint source)
    {
        var point = Preview.ToSourcePoint(pointer);
        PixelRect crop = Recipe.Crop;
        source = new(Math.Clamp(point.X, crop.X, crop.Right), Math.Clamp(point.Y, crop.Y, crop.Bottom));
        return double.IsFinite(point.X) && double.IsFinite(point.Y) && (clamp
            || (point.X >= crop.X && point.X <= crop.Right && point.Y >= crop.Y && point.Y <= crop.Bottom));
    }

    private void OnAnnotationDown(object sender, MouseButtonEventArgs e)
    {
        if (IsExporting || (!_annotationSelectMode && _annotationTool is null)) { return; }
        if (!TryAnnotationPoint(e.GetPosition(Preview), false, out ImageEditPoint source))
        {
            if (_annotationSelectMode) { SelectAnnotationIndex(-1); }
            return;
        }
        if (_annotationSelectMode)
        {
            BeginAnnotationMove(source);
            e.Handled = true;
            return;
        }
        CancelAnnotationGesture();
        if (Recipe.Annotations.Length >= ImageAnnotation.MaximumAnnotations)
        {
            AnnotationLimitStatus();
            e.Handled = true;
            return;
        }
        _annotationGesturePointLimit = Math.Min(ImageAnnotation.MaximumPoints,
            ImageAnnotation.MaximumTotalPoints - Recipe.Annotations.Sum(annotation => annotation.Points.Length));
        if (_annotationGesturePointLimit < (_annotationTool == ImageAnnotationKind.Text ? 1 : 2))
        {
            AnnotationLimitStatus();
            e.Handled = true;
            return;
        }
        _annotationPoints.Add(source);
        if (_annotationTool == ImageAnnotationKind.Text)
        {
            CommitAnnotation();
        }
        else { AnnotationOverlay.CaptureMouse(); }
        e.Handled = true;
    }

    private void OnAnnotationMove(object sender, MouseEventArgs e)
    {
        if (!AnnotationIsDrawing || e.LeftButton != MouseButtonState.Pressed) { return; }
        if (!TryAnnotationPoint(e.GetPosition(Preview), true, out ImageEditPoint point)) { return; }
        if (_annotationDragStart is not null)
        {
            long timestamp = Environment.TickCount64;
            if (timestamp - _annotationLastPreview >= 33)
            {
                _annotationLastPreview = timestamp;
                UpdateAnnotationMove(point);
            }
            e.Handled = true;
            return;
        }
        if (_annotationPoints.Count == 0) { return; }
        UpdateAnnotationEnd(point);
        long now = Environment.TickCount64;
        if (now - _annotationLastPreview >= 33)
        {
            _annotationLastPreview = now;
            if (TryCreateAnnotation(out ImageAnnotation? draft))
            {
                Preview.SetEditRecipe(Recipe.WithAnnotations(Recipe.Annotations.Add(draft!)), fit: false);
            }
        }
        e.Handled = true;
    }

    private void UpdateAnnotationEnd(ImageEditPoint point)
    {
        if (_annotationTool == ImageAnnotationKind.Pen)
        {
            Point previous = Preview.ToCanvasPoint(_annotationPoints[^1].X, _annotationPoints[^1].Y);
            Point next = Preview.ToCanvasPoint(point.X, point.Y);
            if ((next - previous).Length < 1) { return; }
            if (_annotationPoints.Count < _annotationGesturePointLimit) { _annotationPoints.Add(point); }
            else { _annotationPoints[^1] = point; }
        }
        else if (_annotationPoints.Count == 1) { _annotationPoints.Add(point); }
        else { _annotationPoints[1] = point; }
    }

    private void OnAnnotationUp(object sender, MouseButtonEventArgs e)
    {
        if (!AnnotationIsDrawing) { return; }
        if (_annotationDragStart is not null)
        {
            if (TryAnnotationPoint(e.GetPosition(Preview), true, out ImageEditPoint destination)) { UpdateAnnotationMove(destination); }
            CommitAnnotationMove();
            e.Handled = true;
            return;
        }
        if (_annotationPoints.Count == 0) { return; }
        if (TryAnnotationPoint(e.GetPosition(Preview), true, out ImageEditPoint point)) { UpdateAnnotationEnd(point); }
        CommitAnnotation();
        e.Handled = true;
    }

    private bool TryCreateAnnotation(out ImageAnnotation? annotation)
    {
        annotation = null;
        if (_annotationTool is not { } kind || _annotationPoints.Count < (kind == ImageAnnotationKind.Text ? 1 : 2)) { return false; }
        if (kind is not ImageAnnotationKind.Text and not ImageAnnotationKind.Pen && _annotationPoints[0] == _annotationPoints[^1]) { return false; }
        if (kind == ImageAnnotationKind.Text && string.IsNullOrWhiteSpace(_annotationText!.Text)) { return false; }
        try
        {
            double density = AnnotationSourcePixelsPerDip();
            annotation = new(kind, _annotationPoints.ToImmutableArray(), _annotationColor, ScaleAnnotationValue(_annotationWidth!.Value, density),
                kind == ImageAnnotationKind.Text ? _annotationText!.Text : "", ScaleAnnotationValue(_annotationFontSize!.Value, density),
                ScaleAnnotationValue(_annotationStrength!.Value, density));
            ImageAnnotation.ValidateCollection(Recipe.Annotations.Add(annotation));
        }
        catch (ArgumentException) { annotation = null; return false; }
        return true;
    }

    private double AnnotationSourcePixelsPerDip()
    {
        var origin = Preview.ToSourcePoint(new(0, 0));
        var horizontal = Preview.ToSourcePoint(new(1, 0));
        var vertical = Preview.ToSourcePoint(new(0, 1));
        double determinant = ((horizontal.X - origin.X) * (vertical.Y - origin.Y))
            - ((horizontal.Y - origin.Y) * (vertical.X - origin.X));
        double density = Math.Sqrt(Math.Abs(determinant));
        return double.IsFinite(density) && density > 0 ? density : 1;
    }

    private static double ScaleAnnotationValue(double dip, double density) =>
        Math.Clamp(dip * density, ImageAnnotation.MinimumScaledValue, ImageAnnotation.MaximumScaledValue);

    private void CommitAnnotation()
    {
        bool valid = TryCreateAnnotation(out ImageAnnotation? annotation);
        CancelAnnotationGesture();
        if (!valid)
        {
            StatusText.Text = AnnotationLabel("Edit_AnnotationInvalid", "Check the text or selection.", "\u8bf7\u68c0\u67e5\u6587\u5b57\u6216\u9009\u533a\u3002");
            return;
        }
        _session.Apply(Recipe.WithAnnotations(Recipe.Annotations.Add(annotation!)));
        RefreshEditor();
    }

    private void AnnotationLimitStatus() => StatusText.Text = AnnotationLabel("Edit_AnnotationLimit", "Annotation limit reached.", "\u6807\u6ce8\u5df2\u8fbe\u4e0a\u9650\u3002");
}
