using System.Windows;
using System.Windows.Threading;

using Microsoft.Extensions.DependencyInjection;

using ModernImageViewer.Application;
using ModernImageViewer.Codecs;
using ModernImageViewer.Metadata;
using ModernImageViewer.Platform;
using ModernImageViewer.UI;
using ModernImageViewer.UI.Localization;
using ModernImageViewer.UI.ViewModels;

namespace ModernImageViewer.App;

public partial class App : System.Windows.Application
{
    private ServiceProvider? _services;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ServiceCollection services = new();
        ConfigureServices(services);
        _services = services.BuildServiceProvider();
        _services.GetRequiredService<ILocalizationService>().Initialize();

        MainWindow mainWindow = _services.GetRequiredService<MainWindow>();
        MainWindow = mainWindow;
        mainWindow.Show();

        if (e.Args.FirstOrDefault() is { Length: > 0 } path)
        {
            // Let the first layout/render run before scheduling file work.
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            await _services.GetRequiredService<MainWindowViewModel>().OpenInputAsync(path);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _services?.Dispose();
        base.OnExit(e);
    }

    public static void ConfigureServices(IServiceCollection services)
    {
        services.AddApplication().AddCodecs().AddMetadata().AddPlatform().AddUserInterface();
    }
}
