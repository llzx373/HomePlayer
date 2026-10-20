using HomePlayer.Core;

namespace HomePlayer.Core.Tests;

public class WindowSafetyTests
{
    [Theory]
    [InlineData(WindowSafety.ScMove)]
    [InlineData(0xF012)]
    [InlineData(WindowSafety.ScSize)]
    [InlineData(WindowSafety.ScSize | 1)]
    public void Move_and_size_syscommands_are_unsafe(int command)
    {
        Assert.True(WindowSafety.IsUnsafeSysCommand(command));
        Assert.Equal(SysCommandAction.Ignore, WindowSafety.ActionForSysCommand(command));
    }

    [Theory]
    [InlineData(WindowSafety.ScClose, SysCommandAction.Close)]
    [InlineData(WindowSafety.ScMinimize, SysCommandAction.Minimize)]
    [InlineData(WindowSafety.ScMaximize, SysCommandAction.Maximize)]
    [InlineData(WindowSafety.ScMaximize | 2, SysCommandAction.Maximize)]
    [InlineData(WindowSafety.ScRestore, SysCommandAction.Restore)]
    [InlineData(0xF090, SysCommandAction.Ignore)]
    [InlineData(0, SysCommandAction.Ignore)]
    public void Known_syscommands_map_to_window_actions(int command, SysCommandAction expected)
    {
        Assert.Equal(expected, WindowSafety.ActionForSysCommand(command));
    }

    [Fact]
    public void Syscommand_id_strips_hit_test_bits()
    {
        Assert.Equal(WindowSafety.ScMove, WindowSafety.SysCommandId(0xF012));
        Assert.Equal(WindowSafety.ScSize, WindowSafety.SysCommandId(WindowSafety.ScSize | 10));
        Assert.Equal(WindowSafety.ScClose, WindowSafety.SysCommandId(WindowSafety.ScClose));
    }

    [Theory]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    public void Interactive_requires_visible_and_not_minimized(bool visible, bool minimized, bool expected)
    {
        Assert.Equal(expected, WindowSafety.IsInteractive(visible, minimized));
        Assert.Equal(expected, WindowSafety.ShouldCloseWindow(visible, minimized));
    }

    [Fact]
    public void Chrome_is_only_reapplied_on_visibility_or_presenter_changes()
    {
        Assert.True(WindowSafety.ShouldApplyChrome(true, false, true));
        Assert.True(WindowSafety.ShouldApplyChrome(false, true, true));
        Assert.True(WindowSafety.ShouldApplyChrome(true, true, true));
        Assert.False(WindowSafety.ShouldApplyChrome(false, false, true));
        Assert.False(WindowSafety.ShouldApplyChrome(true, false, false));
        Assert.False(WindowSafety.ShouldApplyChrome(false, true, false));
        Assert.False(WindowSafety.ShouldApplyChrome(false, false, false));
    }

    [Theory]
    [InlineData(true, false, true, true)]
    [InlineData(true, false, false, false)]
    [InlineData(true, true, true, false)]
    [InlineData(false, false, true, false)]
    [InlineData(false, true, true, false)]
    public void Rendering_resumes_only_when_the_window_and_panel_are_live(
        bool visible,
        bool minimized,
        bool panelLoaded,
        bool expected)
    {
        Assert.Equal(expected, WindowSafety.CanResumeRendering(visible, minimized, panelLoaded));
    }

    [Theory]
    [InlineData(true, false, false, true)]
    [InlineData(true, false, true, false)]
    [InlineData(true, true, false, false)]
    [InlineData(false, false, false, false)]
    public void Fit_window_skips_hidden_minimized_and_fullscreen(
        bool visible,
        bool minimized,
        bool fullscreen,
        bool expected)
    {
        Assert.Equal(expected, WindowSafety.CanFitWindow(visible, minimized, fullscreen));
    }

    [Theory]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    public void Close_hides_to_tray_only_when_the_option_is_on(bool closeToTray, bool exiting, bool expected)
    {
        Assert.Equal(expected, WindowSafety.ShouldHideToTray(closeToTray, exiting));
    }
}
