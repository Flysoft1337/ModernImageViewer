using System.ComponentModel;
using System.IO;
using System.Threading.Channels;
using System.Windows.Threading;

using ModernImageViewer.Application.Browsing;
using ModernImageViewer.UI.ViewModels;

namespace ModernImageViewer.UI;

public sealed class FolderChangeMonitor : IDisposable
{
    private const int MaximumPendingChanges = 256;
    private static readonly TimeSpan ChangeDelay = TimeSpan.FromMilliseconds(250);
    private CancellationTokenSource? _lifetime;
    private Task _watching = Task.CompletedTask;
    private string? _directory;

    internal void Update(MainWindowViewModel model, Dispatcher dispatcher, bool retry = false)
    {
        string? directory = model.WatchedDirectory;
        if (StringComparer.OrdinalIgnoreCase.Equals(directory, _directory) && (!retry || !_watching.IsCompleted))
        {
            return;
        }
        Dispose();
        if (directory is null) { return; }

        _directory = directory;
        _lifetime = new CancellationTokenSource();
        CancellationToken token = _lifetime.Token;
        _watching = Task.Run(() => WatchAsync(directory, model, dispatcher, token), token);
    }

    public void Dispose()
    {
        _directory = null;
        _lifetime?.Cancel();
        _lifetime?.Dispose();
        _lifetime = null;
    }

    private static async Task WatchAsync(string directory, MainWindowViewModel model, Dispatcher dispatcher,
        CancellationToken token)
    {
        try
        {
            Channel<FolderChange> changes = Channel.CreateBounded<FolderChange>(new BoundedChannelOptions(MaximumPendingChanges)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
            });
            using FileSystemWatcher watcher = new(directory) { NotifyFilter = NotifyFilters.FileName };
            void OnFileNameChanged(object sender, FileSystemEventArgs e)
            {
                if (ImageBrowseSession.IsSupported(e.FullPath)) { changes.Writer.TryWrite(default); }
            }
            watcher.Created += OnFileNameChanged;
            watcher.Deleted += OnFileNameChanged;
            watcher.Renamed += (_, e) => changes.Writer.TryWrite(new(
                e.Name is not null && e.OldName is not null ? new BrowseFileRename(e.OldFullPath, e.FullPath) : null));
            watcher.Error += (_, e) => changes.Writer.TryWrite(new(Rescan: true,
                Stopped: e.GetException() is not InternalBufferOverflowException));
            token.ThrowIfCancellationRequested();
            watcher.EnableRaisingEvents = true;
            changes.Writer.TryWrite(default); // Reconcile changes between the first directory scan and enabling the watcher.

            List<BrowseFileRename> renames = [];
            while (await changes.Reader.WaitToReadAsync(token).ConfigureAwait(false))
            {
                bool stopped = false;
                while (true)
                {
                    for (int i = 0; i < MaximumPendingChanges && changes.Reader.TryRead(out FolderChange change); i++)
                    {
                        stopped |= change.Stopped;
                        if (change.Rescan || renames.Count == MaximumPendingChanges) { renames.Clear(); }
                        if (change.Rename is { } rename) { renames.Add(rename); }
                    }
                    await Task.Delay(ChangeDelay, token).ConfigureAwait(false);
                    if (changes.Reader.TryPeek(out _)) { continue; }

                    bool consumed = await dispatcher.InvokeAsync(
                        () => model.RefreshWatchedDirectoryAsync(directory, renames, () => !changes.Reader.TryPeek(out _), token),
                        DispatcherPriority.Background, token).Task.Unwrap().ConfigureAwait(false);
                    if (consumed) { break; }
                }
                if (stopped) { break; }
                renames.Clear();
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested || dispatcher.HasShutdownStarted)
        {
            return;
        }
        catch (Exception exception) when (exception is IOException or Win32Exception or UnauthorizedAccessException
            or ArgumentException or NotSupportedException or System.Security.SecurityException)
        {
            // Some locations cannot be watched. Manual refresh still uses the ordinary directory scan.
        }
        if (!token.IsCancellationRequested && !dispatcher.HasShutdownStarted)
        {
            await dispatcher.InvokeAsync(() =>
            {
                if (!token.IsCancellationRequested && StringComparer.OrdinalIgnoreCase.Equals(directory, model.WatchedDirectory))
                {
                    model.ShowMessage("FolderWatch_Unavailable");
                }
            }, DispatcherPriority.Background, token).Task.ConfigureAwait(false);
        }
    }

    private readonly record struct FolderChange(BrowseFileRename? Rename = null, bool Rescan = false, bool Stopped = false);
}
