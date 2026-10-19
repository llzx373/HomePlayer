using System.Windows.Input;
using HomePlayer.Core;
using H.NotifyIcon;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;

namespace HomePlayer;

public sealed partial class MainWindow : Window
{
    private readonly TaskbarIcon _tray;
    private bool _fullscreen;

    public MainWindow()
    {
        InitializeComponent();
        var work = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest).WorkArea;
        var width = Math.Clamp(work.Width * 3 / 4, 1100, work.Width);
        var height = Math.Clamp(work.Height * 3 / 4, 720, work.Height);
        AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(
            work.X + (work.Width - width) / 2,
            work.Y + (work.Height - height) / 2,
            width,
            height));
        AppWindow.Closing += OnClosing;
        ExtendsContentIntoTitleBar = true;
        Nav.SelectedItem = Nav.MenuItems[0];
        _tray = new TaskbarIcon
        {
            ToolTipText = "HomePlayer",
            ContextMenuMode = ContextMenuMode.SecondWindow
        };
        TrySetTrayIcon();
        var menu = new MenuFlyout();
        menu.Items.Add(Item("打开主窗口", ShowFromTray));
        menu.Items.Add(Item("播放/暂停", () => App.Host.Playback.TogglePlay()));
        menu.Items.Add(Item("下一首", () => _ = App.Host.Playback.NextAsync()));
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(Item("退出", () => _ = ExitAsync()));
        _tray.ContextFlyout = menu;
        _tray.LeftClickCommand = new TrayCommand(ShowFromTray);
        _tray.ForceCreate();
        Root.Children.Add(_tray);
    }

    public void SelectPlayer() => Nav.SelectedItem = Nav.MenuItems[1];

    public void ShowFromTray()
    {
        AppWindow.Show();
        Activate();
    }

    public void SetFullscreen(bool enabled)
    {
        _fullscreen = enabled;
        Nav.IsPaneVisible = !enabled;
        AppWindow.SetPresenter(enabled ? AppWindowPresenterKind.FullScreen : AppWindowPresenterKind.Overlapped);
    }

    private void OnClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (App.Host.Exiting || !App.Host.Settings.CloseToTray)
            return;
        args.Cancel = true;
        AppWindow.Hide();
    }

    private void Nav_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        var tag = (args.SelectedItem as NavigationViewItem)?.Tag as string;
        var page = tag switch
        {
            "player" => typeof(PlayerPage),
            "settings" => typeof(SettingsPage),
            _ => typeof(LibraryPage)
        };
        RootFrame.Navigate(page);
    }

    private async void Root_Drop(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems))
            return;
        var items = await e.DataView.GetStorageItemsAsync();
        var paths = VideoFiles.Expand(items.Select(item => item.Path));
        if (paths.Count == 0)
            return;
        await App.Host.Playback.PlayPathsAsync(paths, 0);
        Nav.SelectedItem = Nav.MenuItems[1];
    }

    private void Root_DragOver(object sender, DragEventArgs e)
    {
        e.AcceptedOperation = DataPackageOperation.Copy;
    }

    private async Task ExitAsync()
    {
        App.Host.Exiting = true;
        _tray.Dispose();
        await App.Host.DisposeAsync();
        Close();
        Environment.Exit(0);
    }

    private sealed class TrayCommand(Action action) : ICommand
    {
        public event EventHandler? CanExecuteChanged
        {
            add { }
            remove { }
        }

        public bool CanExecute(object? parameter) => true;
        public void Execute(object? parameter) => action();
    }

    private static MenuFlyoutItem Item(string text, Action action)
    {
        var item = new MenuFlyoutItem { Text = text };
        item.Click += (_, _) => action();
        return item;
    }

    private void TrySetTrayIcon()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Assets", "App.ico");
        if (!File.Exists(path))
            return;
        try
        {
            _tray.IconSource = new BitmapImage(new Uri(path));
        }
        catch
        {
        }
    }
}
