using System.Runtime.InteropServices;
using HomePlayer.Core;

namespace HomePlayer;

public sealed record DisplayRefreshRateOption(int Hertz, string Label);

public static class DisplayRefreshRate
{
    private const int EnumCurrentSettings = -1;
    private const uint CdsUpdateRegistry = 0x1;
    private const uint DmDisplayFrequency = 0x400000;

    public static IReadOnlyList<DisplayRefreshRateOption> List(nint window, out string displayName, out int systemReported)
    {
        var device = DeviceName(window);
        displayName = device;
        var current = Current(device);
        systemReported = (int)current.dmDisplayFrequency;
        var available = new List<int>();
        for (var index = 0; EnumDisplaySettings(device, index, out var mode); index++)
        {
            if (mode.dmPelsWidth == current.dmPelsWidth
                && mode.dmPelsHeight == current.dmPelsHeight
                && mode.dmBitsPerPel == current.dmBitsPerPel)
            {
                available.Add((int)mode.dmDisplayFrequency);
            }
        }

        var reported = systemReported;
        return
        [
            new DisplayRefreshRateOption(0, "跟随系统（不修改 Windows）"),
            .. DisplayRefreshRateOptions.Merge(available, reported)
                .Select(hertz => new DisplayRefreshRateOption(hertz, $"{hertz} Hz" + (hertz == reported ? "（系统报告）" : "")))
        ];
    }

    public static int Apply(nint window, int hertz)
    {
        if (hertz <= 0)
            throw new ArgumentOutOfRangeException(nameof(hertz));
        var device = DeviceName(window);
        var mode = Current(device);
        mode.dmDisplayFrequency = (uint)hertz;
        mode.dmFields = DmDisplayFrequency;
        var result = ChangeDisplaySettingsEx(device, ref mode, 0, CdsUpdateRegistry, 0);
        if (result != 0)
            throw new InvalidOperationException($"Windows 无法应用 {hertz} Hz（{DescribeResult(result)}）。");
        return (int)Current(device).dmDisplayFrequency;
    }

    public static void ApplySaved(nint window, int hertz)
    {
        if (hertz <= 0)
            return;
        try
        {
            Apply(window, hertz);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine("Apply saved display refresh rate failed: " + ex.Message);
        }
    }

    private static string DeviceName(nint window)
    {
        var monitor = MonitorFromWindow(window, 2);
        if (monitor == 0)
            throw new InvalidOperationException("找不到应用窗口所在的显示器。");
        var info = new MonitorInfoEx { cbSize = (uint)Marshal.SizeOf<MonitorInfoEx>() };
        if (!GetMonitorInfo(monitor, ref info))
            throw new InvalidOperationException("读取显示器信息失败。");
        return info.szDevice;
    }

    private static DevMode Current(string device)
    {
        if (!EnumDisplaySettings(device, EnumCurrentSettings, out var mode))
            throw new InvalidOperationException("读取当前显示模式失败。");
        return mode;
    }

    private static string DescribeResult(int result) => result switch
    {
        1 => "需要重启 Windows",
        -1 => "驱动拒绝该模式",
        -2 => "该模式不受支持",
        -5 => "参数无效",
        _ => $"错误码 {result}"
    };

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplaySettings(string deviceName, int modeNum, out DevMode devMode);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int ChangeDisplaySettingsEx(string deviceName, ref DevMode devMode, nint hwnd, uint flags, nint param);

    [DllImport("user32.dll")]
    private static extern nint MonitorFromWindow(nint hwnd, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfoEx info);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfoEx
    {
        public uint cbSize;
        public Rect rcMonitor;
        public Rect rcWork;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szDevice;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int left;
        public int top;
        public int right;
        public int bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DevMode
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string dmDeviceName;
        public ushort dmSpecVersion;
        public ushort dmDriverVersion;
        public ushort dmSize;
        public ushort dmDriverExtra;
        public uint dmFields;
        public int dmPositionX;
        public int dmPositionY;
        public uint dmDisplayOrientation;
        public uint dmDisplayFixedOutput;
        public short dmColor;
        public short dmDuplex;
        public short dmYResolution;
        public short dmTTOption;
        public short dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string dmFormName;
        public ushort dmLogPixels;
        public uint dmBitsPerPel;
        public uint dmPelsWidth;
        public uint dmPelsHeight;
        public uint dmDisplayFlags;
        public uint dmDisplayFrequency;
        public uint dmICMMethod;
        public uint dmICMIntent;
        public uint dmMediaType;
        public uint dmDitherType;
        public uint dmReserved1;
        public uint dmReserved2;
        public uint dmPanningWidth;
        public uint dmPanningHeight;
    }
}
