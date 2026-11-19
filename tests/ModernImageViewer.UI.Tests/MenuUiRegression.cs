using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

using ModernImageViewer.Application.Settings;
using ModernImageViewer.UI.Commands;
using ModernImageViewer.UI.Controls;
using ModernImageViewer.UI.Localization;
using ModernImageViewer.UI.Themes;
using ModernImageViewer.UI.ViewModels;

using ShapePath = System.Windows.Shapes.Path;

namespace ModernImageViewer.UI.Tests;

// Runs inside WindowResourcesLoadAndThemesSwitch's single STA Application.
internal static class MenuUiRegression
{
    public static void VerifyEmptyState(MainWindow window, ThemeService themes, LocalizationService localization)
    {
        double savedWidth = window.Width;
        double savedHeight = window.Height;
        try
        {
            window.Width = 720;
            window.Height = 480;
            Drain(window);
            VerifyLiveMenu(window, themes, localization, hasImage: false);
        }
        finally
        {
            window.Width = savedWidth;
            window.Height = savedHeight;
            Drain(window);
        }
    }

    public static void VerifyLoadedState(MainWindow window, ThemeService themes, LocalizationService localization)
    {
        VerifyLiveMenu(window, themes, localization, hasImage: true);
        VerifyCombinedStatesAndScrolling(window, themes);
        VerifySimulatedDpiLayouts(window);
        VerifyTitleBarFocus(window);
        VerifyKeyboardRouting(window);
        VerifyZoomInput(window, themes, localization);
        VerifyCanvasBackgroundMenu(window, themes, localization);
    }

    private static void VerifyCanvasBackgroundMenu(MainWindow window, ThemeService themes, LocalizationService localization)
    {
        ContextMenu menu = ((Button)window.FindName("SettingsButton")).ContextMenu!;
        ImageViewport viewport = (ImageViewport)window.FindName("Viewport");
        MainWindowViewModel model = (MainWindowViewModel)window.DataContext;
        AppTheme savedTheme = themes.CurrentTheme;
        string savedLanguage = localization.CurrentCulture.Name;
        CanvasBackgroundMode savedMode = model.CanvasBackground;
        try
        {
            foreach ((AppTheme theme, string language, CanvasBackgroundMode mode) in new[]
            {
                (AppTheme.Dark, "zh-CN", CanvasBackgroundMode.White),
                (AppTheme.Light, "en-US", CanvasBackgroundMode.Black),
            })
            {
                themes.Apply(theme);
                localization.SetCulture(language);
                menu.IsOpen = true;
                Drain(menu);
                MenuItem background = Item(menu, model.CanvasBackgroundLabel);
                background.IsSubmenuOpen = true;
                Drain(menu);
                Assert.Equal(model.CanvasBackground, Assert.Single(background.Items.OfType<MenuItem>(), item => item.IsChecked).Tag);

                MenuItem choice = background.Items.OfType<MenuItem>().Single(item => Equals(item.Tag, mode));
                choice.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                Drain(window);
                Assert.Equal(mode, model.CanvasBackground);
                Assert.Equal(mode, viewport.TransparencyBackground);
                Assert.Same(choice, Assert.Single(background.Items.OfType<MenuItem>(), item => item.IsChecked));
                choice.IsChecked = false;
                choice.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                Assert.True(choice.IsChecked);
                SaveScreenshot(Part<Border>(background, "SubmenuSurface"), $"canvas-background-menu-{theme}-{language}-rendertarget");
                background.IsSubmenuOpen = false;
                menu.IsOpen = false;
            }
        }
        finally
        {
            menu.IsOpen = false;
            model.CanvasBackground = savedMode;
            themes.Apply(savedTheme);
            localization.SetCulture(savedLanguage);
            Drain(window);
            viewport.Focus();
        }
    }

