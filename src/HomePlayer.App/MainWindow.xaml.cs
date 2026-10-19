using System.Runtime.InteropServices;
using System.Windows.Input;
using HomePlayer.Core;
using H.NotifyIcon;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using WinRT.Interop;

namespace HomePlayer;

public sealed partial class MainWindow : Window
{
    private readonly TaskbarIcon _tray;
    private readonly DispatcherTimer _overlayTimer = new() { Interval = TimeSpan.FromMilliseconds(50) };
    private bool _fullscreen;
    private bool _overlayVisible;
    private DateTime _overlayHoldUntil;
    private bool _navOverlayVisible;
    private DateTime _navHoldUntil;
    private string _pageTag = "library";
    private const double NavOverlayWidth = 168;

    public bool IsFullscreen => _fullscreen;

    public event Action? FullscreenChanged;
    public event Action? OverlayTick;

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
        AppWindow.Changed += OnAppWindowChanged;
        Root.SizeChanged += (_, _) =>
        {
            OverlayTitleBar.Width = Root.ActualWidth;
            OverlayNav.Height = Root.ActualHeight;
        };
        _overlayTimer.Tick += (_, _) =>
        {
            UpdateOverlay();
            UpdateNavOverlay();
            OverlayTick?.Invoke();
        };
        Root.Loaded += OnRootLoaded;
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
        menu.Items.Add(Item("下一个", () => _ = App.Host.Playback.NextAsync()));
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

    private void OnRootLoaded(object sender, RoutedEventArgs e)
    {
        Root.Loaded -= OnRootLoaded;
        ApplyTitleBar();
        _overlayTimer.Start();
    }

    public void ApplyTitleBar()
    {
        var overlay = App.Host.Settings.TitleBar == TitleBarMode.Overlay;
        ExtendsContentIntoTitleBar = false;
        if (!_fullscreen && AppWindow.Presenter is OverlappedPresenter presenter
            && (!presenter.HasBorder || presenter.HasTitleBar == overlay))
            presenter.SetBorderAndTitleBar(true, !overlay);
        ApplyNavPane();
        if (!overlay || _fullscreen)
        {
            _overlayHoldUntil = DateTime.MinValue;
            SetOverlayOpen(false);
            OverlayTick?.Invoke();
            return;
        }

        _overlayHoldUntil = DateTime.UtcNow.AddSeconds(2.5);
        SetOverlayOpen(true);
        OverlayTick?.Invoke();
    }

    public void SetFullscreen(bool enabled)
    {
        if (_fullscreen == enabled)
            return;
        _fullscreen = enabled;
        SetOverlayOpen(false);
        AppWindow.SetPresenter(enabled ? AppWindowPresenterKind.FullScreen : AppWindowPresenterKind.Overlapped);
        ApplyNavPane();
        if (!enabled)
            ApplyTitleBar();
        FullscreenChanged?.Invoke();
    }

