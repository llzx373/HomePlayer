using System.Runtime.InteropServices;
using System.Threading;

namespace HomePlayer;

internal sealed class NativeTray : IDisposable
{
    private const uint NimAdd = 0;
    private const uint NimDelete = 2;
    private const uint NifMessage = 1;
    private const uint NifIcon = 2;
    private const uint NifTip = 4;
    private const uint WmAppTray = 0x8001;
    private const uint WmLButtonUp = 0x0202;
    private const uint WmLButtonDblClk = 0x0203;
    private const uint WmRButtonUp = 0x0205;
    private const uint WmNull = 0x0000;
    private const uint WmQuit = 0x0012;
    private const uint MfString = 0;
    private const uint MfSeparator = 0x00000800;
    private const uint TpmRightButton = 0x0002;
    private const uint TpmReturnCmd = 0x0100;
    private const int CmdOpen = 1;
    private const int CmdToggle = 2;
    private const int CmdNext = 3;
    private const int CmdExit = 4;
    private static readonly IntPtr MessageWindowParent = new(-3);

    private readonly Thread _thread;
    private readonly ManualResetEventSlim _ready = new();
    private readonly Action _open;
    private readonly Action _toggle;
    private readonly Action _next;
    private readonly Action _exit;
    private WndProc? _proc;
    private IntPtr _hwnd;
    private IntPtr _icon;
    private uint _threadId;

    public NativeTray(Action open, Action toggle, Action next, Action exit)
    {
        _open = open;
        _toggle = toggle;
        _next = next;
        _exit = exit;
        _thread = new Thread(Loop)
        {
            IsBackground = true,
            Name = "HomePlayer tray"
        };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        _ready.Wait();
    }

    public void Dispose()
    {
        if (_threadId != 0)
            PostThreadMessage(_threadId, WmQuit, IntPtr.Zero, IntPtr.Zero);
        _thread.Join(1000);
        _ready.Dispose();
    }

    private void Loop()
    {
        _threadId = GetCurrentThreadId();
        _proc = WindowProc;
        var className = "HomePlayerTray" + Environment.ProcessId;
        var window = new WndClass
        {
            lpfnWndProc = _proc,
            hInstance = GetModuleHandle(null),
            lpszClassName = className
        };
        RegisterClass(ref window);
        _hwnd = CreateWindowEx(0, className, "HomePlayerTray", 0, 0, 0, 0, 0, MessageWindowParent, IntPtr.Zero, window.hInstance, IntPtr.Zero);
        LoadIcon();
        AddIcon();
        _ready.Set();
        while (GetMessage(out var message, IntPtr.Zero, 0, 0) > 0)
        {
            TranslateMessage(ref message);
            DispatchMessage(ref message);
        }

        RemoveIcon();
        if (_icon != IntPtr.Zero)
        {
            DestroyIcon(_icon);
            _icon = IntPtr.Zero;
        }
    }

    private IntPtr WindowProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam)
    {
        if (message == WmAppTray)
        {
            var eventCode = (uint)lParam.ToInt64() & 0xFFFF;
            if (eventCode is WmLButtonUp or WmLButtonDblClk)
                Safe(_open);
            else if (eventCode == WmRButtonUp)
                ShowMenu();
            return IntPtr.Zero;
        }

        return DefWindowProc(hwnd, message, wParam, lParam);
    }

    private void ShowMenu()
    {
        var menu = CreatePopupMenu();
        AppendMenu(menu, MfString, (IntPtr)CmdOpen, "打开主窗口");
        AppendMenu(menu, MfString, (IntPtr)CmdToggle, "播放/暂停");
        AppendMenu(menu, MfString, (IntPtr)CmdNext, "下一个");
        AppendMenu(menu, MfSeparator, IntPtr.Zero, "");
        AppendMenu(menu, MfString, (IntPtr)CmdExit, "退出");
        GetCursorPos(out var pos);
        SetForegroundWindow(_hwnd);
        var command = TrackPopupMenu(menu, TpmReturnCmd | TpmRightButton, pos.X, pos.Y, 0, _hwnd, IntPtr.Zero);
        PostMessage(_hwnd, WmNull, IntPtr.Zero, IntPtr.Zero);
        DestroyMenu(menu);
        switch (command)
        {
            case CmdOpen:
                Safe(_open);
                break;
            case CmdToggle:
                Safe(_toggle);
                break;
            case CmdNext:
                Safe(_next);
                break;
            case CmdExit:
                Safe(_exit);
                break;
        }
    }

    private void AddIcon()
    {
        var data = CreateData();
        ShellNotifyIcon(NimAdd, ref data);
    }

    private void RemoveIcon()
    {
        var data = CreateData();
        ShellNotifyIcon(NimDelete, ref data);
    }

    private NotifyIconData CreateData() => new()
    {
        cbSize = Marshal.SizeOf<NotifyIconData>(),
        hWnd = _hwnd,
        uID = 1,
        uFlags = NifMessage | NifIcon | NifTip,
        uCallbackMessage = WmAppTray,
        hIcon = _icon,
        szTip = "HomePlayer"
    };

    private void LoadIcon()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Assets", "App.ico");
        if (File.Exists(path))
            _icon = LoadImage(IntPtr.Zero, path, 1, 0, 0, 0x00000010);
        if (_icon != IntPtr.Zero)
            return;
        ExtractIconEx(Environment.ProcessPath ?? "", 0, out _, out _icon, 1);
    }

    private static void Safe(Action action)
    {
        try
        {
            action();
        }
        catch
        {
        }
    }

    private delegate IntPtr WndProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WndClass
    {
        public uint style;
        public WndProc lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public int cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PointNative
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Msg
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public PointNative pt;
    }

    [DllImport("user32.dll", EntryPoint = "RegisterClassW", CharSet = CharSet.Unicode)]
    private static extern ushort RegisterClass(ref WndClass window);

    [DllImport("user32.dll", EntryPoint = "CreateWindowExW", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowEx(uint exStyle, string className, string windowName, uint style, int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll", EntryPoint = "DefWindowProcW")]
    private static extern IntPtr DefWindowProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern int GetMessage(out Msg message, IntPtr hwnd, uint min, uint max);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref Msg message);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessage(ref Msg message);

    [DllImport("user32.dll")]
    private static extern bool PostThreadMessage(uint threadId, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? name);

    [DllImport("shell32.dll", EntryPoint = "Shell_NotifyIconW")]
    private static extern bool ShellNotifyIcon(uint message, ref NotifyIconData data);

    [DllImport("user32.dll")]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool AppendMenu(IntPtr menu, uint flags, IntPtr id, string text);

    [DllImport("user32.dll")]
    private static extern int TrackPopupMenu(IntPtr menu, uint flags, int x, int y, int reserved, IntPtr hwnd, IntPtr rect);

    [DllImport("user32.dll")]
    private static extern bool DestroyMenu(IntPtr menu);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out PointNative point);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hwnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadImage(IntPtr instance, string name, uint type, int width, int height, uint load);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern uint ExtractIconEx(string file, int index, out IntPtr large, out IntPtr small, uint count);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr icon);
}
