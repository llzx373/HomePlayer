using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using HomePlayer.Core;
using HomePlayer.Ffmpeg;

namespace HomePlayer.Playback.Tests;

public sealed class PlaybackPerformanceTests
{
    private static bool DirectYuvEnabled => Environment.GetEnvironmentVariable("HOMEPLAYER_PERF_DISABLE_YUV") != "1";
    private static string Quality => DirectYuvEnabled
        ? "FFmpeg software decode; direct YUV planes with BGRA fallback; no renderer"
        : "FFmpeg software decode; swscale bilinear BGRA; no renderer";

    [Fact]
    public async Task Measure_native_playback_pipeline()
    {
        var media = Environment.GetEnvironmentVariable("HOMEPLAYER_PERF_MEDIA");
        if (string.IsNullOrWhiteSpace(media))
            return;

        media = Path.GetFullPath(media);
        var output = Environment.GetEnvironmentVariable("HOMEPLAYER_PERF_OUTPUT")
            ?? Path.Combine("artifacts", "performance", $"playback-{DateTime.UtcNow:yyyyMMdd-HHmmss}.json");
        var openIterations = PositiveEnvironmentInteger("HOMEPLAYER_PERF_OPENS", 30);
        var seekIterations = PositiveEnvironmentInteger("HOMEPLAYER_PERF_SEEKS", 100);
        var steadyMilliseconds = PositiveEnvironmentInteger("HOMEPLAYER_PERF_STEADY_MS", 5000);
        var openSamples = new List<double>(openIterations);
        var firstDecodedSamples = new List<double>(openIterations);
        var openFailures = 0;

        for (var i = 0; i < openIterations; i++)
        {
            try
            {
                var sample = await MeasureOpenAsync(media);
                openSamples.Add(sample.OpenMilliseconds);
                firstDecodedSamples.Add(sample.FirstDecodedMilliseconds);
            }
            catch
            {
                openFailures++;
            }
        }

        var seekSamples = new List<double>(seekIterations);
        var seekFailureIndices = new List<int>();
        var seekFailures = 0;
        Exception? playbackFault = null;
        using (var player = new FfmpegPlayer { EnableDirectYuv = DirectYuvEnabled })
        {
            player.Faulted += error => playbackFault = error;
            var nextFrame = NewFrameSignal(player);
            player.Open(media);
            player.Play();
            await nextFrame.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var duration = player.Duration;
            for (var i = 0; i < seekIterations; i++)
            {
                try
                {
                    var fraction = ((i * 37) % 89 + 5) / 100d;
                    var target = TimeSpan.FromTicks(Math.Max(0, (long)(duration.Ticks * fraction)));
                    var expectedEpoch = player.Epoch + 1;
                    nextFrame = NewFrameSignal(player, frame =>
                    {
                        var tolerance = Math.Max(1, frame.TimeBase.ToTimeSpanTicks(frame.Duration) / 2);
                        return frame.Epoch == expectedEpoch
                            && frame.TimeBase.ToTimeSpanTicks(frame.PresentationTimestamp) + tolerance >= target.Ticks;
                    });
                    var started = Stopwatch.GetTimestamp();
                    player.Seek(target);
                    await nextFrame.Task.WaitAsync(TimeSpan.FromSeconds(1));
                    seekSamples.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                }
                catch (Exception ex)
                {
                    seekFailures++;
                    seekFailureIndices.Add(i);
                    Console.Error.WriteLine($"Seek {i} failed: {ex.Message}");
                }
                if (playbackFault != null)
                {
                    for (var remaining = i + 1; remaining < seekIterations; remaining++)
                    {
                        seekFailures++;
                        seekFailureIndices.Add(remaining);
                    }
                    break;
                }
                if ((i + 1) % 10 == 0)
                    Console.Error.WriteLine($"Completed {i + 1}/{seekIterations} seeks");
            }

            player.Pause();
        }

        var steady = await MeasureSteadyAsync(media, steadyMilliseconds);
        var result = new
        {
            schema = "homeplayer.playback.performance.v1",
            recordedUtc = DateTime.UtcNow,
            media = new
            {
                fileName = Path.GetFileName(media),
                bytes = new FileInfo(media).Length,
                sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(media))).ToLowerInvariant()
            },
            environment = new
            {
                os = RuntimeInformation.OSDescription,
                architecture = RuntimeInformation.OSArchitecture.ToString(),
                processorCount = Environment.ProcessorCount,
                dotnet = RuntimeInformation.FrameworkDescription,
                gpu = "Unknown",
                display = "Unknown",
                quality = Quality
            },
            protocol = new
            {
                openIterations,
                seekIterations,
                steadyMilliseconds,
                steadyWarmupMilliseconds = 500,
                openObservation = "Decoded",
                seekObservation = "TargetDecodedFrameAfterSeekCommand",
                presentationObservation = "Unknown",
                note = "The first open is process-cold only; OS storage-cache coldness is not claimed."
            },
            open = Statistics(openSamples, openFailures, unknown: 0),
            firstDecodedFrame = Statistics(firstDecodedSamples, openFailures, unknown: 0),
            seekToTargetDecodedFrame = Statistics(seekSamples, seekFailures, unknown: 0),
            seekFailureIndices,
            playbackFault = playbackFault?.ToString(),
            firstPresentReturned = Statistics([], 0, unknown: openIterations),
            steady
        };

        output = Path.GetFullPath(output);
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        await File.WriteAllTextAsync(output, JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"Playback performance written to {output}");
    }

    private static async Task<(double OpenMilliseconds, double FirstDecodedMilliseconds)> MeasureOpenAsync(string media)
    {
        using var player = new FfmpegPlayer { EnableDirectYuv = DirectYuvEnabled };
        var firstFrame = NewFrameSignal(player);
        var started = Stopwatch.GetTimestamp();
        player.Open(media);
        var opened = Stopwatch.GetTimestamp();
        player.Play();
        await firstFrame.Task.WaitAsync(TimeSpan.FromSeconds(10));
        return (Stopwatch.GetElapsedTime(started, opened).TotalMilliseconds,
            Stopwatch.GetElapsedTime(started).TotalMilliseconds);
    }

    private static async Task<object> MeasureSteadyAsync(string media, int milliseconds)
    {
        using var player = new FfmpegPlayer { EnableDirectYuv = DirectYuvEnabled };
        var frames = 0;
        player.FrameArrived += frame =>
        {
            frame.ReturnBuffer();
            Interlocked.Increment(ref frames);
        };
        player.Open(media);
        player.Play();
        await Task.Delay(500);
        Interlocked.Exchange(ref frames, 0);
        using var process = Process.GetCurrentProcess();
        var cpuStart = process.TotalProcessorTime;
        var workingSetStart = process.WorkingSet64;
        var privateStart = process.PrivateMemorySize64;
        var allocatedStart = GC.GetTotalAllocatedBytes(true);
        var managedStart = GC.GetTotalMemory(false);
        var started = Stopwatch.GetTimestamp();
        player.Play();
        await Task.Delay(milliseconds);
        player.Pause();
        var elapsed = Stopwatch.GetElapsedTime(started);
        var cpu = process.TotalProcessorTime - cpuStart;
        var pipeline = player.GetPipeline();
        return new
        {
            elapsedMilliseconds = elapsed.TotalMilliseconds,
            decodedFrames = Volatile.Read(ref frames),
            pixelFormat = pipeline.PixelFormat,
            zeroCopyLevel = pipeline.ZeroCopyLevel,
            processCpuMilliseconds = cpu.TotalMilliseconds,
            cpuUtilizationPercent = cpu.TotalMilliseconds / elapsed.TotalMilliseconds / Environment.ProcessorCount * 100,
            allocatedBytes = GC.GetTotalAllocatedBytes(true) - allocatedStart,
            managedHeapBytes = GC.GetTotalMemory(false) - managedStart,
            workingSetBytes = process.WorkingSet64 - workingSetStart,
            privateMemoryBytes = process.PrivateMemorySize64 - privateStart,
            droppedFrames = (int?)null,
            audioVideoOffset = (double?)null,
            audioUnderruns = (int?)null,
            gpuUtilizationPercent = (double?)null,
            gpuMemoryBytes = (long?)null
        };
    }

    private static TaskCompletionSource NewFrameSignal(FfmpegPlayer player, Func<FrameLease, bool>? predicate = null)
    {
        var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Handler(FrameLease frame)
        {
            var matches = predicate?.Invoke(frame) != false;
            frame.ReturnBuffer();
            if (matches)
                signal.TrySetResult();
        }

        player.FrameArrived += Handler;
        _ = signal.Task.ContinueWith(
            (_, state) => ((FfmpegPlayer)state!).FrameArrived -= Handler,
            player,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        return signal;
    }

    private static object Statistics(List<double> samples, int failures, int unknown)
    {
        samples.Sort();
        return new
        {
            count = samples.Count,
            failures,
            cancelled = 0,
            unknown,
            p50Milliseconds = Percentile(samples, 0.50),
            p95Milliseconds = Percentile(samples, 0.95),
            p99Milliseconds = Percentile(samples, 0.99),
            maxMilliseconds = samples.Count == 0 ? (double?)null : samples[^1]
        };
    }

    private static double? Percentile(IReadOnlyList<double> samples, double percentile)
    {
        if (samples.Count == 0)
            return null;
        var rank = Math.Clamp((int)Math.Ceiling(percentile * samples.Count), 1, samples.Count);
        return samples[rank - 1];
    }

    private static int PositiveEnvironmentInteger(string name, int fallback) =>
        int.TryParse(Environment.GetEnvironmentVariable(name), out var value) && value > 0 ? value : fallback;
}
