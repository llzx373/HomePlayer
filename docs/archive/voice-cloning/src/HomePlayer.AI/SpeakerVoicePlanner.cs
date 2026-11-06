using HomePlayer.Core;

namespace HomePlayer.AI;

public sealed record SpeakerReference(int Speaker, string AudioPath, string Transcript);

/// <summary>
/// 把字幕句分配到说话人，并按音高给每个说话人挑一个预设音色。
/// 跨语言音色克隆在本模型的 ONNX 版本上效果不可用，所以这里先用预设音色区分角色。
/// </summary>
public static class SpeakerVoicePlanner
{
    public static IReadOnlyDictionary<int, string> Assign(
        IReadOnlyList<SubtitleCue> cues,
        SpeakerAnalysis analysis,
        IReadOnlyList<VoiceProfile> voices)
    {
        var result = new Dictionary<int, string>();
        if (voices.Count == 0 || analysis.Segments.Count == 0)
            return result;

        var bySpeaker = new Dictionary<int, List<int>>();
        for (var index = 0; index < cues.Count; index++)
        {
            var speaker = DominantSpeaker(cues[index], analysis.Segments);
            if (speaker < 0)
                continue;
            if (!bySpeaker.TryGetValue(speaker, out var list))
                bySpeaker[speaker] = list = [];
            list.Add(index);
        }

        var female = voices.FirstOrDefault(voice => voice.Id.Contains("female", StringComparison.OrdinalIgnoreCase));
        var male = voices.FirstOrDefault(voice =>
            voice.Id.Contains("male", StringComparison.OrdinalIgnoreCase)
            && !voice.Id.Contains("female", StringComparison.OrdinalIgnoreCase));

        foreach (var (_, indices) in bySpeaker.OrderBy(pair => pair.Key))
        {
            var longest = indices
                .OrderByDescending(index => (cues[index].End - cues[index].Start).TotalSeconds)
                .First();
            var pitch = MedianPitch(analysis.AudioPath, cues[longest].Start, cues[longest].End);
            var voice = pitch > 0 && pitch < 165 ? male ?? female : female ?? male;
            voice ??= voices[0];
            foreach (var index in indices)
                result[index] = voice.Id;
        }

        return result;
    }

    /// <summary>
    /// VoxCPM 用：给每个说话人切一段参考音频（最长的那句），并带上该句原文作为逐字稿。
    /// </summary>
    public static IReadOnlyDictionary<int, SpeakerReference> AssignReferences(
        IReadOnlyList<SubtitleCue> cues,
        SpeakerAnalysis analysis,
        string directory)
    {
        var result = new Dictionary<int, SpeakerReference>();
        var groups = GroupBySpeaker(cues, analysis.Segments);
        if (groups.Count == 0)
            return result;

        Directory.CreateDirectory(directory);
        foreach (var (speaker, indices) in groups.OrderBy(pair => pair.Key))
        {
            var longest = indices
                .OrderByDescending(index => (cues[index].End - cues[index].Start).TotalSeconds)
                .First();
            var path = Path.Combine(directory, $"speaker-{speaker}.wav");
            if (!CutReference(analysis.AudioPath, cues[longest].Start, cues[longest].End, path))
                continue;
            var reference = new SpeakerReference(speaker, path, cues[longest].Text.Trim());
            foreach (var index in indices)
                result[index] = reference;
        }

        return result;
    }

    private static Dictionary<int, List<int>> GroupBySpeaker(IReadOnlyList<SubtitleCue> cues, IReadOnlyList<SpeakerSegment> segments)
    {
        var groups = new Dictionary<int, List<int>>();
        for (var index = 0; index < cues.Count; index++)
        {
            var speaker = DominantSpeaker(cues[index], segments);
            if (speaker < 0)
                continue;
            if (!groups.TryGetValue(speaker, out var list))
                groups[speaker] = list = [];
            list.Add(index);
        }

        return groups;
    }

