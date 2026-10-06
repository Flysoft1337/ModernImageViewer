using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Security;
using System.Windows;
using System.Windows.Threading;

using Microsoft.Extensions.DependencyInjection;

using ModernImageViewer.Application;
using ModernImageViewer.Application.Images;
using ModernImageViewer.Codecs;
using ModernImageViewer.Metadata;
using ModernImageViewer.Platform;
using ModernImageViewer.Platform.Activation;
using ModernImageViewer.UI;
using ModernImageViewer.UI.Localization;
using ModernImageViewer.UI.Observations;
using ModernImageViewer.UI.ViewModels;

namespace ModernImageViewer.App;

[SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable",
    Justification = "WPF owns the application lifetime; OnExit disposes resources on the owning UI thread.")]
public partial class App : System.Windows.Application
{
    private readonly object _activationGate = new();
    private ServiceProvider? _services;
    private SingleInstanceService? _instance;
    private string[]? _pendingPaths;
    private bool _bringToFront;
    private bool _activationScheduled;
    private bool _exiting;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        if (e.Args.Length > OpenRequest.MaximumPaths)
        {
            ShowStartupFailure("Input_TooManyFiles");
            Shutdown(1);
            return;
        }
        try
        {
            // Resolve relative arguments in the launching process, before IPC changes
            // their working directory. Empty invalid entries retain normal UI feedback.
            string[] paths = e.Args.Select(path =>
            {
                OpenRequest request = OpenRequest.Create([path]);
                return request.Paths.Count > 0 ? request.Paths[0] : string.Empty;
            }).ToArray();
            _instance = new SingleInstanceService();
            if (!_instance.TryAcquirePrimary())
            {
                bool accepted = await _instance.SendAsync(paths);
                if (!accepted)
                {
                    ShowStartupFailure("Activation_Failed");
                }
                Shutdown(accepted ? 0 : 1);
                return;
            }

            ServiceCollection services = new();
            ConfigureServices(services);
            _services = services.BuildServiceProvider();
            _services.GetRequiredService<ILocalizationService>().Initialize();
            BrowsingObservationOptions.Current = BrowsingObservationOptions.FromEnvironment();
            MainWindow mainWindow = _services.GetRequiredService<MainWindow>();
            MainWindow = mainWindow;
            mainWindow.Show();
            ShutdownMode = ShutdownMode.OnMainWindowClose;

            QueueActivation(paths, bringToFront: false);
            _instance.StartListening(received =>
            {
                QueueActivation(received, bringToFront: true);
                return Task.CompletedTask;
            });
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or SecurityException or InvalidOperationException or ArgumentException or Win32Exception)
        {
            ShowStartupFailure("Activation_StartFailed");
            Shutdown(1);
        }
    }

    private void QueueActivation(string[] paths, bool bringToFront)
    {
        lock (_activationGate)
        {
            if (_exiting || Dispatcher.HasShutdownStarted)
            {
                throw new InvalidOperationException("The application is closing.");
            }
            // Coalesce requests while layout or the dispatcher is busy. A no-path
            // activation restores the window without dropping an accepted file request.
            if (paths.Length > 0 || _pendingPaths is null)
            {
                _pendingPaths = paths;
            }
            _bringToFront |= bringToFront;
            if (!_activationScheduled)
            {
                _activationScheduled = true;
                Dispatcher.BeginInvoke(DrainActivation, DispatcherPriority.ContextIdle);
            }
        }
    }

    private void DrainActivation()
    {
        string[]? paths;
        bool activate;
        lock (_activationGate)
        {
            paths = _pendingPaths;
            activate = _bringToFront;
            _pendingPaths = null;
            _bringToFront = false;
            _activationScheduled = false;
            if (_exiting)
            {
                return;
            }
        }
        if (activate && MainWindow is MainWindow window)
        {
            window.ActivateForExternalOpen();
        }
        if (paths is { Length: > 0 })
        {
            _ = OpenActivationAsync(paths);
        }
    }

    private async Task OpenActivationAsync(string[] paths)
    {
        if (_services is null)
        {
            return;
        }
        MainWindowViewModel viewModel = _services.GetRequiredService<MainWindowViewModel>();
        try
        {
            await viewModel.OpenInputsAsync(paths);
        }
        catch (ObjectDisposedException) when (_exiting)
        {
            // A queued request may overlap the main window closing.
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or ArgumentException or NotSupportedException)
        {
            if (!_exiting)
            {
                viewModel.ShowMessage("Input_NoSupportedFiles");
            }
        }
    }

    private void ShowStartupFailure(string key)
    {
        ILocalizationService? localization = _services?.GetService<ILocalizationService>();
        bool chinese = CultureInfo.CurrentUICulture.Name.StartsWith("zh", StringComparison.OrdinalIgnoreCase);
        string message = localization?.GetString(key) ?? (key == "Input_TooManyFiles"
            ? chinese ? "每次最多选择 128 张图片。" : "Select up to 128 images at a time."
            : chinese ? "应用无法接收此次请求。请重试；若已有窗口无响应，请关闭后重新打开。"
                : "The application could not receive this request. Try again, or close any unresponsive window.");
        MessageBox.Show(message, localization?.GetString("MainWindow_Title") ?? "Modern Image Viewer",
            MessageBoxButton.OK, MessageBoxImage.Information);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        lock (_activationGate)
        {
            _exiting = true;
            _pendingPaths = null;
        }
        _instance?.Dispose();
        _services?.Dispose();
        base.OnExit(e);
    }

    public static void ConfigureServices(IServiceCollection services)
    {
        services.AddApplication().AddCodecs().AddMetadata().AddPlatform().AddUserInterface();
    }
}