    private static void VerifyZoomInput(MainWindow window, ThemeService themes, LocalizationService localization)
    {
        TextBox input = (TextBox)window.FindName("ZoomInput");
        ImageViewport viewport = (ImageViewport)window.FindName("Viewport");
        MainWindowViewModel model = (MainWindowViewModel)window.DataContext;
        double savedWidth = window.Width;
        double savedHeight = window.Height;
        AppTheme savedTheme = themes.CurrentTheme;
        string savedCulture = localization.CurrentCulture.Name;
        try
        {
            window.Width = 720;
            window.Height = 480;
            Drain(window);
            Assert.True(input.Focus());
            Assert.Equal(input.Text, input.SelectedText);
            input.SetCurrentValue(TextBox.TextProperty, "37.5 %");
            KeyEventArgs enter = KeyEvent(window, Key.Enter, Keyboard.PreviewKeyDownEvent);
            input.RaiseEvent(enter);
            Drain(window);
            Assert.True(enter.Handled);
            Assert.Equal("37.5%", input.Text);
            Assert.Same(viewport, Keyboard.FocusedElement);

            Assert.True(input.Focus());
            input.SetCurrentValue(TextBox.TextProperty, "0");
            input.RaiseEvent(KeyEvent(window, Key.Enter, Keyboard.PreviewKeyDownEvent));
            Assert.Equal("37.5%", model.ZoomText);
            Assert.True(input.IsKeyboardFocused);
            Assert.Equal("0", input.SelectedText);
            input.RaiseEvent(KeyEvent(window, Key.Escape, Keyboard.PreviewKeyDownEvent));
            Drain(window);
            Assert.Equal("37.5%", input.Text);
            Assert.Same(viewport, Keyboard.FocusedElement);

            Assert.True(input.Focus());
            input.SetCurrentValue(TextBox.TextProperty, "200");
            model.UpdateScale(.375);
            Assert.Equal("200", input.Text);
            Assert.True(viewport.Focus());
            Assert.Equal("37.5%", input.Text);

            foreach ((AppTheme theme, string culture) in new[] { (AppTheme.Dark, "zh-CN"), (AppTheme.Light, "en-US") })
            {
                themes.Apply(theme);
                localization.SetCulture(culture);
                Drain(window);
                Assert.Equal(model.ZoomInputLabel, AutomationProperties.GetName(input));
                Assert.True(input.Focus());
                Drain(window);
                Assert.Equal(ResourceColor(input, "AccentBrush"), BrushColor(Part<Border>(input, "InputBorder").BorderBrush));
                SaveScreenshot(window, $"zoom-input-{theme}-{culture}-rendertarget");
                viewport.Focus();
            }
        }
        finally
        {
            window.Width = savedWidth;
            window.Height = savedHeight;
            themes.Apply(savedTheme);
            localization.SetCulture(savedCulture);
            Drain(window);
            viewport.Fit();
        }
    }

    public static void VerifyFileOperationDialogs(MainWindow window, ThemeService themes, LocalizationService localization)
    {
        MainWindowViewModel model = (MainWindowViewModel)window.DataContext;
        AppTheme savedTheme = themes.CurrentTheme;
        string savedLanguage = localization.CurrentCulture.Name;
        try
        {
            foreach ((AppTheme theme, string language) in new[] { (AppTheme.Dark, "zh-CN"), (AppTheme.Light, "en-US") })
            {
                themes.Apply(theme);
                localization.SetCulture(language);
                foreach (bool rename in new[] { true, false })
                {
                    FileOperationWindow dialog = new(model, model.CurrentFilePath, rename) { Owner = window };
                    dialog.Show();
                    try
                    {
                        Drain(dialog);
                        Button confirm = (Button)dialog.FindName("ConfirmButton");
                        Button cancel = (Button)dialog.FindName("CancelButton");
                        TextBox input = (TextBox)dialog.FindName("NameInput");
                        Assert.Equal(rename, confirm.IsDefault);
                        Assert.True(cancel.IsCancel);
                        Assert.Equal(rename, input.IsVisible);
                        Assert.Same(rename ? (IInputElement)input : cancel, Keyboard.FocusedElement);
                        if (rename)
                        {
                            Assert.Equal(input.Text, input.SelectedText);
                            Assert.Equal(".png", ((TextBlock)dialog.FindName("Extension")).Text);
                        }
                        SaveScreenshot(dialog, $"file-{(rename ? "rename" : "recycle")}-{theme}-{language}-rendertarget");
                        if (rename)
                        {
                            input.Text = @"..\outside";
                            confirm.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                            Drain(dialog);
                            Assert.Equal(localization.GetString("File_InvalidName"), ((TextBlock)dialog.FindName("Feedback")).Text);
                            Assert.True(input.IsKeyboardFocused);
                        }
                    }
                    finally
                    {
                        dialog.Close();
                    }
                }
            }
        }
        finally
        {
            themes.Apply(savedTheme);
            localization.SetCulture(savedLanguage);
            Drain(window);
        }
    }