    private void OnClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        App.Host.Playback.Pause();
        if (App.Host.Exiting || !App.Host.Settings.CloseToTray)
            return;
        args.Cancel = true;
        AppWindow.Hide();
    }

    private void OnAppWindowChanged(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (args.DidVisibilityChange && !sender.IsVisible)
        {
            App.Host.Playback.Pause();
            return;
        }

        if (args.DidPresenterChange && sender.Presenter is OverlappedPresenter presenter
            && presenter.State == OverlappedPresenterState.Minimized)
            App.Host.Playback.Pause();
    }

    private void Nav_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        var tag = (args.SelectedItem as NavigationViewItem)?.Tag as string;
        _pageTag = tag ?? "library";
        var page = _pageTag switch
        {
            "player" => typeof(PlayerPage),
            "settings" => typeof(SettingsPage),
            _ => typeof(LibraryPage)
        };
        RootFrame.Navigate(page);
        ApplyNavPane();
    }

    private void ApplyNavPane()
    {
        if (_fullscreen)
        {
            Nav.IsPaneVisible = false;
            Nav.IsPaneOpen = false;
            _navHoldUntil = DateTime.MinValue;
            SetNavOverlayOpen(false);
            return;
        }

        var player = _pageTag == "player";
        var overlay = App.Host.Settings.TitleBar == TitleBarMode.Overlay;
        if (player && overlay)
        {
            Nav.IsPaneVisible = false;
            Nav.IsPaneOpen = false;
            _navHoldUntil = DateTime.UtcNow.AddSeconds(2.5);
            SetNavOverlayOpen(true);
            return;
        }

        _navHoldUntil = DateTime.MinValue;
        SetNavOverlayOpen(false);
        Nav.IsPaneVisible = true;
        Nav.PaneDisplayMode = player
            ? NavigationViewPaneDisplayMode.LeftCompact
            : NavigationViewPaneDisplayMode.Left;
        Nav.IsPaneOpen = !player;
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

    public bool TryGetClientCursor(out double x, out double y, out double width, out double height, out bool inside)
    {
        x = y = width = height = 0;
        inside = false;
        var hwnd = WindowNative.GetWindowHandle(this);
        if (!GetCursorPos(out var point) || !ScreenToClient(hwnd, ref point) || !GetClientRect(hwnd, out var rect))
            return false;
        var dpi = GetDpiForWindow(hwnd);
        if (dpi == 0)
            dpi = 96;
        var scale = 96.0 / dpi;
        x = point.X * scale;
        y = point.Y * scale;
        width = rect.Right * scale;
        height = rect.Bottom * scale;
        inside = point.X >= 0 && point.Y >= 0 && point.X < rect.Right && point.Y < rect.Bottom;
        return true;
    }

    private void UpdateOverlay()
    {
        if (_fullscreen || App.Host.Settings.TitleBar != TitleBarMode.Overlay || !AppWindow.IsVisible)
        {
            SetOverlayOpen(false);
            return;
        }

        if (AppWindow.Presenter is OverlappedPresenter presenter && presenter.State == OverlappedPresenterState.Minimized)
        {
            SetOverlayOpen(false);
            return;
        }

        if (!TryGetClientCursor(out _, out var dipY, out _, out _, out var inside))
            return;
        if (!inside)
        {
            if (DateTime.UtcNow >= _overlayHoldUntil)
                SetOverlayOpen(false);
            return;
        }

        var nearTop = _overlayVisible ? dipY <= 46 : dipY <= 12;
        SetOverlayOpen(nearTop || DateTime.UtcNow < _overlayHoldUntil);
    }

    private bool WantsNavOverlay() =>
        !_fullscreen
        && _pageTag == "player"
        && App.Host.Settings.TitleBar == TitleBarMode.Overlay
        && AppWindow.IsVisible;

    private void UpdateNavOverlay()
    {
        if (!WantsNavOverlay())
        {
            SetNavOverlayOpen(false);
            return;
        }

        if (AppWindow.Presenter is OverlappedPresenter presenter && presenter.State == OverlappedPresenterState.Minimized)
        {
            SetNavOverlayOpen(false);
            return;
        }

        if (!TryGetClientCursor(out var dipX, out _, out _, out _, out var inside))
            return;
        if (!inside)
        {
            if (DateTime.UtcNow >= _navHoldUntil)
                SetNavOverlayOpen(false);
            return;
        }

        var nearLeft = _navOverlayVisible ? dipX <= NavOverlayWidth + 8 : dipX <= 12;
        SetNavOverlayOpen(nearLeft || DateTime.UtcNow < _navHoldUntil);
    }

    private void SetNavOverlayOpen(bool open)
    {
        if (!open && !_navOverlayVisible && !NavPopup.IsOpen)
            return;
        if (open && !WantsNavOverlay())
            open = false;
        if (_navOverlayVisible == open && NavPopup.IsOpen == open)
            return;
        _navOverlayVisible = open;
        if (open)
        {
            OverlayNav.Height = Root.ActualHeight;
            SyncOverlayNavButtons();
        }

        OpenPopup(NavPopup, open);
    }

    private void SyncOverlayNavButtons()
    {
        MarkNavButton(NavLibraryButton, _pageTag == "library");
        MarkNavButton(NavPlayerButton, _pageTag == "player");
        MarkNavButton(NavSettingsButton, _pageTag == "settings");
    }

    private static void MarkNavButton(Button button, bool selected)
    {
        button.Background = selected
            ? new SolidColorBrush(Windows.UI.Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF))
            : new SolidColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0));
    }

    private void OverlayNav_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string tag })
            return;
        object? item = tag switch
        {
            "library" => Nav.MenuItems[0],
            "player" => Nav.MenuItems[1],
            "settings" => Nav.FooterMenuItems[0],
            _ => null
        };
        if (item != null)
            Nav.SelectedItem = item;
    }

    private void SetOverlayOpen(bool open)
    {
        if (_overlayVisible == open && TitlePopup.IsOpen == open)
            return;
        _overlayVisible = open;
        if (open)
        {
            OverlayTitleBar.Width = Root.ActualWidth;
            UpdateMaximizeIcon();
        }

        OpenPopup(TitlePopup, open);
    }

    private void OpenPopup(Popup popup, bool open)
    {
        popup.XamlRoot ??= Root.XamlRoot ?? Content?.XamlRoot;
        if (open && popup.XamlRoot == null)
            return;
        popup.IsOpen = open;
    }

    private void UpdateMaximizeIcon()
    {
        var maximized = AppWindow.Presenter is OverlappedPresenter presenter
            && presenter.State == OverlappedPresenterState.Maximized;
        MaximizeIcon.Glyph = maximized ? "\uE923" : "\uE922";
    }

    private void OverlayDrag_Pressed(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(OverlayDrag);
        if (!point.Properties.IsLeftButtonPressed)
            return;
        var hwnd = WindowNative.GetWindowHandle(this);
        ReleaseCapture();
        SendMessage(hwnd, 0xA1, (IntPtr)2, IntPtr.Zero);
        e.Handled = true;
    }

    private void OverlayDrag_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        ToggleMaximize();
        e.Handled = true;
    }

    private void Minimize_Click(object sender, RoutedEventArgs e)
    {
        App.Host.Playback.Pause();
        if (AppWindow.Presenter is OverlappedPresenter presenter)
            presenter.Minimize();
    }

    private void Maximize_Click(object sender, RoutedEventArgs e) => ToggleMaximize();

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void ToggleMaximize()
    {
        if (AppWindow.Presenter is not OverlappedPresenter presenter)
            return;
        if (presenter.State == OverlappedPresenterState.Maximized)
            presenter.Restore();
        else
            presenter.Maximize();
        UpdateMaximizeIcon();
    }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out PointNative point);

    [DllImport("user32.dll")]
    private static extern bool ScreenToClient(IntPtr hwnd, ref PointNative point);

    [DllImport("user32.dll")]
    private static extern bool GetClientRect(IntPtr hwnd, out RectNative rect);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool ReleaseCapture();

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct PointNative
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RectNative
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
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
