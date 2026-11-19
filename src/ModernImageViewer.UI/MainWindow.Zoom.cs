using System.Globalization;
using System.Windows.Controls;
using System.Windows.Input;

namespace ModernImageViewer.UI;

public partial class MainWindow
{
    private void OnZoomInputMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (!ZoomInput.IsKeyboardFocused && ZoomInput.Focus()) { e.Handled = true; }
    }

    private void OnZoomInputFocus(object sender, KeyboardFocusChangedEventArgs e) => ZoomInput.SelectAll();

    private void OnZoomInputLostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        ZoomInput.GetBindingExpression(TextBox.TextProperty)?.UpdateTarget();
        _viewModel.ClearMessage("Zoom_Invalid");
    }

    private bool TryApplyZoomInput()
    {
        if (ZoomInput.Text == _viewModel.ZoomText) { return true; }

        CultureInfo culture = _viewModel.Localization.CurrentCulture;
        string text = ZoomInput.Text.Trim();
        string symbol = culture.NumberFormat.PercentSymbol;
        if (text.EndsWith(symbol, StringComparison.Ordinal)) { text = text[..^symbol.Length].TrimEnd(); }

        if (!double.TryParse(text, NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign, culture, out double percent)
            || !double.IsFinite(percent) || percent <= 0)
        {
            _messageTimer.Stop();
            _viewModel.ShowMessage("Zoom_Invalid");
            ZoomInput.SelectAll();
            return false;
        }

        Viewport.ZoomTo(percent / 100);
        return true;
    }
}
