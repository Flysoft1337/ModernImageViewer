using System.Windows;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

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
    private static readonly Action<ILogger, Exception?> LogStarting =
        LoggerMessage.Define(LogLevel.Information, new EventId(1, nameof(LogStarting)), "Modern Image Viewer is starting.");

    private static readonly Action<ILogger, Exception?> LogStopping =
        LoggerMessage.Define(LogLevel.Information, new EventId(2, nameof(LogStopping)), "Modern Image Viewer is stopping.");

    private IHost? _host;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        HostApplicationBuilder builder = Host.CreateApplicationBuilder(e.Args);
        builder.Logging.ClearProviders();
        builder.Logging.AddDebug();

        ConfigureServices(builder.Services);

        _host = builder.Build();
        await _host.StartAsync();

        ILogger<App> logger = _host.Services.GetRequiredService<ILogger<App>>();
        LogStarting(logger, null);

        ILocalizationService localizationService = _host.Services.GetRequiredService<ILocalizationService>();
        localizationService.Initialize();

        MainWindow mainWindow = _host.Services.GetRequiredService<MainWindow>();
        MainWindow = mainWindow;
        mainWindow.Show();

        if (e.Args.FirstOrDefault() is { Length: > 0 } path)
        {
            MainWindowViewModel viewModel = _host.Services.GetRequiredService<MainWindowViewModel>();
            await viewModel.OpenPathAsync(path);
        }
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        if (_host is not null)
        {
            ILogger<App> logger = _host.Services.GetRequiredService<ILogger<App>>();
            LogStopping(logger, null);

            await _host.StopAsync();
            _host.Dispose();
        }

        base.OnExit(e);
    }

    public static void ConfigureServices(IServiceCollection services)
    {
        services
            .AddApplication()
            .AddCodecs()
            .AddMetadata()
            .AddPlatform()
            .AddUserInterface();
    }
}
