using System.Diagnostics;
using System.Text.Json;
using HomePlayer.Core;

namespace HomePlayer.Core.Tests;

public sealed class FrameLeasePerformanceTests
{
    [Fact]
    public void Measure_frame_lease_allocation_and_release()
    {
        var output = Environment.GetEnvironmentVariable("HOMEPLAYER_PERF_FRAME_LEASE_OUTPUT");
        if (string.IsNullOrWhiteSpace(output))
            return;

        var iterations = int.TryParse(Environment.GetEnvironmentVariable("HOMEPLAYER_PERF_FRAME_LEASES"), out var configured) && configured > 0
            ? configured
            : 200_000;
        var data = new byte[4];
        var planes = new[] { new VideoFramePlane(0, 4) };
        var released = 0;
        Action release = () => released++;
        Measure(iterations, data, planes, release);
        released = 0;
        var allocatedStart = GC.GetTotalAllocatedBytes(true);
        var started = Stopwatch.GetTimestamp();
        Measure(iterations, data, planes, release);
        var elapsed = Stopwatch.GetElapsedTime(started);
        var allocated = GC.GetTotalAllocatedBytes(true) - allocatedStart;
        var result = new
        {
            schema = "homeplayer.frame-lease.performance.v1",
            recordedUtc = DateTime.UtcNow,
            iterations,
            elapsedMilliseconds = elapsed.TotalMilliseconds,
            leasesPerSecond = iterations / elapsed.TotalSeconds,
            allocatedBytes = allocated,
            allocatedBytesPerLease = (double)allocated / iterations,
            released
        };
        output = Path.GetFullPath(output);
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        File.WriteAllText(output, JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"Frame lease performance written to {output}");
    }

    private static void Measure(int iterations, byte[] data, IReadOnlyList<VideoFramePlane> planes, Action release)
    {
        for (var i = 0; i < iterations; i++)
        {
            var lease = new CpuFrameLease
            {
                Width = 1,
                Height = 1,
                PixelFormat = VideoPixelFormat.Bgra32,
                Data = data,
                Planes = planes,
                ReleaseBuffer = release
            };
            lease.Dispose();
        }
    }
}
