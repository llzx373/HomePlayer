using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using HomePlayer.Core;

namespace HomePlayer.AI;

public sealed record DubbingRequest(string MediaPath, IReadOnlyList<SubtitleCue> Cues, string VoiceId, int Steps = 2);

public sealed record DubbingResult(
    DubbingManifest Manifest,
    int Generated,
    int Failed,
    int Truncated,
    int Overlapped);

/// <summary>
/// 把带译文的字幕逐句合成配音，并拼成一条与视频时间轴对齐的配音音轨。
/// 原声混音与实时预取留到播放器接入阶段。
/// </summary>
public sealed class DubbingService(AiModelManager models, Func<string> mirror, string voicesDirectory)
{
    private const double MaxCompression = 1.25;
    private const double FitTolerance = 1.02;

    public async Task<DubbingResult> GenerateAsync(
        DubbingRequest request,
        AiComputeDevice device,
        IProgress<AiSubtitleProgress>? progress,
        CancellationToken cancellationToken)
    {
        var cues = request.Cues
            .Where(cue => cue.HasTranslation && cue.End > cue.Start)
            .OrderBy(cue => cue.Start)
            .ToArray();
        if (cues.Length == 0)
            throw new InvalidOperationException("当前字幕没有译文，无法生成配音。");
        if (string.IsNullOrWhiteSpace(request.VoiceId))
            throw new InvalidOperationException("请先选择或导入音色。");

        var work = Path.Combine(AppPaths.DubbingCache, MediaKey(request.MediaPath));
        if (Directory.Exists(work))
            Directory.Delete(work, true);
        Directory.CreateDirectory(work);

        await using var tts = new TtsService(models, device, mirror, voicesDirectory);
        await tts.StartAsync(new Progress<AiSubtitleProgress>(value => Report(progress, value.Stage, value.Value * 0.12)), cancellationToken);

        var segments = new List<DubbingSegment>(cues.Length);
        var failed = 0;
        var truncated = 0;
        for (var index = 0; index < cues.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var cue = cues[index];
            Report(progress, $"合成配音 {index + 1}/{cues.Length}", 0.12 + 0.78 * index / cues.Length);
            var path = Path.Combine(work, index.ToString("D5", CultureInfo.InvariantCulture) + ".wav");
            var text = cue.Translation!.Trim();
            try
            {
                var (sampleRate, _) = await tts.SynthesizeAsync(text, request.VoiceId, path, request.Steps, cancellationToken);
                var (samples, wasTruncated) = Fit(path, sampleRate, (cue.End - cue.Start).TotalSeconds);
                if (wasTruncated)
                    truncated++;
                segments.Add(new DubbingSegment(
                    index,
                    (int)Math.Round(cue.Start.TotalMilliseconds),
                    (int)Math.Round(cue.End.TotalMilliseconds),
                    cue.Text,
                    text,
                    request.VoiceId,
                    path,
                    sampleRate,
                    samples));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception)
            {
                failed++;
            }
        }

        if (segments.Count == 0)
            throw new InvalidOperationException("所有句子都合成失败，请检查音色和语音合成模型。");

        Report(progress, "写入配音音轨", 0.93);
        var audioPath = DubbingFile.AudioPathFor(request.MediaPath);
        var overlapped = WriteTrack(audioPath, segments);
        var manifest = new DubbingManifest(
            DubbingFile.FormatVersion,
            request.MediaPath,
            request.VoiceId,
            audioPath,
            segments[0].SampleRate,
            segments);
        DubbingFile.Save(manifest);
        Report(progress, "配音完成", 1);
        return new DubbingResult(manifest, segments.Count, failed, truncated, overlapped);
    }

    private static (long Samples, bool Truncated) Fit(string path, int sampleRate, double targetSeconds)
    {
        var (samples, rate, channels) = WavFile.ReadPcm16(path);
        if (rate != sampleRate || channels != 1 || targetSeconds <= 0)
            return (samples.Length, false);

        var targetSamples = (int)Math.Round(targetSeconds * sampleRate);
        var duration = samples.Length / (double)sampleRate;
        if (duration > targetSeconds * FitTolerance && duration > 0.4)
        {
            var factor = Math.Min(duration / Math.Max(targetSeconds, 0.3), MaxCompression);
            if (factor > 1.01)
            {
                var bytes = ToBytes(samples);
                var stretcher = new PcmTimeStretcher();
                samples = ToShorts(stretcher.Process(bytes, bytes.Length, 1, factor), stretcher.Flush(1, factor));
            }
        }

        var truncated = false;
        if (samples.Length > targetSamples && targetSamples > sampleRate / 20)
        {
            samples = Fade(samples, targetSamples, sampleRate);
            truncated = true;
        }

        WavFile.WritePcm16(path, samples, sampleRate, 1);
        return (samples.Length, truncated);
    }

    private static short[] Fade(short[] samples, int length, int sampleRate)
    {
        var result = new short[length];
        Array.Copy(samples, result, length);
        var fade = Math.Min(length, Math.Max(1, sampleRate * 15 / 1000));
        for (var i = 0; i < fade; i++)
        {
            var gain = (fade - i) / (double)fade;
            result[length - fade + i] = (short)Math.Round(result[length - fade + i] * gain);
        }

        return result;
    }

    private static int WriteTrack(string path, IReadOnlyList<DubbingSegment> segments)
    {
        var sampleRate = segments[0].SampleRate;
        var total = segments.Max(segment => (long)Math.Ceiling(segment.EndMs * sampleRate / 1000.0));
        using var writer = WavFile.CreatePcm16Writer(path, checked((int)(total * 2)), sampleRate, 1);
        long cursor = 0;
        var overlapped = 0;

        foreach (var segment in segments)
        {
            var (samples, rate, channels) = WavFile.ReadPcm16(segment.AudioPath);
            if (rate != sampleRate || channels != 1)
                continue;
            var start = (long)Math.Round(segment.StartMs * sampleRate / 1000.0);
            if (start < cursor)
            {
                var skip = (int)Math.Min(cursor - start, samples.Length);
                samples = samples[skip..];
                start = cursor;
                if (skip > 0)
                    overlapped++;
            }

            WriteSilence(writer, start - cursor);
            var count = (int)Math.Min(samples.Length, Math.Max(0, total - start));
            for (var i = 0; i < count; i++)
                writer.Write(samples[i]);
            cursor = start + count;
        }

        WriteSilence(writer, total - cursor);
        return overlapped;
    }

    private static void WriteSilence(BinaryWriter writer, long samples)
    {
        var buffer = new byte[8192];
        while (samples > 0)
        {
            var chunk = (int)Math.Min(samples, buffer.Length / 2);
            writer.Write(buffer, 0, chunk * 2);
            samples -= chunk;
        }
    }

    private static byte[] ToBytes(short[] samples)
    {
        var bytes = new byte[samples.Length * 2];
        Buffer.BlockCopy(samples, 0, bytes, 0, bytes.Length);
        return bytes;
    }

    private static short[] ToShorts(byte[] first, byte[] second)
    {
        var result = new short[(first.Length + second.Length) / 2];
        Buffer.BlockCopy(first, 0, result, 0, first.Length);
        Buffer.BlockCopy(second, 0, result, first.Length, second.Length);
        return result;
    }

    private static string MediaKey(string mediaPath)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(mediaPath.ToLowerInvariant()));
        return Convert.ToHexString(hash)[..16];
    }

    private static void Report(IProgress<AiSubtitleProgress>? progress, string stage, double value) =>
        progress?.Report(new AiSubtitleProgress(stage, Math.Clamp(value, 0, 1)));
}