    public static void VerifyInformationSelection(MainWindow window, ThemeService themes, LocalizationService localization)
    {
        MainWindowViewModel model = (MainWindowViewModel)window.DataContext;
        FrameworkElement panel = (FrameworkElement)window.FindName("InformationPanel");
        AppTheme savedTheme = themes.CurrentTheme;
        string savedLanguage = localization.CurrentCulture.Name;
        bool wasVisible = model.ShowInformation;
        double savedWidth = window.Width;
        double savedHeight = window.Height;
        try
        {
            window.Width = 720;
            window.Height = 480;
            model.ShowInformation = true;
            foreach ((AppTheme theme, string language) in new[] { (AppTheme.Dark, "zh-CN"), (AppTheme.Light, "en-US") })
            {
                themes.Apply(theme);
                localization.SetCulture(language);
                Drain(window);
                TextBox fileName = Descendants<TextBox>(panel).Single(box => AutomationProperties.GetName(box) == model.FileNameLabel);
                Assert.True(fileName.IsReadOnly);
                Assert.Equal(model.CurrentFileName, fileName.Text);
                Assert.True(fileName.Focus());
                TextBox camera = Descendants<TextBox>(panel).Single(box => AutomationProperties.GetName(box) == localization.GetString("Exif_Camera"));
                Assert.True(camera.IsReadOnly);
                Assert.True(camera.Focus());
                Assert.False(ApplicationCommands.Copy.CanExecute(null, camera));
                camera.Select(0, "Synthetic Camera".Length);
                Assert.Equal("Synthetic Camera", camera.SelectedText);
                Assert.True(ApplicationCommands.Copy.CanExecute(null, camera));
                camera.BringIntoView();
                Drain(window);
                Assert.Equal(ResourceColor(camera, "AccentBrush"), BrushColor(Part<Border>(camera, "ValueBorder").BorderBrush));
                SaveScreenshot(window, $"information-selection-{theme}-{language}-rendertarget");
            }

            TextBox activeField = Assert.IsType<TextBox>(Keyboard.FocusedElement);
            KeyEventArgs escape = KeyEvent(window, Key.Escape, Keyboard.PreviewKeyDownEvent);
            activeField.RaiseEvent(escape);
            Drain(window);
            Assert.True(escape.Handled);
            Assert.False(model.ShowInformation);
            Assert.Same(window.FindName("Viewport"), Keyboard.FocusedElement);
        }
        finally
        {
            model.ShowInformation = wasVisible;
            window.Width = savedWidth;
            window.Height = savedHeight;
            themes.Apply(savedTheme);
            localization.SetCulture(savedLanguage);
            Drain(window);
        }
    }

