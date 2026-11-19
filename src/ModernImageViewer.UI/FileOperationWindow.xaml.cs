using System.IO;
using System.Windows;
using System.Windows.Automation;

using ModernImageViewer.UI.ViewModels;

namespace ModernImageViewer.UI;

public partial class FileOperationWindow : Window
{
    private readonly MainWindowViewModel _model;
    private readonly string _path;
    private readonly bool _rename;
    private bool _busy;

    internal FileOperationWindow(MainWindowViewModel model, string path, bool rename)
    {
        _model = model;
        _path = path;
        _rename = rename;
        InitializeComponent();

        Title = Text(rename ? "File_RenameTitle" : "File_RecycleTitle");
        Description.Text = Text(rename ? "File_RenameDescription" : "File_RecycleDescription");
        FileName.Text = Path.GetFileName(path);
        FileName.ToolTip = path;
        CancelButton.Content = model.CancelLabel;
        ConfirmButton.Content = Text(rename ? "File_RenameConfirm" : "File_RecycleTitle");
        ConfirmButton.IsDefault = rename;
        CancelButton.IsDefault = !rename;
        if (rename)
        {
            NameInput.Text = Path.GetFileNameWithoutExtension(path);
            Extension.Text = Path.GetExtension(path);
            AutomationProperties.SetName(NameInput, model.FileNameLabel);
        }
        else
        {
            RenameFields.Visibility = Visibility.Collapsed;
            ConfirmButton.SetResourceReference(BackgroundProperty, "DangerPressedBrush");
            ConfirmButton.SetResourceReference(ForegroundProperty, "DangerTextBrush");
        }
        Loaded += (_, _) =>
        {
            if (rename)
            {
                NameInput.Focus();
                NameInput.SelectAll();
            }
            else
            {
                CancelButton.Focus();
            }
        };
        Closing += (_, args) => args.Cancel = _busy;
    }

    private async void OnConfirm(object sender, RoutedEventArgs e)
    {
        _busy = true;
        NameInput.IsReadOnly = true;
        ConfirmButton.IsEnabled = CancelButton.IsEnabled = false;
        ShowFeedback("File_OperationWorking", "SecondaryTextBrush");
        string? error;
        try
        {
            error = _rename ? await _model.RenameCurrentFileAsync(_path, NameInput.Text)
                : await _model.RecycleCurrentFileAsync(_path);
        }
        finally
        {
            _busy = false;
            NameInput.IsReadOnly = false;
            ConfirmButton.IsEnabled = CancelButton.IsEnabled = true;
        }
        if (error is null) { DialogResult = true; }
        else
        {
            ShowFeedback(error, "PrimaryTextBrush");
            if (_rename) { NameInput.Focus(); }
            else { CancelButton.Focus(); }
        }
    }

    private void ShowFeedback(string key, string brush)
    {
        Feedback.Text = Text(key);
        Feedback.SetResourceReference(ForegroundProperty, brush);
        Feedback.Visibility = Visibility.Visible;
    }

    private string Text(string key) => _model.Localization.GetString(key);
}
