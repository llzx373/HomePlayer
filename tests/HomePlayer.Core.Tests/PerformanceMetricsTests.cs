using HomePlayer.Core;

namespace HomePlayer.Core.Tests;

public class PerformanceMetricsTests
{
    [Fact]
    public void Metrics_include_counts_timings_and_memory()
    {
        PerformanceMetrics.Reset();
        PerformanceMetrics.Record("test", 2);
        PerformanceMetrics.Record("test", 4);

        var text = PerformanceMetrics.Format();

        Assert.Contains("test: count=2", text);
        Assert.Contains("avg=3.00ms", text);
        Assert.Contains("Managed memory bytes:", text);
    }
}
