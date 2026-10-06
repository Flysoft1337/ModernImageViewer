using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Markup;

using ModernImageViewer.Application.Settings;

namespace ModernImageViewer.UI;

public partial class EditWindow
{
    private IUserSettingsService? _presetSettings;
    private ComboBox? _presetPicker;
    private TextBox? _presetName;
    private Button? _applyPresetButton;
    private Button? _deletePresetButton;
    private readonly List<(DependencyObject Target, DependencyProperty Property, string Key)> _presetLabels = [];

    private void InitializePresets(IUserSettingsService? settings)
    {
        if (FindName("PresetFields") is not StackPanel panel) { return; }
        _presetSettings = settings;
        panel.Visibility = settings is null ? Visibility.Collapsed : Visibility.Visible;
        if (settings is null) { return; }

        TextBlock title = new() { FontSize = 15, FontWeight = FontWeights.SemiBold, Margin = new(0, 0, 0, 12) };
        AddPresetText(title, TextBlock.TextProperty, "Edit_Presets");
        panel.Children.Add(title);
        _presetPicker = new()
        {
            Name = "EditorPresetPicker",
            MinHeight = 36,
            FontSize = 13,
            Padding = new(10, 8, 28, 8),
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Template = CreatePresetPickerTemplate(),
            ItemContainerStyle = CreatePresetItemStyle(),
            ItemTemplate = (DataTemplate)XamlReader.Parse("""
                <DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
                  <TextBlock Text="{Binding Name}" TextTrimming="CharacterEllipsis" />
                </DataTemplate>
                """),
        };
        _presetPicker.SetResourceReference(Control.ForegroundProperty, "PrimaryTextBrush");
        RegisterName(_presetPicker.Name, _presetPicker);
        AddPresetText(_presetPicker, AutomationProperties.NameProperty, "Edit_Presets");
        _presetPicker.SelectionChanged += (_, _) =>
        {
            if (_presetPicker.SelectedItem is EditorPresetData selected) { _presetName!.Text = selected.Name; }
            bool selectedPreset = _presetPicker.SelectedItem is EditorPresetData;
            _applyPresetButton!.IsEnabled = selectedPreset;
            _deletePresetButton!.IsEnabled = selectedPreset;
        };
        panel.Children.Add(_presetPicker);
        TextBlock label = new() { Style = (Style)FindResource("PanelLabelStyle"), Margin = new(0, 12, 0, 5) };
        AddPresetText(label, TextBlock.TextProperty, "Edit_PresetName");
        panel.Children.Add(label);
        _presetName = new()
        {
            Name = "EditorPresetName",
            MaxLength = EditorPresetData.MaximumNameLength,
            Style = (Style)FindResource("EditorInput"),
        };
        AddPresetText(_presetName, AutomationProperties.NameProperty, "Edit_PresetName");
        RegisterName(_presetName.Name, _presetName);
        panel.Children.Add(_presetName);
        System.Windows.Controls.Primitives.UniformGrid commands = new() { Columns = 3, Margin = new(0, 8, 0, 0) };
        _applyPresetButton = CreatePresetButton("EditorPresetApply", "Edit_PresetApply", OnApplyPresetClick);
        commands.Children.Add(_applyPresetButton);
        commands.Children.Add(CreatePresetButton("EditorPresetSave", "Edit_PresetSave", OnSavePresetClick));
        _deletePresetButton = CreatePresetButton("EditorPresetDelete", "Edit_PresetDelete", OnDeletePresetClick);
        commands.Children.Add(_deletePresetButton);
        panel.Children.Add(commands);
        ReloadPresets();
        _localization.CultureChanged += OnPresetCultureChanged;
        Closed += (_, _) => _localization.CultureChanged -= OnPresetCultureChanged;
    }

    private Button CreatePresetButton(string name, string key, RoutedEventHandler click)
    {
        Button button = new() { Name = name, Style = (Style)FindResource("ToolbarButtonStyle"), Padding = new(5, 8, 5, 8) };
        button.SetResourceReference(Control.BorderBrushProperty, "BorderBrush");
        TextBlock caption = new() { TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center };
        AddPresetText(caption, TextBlock.TextProperty, key);
        button.Content = caption;
        RegisterName(button.Name, button);
        AddPresetText(button, AutomationProperties.NameProperty, key);
        button.Click += click;
        return button;
    }

    private void AddPresetText(DependencyObject target, DependencyProperty property, string key)
    {
        _presetLabels.Add((target, property, key));
        target.SetValue(property, Text(key));
    }

    private void OnPresetCultureChanged(object? sender, EventArgs e)
    {
        foreach (var (target, property, key) in _presetLabels) { target.SetValue(property, Text(key)); }
    }

