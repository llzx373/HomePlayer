using System.Diagnostics;
using HomePlayer.Ffmpeg;

namespace HomePlayer.Playback.Tests;

public sealed class SeekPreviewServiceTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), "HomePlayer.SeekPreview." + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Capture_returns_the_frame_covering_the_target()
    {
        var media = CreateMedia("frames.mp4");
        using var service = new SeekPreviewService();

        var atFrame = await Capture(service, media, 1.04);
        var insideSameFrame = await Capture(service, media, 1.05);
        var nextFrame = await Capture(service, media, 1.08);

        Assert.Equal(TimeSpan.FromMilliseconds(1040).Ticks, atFrame.Frame.PresentationTimestamp);
        Assert.Equal(TimeSpan.FromMilliseconds(40).Ticks, atFrame.Frame.Duration);
        Assert.Equal(atFrame.Frame.Data, insideSameFrame.Frame.Data);
        Assert.NotEqual(atFrame.Frame.Data, nextFrame.Frame.Data);
        Assert.Equal(TimeSpan.FromMilliseconds(1080).Ticks, nextFrame.Frame.PresentationTimestamp);
    }

    [Fact]
    public async Task Capture_handles_media_origin_and_the_last_frame()
    {
        var media = CreateMedia("offset.mp4", "-output_ts_offset 5");
        using var service = new SeekPreviewService();

        var middle = await Capture(service, media, 1.04);
        var last = await Capture(service, media, 2.99);

        Assert.Equal(TimeSpan.FromMilliseconds(1040).Ticks, middle.Frame.PresentationTimestamp);
        Assert.Equal(TimeSpan.FromMilliseconds(2960).Ticks, last.Frame.PresentationTimestamp);
    }

    [Fact]
    public async Task Cache_only_hits_the_identical_target_and_stays_bounded()
    {
        var media = CreateMedia("cache.mp4");
        using var service = new SeekPreviewService(cacheCapacity: 2);

        var first = await Capture(service, media, 0.4);
        var cached = await Capture(service, media, 0.4);
        var nearby = await Capture(service, media, 0.41);
        await Capture(service, media, 0.8);

        Assert.False(first.CacheHit);
        Assert.True(cached.CacheHit);
        Assert.False(nearby.CacheHit);
        Assert.Equal(2, service.CacheCount);
        var evicted = await Capture(service, media, 0.4);
        Assert.False(evicted.CacheHit);
    }

    [Fact]
    public async Task Cancelled_requests_do_not_block_the_latest_target()
    {
        var media = CreateMedia("cancel.mp4");
        using var service = new SeekPreviewService(cacheCapacity: 1);
        using var first = new CancellationTokenSource();
        var pending = service.CaptureAsync(media, TimeSpan.FromMilliseconds(500), first.Token);
        var obsolete = new List<(Task<SeekPreviewResult?> Task, CancellationTokenSource Source)>();
        for (var i = 1; i <= 8; i++)
        {
            var source = new CancellationTokenSource();
            obsolete.Add((service.CaptureAsync(media, TimeSpan.FromMilliseconds(500 + i * 40)), source));
            source.Cancel();
        }

        first.Cancel();
        foreach (var (task, source) in obsolete)
        {
            try
            {
                await task;
            }
            catch (OperationCanceledException)
            {
            }
            source.Dispose();
        }
        try
        {
            await pending;
        }
        catch (OperationCanceledException)
        {
        }

        var latest = await Capture(service, media, 1.4);
        Assert.Equal(TimeSpan.FromMilliseconds(1400), latest.Target);
        Assert.Equal(1, service.CacheCount);
        var cachedLatest = await Capture(service, media, 1.4);
        Assert.True(cachedLatest.CacheHit);
    }

    [Fact]
    public async Task Capture_does_not_resume_on_the_callers_synchronization_context()
    {
        var media = CreateMedia("context.mp4");
        using var service = new SeekPreviewService();
        var previous = SynchronizationContext.Current;
        var context = new TrackingSynchronizationContext();
        Task<SeekPreviewResult?> pending;
        try
        {
            SynchronizationContext.SetSynchronizationContext(context);
            pending = service.CaptureAsync(media, TimeSpan.FromMilliseconds(1040));
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }

        var result = await pending.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.NotNull(result);
        Assert.Equal(0, context.PostCount);
    }

    [Fact]
    public async Task Missing_media_fails_without_creating_a_cache_entry()
    {
        using var service = new SeekPreviewService();
        await Assert.ThrowsAsync<FileNotFoundException>(() =>
            service.CaptureAsync(Path.Combine(_directory, "missing.mp4"), TimeSpan.Zero));
        Assert.Equal(0, service.CacheCount);
    }

    private static async Task<SeekPreviewResult> Capture(SeekPreviewService service, string media, double seconds)
    {
        var result = await service.CaptureAsync(media, TimeSpan.FromSeconds(seconds)).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.NotNull(result);
        return result;
    }

    private string CreateMedia(string name, string extraArguments = "")
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, name);
        if (File.Exists(path))
            return path;
        using var process = Process.Start(new ProcessStartInfo("ffmpeg")
        {
            UseShellExecute = false,
            Arguments = $"-y -loglevel error -f lavfi -i \"testsrc2=size=64x48:rate=25:duration=3\" -c:v libx264 -pix_fmt yuv420p -g 25 -keyint_min 25 -sc_threshold 0 -bf 3 {extraArguments} -an \"{path}\""
        })!;
        Assert.True(process.WaitForExit(30_000), "生成预览测试媒体超时。");
        Assert.Equal(0, process.ExitCode);
        return path;
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

    private sealed class TrackingSynchronizationContext : SynchronizationContext
    {
        public int PostCount { get; private set; }

        public override void Post(SendOrPostCallback d, object? state)
        {
            PostCount++;
            ThreadPool.QueueUserWorkItem(_ => d(state));
        }
    }
}
