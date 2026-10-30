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
