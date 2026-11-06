using HomePlayer.Core;
using HomePlayer.Playback;

namespace HomePlayer.Playback.Tests;

public class LiveDubbingMixSourceTests
{
    [Fact]
    public void Mixes_segment_and_ducks_original()
    {
        var track = new LiveDubbingTrack();
        track.Add(Segment(0, 0, 2, sample: 8000));
        using var source = new LiveDubbingMixSource(track);

        var pcm = BuildPcm(frames: 480, sample: 1000);
        source.Process(0.5, pcm, pcm.Length, 48000, 1);

        var output = Samples(pcm);
        Assert.All(output, value => Assert.InRange(value, 7990, 9000));
    }

    [Fact]
    public void Leaves_original_untouched_outside_segments()
    {
        var track = new LiveDubbingTrack();
        track.Add(Segment(0, 0, 1, sample: 8000));
        using var source = new LiveDubbingMixSource(track);

        var pcm = BuildPcm(frames: 480, sample: 1000);
        source.Process(2.0, pcm, pcm.Length, 48000, 1);

        Assert.All(Samples(pcm), value => Assert.Equal(1000, value));
    }

    [Fact]
    public void Reset_drops_segments_until_new_audio_arrives()
    {
        var track = new LiveDubbingTrack();
        track.Add(Segment(0, 0, 2, sample: 8000));
        using var source = new LiveDubbingMixSource(track);

        track.Reset(0);
        var pcm = BuildPcm(frames: 480, sample: 1000);
        source.Process(0.5, pcm, pcm.Length, 48000, 1);

        Assert.All(Samples(pcm), value => Assert.Equal(1000, value));
    }

    [Fact]
    public void Resamples_24k_segment_to_48k_output()
    {
        var track = new LiveDubbingTrack();
        track.Add(Segment(0, 0, 1, sample: 12000));
        using var source = new LiveDubbingMixSource(track);

        var pcm = BuildPcm(frames: 480, sample: 0);
        source.Process(0.25, pcm, pcm.Length, 48000, 1);

        var output = Samples(pcm);
        Assert.All(output, value => Assert.InRange(value, 11990, 12010));
    }

    private static LiveDubbingSegment Segment(long key, double start, double end, short sample)
    {
        var samples = new short[(int)Math.Round((end - start) * 24000)];
        Array.Fill(samples, sample);
        return new LiveDubbingSegment(key, start, end, 24000, samples);
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
