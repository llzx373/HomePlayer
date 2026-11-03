using HomePlayer.Ffmpeg;

namespace HomePlayer.Playback.Tests;

public sealed class FfmpegAudioExtractorTests
{
    [Fact]
    public void ExtractWav_converts_stereo_48khz_to_mono_16khz()
    {
        var directory = Path.Combine(Path.GetTempPath(), "HomePlayer-audio-extract-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var input = Path.Combine(directory, "input.wav");
            var output = Path.Combine(directory, "output.wav");
            WriteTone(input);
            FfmpegAudioExtractor.ExtractWav(input, 0, output, CancellationToken.None);

            using var stream = File.OpenRead(output);
            using var reader = new BinaryReader(stream);
            Assert.Equal("RIFF", new string(reader.ReadChars(4)));
            reader.ReadUInt32();
            Assert.Equal("WAVE", new string(reader.ReadChars(4)));
            Assert.Equal("fmt ", new string(reader.ReadChars(4)));
            Assert.Equal(16, reader.ReadInt32());
            Assert.Equal(1, reader.ReadInt16());
            Assert.Equal(1, reader.ReadInt16());
            Assert.Equal(16_000, reader.ReadInt32());
            Assert.Equal(32_000, reader.ReadInt32());
            Assert.Equal(2, reader.ReadInt16());
            Assert.Equal(16, reader.ReadInt16());
            while (stream.Position < stream.Length - 8)
            {
                var chunk = new string(reader.ReadChars(4));
                var length = reader.ReadUInt32();
                if (chunk == "data")
                {
                    Assert.Equal(length, (uint)(stream.Length - stream.Position));
                    return;
                }
                stream.Position += length + length % 2;
            }
            Assert.Fail("没有找到 WAV data 块。");
            var bytes = 0u;
            Assert.InRange(bytes, 30_000u, 34_000u);
            Assert.Equal(bytes, stream.Length - stream.Position);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    private static void WriteTone(string path)
    {
        using var stream = File.Create(path);
        using var writer = new BinaryWriter(stream);
        const int samples = 48_000;
        const int dataBytes = samples * 4;
        writer.Write("RIFF"u8.ToArray());
        writer.Write(36 + dataBytes);
        writer.Write("WAVEfmt "u8.ToArray());
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)2);
        writer.Write(48_000);
        writer.Write(192_000);
        writer.Write((short)4);
        writer.Write((short)16);
        writer.Write("data"u8.ToArray());
        writer.Write(dataBytes);
        for (var index = 0; index < samples; index++)
        {
            var sample = (short)(Math.Sin(index * Math.PI * 2 / 48) * 10_000);
            writer.Write(sample);
            writer.Write(sample);
        }
    }
}
