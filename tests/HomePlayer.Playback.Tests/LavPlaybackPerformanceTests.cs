using System.Diagnostics;
using System.Text.Json;
using HomePlayer.Core;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace HomePlayer.Playback.Tests;

public sealed class LavPlaybackPerformanceTests
{
    [Fact]
    public async Task Measure_lav_playback_pipeline()
    {
        var media = Environment.GetEnvironmentVariable("HOMEPLAYER_PERF_MEDIA");
        if (string.IsNullOrWhiteSpace(media))
            return;
        var plan = CodecCatalog.Plan(new AppSettings());
        if (plan.Splitters.Count == 0 || plan.VideoDecoders.Count == 0)
            return;
        var d3d = Environment.GetEnvironmentVariable("HOMEPLAYER_PERF_D3D11") == "1";
        using var device = d3d ? new PerformanceDevice() : null;
        var opens = new List<double>(30);
        var openFailures = 0;
        for (var i = 0; i < 30; i++)
        {
            try
            {
                using var engine = new LavPlaybackEngine(plan) { UseD3D11Sink = d3d, D3D11Device = device?.Shared };
                var signal = FirstFrame(engine);
                var started = Stopwatch.GetTimestamp();
                engine.Open(media);
                engine.Play();
                await signal.Task.WaitAsync(TimeSpan.FromSeconds(10));
                opens.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            }
            catch
            {
                openFailures++;
            }
        }

        var seeks = new List<double>(100);
        var seekFailures = 0;
        string? observedFormat = null;
        string? observedZeroCopy = null;
        using (var engine = new LavPlaybackEngine(plan) { UseD3D11Sink = d3d, D3D11Device = device?.Shared })
        {
            var signal = FirstFrame(engine);
            engine.Open(media);
            engine.Play();
            await signal.Task.WaitAsync(TimeSpan.FromSeconds(10));
            for (var i = 0; i < 100; i++)
            {
                try
                {
                    signal = FirstFrame(engine);
                    var target = TimeSpan.FromTicks((long)(engine.Duration.Ticks * ((i * 37 % 89 + 5) / 100d)));
                    var started = Stopwatch.GetTimestamp();
                    engine.Seek(target);
                    await signal.Task.WaitAsync(TimeSpan.FromSeconds(2));
                    seeks.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                }
                catch
                {
                    seekFailures++;
                }
            }
            observedFormat = engine.GetPipeline().PixelFormat;
            observedZeroCopy = engine.GetPipeline().ZeroCopyLevel;
            engine.Pause();
        }

        var result = new
        {
            schema = "homeplayer.lav.performance.v1",
            recordedUtc = DateTime.UtcNow,
            quality = d3d ? "LAV D3D11 Native to HomePlayer shared texture; no presenter" : "LAV RGB32 BGRA; no presenter",
            observedFormat,
            observedZeroCopy,
            open = Statistics(opens, openFailures),
            firstFrame = Statistics(opens, openFailures),
            seek = Statistics(seeks, seekFailures)
        };
        var output = Environment.GetEnvironmentVariable("HOMEPLAYER_PERF_OUTPUT")
            ?? Path.Combine("artifacts", "performance", d3d ? "lav-z4.json" : "lav-bgra.json");
        output = Path.GetFullPath(output);
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        await File.WriteAllTextAsync(output, JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"LAV performance written to {output}");
    }

    private static TaskCompletionSource FirstFrame(LavPlaybackEngine engine)
    {
        var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Handler(FrameLease frame)
        {
            frame.ReturnBuffer();
            signal.TrySetResult();
        }
        engine.FrameArrived += Handler;
        _ = signal.Task.ContinueWith(
            (_, state) => ((LavPlaybackEngine)state!).FrameArrived -= Handler,
            engine,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        return signal;
    }

    private static object Statistics(List<double> samples, int failures)
    {
        samples.Sort();
        return new
        {
            count = samples.Count,
            failures,
            p50Milliseconds = Percentile(samples, 0.5),
            p95Milliseconds = Percentile(samples, 0.95),
            p99Milliseconds = Percentile(samples, 0.99)
        };
    }

    private static double? Percentile(IReadOnlyList<double> samples, double percentile)
    {
        if (samples.Count == 0)
            return null;
        return samples[Math.Clamp((int)Math.Ceiling(percentile * samples.Count), 1, samples.Count) - 1];
    }

    private sealed class PerformanceDevice : IDisposable
    {
        private readonly ID3D11Device _device;
        private readonly ID3D11DeviceContext _context;

        public PerformanceDevice()
        {
            var result = D3D11.D3D11CreateDevice(null, DriverType.Hardware, DeviceCreationFlags.BgraSupport,
                new[] { FeatureLevel.Level_11_0 }, out _device, out _context);
            result.CheckError();
            using var dxgi = _device.QueryInterface<IDXGIDevice>();
            using var adapter = dxgi.GetAdapter();
            var luid = (uint)adapter.Description.Luid.LowPart | ((long)adapter.Description.Luid.HighPart << 32);
            Shared = new D3D11SharedDevice(_device.NativePointer, _context.NativePointer, luid, 0, new object());
        }

        public D3D11SharedDevice Shared { get; }

        public void Dispose()
        {
            _context.Dispose();
            _device.Dispose();
        }
    }
}
