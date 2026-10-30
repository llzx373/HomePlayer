using System.Runtime.InteropServices;

namespace HomePlayer.Playback;

[ComImport, Guid("56A868B4-0AD4-11CE-B03A-0020AF0BA770"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
internal interface IVideoWindow
{
    [PreserveSig] int put_Caption([MarshalAs(UnmanagedType.BStr)] string caption);
    [PreserveSig] int get_Caption([MarshalAs(UnmanagedType.BStr)] out string caption);
    [PreserveSig] int put_WindowStyle(int style);
    [PreserveSig] int get_WindowStyle(out int style);
    [PreserveSig] int put_WindowStyleEx(int style);
    [PreserveSig] int get_WindowStyleEx(out int style);
    [PreserveSig] int put_AutoShow(int autoShow);
    [PreserveSig] int get_AutoShow(out int autoShow);
    [PreserveSig] int put_WindowState(int state);
    [PreserveSig] int get_WindowState(out int state);
    [PreserveSig] int put_BackgroundPalette(int palette);
    [PreserveSig] int get_BackgroundPalette(out int palette);
    [PreserveSig] int put_Visible(int visible);
    [PreserveSig] int get_Visible(out int visible);
    [PreserveSig] int put_Left(int left);
    [PreserveSig] int get_Left(out int left);
    [PreserveSig] int put_Width(int width);
    [PreserveSig] int get_Width(out int width);
    [PreserveSig] int put_Top(int top);
    [PreserveSig] int get_Top(out int top);
    [PreserveSig] int put_Height(int height);
    [PreserveSig] int get_Height(out int height);
    [PreserveSig] int put_Owner(nint owner);
    [PreserveSig] int get_Owner(out nint owner);
    [PreserveSig] int put_MessageDrain(nint window);
    [PreserveSig] int get_MessageDrain(out nint window);
    [PreserveSig] int put_BorderColor(int color);
    [PreserveSig] int get_BorderColor(out int color);
    [PreserveSig] int put_FullScreenMode(int fullScreen);
    [PreserveSig] int get_FullScreenMode(out int fullScreen);
    [PreserveSig] int SetWindowForeground(int focus);
    [PreserveSig] int GetRestorePosition(out int left, out int top, out int width, out int height);
    [PreserveSig] int HideCursor(int hide);
    [PreserveSig] int GetCursorPosition(out int x, out int y);
}

[ComImport, Guid("56A868B5-0AD4-11CE-B03A-0020AF0BA770"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
internal interface IBasicVideo
{
    [PreserveSig] int GetAvgTimePerFrame(out double averageTime);
    [PreserveSig] int GetBitRate(out int bitRate);
    [PreserveSig] int GetBitErrorRate(out int bitErrorRate);
    [PreserveSig] int GetVideoWidth(out int width);
    [PreserveSig] int GetVideoHeight(out int height);
    [PreserveSig] int put_SourceLeft(int left);
    [PreserveSig] int get_SourceLeft(out int left);
    [PreserveSig] int put_SourceWidth(int width);
    [PreserveSig] int get_SourceWidth(out int width);
    [PreserveSig] int put_SourceTop(int top);
    [PreserveSig] int get_SourceTop(out int top);
    [PreserveSig] int put_SourceHeight(int height);
    [PreserveSig] int get_SourceHeight(out int height);
    [PreserveSig] int put_DestinationLeft(int left);
    [PreserveSig] int get_DestinationLeft(out int left);
    [PreserveSig] int put_DestinationWidth(int width);
    [PreserveSig] int get_DestinationWidth(out int width);
    [PreserveSig] int put_DestinationTop(int top);
    [PreserveSig] int get_DestinationTop(out int top);
    [PreserveSig] int put_DestinationHeight(int height);
    [PreserveSig] int get_DestinationHeight(out int height);
    [PreserveSig] int SetDefaultSourcePosition();
    [PreserveSig] int SetDefaultDestinationPosition();
    [PreserveSig] int GetSourcePosition(out int left, out int top, out int width, out int height);
    [PreserveSig] int GetDestinationPosition(out int left, out int top, out int width, out int height);
    [PreserveSig] int GetVideoSize(out int width, out int height);
    [PreserveSig] int GetVideoPaletteEntries(int startIndex, int entries, out int palette);
    [PreserveSig] int GetCurrentImage(out int bufferSize, nint dibImage);
    [PreserveSig] int IsUsingDefaultSource();
    [PreserveSig] int IsUsingDefaultDestination();
}
