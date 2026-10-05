using System.ComponentModel;

using ModernImageViewer.UI.Localization;

namespace ModernImageViewer.UI.ViewModels;

public sealed class ShortcutHelpViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly ILocalizationService _localization;
    private bool _disposed;

    public ShortcutHelpViewModel(ILocalizationService localization)
    {
        ArgumentNullException.ThrowIfNull(localization);
        _localization = localization;
        Groups = ShortcutCatalog.CreateHelpGroups(localization);
        _localization.CultureChanged += OnCultureChanged;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Title => _localization.GetString("Shortcut_Title");
    public string Heading => _localization.GetString("Shortcut_Heading");
    public string Intro => _localization.GetString("Shortcut_Intro");
    public string FooterHint => _localization.GetString("Shortcut_FooterHint");
    public string CloseLabel => _localization.GetString("Window_Close");
    public IReadOnlyList<ShortcutHelpGroup> Groups { get; private set; }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _localization.CultureChanged -= OnCultureChanged;
    }

    private void OnCultureChanged(object? sender, EventArgs e)
    {
        Groups = ShortcutCatalog.CreateHelpGroups(_localization);
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
    }
}
