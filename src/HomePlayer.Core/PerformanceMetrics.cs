using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;

namespace HomePlayer.Core;

public static class PerformanceMetrics
{
    private sealed record Metric(int Count, double TotalMs, double MaxMs, double LastMs)
    {
        public Metric Add(double value) => new(Count + 1, TotalMs + value, Math.Max(MaxMs, value), value);
    }

    private static readonly ConcurrentDictionary<string, Metric> Values = new();

    public static IDisposable Measure(string name) => new Scope(name);

    public static void Record(string name, double milliseconds)
    {
        if (milliseconds >= 0)
            Values.AddOrUpdate(name, new Metric(1, milliseconds, milliseconds, milliseconds), (_, old) => old.Add(milliseconds));
    }

    public static string Format()
    {
        var text = new StringBuilder();
        text.AppendLine("Managed memory bytes: " + GC.GetTotalMemory(false));
        foreach (var (name, metric) in Values.OrderBy(pair => pair.Key))
            text.AppendLine($"{name}: count={metric.Count}, last={metric.LastMs:F2}ms, avg={metric.TotalMs / metric.Count:F2}ms, max={metric.MaxMs:F2}ms");
        return text.ToString().TrimEnd();
    }

    public static void Reset() => Values.Clear();

    private sealed class Scope(string name) : IDisposable
    {
        private readonly Stopwatch _watch = Stopwatch.StartNew();

        public void Dispose() => Record(name, _watch.Elapsed.TotalMilliseconds);
    }
}
