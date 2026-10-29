using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.Graphics;
using WinRT.Interop;

namespace HomePlayer;

public sealed class SettingsWindow : Window
{
    private const int GwlHwndParent = -8;

    public SettingsWindow()
    {
        Title = "设置 - HomePlayer";
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "App.ico"));
        Content = new SettingsPage();

        var hwnd = WindowNative.GetWindowHandle(this);
        var owner = App.Window.AppWindow;
        var work = DisplayArea.GetFromWindowId(owner.Id, DisplayAreaFallback.Nearest).WorkArea;
        var dpiScale = GetDpiForWindow(hwnd) / 96.0;
        var width = Math.Min((int)Math.Round(1040 * dpiScale), work.Width);
        var height = Math.Min((int)Math.Round(880 * dpiScale), work.Height);
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
        }

        var x = owner.Position.X + (owner.Size.Width - width) / 2;
        var y = owner.Position.Y + (owner.Size.Height - height) / 2;
        x = Math.Clamp(x, work.X, Math.Max(work.X, work.X + work.Width - width));
        y = Math.Clamp(y, work.Y, Math.Max(work.Y, work.Y + work.Height - height));
        AppWindow.MoveAndResize(new RectInt32(x, y, width, height));

        var ownerHwnd = WindowNative.GetWindowHandle(App.Window);
        SetWindowLongPtr(hwnd, GwlHwndParent, ownerHwnd);
    }

    public void ShowOrActivate()
    {
        if (AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } presenter)
            presenter.Restore();
        if (!AppWindow.IsVisible)
            AppWindow.Show();
        Activate();
        SetForegroundWindow(WindowNative.GetWindowHandle(this));
    }

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);
}