    private static void VerifyLiveMenu(MainWindow window, ThemeService themes, LocalizationService localization, bool hasImage)
    {
        Button settings = (Button)window.FindName("SettingsButton");
        ContextMenu menu = settings.ContextMenu!;
        MainWindowViewModel model = (MainWindowViewModel)window.DataContext;
        AppTheme savedTheme = themes.CurrentTheme;
        string savedLanguage = localization.CurrentCulture.Name;
        menu.PlacementTarget = settings;
        menu.IsOpen = true;
        try
        {
            foreach (AppTheme theme in new[] { AppTheme.Light, AppTheme.Dark, AppTheme.System })
            {
                themes.Apply(theme);
                foreach (string language in new[] { "en-US", "zh-CN" })
                {
                    // Change resources while the same popup is open, not just before opening.
                    localization.SetCulture(language);
                    Drain(menu);
                    Assert.True(menu.IsOpen);
                    Border surface = Part<Border>(menu, "MenuSurface");
                    Assert.Equal(ResourceColor(menu, "SurfaceBrush"), BrushColor(surface.Background));
                    Assert.Equal(ResourceColor(menu, "BorderBrush"), BrushColor(surface.BorderBrush));
                    AssertSurfacePixels(menu, ResourceColor(menu, "SurfaceBrush"));
                    Assert.Equal(MenuPopupBounds.GetMaximumHeight(menu), menu.MaxHeight);
                    Assert.Equal(MenuPopupBounds.GetMaximumWidth(menu), menu.MaxWidth);
                    Assert.InRange(menu.ActualHeight, 1, MenuPopupBounds.GetMaximumHeight(menu) + 1);
                    Assert.InRange(menu.ActualWidth, 1, MenuPopupBounds.GetMaximumWidth(menu) + 1);
                    MenuItem orientation = Item(menu, model.ViewOrientationLabel);
                    Assert.Equal(hasImage, orientation.IsEnabled);
                    Assert.Equal(hasImage, Item(menu, model.RefreshLabel).IsEnabled);
                    Assert.Same(model.PasteFilesCommand, Item(menu, model.PasteFilesLabel).Command);
                    MenuItem fileActions = Item(menu, model.FileActionsLabel);
                    Assert.Equal(model.CanChangeFile, Item(fileActions, model.RenameFileLabel).IsEnabled);
                    Assert.Equal(model.CanChangeFile, Item(fileActions, model.RecycleFileLabel).IsEnabled);
                    Assert.Same(model.CopyPreviewCommand, Item(fileActions, model.CopyPreviewLabel).Command);
                    Assert.Same(model.CopyOriginalCommand, Item(fileActions, model.CopyOriginalLabel).Command);
                    foreach (MenuItem item in MenuItems(menu))
                    {
                        if (!string.IsNullOrEmpty(item.InputGestureText))
                        {
                            Assert.DoesNotContain(item.InputGestureText, Assert.IsType<string>(item.Header), StringComparison.OrdinalIgnoreCase);
                        }
                    }
                    Assert.Equal(localization.GetString("Menu_FullScreen"), Item(menu, model.FullScreenMenuLabel).Header);
                    Assert.Equal("F11", Item(menu, model.FullScreenMenuLabel).InputGestureText);
                    Assert.Equal("F6", Item(menu, model.SlideshowMenuLabel).InputGestureText);
                    Assert.Contains("F11", model.FullScreenLabel, StringComparison.Ordinal);
                    Assert.Contains("F6", model.SlideshowLabel, StringComparison.Ordinal);
                    Assert.Contains(Descendants<Button>((DependencyObject)window.FindName("Toolbar")),
                        button => Equals(button.ToolTip, model.ZoomInLabel));
                    Assert.Contains(Descendants<Button>(window),
                        button => ReferenceEquals(button.Command, model.OpenCommand) && Equals(button.ToolTip, "Ctrl+O"));
                    Separator separator = menu.Items.OfType<Separator>().First();
                    Assert.Equal(ResourceColor(menu, "BorderBrush"), BrushColor(Descendants<Border>(separator).Single().Background));
                    if (theme != AppTheme.System)
                    {
                        SaveScreenshot(menu, $"menu-{(hasImage ? "image" : "empty")}-{theme}-{language}-rendertarget");
                    }
                    if (hasImage)
                    {
                        orientation.IsSubmenuOpen = true;
                        Drain(menu);
                        Popup popup = Part<Popup>(orientation, "PART_Popup");
                        Border submenu = Part<Border>(orientation, "SubmenuSurface");
                        Assert.True(popup.IsOpen);
                        Assert.Equal(menu.MaxHeight, submenu.MaxHeight);
                        Assert.Equal(menu.MaxWidth, submenu.MaxWidth);
                        Assert.Equal(ResourceColor(menu, "SurfaceBrush"), BrushColor(submenu.Background));
                        Assert.Equal(ResourceColor(menu, "BorderBrush"), BrushColor(submenu.BorderBrush));
                        foreach (MenuItem rotate in orientation.Items.OfType<MenuItem>().Where(item => item.Tag is "Left" or "Right"))
                        {
                            ContentPresenter icon = Part<ContentPresenter>(rotate, "IconPresenter");
                            Assert.Same(rotate.Icon, icon.Content);
                            Assert.True(icon.IsVisible);
                            Assert.InRange(icon.ActualWidth, 1, 20);
                            Assert.NotEmpty(Descendants<ShapePath>(icon));
                        }
                        SaveScreenshot(submenu, $"menu-orientation-{theme}-{language}-rendertarget");
                        orientation.IsSubmenuOpen = false;
                    }
                }
            }
        }
        finally
        {
            menu.IsOpen = false;
            themes.Apply(savedTheme);
            localization.SetCulture(savedLanguage);
            Drain(window);
        }
    }

