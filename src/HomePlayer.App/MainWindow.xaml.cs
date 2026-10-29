using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using HomePlayer.Core;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics;
using WinRT.Interop;

namespace HomePlayer;

public sealed partial class MainWindow : Window
{
    private readonly NativeTray _tray;
    private SettingsWindow? _settingsWindow;
    // 光标位置用轮询是为了在无边框/全屏时判断控制条该不该出现。
    // 10 Hz 足够跟手，比原来的 20 Hz 少一半常驻唤醒。
    private readonly DispatcherTimer _overlayTimer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private readonly OverlayFade _overlayFade;
    private bool _fullscreen;
    private bool _alwaysOnTop;
    private int _fitRestoreTries;
    private bool _overlayVisible;
    private DateTime _overlayHoldUntil;
    private string _pageTag = "library";
    private bool _syncingNav;
    private bool _navigationErrorShown;
    private Exception? _lastNavigationError;
    private uint _captionClickTime;
    private int _captionClickX;
    private int _captionClickY;
    private int _captionGrabX;
    private int _captionGrabY;
    private bool _captionArmed;
    private bool _captionMoved;
    private bool _chromeReady;
    private int _exitWatchdog;
    private readonly ExitTrace _exitTrace = new();
    private const double PlayerTitleHeight = 40;
    private const int SmCxDoubleClk = 36;
    private const int SmCyDoubleClk = 37;
    private const int SmCxDrag = 68;
    private const int SmCyDrag = 69;
    private const int GwlStyle = -16;
    private const long WsSysMenu = 0x00080000;
    private const uint TpmReturnCmd = 0x0100;
    private const uint TpmRightButton = 0x0002;

    public bool IsFullscreen => _fullscreen;

    public bool IsInteractive => IsWindowInteractive();

    public event Action? FullscreenChanged;
    public event Action? OverlayTick;
    public event Action? SuspendRendering;
    public event Action? ResumeRendering;

