using HomePlayer.Core;

namespace HomePlayer.Core.Tests;

public class WavFileTests
{
    [Fact]
    public void Pcm16_roundtrip_preserves_samples_and_format()
    {
        var root = Path.Combine(Path.GetTempPath(), "HomePlayer-wav-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "voice.wav");
            short[] samples = [0, 100, -100, 32767, -32768];

            WavFile.WritePcm16(path, samples, 24000, 1);
            var (read, rate, channels) = WavFile.ReadPcm16(path);

            Assert.Equal(samples, read);
            Assert.Equal(24000, rate);
            Assert.Equal(1, channels);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void Streaming_writer_matches_one_shot_writer()
    {
        var root = Path.Combine(Path.GetTempPath(), "HomePlayer-wav-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            short[] samples = [1, 2, 3, 4];
            var path = Path.Combine(root, "track.wav");
            using (var writer = WavFile.CreatePcm16Writer(path, samples.Length * 2, 16000, 1))
            {
                foreach (var sample in samples)
                    writer.Write(sample);
            }

            var (read, rate, _) = WavFile.ReadPcm16(path);

            Assert.Equal(samples, read);
            Assert.Equal(16000, rate);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }
}