    private static void VerifyCombinedStatesAndScrolling(MainWindow window, ThemeService themes)
    {
        ContextMenu menu = CreateStateMenu(window);
        menu.PlacementTarget = (Button)window.FindName("SettingsButton");
        MenuPopupBounds.SetMaximumHeight(menu, 210);
        menu.IsOpen = true;
        AppTheme savedTheme = themes.CurrentTheme;
        try
        {
            foreach (AppTheme theme in new[] { AppTheme.Light, AppTheme.Dark })
            {
                themes.Apply(theme);
                Drain(menu);
                Assert.Equal(210, menu.MaxHeight);
                Assert.InRange(menu.ActualHeight, 1, 211);
            }
            Drain(menu);
            VerifyLeadingLayout(menu);
            MenuItem disabled = (MenuItem)menu.Items[4];
            Assert.False(disabled.IsEnabled);
            Assert.Equal(ResourceColor(menu, "DisabledTextBrush"), BrushColor(disabled.Foreground));
            Assert.Equal(0.55, Part<ContentPresenter>(disabled, "IconPresenter").Opacity);
            Assert.Equal(ResourceColor(menu, "DisabledTextBrush"), BrushColor(Part<ShapePath>(disabled, "CheckMark").Stroke));
            ScrollViewer scroll = Descendants<ScrollViewer>(menu).Single();
            Assert.InRange(menu.ActualHeight, 1, 211);
            Assert.True(scroll.ScrollableHeight > 0);
            Assert.Equal(Visibility.Visible, scroll.ComputedVerticalScrollBarVisibility);
            scroll.ScrollToEnd();
            Drain(menu);
            Assert.InRange(scroll.VerticalOffset, scroll.ScrollableHeight - 0.1, scroll.ScrollableHeight + 0.1);
            MenuItem last = (MenuItem)menu.Items[^1];
            Rect visible = Bounds(last, menu);
            Assert.InRange(visible.Bottom, 1, menu.ActualHeight);
            int invocations = 0;
            last.Command = new AsyncRelayCommand(() => { invocations++; return Task.CompletedTask; });
            IInvokeProvider invoke = Assert.IsAssignableFrom<IInvokeProvider>(new MenuItemAutomationPeer(last).GetPattern(PatternInterface.Invoke));
            invoke.Invoke();
            Drain(window);
            Assert.Equal(1, invocations);
            Assert.False(menu.IsOpen);
        }
        finally { menu.IsOpen = false; themes.Apply(savedTheme); }
    }

    private static void VerifySimulatedDpiLayouts(MainWindow window)
    {
        foreach (double scale in new[] { 1d, 1.25, 1.5, 2 })
        {
            // Disconnected WPF root DPI and physical work-area conversion are simulated.
            // This does not exercise a monitor transition or constitute desktop acceptance.
            ContextMenu menu = CreateStateMenu(window);
            VisualTreeHelper.SetRootDpi(menu, new DpiScale(scale, scale));
            MenuPopupBounds.SetMaximumWidth(menu, 640 / scale);
            MenuPopupBounds.SetMaximumHeight(menu, 720 / scale);
            menu.Measure(new Size(640 / scale, 720 / scale));
            menu.Arrange(new Rect(new Point(), menu.DesiredSize));
            Drain(menu);
            Assert.Equal(scale, VisualTreeHelper.GetDpi(menu).DpiScaleX);
            Assert.InRange(menu.ActualWidth * scale, 1, 641);
            Assert.InRange(menu.ActualHeight * scale, 1, 721);
            VerifyLeadingLayout(menu);
            MenuItem shortcut = (MenuItem)menu.Items[3];
            Rect header = Bounds(Part<ContentPresenter>(shortcut, "HeaderPresenter"), shortcut);
            Rect gesture = Bounds(Part<TextBlock>(shortcut, "GestureText"), shortcut);
            Assert.True(header.Right <= gesture.Left);
            SaveScreenshot(menu, $"menu-simulated-dpi-{scale * 100:0}-rendertarget", scale);
        }
    }

