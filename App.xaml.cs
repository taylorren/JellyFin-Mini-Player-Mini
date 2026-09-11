using Microsoft.Extensions.DependencyInjection;
using System.Windows;
using RoundSoundMimic.Services;

namespace RoundSoundMimic;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    public ServiceProvider ServiceProvider { get; private set; }

    public App()
    {
        var services = new ServiceCollection();
        
        // Register services
        services.AddSingleton<IJellyfinService, JellyfinService>();
        services.AddSingleton<IAudioProcessingService, AudioProcessingService>();
        services.AddSingleton<ITrayIconService, TrayIconService>();
        services.AddTransient<MainViewModel>();
        services.AddTransient<MainWindow>();  // Register MainWindow
        
        ServiceProvider = services.BuildServiceProvider();
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        // Only allow a single running instance. If another copy is already
        // active, exit immediately without creating a window or tray icon.
        if (!SingleInstanceGuard.Acquire())
        {
            StartupLogger.Log("Startup: another instance is running, exiting.");
            base.OnStartup(e);
            Shutdown();
            return;
        }

        StartupLogger.Log("Startup: acquired single-instance mutex, launching.");

        var mainWindow = ServiceProvider.GetService<MainWindow>();
        mainWindow?.Show();
        
        base.OnStartup(e);
    }
}