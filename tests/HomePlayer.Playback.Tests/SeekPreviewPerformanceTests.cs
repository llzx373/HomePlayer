using System.Diagnostics;
using HomePlayer.Ffmpeg;

namespace HomePlayer.Playback.Tests;

public sealed class SeekPreviewPerformanceTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), "HomePlayer.SeekPreviewPerf." + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Measure_exact_preview_latency_and_cache_hits()
    {
        var media = Environment.GetEnvironmentVariable("HOMEPLAYER_PERF_MEDIA");
        if (string.IsNullOrWhiteSpace(media))
            media = CreateMedia();
        media = Path.GetFullPath(media);
        using var service = new SeekPreviewService();
        var cold = new List<double>();
        const int iterations = 24;
        for (var i = 0; i < iterations; i++)
        {
            var target = TimeSpan.FromSeconds(0.5 + (i * 1.73 % 10));
            var started = Stopwatch.GetTimestamp();
            var result = await service.CaptureAsync(media, target).WaitAsync(TimeSpan.FromSeconds(5));
            cold.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            Assert.NotNull(result);
            Assert.False(result.CacheHit);
            Assert.Equal(target, result.Target);
            Assert.True(result.Frame.PresentationTimestamp <= target.Ticks);
            Assert.True(result.Frame.PresentationTimestamp + result.Frame.Duration >= target.Ticks);
        }

        var cachedTarget = TimeSpan.FromSeconds(0.5);
        var startedCached = Stopwatch.GetTimestamp();
        var cached = await service.CaptureAsync(media, cachedTarget).WaitAsync(TimeSpan.FromSeconds(1));
        var cachedMilliseconds = Stopwatch.GetElapsedTime(startedCached).TotalMilliseconds;
        Assert.NotNull(cached);
        Assert.True(cached.CacheHit);
        cold.Sort();
        var p50 = Percentile(cold, 0.50);
        var p95 = Percentile(cold, 0.95);
        Console.WriteLine($"Seek preview exact-frame latency: p50={p50:F1}ms p95={p95:F1}ms max={cold[^1]:F1}ms cached={cachedMilliseconds:F1}ms");
        Assert.True(p95 < 2000, $"精确预览 p95 超过 2 秒：{p95:F1}ms");
        Assert.True(cachedMilliseconds < 100, $"精确缓存命中过慢：{cachedMilliseconds:F1}ms");
    }

    private string CreateMedia()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "preview-1080p-long-gop.mp4");
        using var process = Process.Start(new ProcessStartInfo("ffmpeg")
        {
            UseShellExecute = false,
            Arguments = $"-y -loglevel error -f lavfi -i \"testsrc2=size=1920x1080:rate=30:duration=12\" -c:v libx264 -preset ultrafast -pix_fmt yuv420p -g 120 -keyint_min 120 -sc_threshold 0 -bf 2 -an \"{path}\""
        })!;
        Assert.True(process.WaitForExit(60_000), "生成 1080p 预览性能媒体超时。");
        Assert.Equal(0, process.ExitCode);
        return path;
    }

    private static double Percentile(IReadOnlyList<double> samples, double percentile)
    {
        var rank = Math.Clamp((int)Math.Ceiling(percentile * samples.Count), 1, samples.Count);
        return samples[rank - 1];
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
