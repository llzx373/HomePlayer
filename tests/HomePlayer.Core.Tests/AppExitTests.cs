using HomePlayer.Core;

namespace HomePlayer.Core.Tests;

public class AppExitTests
{
    [Fact]
    public void Closing_again_after_exit_started_lets_the_window_die()
    {
        var plan = AppExit.Plan(true, true);
        Assert.Equal(AppExitAction.AllowClose, plan.Action);
        Assert.False(plan.PauseGraph);
        Assert.False(plan.JoinGraphOnUi);
    }

    [Fact]
    public void Close_to_tray_pauses_without_joining_the_graph()
    {
        var plan = AppExit.Plan(true, false);
        Assert.Equal(AppExitAction.HideToTray, plan.Action);
        Assert.True(plan.PauseGraph);
        Assert.False(plan.JoinGraphOnUi);
    }

    [Fact]
    public void Exit_does_not_pause_or_join_on_the_ui_thread()
    {
        var plan = AppExit.Plan(false, false);
        Assert.Equal(AppExitAction.BeginExit, plan.Action);
        Assert.False(plan.PauseGraph);
        Assert.False(plan.JoinGraphOnUi);
    }

    [Fact]
    public void Watchdog_kill_does_not_wait_on_the_ui_thread()
    {
        Assert.True(AppExit.FallbackKillMs > AppExit.GraphShutdownTimeoutMs);
        Assert.InRange(AppExit.FallbackKillMs, 1000, 3000);
    }
}