    private static bool CutReference(string audioPath, TimeSpan from, TimeSpan to, string outputPath, double maxSeconds = 10)
    {
        try
        {
            var info = WavFile.ReadInfo(audioPath);
            if (info.Channels != 1 || info.SampleRate < 8000)
                return false;
            var rate = info.SampleRate;
            var first = Math.Max(0, (int)(from.TotalSeconds * rate));
            var last = Math.Min((int)info.Frames, (int)(to.TotalSeconds * rate));
            last = Math.Min(last, first + (int)(maxSeconds * rate));
            if (last - first < rate / 2)
                return false;

            using var stream = new FileStream(audioPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            stream.Position = info.DataOffset + first * 2L;
            var bytes = new byte[(last - first) * 2];
            stream.ReadExactly(bytes);
            var samples = new short[bytes.Length / 2];
            Buffer.BlockCopy(bytes, 0, samples, 0, bytes.Length);
            WavFile.WritePcm16(outputPath, samples, rate, 1);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return false;
        }
    }

    private static int DominantSpeaker(SubtitleCue cue, IReadOnlyList<SpeakerSegment> segments)
    {
        var best = -1;
        var bestOverlap = 0.1;
        var start = cue.Start.TotalSeconds;
        var end = cue.End.TotalSeconds;
        foreach (var segment in segments)
        {
            var overlap = Math.Min(end, segment.EndSeconds) - Math.Max(start, segment.StartSeconds);
            if (overlap > bestOverlap)
            {
                bestOverlap = overlap;
                best = segment.Speaker;
            }
        }

        return best;
    }

    internal static double MedianPitch(string audioPath, TimeSpan from, TimeSpan to)
    {
        try
        {
            var info = WavFile.ReadInfo(audioPath);
            if (info.Channels != 1 || info.SampleRate < 8000)
                return 0;
            var rate = info.SampleRate;
            var first = Math.Max(0, (int)(from.TotalSeconds * rate));
            var last = Math.Min((int)info.Frames, (int)(to.TotalSeconds * rate));
            if (last - first < rate / 2)
                return 0;

            using var stream = new FileStream(audioPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            stream.Position = info.DataOffset + first * 2L;
            var bytes = new byte[(last - first) * 2];
            stream.ReadExactly(bytes);
            var samples = new short[bytes.Length / 2];
            Buffer.BlockCopy(bytes, 0, samples, 0, bytes.Length);

            var frame = rate * 40 / 1000;
            var hop = rate * 20 / 1000;
            var minLag = rate / 350;
            var maxLag = rate / 70;
            var estimates = new List<double>();
            for (var offset = 0; offset + frame + maxLag <= samples.Length; offset += hop)
            {
                double energy = 0;
                for (var index = 0; index < frame; index++)
                    energy += samples[offset + index] * (double)samples[offset + index];
                if (energy / frame < 1e-4)
                    continue;

                var bestScore = 0.0;
                var scores = new double[maxLag - minLag + 1];
                for (var lag = minLag; lag <= maxLag; lag++)
                {
                    double sum = 0, left = 0, right = 0;
                    for (var index = 0; index < frame; index++)
                    {
                        double a = samples[offset + index];
                        double b = samples[offset + index + lag];
                        sum += a * b;
                        left += a * a;
                        right += b * b;
                    }

                    var score = sum / (Math.Sqrt(left * right) + 1e-9);
                    scores[lag - minLag] = score;
                    if (score > bestScore)
                        bestScore = score;
                }

                if (bestScore <= 0.5)
                    continue;

                // 全局峰值可能是真实周期的整数倍，若某个更短的周期同样接近峰值则取它。
                var bestLag = 0;
                for (var lag = minLag; lag <= maxLag; lag++)
                {
                    if (scores[lag - minLag] >= bestScore)
                    {
                        bestLag = lag;
                        break;
                    }
                }

                for (var divisor = 2; divisor <= 3; divisor++)
                {
                    var candidate = bestLag / divisor;
                    if (candidate >= minLag && scores[candidate - minLag] >= bestScore * 0.85)
                    {
                        bestLag = candidate;
                        break;
                    }
                }

                if (bestLag > 0)
                    estimates.Add(rate / (double)bestLag);
            }

            if (estimates.Count < 4)
                return 0;
            estimates.Sort();
            return estimates[estimates.Count / 2];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return 0;
        }
    }
}
