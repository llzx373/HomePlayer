namespace HomePlayer.Core;

public static class AppExit
{
    public const int GraphShutdownTimeoutMs = 3000;
    public const int FallbackKillMs = 4000;

    public static AppExitPlan Plan(bool closeToTray, bool exiting)
    {
        if (exiting)
            return new AppExitPlan(AppExitAction.AllowClose, false, false);
        if (WindowSafety.ShouldHideToTray(closeToTray, exiting))
            return new AppExitPlan(AppExitAction.HideToTray, true, false);
        return new AppExitPlan(AppExitAction.BeginExit, false, false);
    }
}

public enum AppExitAction
{
    AllowClose,
    HideToTray,
    BeginExit
}

public readonly record struct AppExitPlan(AppExitAction Action, bool PauseGraph, bool JoinGraphOnUi);
