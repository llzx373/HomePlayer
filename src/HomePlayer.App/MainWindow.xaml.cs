using System.Runtime.InteropServices;
using System.Windows.Input;
using HomePlayer.Core;
using H.NotifyIcon;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.ApplicationModel.DataTransfer;
using WinRT.Interop;

namespace HomePlayer;

public sealed partial class MainWindow : Window
{
    private readonly TaskbarIcon _tray;
    private readonly DispatcherTimer _overlayTimer = new() { Interval = TimeSpan.FromMilliseconds(50) };
    private bool _fullscreen;
    private bool _alwaysOnTop;
    private int _fitRestoreTries;
    private bool _overlayVisible;
    private DateTime _overlayHoldUntil;
    private string _pageTag = "library";
    private uint _captionClickTime;
    private int _captionClickX;
    private int _captionClickY;
    private const double PlayerTitleHeight = 40;
    private const uint WmNcLButtonDown = 0x00A1;
    private const uint WmSysCommand = 0x0112;
    private const int HtCaption = 2;
    private const int SmCxDoubleClk = 36;
    private const int SmCyDoubleClk = 37;
    private const uint TpmReturnCmd = 0x0100;
    private const uint TpmRightButton = 0x0002;
    private const int SwRestore = 9;

    public bool IsFullscreen => _fullscreen;

    public event Action? FullscreenChanged;
    public event Action? OverlayTick;
    public event Action? SuspendRendering;
    public event Action? ResumeRendering;

    public MainWindow()
    {
        InitializeComponent();
        ApplyWindowChrome();
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
        _overlayTimer.Tick += (_, _) =>
        {
            try
            {
                UpdateOverlay();
            }
            catch (Exception ex)
            {
                CrashLog.Write(CrashLog.UnhandledKind, ex.ToString());
            }

            RaiseOverlayTick();
        };
        Root.Loaded += OnRootLoaded;
        SetAlwaysOnTop(App.Host.Settings.AlwaysOnTop);
        Nav.SelectedItem = Nav.MenuItems[0];
        _tray = new TaskbarIcon
        {
            ToolTipText = "HomePlayer",
            ContextMenuMode = ContextMenuMode.SecondWindow,
            NoLeftClickDelay = true
        };
        TrySetTrayIcon();
        var menu = new MenuFlyout();
        menu.Items.Add(Item("打开主窗口", ShowFromTray));
        menu.Items.Add(Item("播放/暂停", () => App.Host.Playback.TogglePlay()));
        menu.Items.Add(Item("下一个", () => _ = App.Host.Playback.NextAsync()));
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(Item("退出", () => _ = ExitAsync()));
        _tray.ContextFlyout = menu;
        var show = new TrayCommand(ShowFromTray);
        _tray.LeftClickCommand = show;
        _tray.DoubleClickCommand = show;
        _tray.ForceCreate();
        _tray.IsHitTestVisible = false;
        Grid.SetRowSpan(_tray, 2);
        Root.Children.Add(_tray);
    }

    public void SelectPlayer() => Nav.SelectedItem = Nav.MenuItems[1];

    public void ShowFromTray()
    {
        if (!TryRunOnUi(ShowFromTray))
            return;
        if (AppWindow.Presenter is OverlappedPresenter presenter
            && presenter.State == OverlappedPresenterState.Minimized)
            presenter.Restore();
        AppWindow.Show();
        Activate();
        var hwnd = WindowNative.GetWindowHandle(this);
        ShowWindow(hwnd, SwRestore);
        SetForegroundWindow(hwnd);
    }

    public async Task PlayIncomingFilesAsync(IReadOnlyList<string> paths, bool replace)
    {
        if (paths.Count == 0)
            return;
        if (replace)
            await App.Host.Playback.PlayPathsAsync(paths, 0);
        else
            App.Host.Playback.Enqueue(paths);
        SelectPlayer();
    }

    private void OnRootLoaded(object sender, RoutedEventArgs e)
    {
        Root.Loaded -= OnRootLoaded;
        ApplyTitleBar();
        SetAlwaysOnTop(App.Host.Settings.AlwaysOnTop);
        UpdateOverlayTimer();
    }

    private void UpdateOverlayTimer()
    {
        var minimized = AppWindow.Presenter is OverlappedPresenter presenter
            && presenter.State == OverlappedPresenterState.Minimized;
        var needs = AppWindow.IsVisible
            && !minimized
            && _pageTag == "player"
            && (_fullscreen || App.Host.Settings.TitleBar == TitleBarMode.Overlay);
        if (needs)
        {
            if (!_overlayTimer.IsEnabled)
                _overlayTimer.Start();
        }
        else
        {
            _overlayTimer.Stop();
        }
    }

