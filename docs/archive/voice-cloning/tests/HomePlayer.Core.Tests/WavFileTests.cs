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
    public void Float32_wav_is_converted_to_pcm16()
    {
        var root = Path.Combine(Path.GetTempPath(), "HomePlayer-wav-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "float.wav");
            float[] values = [0f, 0.5f, -0.5f, 1f, -1f];
            using (var stream = new FileStream(path, FileMode.Create))
            using (var writer = new BinaryWriter(stream))
            {
                var dataLength = values.Length * 4;
                writer.Write("RIFF"u8.ToArray());
                writer.Write(36 + dataLength);
                writer.Write("WAVE"u8.ToArray());
                writer.Write("fmt "u8.ToArray());
                writer.Write(16);
                writer.Write((short)3);
                writer.Write((short)1);
                writer.Write(24000);
                writer.Write(24000 * 4);
                writer.Write((short)4);
                writer.Write((short)32);
                writer.Write("data"u8.ToArray());
                writer.Write(dataLength);
                foreach (var value in values)
                    writer.Write(value);
            }

            var (samples, rate, channels) = WavFile.ReadPcm16(path);

            Assert.Equal(24000, rate);
            Assert.Equal(1, channels);
            Assert.Equal(0, samples[0]);
            Assert.Equal(16384, samples[1]);
            Assert.Equal(-16384, samples[2]);
            Assert.Equal(32767, samples[3]);
            Assert.Equal(-32767, samples[4]);
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
