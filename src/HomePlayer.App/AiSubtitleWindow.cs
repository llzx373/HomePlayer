using System.Runtime.InteropServices;
using HomePlayer.Ffmpeg;
using HomePlayer.Core;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.Graphics;
using WinRT.Interop;

namespace HomePlayer;

public sealed class AiSubtitleWindow : Window
{
    private const int GwlHwndParent = -8;
    private readonly AiSubtitlePage _page;

    public event Action<string>? SubtitleSelected
    {
        add => _page.SubtitleSelected += value;
        remove => _page.SubtitleSelected -= value;
    }

    public AiSubtitleWindow(
        string mediaPath,
        IReadOnlyList<MediaTrack> audioTracks,
        int selectedAudioTrack,
        IReadOnlyList<string> sidecars,
        IReadOnlyList<FfmpegSubtitleStream> embedded)
    {
        Title = "AI 自动字幕 - HomePlayer";
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "App.ico"));
        _page = new AiSubtitlePage(mediaPath, audioTracks, selectedAudioTrack, sidecars, embedded);
        Content = _page;
        var hwnd = WindowNative.GetWindowHandle(this);
        var owner = App.Window.AppWindow;
        var work = DisplayArea.GetFromWindowId(owner.Id, DisplayAreaFallback.Nearest).WorkArea;
        var dpiScale = GetDpiForWindow(hwnd) / 96.0;
        var width = Math.Min((int)Math.Round(1100 * dpiScale), work.Width);
        var height = Math.Min((int)Math.Round(850 * dpiScale), work.Height);
        var x = Math.Clamp(owner.Position.X + (owner.Size.Width - width) / 2, work.X, Math.Max(work.X, work.X + work.Width - width));
        var y = Math.Clamp(owner.Position.Y + (owner.Size.Height - height) / 2, work.Y, Math.Max(work.Y, work.Y + work.Height - height));
        AppWindow.MoveAndResize(new RectInt32(x, y, width, height));
        SetWindowLongPtr(hwnd, GwlHwndParent, WindowNative.GetWindowHandle(App.Window));
        Closed += (_, _) => _page.CancelTask();
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