    public MainWindow(bool startOnPlayer)
    {
        InitializeComponent();
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "App.ico"));
        _overlayFade = new OverlayFade(OverlayTitleBar);
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
        Activated += OnWindowActivated;
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
        RootFrame.NavigationFailed += OnNavigationFailed;
        SetAlwaysOnTop(App.Host.Settings.AlwaysOnTop);
        Nav.SelectedItem = Nav.MenuItems[startOnPlayer ? 1 : 0];
        _tray = new NativeTray(
            () => RunOnUi(ShowFromTray),
            RequestExit);
    }

    public void SelectPlayer() => SelectPage("player");

    public void ShowFromTray()
    {
        try
        {
            if (App.Host.Exiting)
                return;
            if (!TryRunOnUi(ShowFromTray))
                return;
            if (!AppWindow.IsVisible)
                AppWindow.Show();
            if (AppWindow.Presenter is OverlappedPresenter presenter
                && presenter.State == OverlappedPresenterState.Minimized)
                presenter.Restore();
            ApplyWindowChrome();
            Activate();
            SetForegroundWindow(WindowNative.GetWindowHandle(this));
            QueueResumeRendering();
        }
        catch (Exception ex)
        {
            CrashLog.Write(CrashLog.UnhandledKind, ex.ToString());
        }
    }

    public async Task PlayIncomingFilesAsync(IReadOnlyList<string> paths, bool replace)
    {
        if (paths.Count == 0)
            return;
        SelectPlayer();
        if (replace)
            await App.Host.Playback.PlayPathsAsync(paths, 0);
        else
            App.Host.Playback.Enqueue(paths);
    }

    private void OnRootLoaded(object sender, RoutedEventArgs e)
    {
        Root.Loaded -= OnRootLoaded;
        ApplyTitleBar();
        SetAlwaysOnTop(App.Host.Settings.AlwaysOnTop);
        UpdateOverlayTimer();
        if (App.Host.SettingsWarning is string warning)
            _ = ShowSettingsWarning(warning);
    }

    private async Task ShowSettingsWarning(string warning)
    {
        try
        {
            await new ContentDialog
            {
                Title = "设置恢复提醒",
                Content = warning,
                CloseButtonText = "知道了",
                XamlRoot = Root.XamlRoot
            }.ShowAsync();
        }
        catch (Exception)
        {
        }
    }

    private void UpdateOverlayTimer()
    {
        var needs = AppWindow.IsVisible
            && !IsMinimized()
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
        if (_fullscreen || !CanUseWindowChrome())
            return;
        try
        {
            if (AppWindow.Presenter is OverlappedPresenter presenter
                && (!presenter.HasBorder || presenter.HasTitleBar))
                presenter.SetBorderAndTitleBar(true, false);
            EnsureSystemMenu();
            _chromeReady = true;
        }
        catch
        {
        }
    }

    private bool CanUseWindowChrome()
    {
        if (IsMinimized())
            return false;
        return !_chromeReady || AppWindow.IsVisible;
    }

    private bool IsMinimized() =>
        AppWindow.Presenter is OverlappedPresenter presenter
        && presenter.State == OverlappedPresenterState.Minimized;

    private bool IsWindowInteractive() =>
        WindowSafety.IsInteractive(AppWindow.IsVisible, IsMinimized());

    private void EnsureSystemMenu()
    {
        var hwnd = WindowNative.GetWindowHandle(this);
        var style = GetWindowLongPtr(hwnd, GwlStyle).ToInt64();
        if ((style & WsSysMenu) == 0)
            SetWindowLongPtr(hwnd, GwlStyle, (IntPtr)(style | WsSysMenu));
    }

    private void OnWindowActivated(object sender, WindowActivatedEventArgs e)
    {
        var opacity = e.WindowActivationState == WindowActivationState.Deactivated ? 0.55 : 1;
        AppTitleBar.Opacity = opacity;
        OverlayTitleBar.Opacity = opacity;
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
        SetOverlayOpen(false);
        if (enabled)
        {
            _fullscreen = true;
            AppWindow.SetPresenter(AppWindowPresenterKind.FullScreen);
        }
        else
        {
            AppWindow.SetPresenter(AppWindowPresenterKind.Overlapped);
            _fullscreen = false;
            ApplyTitleBar();
            SetAlwaysOnTop(App.Host.Settings.AlwaysOnTop);
        }

        ApplyNavPane();
        UpdateOverlayTimer();
        FullscreenChanged?.Invoke();
    }

    private void OnClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        var plan = AppExit.Plan(App.Host.Settings.CloseToTray, App.Host.Exiting);
        if (plan.Action == AppExitAction.AllowClose)
            return;
        args.Cancel = true;
        if (plan.Action == AppExitAction.BeginExit)
        {
            RequestExit();
            return;
        }

        var queue = Root?.DispatcherQueue;
        if (queue == null)
            HideToTray();
        else
            queue.TryEnqueue(HideToTray);
    }

    private void RequestClose()
    {
        var plan = AppExit.Plan(App.Host.Settings.CloseToTray, App.Host.Exiting);
        if (plan.Action == AppExitAction.BeginExit)
            RequestExit();
        else if (plan.Action == AppExitAction.HideToTray)
            HideToTray();
    }

    private void HideToTray()
    {
        try
        {
            ShowWindow(WindowNative.GetWindowHandle(this), 0);
        }
        catch
        {
        }

        App.Host.Playback.Pause();
        try
        {
            SuspendRendering?.Invoke();
        }
        catch
        {
        }
    }

    private void OnAppWindowChanged(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (args.DidSizeChange
            && !_fullscreen
            && AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Restored }
            && (AppWindow.Size.Width < 900 || AppWindow.Size.Height < 600))
        {
            AppWindow.Resize(new SizeInt32(Math.Max(900, AppWindow.Size.Width), Math.Max(600, AppWindow.Size.Height)));
            return;
        }

        if (!args.DidVisibilityChange && !args.DidPresenterChange)
            return;

        if (!IsWindowInteractive())
        {
            SuspendRendering?.Invoke();
            App.Host.Playback.Pause();
        }
        else if (WindowSafety.ShouldApplyChrome(args.DidVisibilityChange, args.DidPresenterChange, true))
        {
            ApplyWindowChrome();
            QueueResumeRendering();
        }

        UpdateOverlayTimer();
        UpdateMaximizeIcon();
    }

    private void Nav_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (_syncingNav)
            return;
        var tag = (args.SelectedItem as NavigationViewItem)?.Tag as string;
        ShowPage(tag ?? "library");
    }

    private static Type PageFor(string tag) => tag switch
    {
        "player" => typeof(PlayerPage),
        _ => typeof(LibraryPage)
    };

    private NavigationViewItem? NavItemFor(string tag) => tag switch
    {
        "player" => Nav.MenuItems.ElementAtOrDefault(1) as NavigationViewItem,
        "settings" => Nav.FooterMenuItems.ElementAtOrDefault(0) as NavigationViewItem,
        _ => Nav.MenuItems.ElementAtOrDefault(0) as NavigationViewItem
    };

    private void SelectPage(string tag)
    {
        var item = NavItemFor(tag);
        if (item == null)
            return;
        if (!ReferenceEquals(Nav.SelectedItem, item))
        {
            Nav.SelectedItem = item;
            return;
        }

        if (_pageTag == tag && RootFrame.Content?.GetType() == PageFor(tag))
            return;

        ShowPage(tag);
    }

    private void ShowPage(string tag)
    {
        if (tag == "settings")
        {
            ShowSettings();
            SyncNavSelection();
            return;
        }

        var page = PageFor(tag);
        _lastNavigationError = null;
        var started = false;
        try
        {
            started = RootFrame.Navigate(page);
        }
        catch (Exception ex)
        {
            _lastNavigationError = ex;
            CrashLog.Write(CrashLog.UnhandledKind, $"页面 {page.Name} 加载失败{Environment.NewLine}{ex}");
        }

        if (!started)
        {
            SyncNavSelection();
            ShowNavigationError(tag, _lastNavigationError);
            return;
        }

        _pageTag = tag;
        ApplyTitleBar();
    }

    private void ShowSettings()
    {
        try
        {
            if (_settingsWindow == null)
            {
                var window = new SettingsWindow();
                _settingsWindow = window;
                window.Closed += (_, _) =>
                {
                    if (ReferenceEquals(_settingsWindow, window))
                        _settingsWindow = null;
                };
            }

            _settingsWindow.ShowOrActivate();
        }
        catch (Exception ex)
        {
            CrashLog.Write(CrashLog.UnhandledKind, $"设置窗口加载失败{Environment.NewLine}{ex}");
            ShowNavigationError("settings", ex);
        }
    }

    private void OnNavigationFailed(object sender, NavigationFailedEventArgs e)
    {
        _lastNavigationError = e.Exception;
        CrashLog.Write(CrashLog.UnhandledKind, $"页面 {e.SourcePageType.Name} 加载失败{Environment.NewLine}{e.Exception}");
    }

    private void SyncNavSelection()
    {
        var item = NavItemFor(_pageTag);
        if (item == null || ReferenceEquals(Nav.SelectedItem, item))
            return;
        _syncingNav = true;
        try
        {
            Nav.SelectedItem = item;
        }
        finally
        {
            _syncingNav = false;
        }
    }

    private async void ShowNavigationError(string tag, Exception? error)
    {
        if (_navigationErrorShown)
            return;
        _navigationErrorShown = true;
        try
        {
            var name = tag switch
            {
                "player" => "正在播放",
                "settings" => "设置",
                _ => "媒体库"
            };
            var dialog = new ContentDialog
            {
                Title = name + "页面打不开",
                Content = error?.Message ?? "页面初始化失败，详细信息已写入崩溃日志。",
                PrimaryButtonText = "重试",
                CloseButtonText = "关闭",
                XamlRoot = Root.XamlRoot
            };
            if (await dialog.ShowAsync() == ContentDialogResult.Primary)
                ShowPage(tag);
        }
        catch (Exception)
        {
        }
        finally
        {
            _navigationErrorShown = false;
        }
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
            SelectPlayer();
        }
        catch (Exception ex)
        {
            try
            {
                CrashLog.Write("打开文件失败", ex.ToString());
            }
            catch (Exception)
            {
            }

            _ = ShowDropError(ex);
        }
    }

    private async Task ShowDropError(Exception error)
    {
        try
        {
            await new ContentDialog
            {
                Title = "无法打开拖放的文件",
                Content = error.Message,
                CloseButtonText = "知道了",
                XamlRoot = Root.XamlRoot
            }.ShowAsync();
        }
        catch (Exception)
        {
        }
    }

    private void Root_DragOver(object sender, DragEventArgs e)
    {
        e.AcceptedOperation = DataPackageOperation.Copy;
    }

    private void RequestExit()
    {
        ArmExitWatchdog();
        BeginExit();
    }

    private void ArmExitWatchdog()
    {
        if (Interlocked.Exchange(ref _exitWatchdog, 1) != 0)
            return;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(AppExit.FallbackKillMs);
            }
            catch
            {
            }

            try
            {
                CrashGuard.ReportHang(ThreadSnapshot.Format(
                    ThreadSnapshot.Capture(),
                    Environment.StackTrace,
                    _exitTrace.Steps));
            }
            catch
            {
            }

            KillProcess();
        });
    }

    private void BeginExit()
    {
        if (App.Host.Exiting)
            return;
        App.Host.Exiting = true;
        _exitTrace.Run("PersistProgress", App.Host.Playback.PersistProgress);
        _ = Task.Run(() =>
        {
            _exitTrace.Run("SaveSettings", App.Host.SaveSettings);
            _exitTrace.Run("StopSession", CrashGuard.StopSession);
            _exitTrace.Run("Kill", KillProcess);
        });
    }

    private static void KillProcess()
    {
        try
        {
            Process.GetCurrentProcess().Kill();
        }
        catch
        {
        }
    }

    private void QueueResumeRendering()
    {
        if (!IsWindowInteractive())
            return;
        var queue = Root.DispatcherQueue;
        if (queue == null)
            return;

        queue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (IsWindowInteractive())
                ResumeRendering?.Invoke();
        });
    }

    private void RunOnUi(Action action)
    {
        if (TryRunOnUi(action))
            action();
    }

    private bool TryRunOnUi(Action action)
    {
        var queue = Root?.DispatcherQueue;
        if (queue == null)
            return false;
        if (queue.HasThreadAccess)
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
        if (_captionArmed)
        {
            SetOverlayOpen(true);
            return;
        }

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

    private void TitleLibrary_Click(object sender, RoutedEventArgs e) => SelectPage("library");

    private void TitlePlayer_Click(object sender, RoutedEventArgs e) => SelectPage("player");

    private void TitleSettings_Click(object sender, RoutedEventArgs e) => SelectPage("settings");

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
        _overlayFade.Set(open);
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
        var element = (UIElement)sender;
        var point = e.GetCurrentPoint(element);
        var hwnd = WindowNative.GetWindowHandle(this);
        if (point.Properties.IsRightButtonPressed)
        {
            EndCaptionGesture(element, e.Pointer);
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
        var isDouble = _captionClickTime != 0
            && !_captionMoved
            && now - _captionClickTime <= GetDoubleClickTime()
            && Math.Abs(pos.X - _captionClickX) <= GetSystemMetrics(SmCxDoubleClk) / 2
            && Math.Abs(pos.Y - _captionClickY) <= GetSystemMetrics(SmCyDoubleClk) / 2;
        if (isDouble)
        {
            EndCaptionGesture(element, e.Pointer);
            _captionClickTime = 0;
            ToggleMaximize();
            e.Handled = true;
            return;
        }

        _captionArmed = true;
        _captionMoved = false;
        _captionClickX = pos.X;
        _captionClickY = pos.Y;
        _captionGrabX = pos.X - AppWindow.Position.X;
        _captionGrabY = pos.Y - AppWindow.Position.Y;
        element.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void CaptionDrag_Moved(object sender, PointerRoutedEventArgs e)
    {
        if (!_captionArmed)
            return;
        if (!e.GetCurrentPoint((UIElement)sender).Properties.IsLeftButtonPressed)
            return;
        if (!GetCursorPos(out var pos))
            return;
        if (!_captionMoved)
        {
            if (Math.Abs(pos.X - _captionClickX) <= GetSystemMetrics(SmCxDrag)
                && Math.Abs(pos.Y - _captionClickY) <= GetSystemMetrics(SmCyDrag))
                return;
            _captionMoved = true;
            _captionClickTime = 0;
            RestoreCaptionDrag(pos);
        }

        HoldOverlayForCaption();
        MoveCaptionTo(pos);
        e.Handled = true;
    }

    private void CaptionDrag_Released(object sender, PointerRoutedEventArgs e)
    {
        if (!_captionArmed)
            return;
        if (_captionMoved)
            SnapCaptionFromCursor();
        else if (GetCursorPos(out var pos))
        {
            _captionClickTime = (uint)Environment.TickCount;
            _captionClickX = pos.X;
            _captionClickY = pos.Y;
        }

        EndCaptionGesture((UIElement)sender, e.Pointer);
        e.Handled = true;
    }

    private void CaptionDrag_Canceled(object sender, PointerRoutedEventArgs e) =>
        EndCaptionGesture((UIElement)sender, e.Pointer);

    private void CaptionDrag_CaptureLost(object sender, PointerRoutedEventArgs e)
    {
        _captionArmed = false;
    }

    private void RestoreCaptionDrag(PointNative pos)
    {
        if (AppWindow.Presenter is not OverlappedPresenter presenter
            || presenter.State != OverlappedPresenterState.Maximized)
            return;
        var width = Math.Max(1, AppWindow.Size.Width);
        var ratio = Math.Clamp((pos.X - AppWindow.Position.X) / (double)width, 0.08, 0.92);
        presenter.Restore();
        _captionGrabX = (int)Math.Round(AppWindow.Size.Width * ratio);
        _captionGrabY = Math.Clamp(_captionGrabY, 0, (int)PlayerTitleHeight);
        UpdateMaximizeIcon();
    }

    private void MoveCaptionTo(PointNative pos)
    {
        try
        {
            AppWindow.Move(new PointInt32(pos.X - _captionGrabX, pos.Y - _captionGrabY));
        }
        catch
        {
        }
    }

    private void SnapCaptionFromCursor()
    {
        if (!GetCursorPos(out var pos) || AppWindow.Presenter is not OverlappedPresenter presenter)
            return;
        var work = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest).WorkArea;
        if (pos.Y <= work.Y + 8)
        {
            presenter.Maximize();
            UpdateMaximizeIcon();
            return;
        }

        if (pos.X <= work.X + 8)
        {
            AppWindow.MoveAndResize(new RectInt32(work.X, work.Y, work.Width / 2, work.Height));
            return;
        }

        if (pos.X >= work.X + work.Width - 8)
            AppWindow.MoveAndResize(new RectInt32(work.X + work.Width / 2, work.Y, work.Width - work.Width / 2, work.Height));
    }

    private void EndCaptionGesture(UIElement element, Pointer pointer)
    {
        _captionArmed = false;
        try
        {
            element.ReleasePointerCapture(pointer);
        }
        catch
        {
        }
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
        if (command == 0)
            return;
        switch (WindowSafety.ActionForSysCommand(command))
        {
            case SysCommandAction.Close:
                RequestClose();
                return;
            case SysCommandAction.Minimize:
                MinimizeWindow();
                return;
            case SysCommandAction.Maximize:
                if (AppWindow.Presenter is OverlappedPresenter maximize
                    && maximize.State != OverlappedPresenterState.Maximized)
                    maximize.Maximize();
                UpdateMaximizeIcon();
                return;
            case SysCommandAction.Restore:
                if (AppWindow.Presenter is OverlappedPresenter restore
                    && restore.State != OverlappedPresenterState.Restored)
                    restore.Restore();
                UpdateMaximizeIcon();
                return;
        }
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => MinimizeWindow();

    private void MinimizeWindow()
    {
        SuspendRendering?.Invoke();
        App.Host.Playback.Pause();
        if (AppWindow.Presenter is OverlappedPresenter presenter)
            presenter.Minimize();
    }

    private void Maximize_Click(object sender, RoutedEventArgs e) => ToggleMaximize();

    private void Close_Click(object sender, RoutedEventArgs e) => RequestClose();

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
        if (!WindowSafety.CanFitWindow(AppWindow.IsVisible, IsMinimized(), _fullscreen)
            || videoWidth < 1 || videoHeight < 1 || area.ActualWidth < 2 || area.ActualHeight < 2)
            return;
        if (AppWindow.Presenter is not OverlappedPresenter presenter)
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
    private static extern bool ShowWindow(IntPtr hwnd, int command);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out PointNative point);

    [DllImport("user32.dll")]
    private static extern bool ScreenToClient(IntPtr hwnd, ref PointNative point);

    [DllImport("user32.dll")]
    private static extern bool GetClientRect(IntPtr hwnd, out RectNative rect);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern uint GetDoubleClickTime();

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll")]
    private static extern IntPtr GetSystemMenu(IntPtr hwnd, bool revert);

    [DllImport("user32.dll")]
    private static extern int TrackPopupMenu(IntPtr menu, uint flags, int x, int y, int reserved, IntPtr hwnd, IntPtr rect);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);

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

}