    public void ApplyTitleBar()
    {
        var player = _pageTag == "player";
        ApplyWindowChrome();
        AppTitleBar.Visibility = player ? Visibility.Collapsed : Visibility.Visible;
        ApplyNavPane();
        SyncChromeButtons();
        if (_fullscreen || !player)
        {
            _overlayHoldUntil = DateTime.MinValue;
            SetOverlayOpen(false);
            RaiseOverlayTick();
            UpdateOverlayTimer();
            return;
        }

        _overlayHoldUntil = WantsPlayerOverlay() ? DateTime.UtcNow.AddSeconds(2.5) : DateTime.MaxValue;
        SetOverlayOpen(true);
        RaiseOverlayTick();
        UpdateOverlayTimer();
    }

    private void RaiseOverlayTick()
    {
        try
        {
            OverlayTick?.Invoke();
        }
        catch (Exception ex)
        {
            CrashLog.Write(CrashLog.UnhandledKind, ex.ToString());
        }
    }

    private void ApplyWindowChrome()
    {
        if (_fullscreen)
            return;
        try
        {
            if (AppWindow.Presenter is OverlappedPresenter presenter
                && (!presenter.HasBorder || presenter.HasTitleBar))
                presenter.SetBorderAndTitleBar(true, false);
        }
        catch
        {
        }
    }

    private bool WantsPlayerOverlay() =>
        App.Host.Settings.TitleBar == TitleBarMode.Overlay && _pageTag == "player";

    public void SetAlwaysOnTop(bool enabled)
    {
        _alwaysOnTop = enabled;
        if (AppWindow.Presenter is OverlappedPresenter presenter)
            presenter.IsAlwaysOnTop = enabled;
    }

    public void SetFullscreen(bool enabled)
    {
        if (_fullscreen == enabled)
            return;
        if (enabled && AppWindow.Presenter is OverlappedPresenter overlapped)
            _alwaysOnTop = overlapped.IsAlwaysOnTop;
        _fullscreen = enabled;
        SetOverlayOpen(false);
        AppWindow.SetPresenter(enabled ? AppWindowPresenterKind.FullScreen : AppWindowPresenterKind.Overlapped);
        ApplyNavPane();
        if (!enabled)
        {
            ApplyTitleBar();
            SetAlwaysOnTop(App.Host.Settings.AlwaysOnTop);
        }
        UpdateOverlayTimer();
        FullscreenChanged?.Invoke();
    }

    private void OnClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        App.Host.Playback.Pause();
        if (App.Host.Exiting)
            return;
        args.Cancel = true;
        if (App.Host.Settings.CloseToTray)
        {
            SuspendRendering?.Invoke();
            AppWindow.Hide();
            return;
        }

