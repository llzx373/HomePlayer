using System.Buffers.Binary;
using HomePlayer.Core;

namespace HomePlayer.Core.Tests;

public class PcmAudioProcessingTests
{
    [Fact]
    public void Time_stretch_preserves_sine_frequency_and_multichannel_alignment()
    {
        const int rate = 48000;
        const int channels = 2;
        var input = new byte[rate * 2 * channels * 2];
        for (var frame = 0; frame < rate * 2; frame++)
        {
            var sample = (short)(Math.Sin(frame * 2 * Math.PI * 440 / rate) * 12000);
            for (var channel = 0; channel < channels; channel++)
                BinaryPrimitives.WriteInt16LittleEndian(input.AsSpan((frame * channels + channel) * 2, 2), sample);
        }
        var stretcher = new PcmTimeStretcher();
        var output = new List<byte>();
        for (var offset = 0; offset < input.Length; offset += 9600)
            output.AddRange(stretcher.Process(input[offset..Math.Min(input.Length, offset + 9600)], Math.Min(9600, input.Length - offset), channels, 1.5));

        Assert.Equal(0, output.Count % (channels * 2));
        var duration = output.Count / 2 / channels / (double)rate;
        Assert.InRange(duration, 1.1, 1.55);
        var crossings = 0;
        var previous = 0;
        for (var frame = 100; frame < output.Count / 2 / channels; frame++)
        {
            var current = BinaryPrimitives.ReadInt16LittleEndian(output.ToArray().AsSpan(frame * channels * 2, 2));
            if (previous <= 0 && current > 0)
                crossings++;
            previous = current;
        }
        var frequency = crossings / duration;
        Assert.InRange(frequency, 400, 480);
    }

    [Fact]
    public void Night_mode_limits_peaks_without_changing_sample_count()
    {
        var pcm = new byte[4];
        BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(0, 2), 30000);
        BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(2, 2), -30000);
        var effects = new PcmAudioEffects();

        effects.Apply(pcm, pcm.Length, normalize: false, nightMode: true);

        Assert.Equal(4, pcm.Length);
        Assert.True(Math.Abs(BinaryPrimitives.ReadInt16LittleEndian(pcm.AsSpan(0, 2))) < 30000);
        Assert.True(Math.Abs(BinaryPrimitives.ReadInt16LittleEndian(pcm.AsSpan(2, 2))) < 30000);
    }

    [Fact]
    public void Preferred_language_is_used_when_no_file_choice_exists()
    {
        var tracks = new[]
        {
            new MediaTrack(1, MediaTrackKind.Audio, "English", "en", true),
            new MediaTrack(2, MediaTrackKind.Audio, "Japanese", "ja", false)
        };

        var selected = MediaPreferenceSelection.AudioTrack(null, tracks, "ja");

        Assert.Equal(2, selected);
    }
}
