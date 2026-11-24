using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;

using ModernImageViewer.Application.Images;
using ModernImageViewer.UI.ViewModels;

namespace ModernImageViewer.UI;

public partial class MainWindow
{
    private Guid? _announcedImage;
    private string _announcedBrowsingStatus = string.Empty;

    private void CycleBrowsingFocus()
    {
        if (Viewport.IsKeyboardFocusWithin && Filmstrip.IsVisible)
        {
            BrowseItem? current = _viewModel.BrowseItems.FirstOrDefault(item => item.IsCurrent);
            // Revealing fullscreen chrome can make the item containers need a layout pass.
            FilmstripItems.UpdateLayout();
            if (current is not null && FilmstripItems.ItemContainerGenerator.ContainerFromItem(current) is ContentPresenter presenter
                && presenter.ContentTemplate.FindName("FilmstripThumbnail", presenter) is Button button && button.Focus())
            {
                button.BringIntoView();
                return;
            }
        }
        if ((Viewport.IsKeyboardFocusWithin || Filmstrip.IsKeyboardFocusWithin)
            && InformationFileName.IsVisible && InformationFileName.Focus())
        {
            InformationFileName.BringIntoView();
            return;
        }
        Viewport.Focus();
    }

    private void AnnounceBrowsingChange()
    {
        if (!IsLoaded || !IsActive || !AutomationPeer.ListenerExists(AutomationEvents.Notification)) { return; }

        ImageOpenState state = _viewModel.Presentation;
        AutomationPeer? peer = UIElementAutomationPeer.CreatePeerForElement(this);
        if (state.Status == ImageOpenStatus.Loaded && state.Source is { } source && _announcedImage != source.Identity)
        {
            _announcedImage = source.Identity;
            peer?.RaiseNotificationEvent(AutomationNotificationKind.Other, AutomationNotificationProcessing.MostRecent,
                _viewModel.ImageAnnouncement, "ImageOpened");
        }
        else if (state.Status == ImageOpenStatus.Empty)
        {
            _announcedImage = null;
        }

        string status = _viewModel.BrowsingStatusAnnouncement;
        if (!string.Equals(_announcedBrowsingStatus, status, StringComparison.Ordinal))
        {
            _announcedBrowsingStatus = status;
            if (status.Length > 0)
            {
                peer?.RaiseNotificationEvent(AutomationNotificationKind.Other, AutomationNotificationProcessing.ImportantMostRecent,
                    status, "BrowsingStatus");
            }
        }
    }
}