    private void ReloadPresets(string? selectedName = null)
    {
        IReadOnlyList<EditorPresetData> presets = EditorPresetData.NormalizeList(_presetSettings?.Current.EditorPresets) ?? [];
        _presetPicker!.ItemsSource = presets;
        _presetPicker.SelectedItem = presets.FirstOrDefault(p => string.Equals(p.Name, selectedName, StringComparison.OrdinalIgnoreCase));
        _applyPresetButton!.IsEnabled = _presetPicker.SelectedItem is EditorPresetData;
        _deletePresetButton!.IsEnabled = _presetPicker.SelectedItem is EditorPresetData;
    }

    private void OnSavePresetClick(object sender, RoutedEventArgs e)
    {
        if (IsExporting || _presetSettings is null || _presetName is null) { return; }
        string name = _presetName.Text.Trim();
        if (name.Length is < 1 or > EditorPresetData.MaximumNameLength || name.Any(char.IsControl))
        {
            StatusText.Text = Text("Edit_PresetInvalidName");
            _presetName.Focus();
            _presetName.SelectAll();
            return;
        }
        if (!TryReadSize(out var size) || !ReadInteger(JpegQuality, 1, 100, out int quality)) { return; }
        string ratio = PresetRatioChoices().FirstOrDefault(r => r.IsChecked == true)?.Tag as string ?? "Free";
        EditorPresetData preset = new(name, size.Width, size.Height, ratio,
            WebpFormat.IsChecked == true ? 2 : JpegFormat.IsChecked == true ? 1 : 0, quality,
            (FindName("WebpLossless") as CheckBox)?.IsChecked == true,
            (FindName("PreserveMetadata") as CheckBox)?.IsChecked == true);
        List<EditorPresetData> presets = (EditorPresetData.NormalizeList(_presetSettings.Current.EditorPresets) ?? []).ToList();
        int existing = presets.FindIndex(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
        if (existing >= 0) { presets[existing] = preset; }
        else if (presets.Count == EditorPresetData.MaximumCount)
        {
            StatusText.Text = Text("Edit_PresetLimit");
            return;
        }
        else { presets.Add(preset); }
        if (!StorePresets(presets)) { return; }
        ReloadPresets(name);
        StatusText.Text = string.Format(_localization.CurrentCulture, Text("Edit_PresetSaved"), name);
    }

    private void OnDeletePresetClick(object sender, RoutedEventArgs e)
    {
        if (IsExporting || _presetPicker?.SelectedItem is not EditorPresetData preset || _presetSettings is null) { return; }
        List<EditorPresetData> presets = (_presetSettings.Current.EditorPresets ?? [])
            .Where(p => !string.Equals(p.Name, preset.Name, StringComparison.OrdinalIgnoreCase)).ToList();
        if (!StorePresets(presets)) { return; }
        ReloadPresets();
        _presetName!.Clear();
        StatusText.Text = string.Format(_localization.CurrentCulture, Text("Edit_PresetDeleted"), preset.Name);
    }

    private bool StorePresets(IReadOnlyList<EditorPresetData> presets)
    {
        try
        {
            if (_presetSettings?.SaveEditorPresets(presets) == true) { return true; }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
        StatusText.Text = Text("Edit_PresetSaveFailed");
        return false;
    }

    private void OnApplyPresetClick(object sender, RoutedEventArgs e)
    {
        if (IsExporting || _presetPicker?.SelectedItem is not EditorPresetData { IsValid: true } preset) { return; }
        RadioButton? ratioChoice = PresetRatioChoices().FirstOrDefault(r => Equals(r.Tag, preset.CropRatio));
        if (ratioChoice is null) { return; }
        bool wasRefreshing = _refreshing;
        _refreshing = true;
        try
        {
            ratioChoice.IsChecked = true;
            OutputWidth.Text = preset.Width.ToString(CultureInfo.InvariantCulture);
            OutputHeight.Text = preset.Height.ToString(CultureInfo.InvariantCulture);
            PngFormat.IsChecked = preset.Format == 0;
            JpegFormat.IsChecked = preset.Format == 1;
            WebpFormat.IsChecked = preset.Format == 2;
            JpegQuality.Text = preset.Quality.ToString(CultureInfo.InvariantCulture);
            if (FindName("WebpLossless") is CheckBox lossless) { lossless.IsChecked = preset.WebpLossless; }
            if (FindName("PreserveMetadata") is CheckBox metadata) { metadata.IsChecked = preset.PreserveCamera; }
        }
        finally { _refreshing = wasRefreshing; }
        // Keep crop and size as drafts so the normal save path commits one undoable recipe.
        OnCropRatioChanged(ratioChoice, e);
        _sizeDirty = true;
        UpdateFormatHint();
        StatusText.Text = string.Format(_localization.CurrentCulture, Text("Edit_PresetApplied"), preset.Name);
    }

    private IEnumerable<RadioButton> PresetRatioChoices() => FindPresetRatioChoices(EditFields);

    private static IEnumerable<RadioButton> FindPresetRatioChoices(DependencyObject parent)
    {
        foreach (object child in LogicalTreeHelper.GetChildren(parent))
        {
            if (child is RadioButton { GroupName: "CropRatio" } ratio) { yield return ratio; }
            if (child is not DependencyObject descendant) { continue; }
            foreach (RadioButton nested in FindPresetRatioChoices(descendant)) { yield return nested; }
        }
    }

    private static Style CreatePresetItemStyle()
    {
        Style style = new(typeof(ComboBoxItem));
        style.Setters.Add(new Setter(Control.ForegroundProperty, new DynamicResourceExtension("PrimaryTextBrush")));
        style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(10, 8, 10, 8)));
        style.Setters.Add(new Setter(Control.TemplateProperty, (ControlTemplate)XamlReader.Parse("""
            <ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" TargetType="ComboBoxItem">
              <Border x:Name="Item" Background="Transparent" Padding="{TemplateBinding Padding}" CornerRadius="4">
                <ContentPresenter />
              </Border>
              <ControlTemplate.Triggers>
                <Trigger Property="IsHighlighted" Value="True"><Setter TargetName="Item" Property="Background" Value="{DynamicResource SurfaceHoverBrush}" /></Trigger>
                <Trigger Property="IsSelected" Value="True"><Setter TargetName="Item" Property="Background" Value="{DynamicResource AccentSurfaceBrush}" /></Trigger>
              </ControlTemplate.Triggers>
            </ControlTemplate>
            """)));
        return style;
    }