    private static void VerifyTitleBarFocus(MainWindow window)
    {
        Button[] buttons = Descendants<Button>((DependencyObject)window.FindName("TitleBar")).TakeLast(3).ToArray();
        foreach (Button button in buttons)
        {
            button.Focus();
            Drain(window);
            Assert.True(button.IsKeyboardFocused);
            Border border = Part<Border>(button, "ButtonBorder");
            Assert.Equal(ResourceColor(button, "AccentBrush"), BrushColor(border.BorderBrush));
            Assert.True(border.BorderThickness.Left > 0);
        }
        Button close = buttons[^1];
        Assert.Equal(BrushColor(close.Foreground), BrushColor(Assert.IsType<ShapePath>(close.Content).Stroke));
        ((Button)window.FindName("SettingsButton")).Focus();
    }

    private static void VerifyKeyboardRouting(MainWindow window)
    {
        ContextMenu menu = ((Button)window.FindName("SettingsButton")).ContextMenu!;
        MainWindowViewModel model = (MainWindowViewModel)window.DataContext;
        Assert.False(model.IsFullScreen);
        menu.IsOpen = true;
        try
        {
            Drain(menu);
            MenuItem orientation = Item(menu, model.ViewOrientationLabel);
            orientation.Focus();
            KeyEventArgs arrow = KeyEvent(menu, Key.Right, Keyboard.PreviewKeyDownEvent);
            orientation.RaiseEvent(arrow);
            Assert.False(arrow.Handled);
            Assert.True(menu.IsOpen);
            Assert.False(model.IsFullScreen);
            orientation.RaiseEvent(KeyEvent(menu, Key.Right, Keyboard.KeyDownEvent));
            Drain(menu);
            Assert.True(orientation.IsSubmenuOpen);
            MenuItem child = orientation.Items.OfType<MenuItem>().First();
            child.Focus();
            child.RaiseEvent(KeyEvent(menu, Key.Left, Keyboard.KeyDownEvent));
            Drain(menu);
            Assert.False(orientation.IsSubmenuOpen);
            Assert.True(menu.IsOpen);
            KeyEventArgs shortcut = KeyEvent(menu, Key.F11, Keyboard.PreviewKeyDownEvent);
            menu.RaiseEvent(shortcut);
            Drain(window);
            Assert.True(shortcut.Handled);
            Assert.True(model.IsFullScreen);
            Assert.False(menu.IsOpen);
            window.RaiseEvent(KeyEvent(window, Key.F11, Keyboard.PreviewKeyDownEvent));
            Drain(window);
            Assert.False(model.IsFullScreen);
        }
        finally
        {
            menu.IsOpen = false;
            if (model.IsFullScreen)
            {
                window.RaiseEvent(KeyEvent(window, Key.F11, Keyboard.PreviewKeyDownEvent));
                Drain(window);
            }
        }
    }

    private static ContextMenu CreateStateMenu(MainWindow window)
    {
        ShapePath Icon() => new() { Style = (Style)window.FindResource("IconStyle"), Data = (Geometry)window.FindResource("Icon.Refresh") };
        ContextMenu menu = new();
        menu.Items.Add(new MenuItem { Header = "Plain" });
        menu.Items.Add(new MenuItem { Header = "Icon", Icon = Icon() });
        menu.Items.Add(new MenuItem { Header = "Checked", IsCheckable = true, IsChecked = true });
        menu.Items.Add(new MenuItem { Header = "Icon and check", Icon = Icon(), IsCheckable = true, IsChecked = true, InputGestureText = "F11" });
        menu.Items.Add(new MenuItem { Header = "Disabled", Icon = Icon(), IsCheckable = true, IsChecked = true, IsEnabled = false });
        MenuItem submenu = new() { Header = "Submenu" };
        submenu.Items.Add(new MenuItem { Header = "Child" });
        menu.Items.Add(submenu);
        menu.Items.Add(new Separator());
        for (int index = 0; index < 10; index++) { menu.Items.Add(new MenuItem { Header = $"Option {index}" }); }
        menu.Items.Add(new MenuItem { Header = "Last option" });
        return menu;
    }

