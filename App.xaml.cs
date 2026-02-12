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
        var mainWindow = ServiceProvider.GetService<MainWindow>();
        mainWindow?.Show();
        
        base.OnStartup(e);
    }
}