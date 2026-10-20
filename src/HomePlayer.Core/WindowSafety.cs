namespace HomePlayer.Core;

public static class WindowSafety
{
    public const int ScSize = 0xF000;
    public const int ScMove = 0xF010;
    public const int ScMinimize = 0xF020;
    public const int ScMaximize = 0xF030;
    public const int ScClose = 0xF060;
    public const int ScRestore = 0xF120;
    public const int ScCommandMask = 0xFFF0;

    public static int SysCommandId(int command) => command & ScCommandMask;

    public static bool IsUnsafeSysCommand(int command)
    {
        var id = SysCommandId(command);
        return id is ScSize or ScMove;
    }

    public static SysCommandAction ActionForSysCommand(int command)
    {
        if (IsUnsafeSysCommand(command))
            return SysCommandAction.Ignore;
        return SysCommandId(command) switch
        {
            ScClose => SysCommandAction.Close,
            ScMinimize => SysCommandAction.Minimize,
            ScMaximize => SysCommandAction.Maximize,
            ScRestore => SysCommandAction.Restore,
            _ => SysCommandAction.Ignore
        };
    }

    public static bool IsInteractive(bool visible, bool minimized) => visible && !minimized;

    public static bool ShouldApplyChrome(bool didVisibilityChange, bool didPresenterChange, bool interactive) =>
        interactive && (didVisibilityChange || didPresenterChange);

    public static bool CanResumeRendering(bool visible, bool minimized, bool panelLoaded) =>
        IsInteractive(visible, minimized) && panelLoaded;

    public static bool CanFitWindow(bool visible, bool minimized, bool fullscreen) =>
        IsInteractive(visible, minimized) && !fullscreen;

    public static bool ShouldCloseWindow(bool visible, bool minimized) =>
        IsInteractive(visible, minimized);

    public static bool ShouldHideToTray(bool closeToTray, bool exiting) =>
        closeToTray && !exiting;
}

public enum SysCommandAction
{
    Ignore,
    Close,
    Minimize,
    Maximize,
    Restore
}