    private static void VerifyLeadingLayout(ContextMenu menu)
    {
        double? headerLeft = null;
        foreach (MenuItem item in menu.Items.OfType<MenuItem>())
        {
            ContentPresenter header = Part<ContentPresenter>(item, "HeaderPresenter");
            Rect bounds = Bounds(header, item);
            headerLeft ??= bounds.Left;
            Assert.InRange(bounds.Left, headerLeft.Value - 1, headerLeft.Value + 1);
            Assert.InRange(item.ActualHeight, 34, 60);
            ContentPresenter icon = Part<ContentPresenter>(item, "IconPresenter");
            ShapePath check = Part<ShapePath>(item, "CheckMark");
            Assert.Equal(item.Icon is null ? Visibility.Collapsed : Visibility.Visible, icon.Visibility);
            Assert.Equal(item.IsChecked ? Visibility.Visible : Visibility.Collapsed, check.Visibility);
            if (item.Icon is not null)
            {
                Rect iconBounds = Bounds(icon, item);
                Assert.InRange(iconBounds.Height, 1, item.ActualHeight);
                Assert.True(iconBounds.Right <= bounds.Left);
                Assert.InRange(iconBounds.Top + iconBounds.Height / 2, item.ActualHeight / 2 - 1, item.ActualHeight / 2 + 1);
                if (item.IsChecked)
                {
                    Rect checkBounds = Bounds(check, item);
                    Assert.True(checkBounds.Left >= 0 && checkBounds.Right <= iconBounds.Left);
                    Assert.True(checkBounds.Top >= 0 && checkBounds.Bottom <= item.ActualHeight);
                }
            }
        }
    }

    private static KeyEventArgs KeyEvent(Visual source, Key key, RoutedEvent routedEvent) =>
        new(Keyboard.PrimaryDevice, PresentationSource.FromVisual(source)!, Environment.TickCount, key) { RoutedEvent = routedEvent };

    private static MenuItem Item(ItemsControl parent, string header) => parent.Items.OfType<MenuItem>().Single(item => Equals(item.Header, header));

    private static IEnumerable<MenuItem> MenuItems(ItemsControl parent)
    {
        foreach (MenuItem item in parent.Items.OfType<MenuItem>())
        {
            yield return item;
            foreach (MenuItem child in MenuItems(item)) { yield return child; }
        }
    }

    private static T Part<T>(Control control, string name) where T : FrameworkElement => Assert.IsType<T>(control.Template.FindName(name, control));

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, index);
            if (child is T match) { yield return match; }
            foreach (T descendant in Descendants<T>(child)) { yield return descendant; }
        }
    }

    private static Rect Bounds(FrameworkElement element, Visual ancestor) => element.TransformToAncestor(ancestor).TransformBounds(new Rect(element.RenderSize));

    private static Color ResourceColor(FrameworkElement element, string key) => BrushColor((Brush)element.FindResource(key));
    private static Color BrushColor(Brush brush) => Assert.IsType<SolidColorBrush>(brush).Color;

    private static void Drain(FrameworkElement element)
    {
        element.Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
        element.UpdateLayout();
    }

    private static RenderTargetBitmap Render(FrameworkElement element, double scale = 1)
    {
        RenderTargetBitmap bitmap = new((int)Math.Ceiling(element.ActualWidth * scale),
            (int)Math.Ceiling(element.ActualHeight * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bitmap.Render(element);
        return bitmap;
    }

    private static void AssertSurfacePixels(ContextMenu menu, Color background)
    {
        RenderTargetBitmap bitmap = Render(menu);
        foreach (int x in new[] { 3, bitmap.PixelWidth - 4 })
        {
            byte[] pixel = new byte[4];
            bitmap.CopyPixels(new Int32Rect(x, bitmap.PixelHeight / 2, 1, 1), pixel, 4, 0);
            Assert.Equal(new byte[] { background.B, background.G, background.R, 255 }, pixel);
        }
    }

    private static void SaveScreenshot(FrameworkElement element, string name, double scale = 1)
    {
        string? directory = Environment.GetEnvironmentVariable("MIV_UI_SCREENSHOT_DIRECTORY");
        if (string.IsNullOrEmpty(directory)) { return; }
        // Offscreen WPF rendering for regression artifacts; not a real desktop capture.
        Directory.CreateDirectory(directory);
        PngBitmapEncoder encoder = new();
        encoder.Frames.Add(BitmapFrame.Create(Render(element, scale)));
        using FileStream file = File.Create(Path.Combine(directory, $"{name}.png"));
        encoder.Save(file);
    }
}
