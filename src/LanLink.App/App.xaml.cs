using System.Windows;
using LanLink.App.Services;
using LanLink.App.ViewModels;
using LanLink.App.Views;

namespace LanLink.App;

public partial class App : System.Windows.Application
{
    private Mutex? _singleInstance;
    private AppServices? _services;
    private TrayService? _tray;
    private MainWindow? _window;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _singleInstance = new Mutex(true, @"Local\LanLink-SingleInstance", out var isFirst);
        if (!isFirst)
        {
            System.Windows.MessageBox.Show("LanLink est déjà lancé (icône dans la zone de notification).",
                "LanLink", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        _services = new AppServices();
        ThemeManager.Initialize(_services.Settings.Theme);
        _services.StartNetwork();

        _tray = new TrayService(ShowWindow, Quit);
        _services.Tray = _tray;

        _window = new MainWindow(new MainViewModel(_services, Dispatcher));
        MainWindow = _window;
        if (!e.Args.Contains("--minimized")) _window.Show();
    }

    private void ShowWindow()
    {
        if (_window is null) return;
        _window.Show();
        if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
        _window.Activate();
    }

    private void Quit()
    {
        _window?.AllowClose();
        _tray?.Dispose();
        var services = _services;
        if (services is not null) Task.Run(() => services.DisposeAsync().AsTask()).Wait(TimeSpan.FromSeconds(4));
        _singleInstance?.Dispose();
        Shutdown();
    }
}
