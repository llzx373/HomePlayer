using HomePlayer.AI;
using HomePlayer.Core;

namespace HomePlayer.AI.Tests;

public class SpeakerVoicePlannerTests
{
    [Fact]
    public void Cues_are_assigned_to_speakers_with_pitch_matched_voices()
    {
        var root = Path.Combine(Path.GetTempPath(), "HomePlayer-speakers-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var wav = Path.Combine(root, "audio.wav");
            const int rate = 16000;
            var samples = new short[rate * 5];
            for (var index = 0; index < samples.Length; index++)
            {
                var seconds = index / (double)rate;
                var frequency = seconds < 2.5 ? 120 : 220;
                samples[index] = (short)(Math.Sin(2 * Math.PI * frequency * seconds) * 8000);
            }

            WavFile.WritePcm16(wav, samples, rate, 1);

            SubtitleCue[] cues =
            [
                new(TimeSpan.FromSeconds(0), TimeSpan.FromSeconds(2), "a", Translation: "甲"),
                new(TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(5), "b", Translation: "乙")
            ];
            var analysis = new SpeakerAnalysis(wav,
            [
                new SpeakerSegment(0, 0, 2.5),
                new SpeakerSegment(1, 2.8, 5)
            ]);
            VoiceProfile[] voices =
            [
                new("preset-female", "女声", "female.wav", "参考"),
                new("preset-male", "男声", "male.wav", "参考")
            ];

            var assignment = SpeakerVoicePlanner.Assign(cues, analysis, voices);

            Assert.Equal(2, assignment.Count);
            Assert.Equal("preset-male", assignment[0]);
            Assert.Equal("preset-female", assignment[1]);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void References_are_cut_one_per_speaker()
    {
        var root = Path.Combine(Path.GetTempPath(), "HomePlayer-speakers-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var wav = Path.Combine(root, "audio.wav");
            const int rate = 16000;
            var samples = new short[rate * 6];
            for (var index = 0; index < samples.Length; index++)
                samples[index] = (short)(Math.Sin(2 * Math.PI * 180 * index / rate) * 6000);
            WavFile.WritePcm16(wav, samples, rate, 1);

            SubtitleCue[] cues =
            [
                new(TimeSpan.FromSeconds(0), TimeSpan.FromSeconds(2), "a", Translation: "甲"),
                new(TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(5), "b", Translation: "乙")
            ];
            var analysis = new SpeakerAnalysis(wav,
            [
                new SpeakerSegment(0, 0, 2.5),
                new SpeakerSegment(1, 2.8, 5)
            ]);

            var references = SpeakerVoicePlanner.AssignReferences(cues, analysis, Path.Combine(root, "refs"));

            Assert.Equal(2, references.Count);
            Assert.NotEqual(references[0].AudioPath, references[1].AudioPath);
            Assert.True(File.Exists(references[0].AudioPath));
            var (cut, cutRate, channels) = WavFile.ReadPcm16(references[0].AudioPath);
            Assert.Equal(rate, cutRate);
            Assert.Equal(1, channels);
            Assert.InRange(cut.Length, rate, rate * 2 + 100);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void Cues_without_speaker_overlap_are_left_unassigned()
    {
        var root = Path.Combine(Path.GetTempPath(), "HomePlayer-speakers-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var wav = Path.Combine(root, "audio.wav");
            WavFile.WritePcm16(wav, new short[16000 * 2], 16000, 1);
            SubtitleCue[] cues = [new(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(6), "a", Translation: "甲")];
            var analysis = new SpeakerAnalysis(wav, [new SpeakerSegment(0, 0, 2)]);
            VoiceProfile[] voices = [new("preset-female", "女声", "female.wav", "参考")];

            var assignment = SpeakerVoicePlanner.Assign(cues, analysis, voices);

            Assert.Empty(assignment);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }
}
