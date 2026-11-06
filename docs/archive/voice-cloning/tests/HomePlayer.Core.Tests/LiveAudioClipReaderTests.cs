namespace HomePlayer.Core.Tests;

public class LiveAudioClipReaderTests
{
    [Fact]
    public void Cuts_requested_range_from_complete_wav()
    {
        var root = CreateRoot();
        try
        {
            var wav = Path.Combine(root, "audio.wav");
            WriteWav(wav, seconds: 3);
            var output = Path.Combine(root, "clip.wav");

            Assert.True(LiveAudioClipReader.TryCut(wav, 0, 0.5, 2.5, output));
            var (samples, rate, channels) = WavFile.ReadPcm16(output);
            Assert.Equal(16000, rate);
            Assert.Equal(1, channels);
            Assert.Equal(32000, samples.Length);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void Honors_extraction_start_offset()
    {
        var root = CreateRoot();
        try
        {
            var wav = Path.Combine(root, "audio.wav");
            WriteWav(wav, seconds: 3);
            var output = Path.Combine(root, "clip.wav");

            Assert.True(LiveAudioClipReader.TryCut(wav, dataStartSeconds: 100, fromSeconds: 100.5, toSeconds: 102.5, output));
            var (samples, _, _) = WavFile.ReadPcm16(output);
            Assert.Equal(32000, samples.Length);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void Refuses_clip_shorter_than_minimum()
    {
        var root = CreateRoot();
        try
        {
            var wav = Path.Combine(root, "audio.wav");
            WriteWav(wav, seconds: 1);
            var output = Path.Combine(root, "clip.wav");

            Assert.False(LiveAudioClipReader.TryCut(wav, 0, 0.2, 0.8, output));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void Clamps_to_available_data_in_growing_wav()
    {
        var root = CreateRoot();
        try
        {
            var wav = Path.Combine(root, "audio.wav");
            WriteWav(wav, seconds: 1);
            var output = Path.Combine(root, "clip.wav");

            Assert.True(LiveAudioClipReader.TryCut(wav, 0, 0, 3, output, maxSeconds: 10, minSeconds: 0.5));
            var (samples, _, _) = WavFile.ReadPcm16(output);
            Assert.Equal(16000, samples.Length);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private static string CreateRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "HomePlayer-clip-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void WriteWav(string path, int seconds)
    {
        var samples = new short[seconds * 16000];
        for (var index = 0; index < samples.Length; index++)
            samples[index] = (short)(index % 1000);
        WavFile.WritePcm16(path, samples, 16000, 1);
    }
}
