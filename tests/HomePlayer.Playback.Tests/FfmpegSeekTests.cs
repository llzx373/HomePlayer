using System.Buffers.Binary;
using System.Diagnostics;
using HomePlayer.Ffmpeg;

namespace HomePlayer.Playback.Tests;

public sealed class FfmpegSeekTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), "HomePlayer.Seek." + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Aac_seek_trims_the_frame_at_the_target_sample()
    {
        var media = CreateAacMedia();
        const int sampleRate = 48000;
        var target = TimeSpan.FromMilliseconds(501);
        using var player = new FfmpegPlayer();
        var expectedEpoch = 0L;
        var collecting = false;
        var samples = new List<short>();
        var enoughSamples = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Exception? fault = null;
        player.FrameArrived += frame => frame.ReturnBuffer();
        player.Faulted += error => fault = error;
        player.AudioPcm += (data, count, epoch) =>
        {
            if (!collecting || epoch != expectedEpoch)
                return;
            lock (samples)
            {
                for (var i = 0; i + 1 < count && samples.Count < 2048; i += 2)
                    samples.Add(BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan(i, 2)));
                if (samples.Count >= 1024)
                    enoughSamples.TrySetResult();
            }
        };
        player.Open(media);
        expectedEpoch = player.Epoch + 1;
        collecting = true;
        player.Seek(target);
        player.Play();

        await enoughSamples.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Null(fault);
        short[] actual;
        lock (samples)
            actual = samples.Take(1024).ToArray();
        var targetSample = (int)Math.Round(target.TotalSeconds * sampleRate);
        var bestOffset = FindBestSineOffset(actual, targetSample, sampleRate);

        Assert.True(Math.Abs(bestOffset) <= 4,
            $"AAC seek 的首段 PCM 应对齐目标样本，实际偏移为 {bestOffset} 个样本。");
    }

    [Fact]
    public async Task Playing_seek_preroll_releases_audio_before_target_frame()
    {
        var media = CreateAacMedia();
        using var player = new FfmpegPlayer();
        var expectedEpoch = 0L;
        var audioReady = 0;
        var prerollReady = 0;
        var orderingError = 0;
        var targetFrame = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        player.AudioPcm += (_, _, epoch) =>
        {
            if (epoch == expectedEpoch)
                Interlocked.Exchange(ref audioReady, 1);
        };
        player.SeekPrerollReady += epoch =>
        {
            if (epoch != expectedEpoch)
                return;
            if (Volatile.Read(ref audioReady) == 0)
                Interlocked.Exchange(ref orderingError, 1);
            Interlocked.Increment(ref prerollReady);
        };
        player.FrameArrived += frame =>
        {
            if (frame.Epoch == expectedEpoch && frame.SeekStartedTimestamp > 0)
            {
                if (Volatile.Read(ref prerollReady) == 0)
                    Interlocked.Exchange(ref orderingError, 1);
                targetFrame.TrySetResult();
            }
            frame.ReturnBuffer();
        };
        player.Open(media);
        expectedEpoch = player.Epoch + 1;
        player.Seek(TimeSpan.FromMilliseconds(501));
        player.Play();

        await targetFrame.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(0, Volatile.Read(ref orderingError));
        Assert.Equal(1, Volatile.Read(ref prerollReady));
    }

    [Fact]
    public async Task Nonzero_container_start_time_is_relative_to_the_media_origin()
    {
        var media = CreateOffsetMedia();
        using var player = new FfmpegPlayer();
        var firstFrame = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
        player.FrameArrived += frame =>
        {
            firstFrame.TrySetResult(frame.TimeBase.ToTimeSpanTicks(frame.PresentationTimestamp));
            frame.ReturnBuffer();
        };
        player.Open(media);
        player.Play();

        var position = await firstFrame.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.InRange(position, 0, TimeSpan.FromMilliseconds(100).Ticks);
    }

    [Fact]
    public async Task Elementary_h264_without_container_timestamps_advances_monotonically()
    {
        var media = CreateTimestampLessMedia();
        using var player = new FfmpegPlayer();
        var positions = new List<long>();
        var durations = new List<long>();
        var enoughFrames = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        player.FrameArrived += frame =>
        {
            lock (positions)
            {
                if (positions.Count < 4)
                {
                    positions.Add(frame.PresentationTimestamp);
                    durations.Add(frame.Duration);
                }
                if (positions.Count == 4)
                    enoughFrames.TrySetResult();
            }
            frame.ReturnBuffer();
        };
        player.Open(media);
        player.Play();

        await enoughFrames.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(4, positions.Count);
        for (var i = 1; i < positions.Count; i++)
            Assert.True(positions[i] > positions[i - 1], $"第 {i} 帧时间戳没有前进。");
        Assert.All(durations, duration => Assert.True(duration > 0));
    }

    [Fact]
    public async Task Open_gop_seek_matches_sequential_decode()
    {
        var media = CreateOpenGopMedia();
        var target = TimeSpan.FromMilliseconds(1040);

        var expected = await CaptureBgra(media, target, false);
        var actual = await CaptureBgra(media, target, true);

        Assert.Equal(expected.PositionTicks, actual.PositionTicks);
        Assert.Equal(expected.Width, actual.Width);
        Assert.Equal(expected.Height, actual.Height);
        Assert.Equal(expected.Data, actual.Data);
    }

    private static async Task<FrameSnapshot> CaptureBgra(string media, TimeSpan target, bool seek)
    {
        using var player = new FfmpegPlayer { EnableDirectYuv = false };
        var captured = new TaskCompletionSource<FrameSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        player.FrameArrived += frame =>
        {
            var position = frame.TimeBase.ToTimeSpanTicks(frame.PresentationTimestamp);
            if (position < target.Ticks)
            {
                frame.ReturnBuffer();
                return;
            }
            var bgra = (HomePlayer.Core.VideoFrame)frame;
            var data = new byte[bgra.Stride * bgra.Height];
            Buffer.BlockCopy(bgra.Data, 0, data, 0, data.Length);
            captured.TrySetResult(new FrameSnapshot(position, bgra.Width, bgra.Height, data));
            frame.ReturnBuffer();
        };
        player.Open(media);
        if (seek)
            player.Seek(target);
        player.Play();
        return await captured.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    private string CreateOffsetMedia()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "offset-start.mp4");
        using var process = Process.Start(new ProcessStartInfo("ffmpeg")
        {
            UseShellExecute = false,
            Arguments = $"-y -loglevel error -f lavfi -i \"testsrc2=size=64x48:rate=25:duration=3\" -c:v libx264 -pix_fmt yuv420p -an -output_ts_offset 5 \"{path}\""
        })!;
        Assert.True(process.WaitForExit(30_000), "生成非零起始时间戳测试媒体超时。");
        Assert.Equal(0, process.ExitCode);
        return path;
    }

    private string CreateTimestampLessMedia()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "missing-timestamps.h264");
        using var process = Process.Start(new ProcessStartInfo("ffmpeg")
        {
            UseShellExecute = false,
            Arguments = $"-y -loglevel error -f lavfi -i \"testsrc2=size=64x48:rate=25:duration=3\" -c:v libx264 -pix_fmt yuv420p -an -f h264 \"{path}\""
        })!;
        Assert.True(process.WaitForExit(30_000), "生成无容器时间戳测试媒体超时。");
        Assert.Equal(0, process.ExitCode);
        return path;
    }

    private string CreateOpenGopMedia()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "open-gop.mp4");
        using var process = Process.Start(new ProcessStartInfo("ffmpeg")
        {
            UseShellExecute = false,
            Arguments = $"-y -loglevel error -f lavfi -i \"testsrc2=size=64x48:rate=25:duration=3\" -c:v libx264 -pix_fmt yuv420p -g 25 -keyint_min 25 -sc_threshold 0 -bf 3 -x264-params \"open-gop=1:b-pyramid=2\" -an \"{path}\""
        })!;
        Assert.True(process.WaitForExit(30_000), "生成 open GOP 测试媒体超时。");
        Assert.Equal(0, process.ExitCode);
        return path;
    }

    private sealed record FrameSnapshot(long PositionTicks, int Width, int Height, byte[] Data);

    private string CreateAacMedia()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "aac-seek.mp4");
        using var process = Process.Start(new ProcessStartInfo("ffmpeg")
        {
            UseShellExecute = false,
            Arguments = $"-y -loglevel error -f lavfi -i \"testsrc2=size=64x48:rate=25:duration=3\" -f lavfi -i \"sine=frequency=440:sample_rate=48000:duration=3\" -map 0:v -map 1:a -c:v libx264 -pix_fmt yuv420p -g 25 -keyint_min 25 -sc_threshold 0 -c:a aac -b:a 128k \"{path}\""
        })!;
        Assert.True(process.WaitForExit(30_000), "生成 AAC seek 测试媒体超时。");
        Assert.Equal(0, process.ExitCode);
        return path;
    }

    private static int FindBestSineOffset(IReadOnlyList<short> actual, int targetSample, int sampleRate)
    {
        var bestOffset = 0;
        var bestScore = double.MinValue;
        var halfPeriod = sampleRate / 440 / 2;
        for (var offset = -halfPeriod; offset <= halfPeriod; offset++)
        {
            double actualEnergy = 0;
            double referenceEnergy = 0;
            double score = 0;
            for (var i = 0; i < actual.Count; i++)
            {
                var reference = Math.Sin((targetSample + i + offset) * 440 * Math.PI * 2 / sampleRate) * 4096;
                score += actual[i] * reference;
                actualEnergy += (double)actual[i] * actual[i];
                referenceEnergy += reference * reference;
            }
            score /= Math.Sqrt(actualEnergy * referenceEnergy);
            if (score > bestScore)
            {
                bestScore = score;
                bestOffset = offset;
            }
        }
        return bestOffset;
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