        _ = ExitAsync();
    }

    private void OnAppWindowChanged(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (args.DidVisibilityChange)
        {
            if (!sender.IsVisible)
            {
                App.Host.Playback.Pause();
                SuspendRendering?.Invoke();
            }
            else
            {
                QueueResumeRendering();
            }

            UpdateOverlayTimer();
            UpdateMaximizeIcon();
            return;
        }

        if (args.DidPresenterChange && sender.Presenter is OverlappedPresenter presenter)
        {
            if (presenter.State == OverlappedPresenterState.Minimized)
            {
                App.Host.Playback.Pause();
                SuspendRendering?.Invoke();
            }
            else
            {
                QueueResumeRendering();
            }
        }

        ApplyWindowChrome();
        UpdateOverlayTimer();
        UpdateMaximizeIcon();
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
        ApplyTitleBar();
    }

    private void ApplyNavPane()
    {
        Nav.IsPaneVisible = false;
        Nav.IsPaneOpen = false;
        Nav.IsPaneToggleButtonVisible = false;
        Nav.PaneDisplayMode = NavigationViewPaneDisplayMode.Left;
        Nav.Margin = _pageTag == "player" && !_fullscreen && !WantsPlayerOverlay()
            ? new Thickness(0, PlayerTitleHeight, 0, 0)
            : new Thickness(0);
    }

    private async void Root_Drop(object sender, DragEventArgs e)
    {
        try
        {
            if (!e.DataView.Contains(StandardDataFormats.StorageItems))
                return;
            var items = await e.DataView.GetStorageItemsAsync();
            var itemPaths = items.Select(item => item.Path).ToList();
            var paths = await Task.Run(() => VideoFiles.Expand(itemPaths));
            if (paths.Count == 0)
                return;
            await App.Host.Playback.PlayPathsAsync(paths, 0);
            Nav.SelectedItem = Nav.MenuItems[1];
        }
        catch (Exception)
        {
        }
    }

    private void Root_DragOver(object sender, DragEventArgs e)
    {
        e.AcceptedOperation = DataPackageOperation.Copy;
    }

    private async Task ExitAsync()
    {
        App.Host.Exiting = true;
        try
        {
            _tray.Dispose();
            await App.Host.DisposeAsync();
        }
        catch (Exception)
        {
        }
        finally
        {
            CrashGuard.StopSession();
            try
            {
                Close();
            }
            catch
            {
            }

            Environment.Exit(0);
        }
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

    private void QueueResumeRendering()
    {
        var queue = Root.DispatcherQueue;
        if (queue == null)
        {
            ResumeRendering?.Invoke();
            return;
        }

        queue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => ResumeRendering?.Invoke());
    }

    private MenuFlyoutItem Item(string text, Action action)
    {
        var item = new MenuFlyoutItem { Text = text };
        item.Click += (_, _) => RunOnUi(action);
        return item;
    }

    private void RunOnUi(Action action)
    {
        if (TryRunOnUi(action))
            action();
    }

    private bool TryRunOnUi(Action action)
    {
        var queue = Root.DispatcherQueue;
        if (queue == null || queue.HasThreadAccess)
            return true;
        queue.TryEnqueue(() => action());
        return false;
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

    private bool WantsCustomTitleBar() =>
        !_fullscreen && _pageTag == "player";

    private void UpdateOverlay()
    {
        if (!WantsCustomTitleBar() || !AppWindow.IsVisible)
        {
            SetOverlayOpen(false);
            return;
        }

        if (AppWindow.Presenter is OverlappedPresenter presenter && presenter.State == OverlappedPresenterState.Minimized)
        {
            SetOverlayOpen(false);
            return;
        }

        if (!WantsPlayerOverlay())
        {
            SetOverlayOpen(true);
            return;
        }

        if (!TryGetClientCursor(out var dipX, out var dipY, out var width, out _, out _))
            return;

        var band = OverlayTitleBar.Height > 0 ? OverlayTitleBar.Height : PlayerTitleHeight;
        var alongTop = dipY >= -16 && dipY <= band
            && dipX >= -16 && dipX <= width + 16;
        SetOverlayOpen(alongTop || DateTime.UtcNow < _overlayHoldUntil);
    }

    private void TitleLibrary_Click(object sender, RoutedEventArgs e) =>
        Nav.SelectedItem = Nav.MenuItems[0];

    private void TitlePlayer_Click(object sender, RoutedEventArgs e) => SelectPlayer();

    private void TitleSettings_Click(object sender, RoutedEventArgs e) =>
        Nav.SelectedItem = Nav.FooterMenuItems[0];

    private void SyncChromeButtons()
    {
        SetChromeSelected(BarLibraryButton, _pageTag == "library", false);
        SetChromeSelected(BarPlayerButton, _pageTag == "player", false);
        SetChromeSelected(BarSettingsButton, _pageTag == "settings", false);
        SetChromeSelected(TitleLibraryButton, _pageTag == "library", true);
        SetChromeSelected(TitlePlayerButton, _pageTag == "player", true);
        SetChromeSelected(TitleSettingsButton, _pageTag == "settings", true);
    }

    private static void SetChromeSelected(Button button, bool selected, bool overlay)
    {
        if (selected)
        {
            if (overlay)
            {
                button.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0x33, 255, 255, 255));
                return;
            }

            if (Application.Current.Resources.TryGetValue("SubtleFillColorSecondaryBrush", out var resource)
                && resource is Brush brush)
            {
                button.Background = brush;
                return;
            }

            button.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0x22, 0, 0, 0));
            return;
        }

        button.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
    }

    private void SetOverlayOpen(bool open)
    {
        var visible = open ? Visibility.Visible : Visibility.Collapsed;
        if (_overlayVisible == open && OverlayTitleBar.Visibility == visible)
            return;

        _overlayVisible = open;
        OverlayTitleBar.Visibility = visible;
        OverlayTitleBar.IsHitTestVisible = open;
        if (open)
            UpdateMaximizeIcon();
    }

    private void UpdateMaximizeIcon()
    {
        var maximized = AppWindow.Presenter is OverlappedPresenter presenter
            && presenter.State == OverlappedPresenterState.Maximized;
        var glyph = maximized ? "\uE923" : "\uE922";
        OverlayMaximizeIcon.Glyph = glyph;
        BarMaximizeIcon.Glyph = glyph;
    }

    private void CaptionDrag_Pressed(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint((UIElement)sender);
        var hwnd = WindowNative.GetWindowHandle(this);
        if (point.Properties.IsRightButtonPressed)
        {
            ShowSystemMenu(hwnd);
            e.Handled = true;
            return;
        }

        if (!point.Properties.IsLeftButtonPressed)
            return;

        HoldOverlayForCaption();
        if (!GetCursorPos(out var pos))
            return;

        var now = (uint)Environment.TickCount;
        var isDouble = now - _captionClickTime <= GetDoubleClickTime()
            && Math.Abs(pos.X - _captionClickX) <= GetSystemMetrics(SmCxDoubleClk) / 2
            && Math.Abs(pos.Y - _captionClickY) <= GetSystemMetrics(SmCyDoubleClk) / 2;
        if (isDouble)
        {
            _captionClickTime = 0;
            ToggleMaximize();
            e.Handled = true;
            return;
        }

        _captionClickTime = now;
        _captionClickX = pos.X;
        _captionClickY = pos.Y;
        ReleaseCapture();
        SendMessage(hwnd, WmNcLButtonDown, (IntPtr)HtCaption, IntPtr.Zero);
        e.Handled = true;
    }

    private void HoldOverlayForCaption()
    {
        if (WantsPlayerOverlay())
            _overlayHoldUntil = DateTime.UtcNow.AddSeconds(2.5);
    }

    private void ShowSystemMenu(IntPtr hwnd)
    {
        var menu = GetSystemMenu(hwnd, false);
        if (menu == IntPtr.Zero || !GetCursorPos(out var pos))
            return;
        var command = TrackPopupMenu(menu, TpmReturnCmd | TpmRightButton, pos.X, pos.Y, 0, hwnd, IntPtr.Zero);
        if (command != 0)
            SendMessage(hwnd, WmSysCommand, (IntPtr)command, IntPtr.Zero);
    }

    private void Minimize_Click(object sender, RoutedEventArgs e)
    {
        App.Host.Playback.Pause();
        SuspendRendering?.Invoke();
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

    public void FitToVideo(int videoWidth, int videoHeight, FrameworkElement area)
    {
        if (_fullscreen || videoWidth < 1 || videoHeight < 1 || area.ActualWidth < 2 || area.ActualHeight < 2)
            return;
        if (AppWindow.Presenter is not OverlappedPresenter presenter)
            return;
        if (presenter.State == OverlappedPresenterState.Minimized)
            return;
        if (presenter.State == OverlappedPresenterState.Maximized)
        {
            if (_fitRestoreTries >= 2)
            {
                _fitRestoreTries = 0;
                return;
            }

            _fitRestoreTries++;
            presenter.Restore();
            area.DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
                FitToVideo(videoWidth, videoHeight, area));
            return;
        }

        _fitRestoreTries = 0;
        var scale = area.XamlRoot?.RasterizationScale ?? 1;
        if (scale < 0.1)
            scale = 1;
        var areaWidth = Math.Max(1, (int)Math.Round(area.ActualWidth * scale));
        var areaHeight = Math.Max(1, (int)Math.Round(area.ActualHeight * scale));
        var outer = AppWindow.Size;
        var work = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest).WorkArea;
        var next = WindowFit.ForVideo(
            videoWidth,
            videoHeight,
            areaWidth,
            areaHeight,
            outer.Width - areaWidth,
            outer.Height - areaHeight,
            Math.Min(480, work.Width),
            Math.Min(320, work.Height),
            work.Width,
            work.Height);
        if (next == null)
            return;
        var pos = AppWindow.Position;
        var x = pos.X;
        var y = pos.Y;
        if (x + next.Value.Width > work.X + work.Width)
            x = work.X + work.Width - next.Value.Width;
        if (y + next.Value.Height > work.Y + work.Height)
            y = work.Y + work.Height - next.Value.Height;
        AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(
            Math.Max(work.X, x),
            Math.Max(work.Y, y),
            next.Value.Width,
            next.Value.Height));
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

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int cmdShow);

    [DllImport("user32.dll")]
    private static extern uint GetDoubleClickTime();

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll")]
    private static extern IntPtr GetSystemMenu(IntPtr hwnd, bool revert);

    [DllImport("user32.dll")]
    private static extern int TrackPopupMenu(IntPtr menu, uint flags, int x, int y, int reserved, IntPtr hwnd, IntPtr rect);

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
