using HomePlayer.Core;

namespace HomePlayer.Core.Tests;

public class ExitHangTests
{
    [Fact]
    public void Format_includes_steps_threads_and_watchdog_stack()
    {
        var text = ThreadSnapshot.Format(
            [new ThreadWaitInfo(12, "Wait", "UserRequest", TimeSpan.FromMilliseconds(40))],
            "at HomePlayer.MainWindow.ArmExitWatchdog()",
            ["SaveSettings 1ms", "StopSession 0ms"]);

        Assert.Contains("退出未在时限内完成。", text);
        Assert.Contains("SaveSettings 1ms", text);
        Assert.Contains("TID 12 state=Wait wait=UserRequest cpu=40ms", text);
        Assert.Contains("at HomePlayer.MainWindow.ArmExitWatchdog()", text);
    }

    [Fact]
    public void Trace_records_duration_and_keeps_going_after_errors()
    {
        var trace = new ExitTrace();
        trace.Run("ok", () => { });
        trace.Run("boom", () => throw new InvalidOperationException("nope"));

        Assert.Equal(2, trace.Steps.Count);
        Assert.Contains("ok ", trace.Steps[0]);
        Assert.Contains("ms", trace.Steps[0]);
        Assert.Contains("boom ", trace.Steps[1]);
        Assert.Contains("InvalidOperationException: nope", trace.Steps[1]);
    }
}
