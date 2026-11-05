using HomePlayer.Core;
using HomePlayer.Playback;

namespace HomePlayer.Playback.Tests;

public class DubbingMixSourceTests
{
    [Fact]
    public void Off_mode_creates_no_mix_source()
    {
        var root = CreateRoot();
        try
        {
            var media = Path.Combine(root, "movie.mkv");
            WriteDub(media, seconds: 1, sample: 8000);

            Assert.Null(DubbingMixSource.TryCreate(media, DubbingMode.Off));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void Dub_only_replaces_original_audio()
    {
        var root = CreateRoot();
        try
        {
            var media = Path.Combine(root, "movie.mkv");
            WriteDub(media, seconds: 2, sample: 8000);

            using var source = DubbingMixSource.TryCreate(media, DubbingMode.DubOnly);
            Assert.NotNull(source);

            var pcm = BuildPcm(frames: 480, sample: 1000);
            source!.Process(0.5, pcm, pcm.Length, 48000, 1);

            var output = Samples(pcm);
            Assert.All(output, value => Assert.InRange(value, 7990, 8010));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void Mixed_mode_ducks_original_under_dubbing()
    {
        var root = CreateRoot();
        try
        {
            var media = Path.Combine(root, "movie.mkv");
            WriteDub(media, seconds: 2, sample: 8000);
            var manifest = new DubbingManifest(
                DubbingFile.FormatVersion, media, "preset-female", DubbingFile.AudioPathFor(media), 24000,
                [new DubbingSegment(0, 0, 2000, "text", "译文", "preset-female", DubbingFile.AudioPathFor(media), 24000, 48000)]);
            DubbingFile.Save(manifest);

            using var source = DubbingMixSource.TryCreate(media, DubbingMode.Mixed, duckLevel: 0.25);
            Assert.NotNull(source);

            var pcm = BuildPcm(frames: 480, sample: 1000);
            source!.Process(0.5, pcm, pcm.Length, 48000, 1);

            var output = Samples(pcm);
            Assert.All(output, value => Assert.InRange(value, 7990, 9000));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void Mixed_mode_leaves_original_untouched_outside_dubbing()
    {
        var root = CreateRoot();
        try
        {
            var media = Path.Combine(root, "movie.mkv");
            WriteDub(media, seconds: 1, sample: 8000);
            var manifest = new DubbingManifest(
                DubbingFile.FormatVersion, media, "preset-female", DubbingFile.AudioPathFor(media), 24000,
                [new DubbingSegment(0, 0, 1000, "text", "译文", "preset-female", DubbingFile.AudioPathFor(media), 24000, 24000)]);
            DubbingFile.Save(manifest);

            using var source = DubbingMixSource.TryCreate(media, DubbingMode.Mixed, duckLevel: 0.25);
            Assert.NotNull(source);

            var pcm = BuildPcm(frames: 480, sample: 1000);
            source!.Process(2.0, pcm, pcm.Length, 48000, 1);

            var output = Samples(pcm);
            Assert.All(output, value => Assert.Equal(1000, value));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private static string CreateRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "HomePlayer-mix-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void WriteDub(string mediaPath, int seconds, short sample)
    {
        var samples = new short[seconds * 24000];
        Array.Fill(samples, sample);
        WavFile.WritePcm16(DubbingFile.AudioPathFor(mediaPath), samples, 24000, 1);
    }

    private static byte[] BuildPcm(int frames, short sample)
    {
        var pcm = new byte[frames * 2];
        for (var i = 0; i < frames; i++)
        {
            pcm[i * 2] = (byte)sample;
            pcm[i * 2 + 1] = (byte)(sample >> 8);
        }
        return pcm;
    }

    private static short[] Samples(byte[] pcm)
    {
        var samples = new short[pcm.Length / 2];
        for (var i = 0; i < samples.Length; i++)
            samples[i] = (short)(pcm[i * 2] | (pcm[i * 2 + 1] << 8));
        return samples;
    }
}
