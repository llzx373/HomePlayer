using HomePlayer.Core;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using System.Diagnostics;

namespace HomePlayer;

public partial class App : Application
{
    private MainWindow? _window;

    public static AppHost Host { get; private set; } = null!;
    public static MainWindow Window { get; private set; } = null!;

    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        if (!ClaimInstance())
            return;

        AppPaths.Ensure();
        var queue = DispatcherQueue.GetForCurrentThread();
        Host = AppHost.Create(action => queue.TryEnqueue(() => action()));
        _window = new MainWindow();
        Window = _window;
        _window.Activate();
    }

    private bool ClaimInstance()
    {
        try
        {
            var instance = AppInstance.FindOrRegisterForKey("HomePlayer.SingleInstance");
            if (instance.IsCurrent)
            {
                instance.Activated += (_, _) => Window?.DispatcherQueue.TryEnqueue(Window.ShowFromTray);
                return true;
            }

            var current = AppInstance.GetCurrent().GetActivatedEventArgs();
            instance.RedirectActivationToAsync(current).AsTask().GetAwaiter().GetResult();
            Process.GetCurrentProcess().Kill();
            return false;
        }
        catch
        {
            return true;
        }
    }
}
