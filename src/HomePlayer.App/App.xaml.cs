using System.Threading;
using HomePlayer.Core;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using Windows.ApplicationModel.Activation;

namespace HomePlayer;

public partial class App : Application
{
    private MainWindow? _window;
    private AppInstance? _instance;
    private int _shellBatch;

    public static AppHost Host { get; private set; } = null!;
    public static MainWindow Window { get; private set; } = null!;

    public App()
    {
        CrashGuard.Attach(this);
        InitializeComponent();
    }

    protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        if (!ClaimInstance())
            return;

        AppPaths.Ensure();
        CrashGuard.StartSession();
        var queue = DispatcherQueue.GetForCurrentThread();
        Host = AppHost.Create(action => queue.TryEnqueue(() => action()));
        var initial = IncomingPaths(AppInstance.GetCurrent().GetActivatedEventArgs(), true);
        _window = new MainWindow(initial.Count > 0);
        Window = _window;
        _window.Activate();
        Host.StartLibraries();
        if (_instance != null)
            _instance.Activated += (_, e) => queue.TryEnqueue(() => HandleActivation(e, false));
        if (initial.Count > 0)
            HandleIncomingFiles(initial);
    }

    private bool ClaimInstance()
    {
        try
        {
            _instance = AppInstance.FindOrRegisterForKey("HomePlayer.SingleInstance");
            if (_instance.IsCurrent)
                return true;

            AppPaths.Ensure();
            WriteInbox(CollectPaths(AppInstance.GetCurrent().GetActivatedEventArgs(), true));
            _instance.RedirectActivationToAsync(AppInstance.GetCurrent().GetActivatedEventArgs()).AsTask().GetAwaiter().GetResult();
            Environment.Exit(0);
            return false;
        }
        catch
        {
            return true;
        }
    }

    private void HandleActivation(AppActivationArguments? args, bool includeCommandLine)
    {
        try
        {
            Window.ShowFromTray();
            var paths = IncomingPaths(args, includeCommandLine);
            if (paths.Count == 0)
                return;
            HandleIncomingFiles(paths);
        }
        catch (Exception)
        {
        }
    }

    private static List<string> IncomingPaths(AppActivationArguments? args, bool includeCommandLine)
    {
        try
        {
            var paths = CollectPaths(args, includeCommandLine);
            paths.AddRange(DrainInbox());
            return VideoFiles.Expand(paths);
        }
        catch (Exception)
        {
            return [];
        }
    }

    private void HandleIncomingFiles(List<string> paths)
    {
        var batch = Interlocked.Increment(ref _shellBatch);
        _ = PlayIncomingAsync(paths, batch);
    }

    private async Task PlayIncomingAsync(List<string> paths, int batch)
    {
        try
        {
            await Window.PlayIncomingFilesAsync(paths, batch == 1);
        }
        finally
        {
            _ = ResetShellBatchAsync(batch);
        }
    }

    private async Task ResetShellBatchAsync(int batch)
    {
        await Task.Delay(800);
        if (Volatile.Read(ref _shellBatch) == batch)
            Interlocked.Exchange(ref _shellBatch, 0);
    }

    private static List<string> CollectPaths(AppActivationArguments? args, bool includeCommandLine)
    {
        var raw = new List<string>();
        string? launch = null;
        try
        {
            if (args?.Kind == ExtendedActivationKind.Launch && args.Data is ILaunchActivatedEventArgs launchArgs)
                launch = launchArgs.Arguments;
            else if (args?.Kind == ExtendedActivationKind.File && args.Data is IFileActivatedEventArgs fileArgs)
            {
                foreach (var item in fileArgs.Files)
                    raw.Add(item.Path);
            }
        }
        catch (Exception)
        {
        }

        var commandLine = includeCommandLine ? Environment.GetCommandLineArgs() : [];
        raw.AddRange(LaunchArgs.Paths(commandLine, launch));
        return raw;
    }

    private static void WriteInbox(IReadOnlyList<string> paths)
    {
        var files = VideoFiles.Expand(paths);
        if (files.Count == 0)
            return;
        Directory.CreateDirectory(InboxDir);
        File.WriteAllLines(Path.Combine(InboxDir, $"{Guid.NewGuid():N}.txt"), files);
    }

    private static List<string> DrainInbox()
    {
        var result = new List<string>();
        if (!Directory.Exists(InboxDir))
            return result;
        foreach (var file in Directory.GetFiles(InboxDir, "*.txt"))
        {
            try
            {
                result.AddRange(File.ReadAllLines(file));
                File.Delete(file);
            }
            catch (Exception)
            {
            }
        }

        return result;
    }

    private static string InboxDir => Path.Combine(AppPaths.Root, "inbox");
}
