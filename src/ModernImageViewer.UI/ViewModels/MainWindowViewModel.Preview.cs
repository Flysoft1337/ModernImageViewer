using ModernImageViewer.Imaging;

namespace ModernImageViewer.UI.ViewModels;

public sealed partial class MainWindowViewModel
{
    private string? _navigationTarget;
    public event EventHandler? BrowsingCacheInvalidated;
    public event EventHandler? BrowsingOpenRequested;
    private void NotifyBrowsingOpenRequested() => BrowsingOpenRequested?.Invoke(this, EventArgs.Empty);
    public bool IsNavigationReady => HasImage && !_isIndexingFolder && !_browseSession.IsIndexing && !IsSorting;
    private void InvalidateBrowsingCache() => BrowsingCacheInvalidated?.Invoke(this, EventArgs.Empty);
    private int NavigationIndex
    {
        get
        {
            if (_navigationTarget is { } pending)
            {
                for (int index = 0; index < _browseSession.Count; index++)
                {
                    if (StringComparer.OrdinalIgnoreCase.Equals(_browseSession.Items[index], pending)) { return index; }
                }
            }
            return _browseSession.CurrentIndex;
        }
    }

    public long CurrentRequestId => _coordinator.RequestVersion;
    internal long CachedNeighborBytes => _coordinator.CachedNeighborBytes;
    public void SetPreviewTarget(PixelSize target) => _coordinator.SetPreviewTarget(target);
    public Task<bool> UpgradePreviewAsync(PixelSize target, ViewOrientation orientation) => _disposed
        ? Task.FromResult(false) : _coordinator.UpgradePreviewAsync(target, orientation);
}
