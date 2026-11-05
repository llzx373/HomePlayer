using System.Speech.AudioFormat;
using System.Speech.Synthesis;
using HomePlayer.AI;
using HomePlayer.Core;

namespace HomePlayer;

/// <summary>
/// 没有导入音色时，用系统语音合成一段参考音频作为内置音色。
/// 这样默认配音不依赖任何未授权的人声样本。
/// </summary>
internal static class VoicePresets
{
    private const string Sample = "大家好，这是一段用于中文配音的参考语音。";

    public static IReadOnlyList<VoiceProfile> Ensure(VoiceLibrary library)
    {
        var voices = library.List();
        if (voices.Count > 0)
            return voices;

        try
        {
            using var synthesizer = new SpeechSynthesizer();
            var installed = synthesizer.GetInstalledVoices()
                .Where(voice => voice.Enabled)
                .Select(voice => voice.VoiceInfo)
                .ToArray();
            if (installed.Length == 0)
                return voices;

            var chinese = installed
                .Where(voice => voice.Culture.Name.StartsWith("zh", StringComparison.OrdinalIgnoreCase))
                .ToArray();
            var pool = chinese.Length > 0 ? chinese : installed;
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (id, gender) in new[] { ("preset-female", VoiceGender.Female), ("preset-male", VoiceGender.Male) })
            {
                var info = pool.FirstOrDefault(voice => voice.Gender == gender)
                    ?? pool.FirstOrDefault(voice => !used.Contains(voice.Name));
                if (info == null || !used.Add(info.Name))
                    continue;

                var temporary = Path.Combine(Path.GetTempPath(), id + "-" + Guid.NewGuid().ToString("N") + ".wav");
                try
                {
                    synthesizer.SelectVoice(info.Name);
                    synthesizer.SetOutputToWaveFile(temporary, new SpeechAudioFormatInfo(16000, AudioBitsPerSample.Sixteen, AudioChannel.Mono));
                    synthesizer.Speak(Sample);
                    synthesizer.SetOutputToNull();
                    library.Add(id, temporary, Sample);
                }
                catch (Exception)
                {
                }
                finally
                {
                    if (File.Exists(temporary))
                        File.Delete(temporary);
                }
            }
        }
        catch (Exception)
        {
        }

        return library.List();
    }
}
