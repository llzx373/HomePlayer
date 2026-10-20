using System.Diagnostics;
using System.Text;

namespace HomePlayer.Core;

public sealed class ExitTrace
{
    private readonly List<string> _steps = [];

    public IReadOnlyList<string> Steps
    {
        get
        {
            lock (_steps)
                return [.. _steps];
        }
    }

    public void Run(string name, Action action)
    {
        var clock = Stopwatch.StartNew();
        try
        {
            action();
            Add($"{name} {clock.ElapsedMilliseconds}ms");
        }
        catch (Exception ex)
        {
            Add($"{name} {clock.ElapsedMilliseconds}ms {ex.GetType().Name}: {ex.Message}");
        }
    }

    public void Add(string line)
    {
        lock (_steps)
            _steps.Add(line);
    }
}

public readonly record struct ThreadWaitInfo(int Id, string State, string? WaitReason, TimeSpan Cpu);

public static class ThreadSnapshot
{
    public static string Format(
        IReadOnlyList<ThreadWaitInfo> threads,
        string? watchdogStack,
        IReadOnlyList<string> steps)
    {
        var text = new StringBuilder();
        text.AppendLine("退出未在时限内完成。");
        if (steps.Count > 0)
        {
            text.AppendLine("已执行步骤:");
            foreach (var step in steps)
                text.AppendLine("  " + step);
        }

        if (!string.IsNullOrWhiteSpace(watchdogStack))
        {
            text.AppendLine("看门狗线程:");
            text.AppendLine(watchdogStack.TrimEnd());
        }

        text.AppendLine("线程:");
        foreach (var thread in threads)
        {
            var wait = string.IsNullOrEmpty(thread.WaitReason) ? "" : " wait=" + thread.WaitReason;
            text.Append("TID ").Append(thread.Id);
            text.Append(" state=").Append(thread.State);
            text.Append(wait);
            text.Append(" cpu=").Append(thread.Cpu.TotalMilliseconds.ToString("0"));
            text.AppendLine("ms");
        }

        return text.ToString();
    }

    public static IReadOnlyList<ThreadWaitInfo> Capture()
    {
        var threads = new List<ThreadWaitInfo>();
        using var process = Process.GetCurrentProcess();
        foreach (ProcessThread thread in process.Threads)
        {
            string? wait = null;
            try
            {
                if (thread.ThreadState == System.Diagnostics.ThreadState.Wait)
                    wait = thread.WaitReason.ToString();
            }
            catch (InvalidOperationException)
            {
            }

            var cpu = TimeSpan.Zero;
            try
            {
                cpu = thread.TotalProcessorTime;
            }
            catch (InvalidOperationException)
            {
            }

            threads.Add(new ThreadWaitInfo(thread.Id, thread.ThreadState.ToString(), wait, cpu));
        }

        return threads;
    }
}