    private static ControlTemplate CreatePresetPickerTemplate() => (ControlTemplate)XamlReader.Parse("""
        <ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                         xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" TargetType="ComboBox">
          <Grid>
            <Border x:Name="Outline" Background="{DynamicResource WindowBackgroundBrush}" BorderBrush="{DynamicResource BorderBrush}" BorderThickness="1" CornerRadius="7">
              <Grid>
                <ContentPresenter Margin="{TemplateBinding Padding}" Content="{TemplateBinding SelectionBoxItem}" ContentTemplate="{TemplateBinding SelectionBoxItemTemplate}" IsHitTestVisible="False" VerticalAlignment="Center" />
                <ToggleButton Focusable="False" Background="Transparent" IsChecked="{Binding IsDropDownOpen, RelativeSource={RelativeSource TemplatedParent}, Mode=TwoWay}">
                  <ToggleButton.Template><ControlTemplate TargetType="ToggleButton">
                    <Border Background="Transparent"><Path HorizontalAlignment="Right" Margin="0,0,12,0" VerticalAlignment="Center" Width="8" Height="4" Stretch="Uniform" Stroke="{DynamicResource SecondaryTextBrush}" StrokeThickness="1.5" Data="M0,0 L4,4 8,0" /></Border>
                  </ControlTemplate></ToggleButton.Template>
                </ToggleButton>
              </Grid>
            </Border>
            <Popup x:Name="PART_Popup" IsOpen="{TemplateBinding IsDropDownOpen}" Placement="Bottom" AllowsTransparency="True" Focusable="False">
              <Border Width="{Binding ActualWidth, RelativeSource={RelativeSource TemplatedParent}}" MaxHeight="240" Background="{DynamicResource SurfaceBrush}" BorderBrush="{DynamicResource BorderBrush}" BorderThickness="1" CornerRadius="7" Padding="4">
                <ScrollViewer CanContentScroll="True" HorizontalScrollBarVisibility="Disabled"><ItemsPresenter KeyboardNavigation.DirectionalNavigation="Contained" /></ScrollViewer>
              </Border>
            </Popup>
          </Grid>
          <ControlTemplate.Triggers>
            <Trigger Property="IsKeyboardFocusWithin" Value="True"><Setter TargetName="Outline" Property="BorderBrush" Value="{DynamicResource AccentBrush}" /></Trigger>
            <Trigger Property="IsMouseOver" Value="True"><Setter TargetName="Outline" Property="BorderBrush" Value="{DynamicResource AccentBrush}" /></Trigger>
            <Trigger Property="IsEnabled" Value="False"><Setter Property="Opacity" Value="0.45" /></Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
        """);
}
