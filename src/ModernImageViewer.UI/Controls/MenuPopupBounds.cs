using System.Windows;

namespace ModernImageViewer.UI.Controls;

public static class MenuPopupBounds
{
    public static readonly DependencyProperty MaximumHeightProperty = DependencyProperty.RegisterAttached(
        "MaximumHeight", typeof(double), typeof(MenuPopupBounds),
        new FrameworkPropertyMetadata(double.PositiveInfinity, FrameworkPropertyMetadataOptions.Inherits));

    public static readonly DependencyProperty MaximumWidthProperty = DependencyProperty.RegisterAttached(
        "MaximumWidth", typeof(double), typeof(MenuPopupBounds),
        new FrameworkPropertyMetadata(double.PositiveInfinity, FrameworkPropertyMetadataOptions.Inherits));

    // Strongly typed paths avoid deferred prefix resolution in shared BAML styles.
    public static PropertyPath MaximumHeightPath { get; } = new(MaximumHeightProperty);
    public static PropertyPath MaximumWidthPath { get; } = new(MaximumWidthProperty);

    public static double GetMaximumHeight(DependencyObject element) => (double)element.GetValue(MaximumHeightProperty);
    public static void SetMaximumHeight(DependencyObject element, double value) => element.SetValue(MaximumHeightProperty, value);
    public static double GetMaximumWidth(DependencyObject element) => (double)element.GetValue(MaximumWidthProperty);
    public static void SetMaximumWidth(DependencyObject element, double value) => element.SetValue(MaximumWidthProperty, value);
}
