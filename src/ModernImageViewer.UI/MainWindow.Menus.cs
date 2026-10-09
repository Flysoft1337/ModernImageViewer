using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

using ModernImageViewer.UI.Controls;
using ModernImageViewer.UI.Themes;

namespace ModernImageViewer.UI;

public partial class MainWindow
{
    private void InitializeMenus()
    {
        // ContextMenu can also be opened directly or by Shift+F10. Give its first
        // Popup measurement the bounds; Opened is too late on short work areas.
        Loaded += (_, _) =>
        {
            if (SettingsButton.ContextMenu is { } menu)
            {
                menu.PlacementTarget = SettingsButton;
                UpdateMenuBounds(menu);
            }
        };
    }

    private void OnSettingsMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (SettingsButton.ContextMenu is { } menu) { UpdateMenuBounds(menu); }
    }

    private void OnSettingsClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button button && SettingsButton.ContextMenu is { } menu)
        {
            RevealImmersiveControls();
            menu.PlacementTarget = button;
            menu.Placement = ReferenceEquals(button, SettingsButton) ? PlacementMode.Bottom : PlacementMode.Top;
            UpdateMenuBounds(menu);
            menu.IsOpen = true;
        }
    }

    private void OnSettingsOpened(object sender, RoutedEventArgs e)
    {
        if (sender is ContextMenu menu) { UpdateMenuBounds(menu); }
        UpdateMenuChecks();
    }

    private void UpdateMenuBounds(ContextMenu menu)
    {
        IntPtr monitor = MonitorFromWindow(new WindowInteropHelper(this).Handle, 2);
        if (menu.PlacementTarget is FrameworkElement { IsLoaded: true } target)
        {
            Point origin = target.PointToScreen(new Point(0, 0));
            Point corner = target.PointToScreen(new Point(target.ActualWidth, target.ActualHeight));
            NativeRect rectangle = new()
            {
                Left = (int)origin.X,
                Top = (int)origin.Y,
                Right = (int)corner.X,
                Bottom = (int)corner.Y,
            };
            monitor = MonitorFromRect(ref rectangle, 2);
        }
        MonitorInfo info = new() { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref info)) { return; }

        DpiScale dpi = VisualTreeHelper.GetDpi(menu.PlacementTarget ?? this);
        double scaleX = dpi.DpiScaleX;
        double scaleY = dpi.DpiScaleY;
        if (GetDpiForMonitor(monitor, 0, out uint dpiX, out uint dpiY) == 0 && dpiX > 0 && dpiY > 0)
        {
            scaleX = dpiX / 96d;
            scaleY = dpiY / 96d;
        }
        // WPF limits Popup height to 75% of its screen. Constrain the scroll host
        // before that limit clips it, using physical work-area pixels converted to DIP.
        MenuPopupBounds.SetMaximumHeight(menu, (info.Work.Bottom - info.Work.Top) / scaleY * 0.75);
        MenuPopupBounds.SetMaximumWidth(menu, (info.Work.Right - info.Work.Left) / scaleX);
    }

    private void UpdateMenuChecks()
    {
        Slideshow2Item.IsChecked = _viewModel.SlideshowSeconds == 2;
        Slideshow5Item.IsChecked = _viewModel.SlideshowSeconds == 5;
        Slideshow10Item.IsChecked = _viewModel.SlideshowSeconds == 10;
        DarkThemeItem.IsChecked = _themes.CurrentTheme == AppTheme.Dark;
        LightThemeItem.IsChecked = _themes.CurrentTheme == AppTheme.Light;
        SystemThemeItem.IsChecked = _themes.CurrentTheme == AppTheme.System;
    }

    private void OnThemeClick(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: string name } && Enum.TryParse(name, out AppTheme theme))
        {
            _themes.Apply(theme);
            UpdateMenuChecks();
        }
    }

    private void OnLanguageClick(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: string name })
        {
            _viewModel.SelectedLanguage = _viewModel.SupportedLanguages.Single(language => language.CultureName == name);
        }
    }

    private void OnMenuPreviewKeyDown(object sender, KeyEventArgs e)
    {
        ModifierKeys modifiers = Keyboard.Modifiers;
        if (sender is not ContextMenu menu || ShortcutCatalog.Match(e.Key, modifiers) is null
            || (!modifiers.HasFlag(ModifierKeys.Control) && e.Key is not (Key.F1 or Key.F5 or Key.F6 or Key.F11)))
        {
            return;
        }
        menu.IsOpen = false;
        // The popup has its own focus scope. Restore window focus before using
        // the existing shortcut handler, which deliberately ignores MenuItem focus.
        (menu.PlacementTarget as UIElement)?.Focus();
        OnPreviewKeyDown(this, e);
    }
}
