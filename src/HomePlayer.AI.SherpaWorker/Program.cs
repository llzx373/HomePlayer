using System.Diagnostics;
using System.Text;
using System.Text.Json;
using SherpaOnnx;

if (args.Length != 1)
    throw new ArgumentException("Usage: HomePlayer.AI.SherpaWorker <job.json>");
var job = JsonSerializer.Deserialize<SherpaJob>(File.ReadAllText(args[0]))
    ?? throw new InvalidDataException("Empty Sherpa job.");
var config = new OfflineRecognizerConfig();
config.FeatConfig.SampleRate = 16000;
config.FeatConfig.FeatureDim = 80;
config.ModelConfig.Tokens = Path.Combine(job.ModelDirectory, "tokens.txt");
config.ModelConfig.SenseVoice.Model = Path.Combine(job.ModelDirectory, "model.int8.onnx");
config.ModelConfig.SenseVoice.UseInverseTextNormalization = 1;
config.ModelConfig.Provider = job.UseCuda ? "cuda" : "cpu";
config.ModelConfig.NumThreads = 2;
config.ModelConfig.Debug = 0;
using var recognizer = new OfflineRecognizer(config);
using var vad = CreateVad(job.ModelDirectory);
while (!File.Exists(job.AudioPath))
{
    if (File.Exists(job.AudioPath + ".error"))
        throw new InvalidOperationException("音频提取失败。");
    await Task.Delay(50);
}
using var audio = new FileStream(job.AudioPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
var header = await ReadExactAsync(audio, 12, job.AudioPath);
if (Encoding.ASCII.GetString(header, 0, 4) != "RIFF"
    || Encoding.ASCII.GetString(header, 8, 4) != "WAVE")
    throw new InvalidDataException("识别音频不是有效 WAV。");
while (true)
{
    var chunkHeader = await ReadExactAsync(audio, 8, job.AudioPath);
    var length = BitConverter.ToUInt32(chunkHeader, 4);
    if (Encoding.ASCII.GetString(chunkHeader, 0, 4) == "data")
        break;
    if (audio.Position + length + length % 2 > audio.Length)
        throw new InvalidDataException("WAV 元数据块不完整。");
    audio.Position += length + length % 2;
}

var segmenter = new SilenceSegmenter(job.MinChunkSeconds, job.MaxChunkSeconds, vad, job.Realtime);
var pending = new List<float>(16000 * 32);
var pendingStart = 0L;
var currentId = 0;
var draftInterval = Math.Max(1.0, job.DraftIntervalSeconds);
var lastDraftAt = 0L;
var lastDraftPending = 0;
var lastDraftText = "";
var lastDraftEnd = 0;
var draftStart = -1;

while (true)
{
    if (File.Exists(job.AudioPath + ".error"))
        throw new InvalidOperationException("音频提取失败。");
    var complete = File.Exists(job.AudioPath + ".done");
    var block = ReadChunk(audio, 16000);
    if (block.Length > 0)
        pending.AddRange(block);
    var endOfAudio = complete && block.Length == 0;

    MaybeDraft();

    while (true)
    {
        var cut = segmenter.FindCut(pending, endOfAudio);
        if (cut <= 0)
            break;
        EmitFinal(pending, cut, pendingStart);
        pending.RemoveRange(0, cut);
        pendingStart += cut;
    }

    if (block.Length == 0)
    {
        if (complete)
            break;
        MaybeDraft();
        await Task.Delay(100);
    }
}

if (pending.Count > 0)
    EmitFinal(pending, pending.Count, pendingStart);

string Decode(float[] samples)
{
    using var stream = recognizer.CreateStream();
    stream.AcceptWaveform(16000, samples);
    recognizer.Decode(stream);
    return Sanitize(stream.Result.Text);
}

void EmitFinal(List<float> buffer, int count, long startSample)
{
    var samples = new float[count];
    buffer.CopyTo(0, samples, 0, count);
    if (segmenter.SpeechBounds(samples) is not { } speech)
    {
        if (draftStart >= 0)
        {
            Console.WriteLine(JsonSerializer.Serialize(new SherpaResult(
                currentId++,
                job.StartSeconds + (double)startSample / 16000,
                job.StartSeconds + (double)(startSample + count) / 16000,
                "",
                true)));
        }
        ResetDraftState();
        return;
    }
    var audioChunk = new float[speech.End - speech.Start];
    Array.Copy(samples, speech.Start, audioChunk, 0, audioChunk.Length);
    Console.WriteLine(JsonSerializer.Serialize(new SherpaResult(
        currentId++,
        job.StartSeconds + (double)(startSample + speech.Start) / 16000,
        job.StartSeconds + (double)(startSample + speech.End) / 16000,
        Decode(audioChunk),
        true)));
    ResetDraftState();
}

void MaybeDraft()
{
    if (!job.Realtime || !job.Drafts || pending.Count < 16000 * 3 / 2)
        return;
    var now = Environment.TickCount64;
    if (lastDraftAt > 0 && now - lastDraftAt < draftInterval * 1000)
        return;
    if (lastDraftPending > 0 && pending.Count - lastDraftPending < 16000)
        return;
    if (segmenter.SpeechBounds(pending) is not { } speech)
        return;
    if (speech.End - speech.Start < 16000 * 4 / 5)
        return;
    var start = draftStart >= 0 ? draftStart : speech.Start;
    var end = Math.Min(speech.End, start + (int)(16000 * job.MaxChunkSeconds));
    if (lastDraftEnd > 0 && end < lastDraftEnd)
        end = lastDraftEnd;
    var audioChunk = new float[end - start];
    for (var index = 0; index < audioChunk.Length; index++)
        audioChunk[index] = pending[start + index];
    var stopwatch = Stopwatch.StartNew();
    var text = Decode(audioChunk);
    stopwatch.Stop();
    if (text.Length == 0)
    {
        lastDraftAt = now;
        lastDraftPending = pending.Count;
        return;
    }
    lastDraftAt = now;
    lastDraftPending = pending.Count;
    if (text == lastDraftText)
    {
        lastDraftEnd = end;
        return;
    }
    draftStart = start;
    lastDraftText = text;
    lastDraftEnd = end;
    Console.WriteLine(JsonSerializer.Serialize(new SherpaResult(
        currentId,
        job.StartSeconds + (double)(pendingStart + start) / 16000,
        job.StartSeconds + (double)(pendingStart + end) / 16000,
        text,
        false)));
    if (stopwatch.Elapsed.TotalSeconds * 2 > draftInterval)
        draftInterval = Math.Min(5.0, draftInterval * 1.5);
}

void ResetDraftState()
{
    draftStart = -1;
    lastDraftAt = 0;
    lastDraftPending = 0;
    lastDraftText = "";
    lastDraftEnd = 0;
}

static VoiceActivityDetector? CreateVad(string modelDirectory)
{
    var path = Path.Combine(modelDirectory, "silero_vad.onnx");
    if (!File.Exists(path))
        return null;
    try
    {
        var config = new VadModelConfig();
        config.SileroVad.Model = path;
        config.SileroVad.Threshold = 0.5f;
        config.SileroVad.MinSilenceDuration = 0.25f;
        config.SileroVad.MinSpeechDuration = 0.25f;
        config.SileroVad.MaxSpeechDuration = 20f;
        config.SileroVad.WindowSize = 512;
        config.SampleRate = 16000;
        config.NumThreads = 1;
        config.Provider = "cpu";
        return new VoiceActivityDetector(config, 60f);
    }
    catch
    {
        return null;
    }
}

static async Task<byte[]> ReadExactAsync(Stream audio, int count, string path)
{
    var bytes = new byte[count];
    var offset = 0;
    while (offset < count)
    {
        var read = await audio.ReadAsync(bytes.AsMemory(offset, count - offset));
        if (read > 0)
        {
            offset += read;
            continue;
        }
        if (File.Exists(path + ".error"))
            throw new InvalidOperationException("音频提取失败。");
        if (File.Exists(path + ".done"))
            throw new InvalidDataException("WAV 在元数据块中途结束。");
        await Task.Delay(100);
    }
    return bytes;
}

static float[] ReadChunk(Stream audio, int count)
{
    var bytes = new byte[count * 2];
    var offset = 0;
    while (offset < bytes.Length)
    {
        var read = audio.Read(bytes, offset, bytes.Length - offset);
        if (read == 0)
            break;
        offset += read;
    }
    var samples = new float[offset / 2];
    for (var index = 0; index < samples.Length; index++)
        samples[index] = BitConverter.ToInt16(bytes, index * 2) / 32768f;
    return samples;
}

static string Sanitize(string? text)
{
    if (string.IsNullOrWhiteSpace(text))
        return "";
    text = text.Trim();
    foreach (var character in text)
    {
        if (char.IsLetterOrDigit(character))
            return text;
    }
    return "";
}

public sealed record SherpaJob(string ModelDirectory, string AudioPath, double StartSeconds, double MinChunkSeconds, double MaxChunkSeconds, bool UseCuda, bool Realtime, bool Drafts, double DraftIntervalSeconds);
public sealed record SherpaResult(int Id, double StartSeconds, double EndSeconds, string Text, bool Final);

internal sealed class SilenceSegmenter(double minChunkSeconds, double maxChunkSeconds, VoiceActivityDetector? vad, bool realtime)
{
    private const int SampleRate = 16000;
    private const int Hop = 160;
    private const int Window = 400;
    private const int MinimumSilenceFrames = 30;
    private const int ConfirmationFrames = 40;

    public int FindCut(List<float> samples, bool endOfAudio)
    {
        var duration = samples.Count / (double)SampleRate;
        if (endOfAudio && duration <= maxChunkSeconds)
            return samples.Count;
        if (duration < minChunkSeconds)
            return 0;

        var frames = FrameRms(samples);
        if (frames.Length == 0)
            return endOfAudio ? samples.Count : 0;
        var noise = Math.Clamp(Percentile(frames, 0.15f), 0.006f, 0.025f);
        var silence = Math.Max(0.012f, noise * 1.05f);

        var candidates = new List<(int Cut, int Run)>();
        var runStart = -1;
        for (var frame = 0; frame <= frames.Length; frame++)
        {
            var silent = frame < frames.Length && frames[frame] < silence;
            if (silent)
            {
                if (runStart < 0)
                    runStart = frame;
                continue;
            }
            if (runStart < 0)
                continue;
            var runLength = frame - runStart;
            var middle = (runStart + frame) / 2;
            var middleSeconds = middle * Hop / (double)SampleRate;
            var confirmed = frame < frames.Length - ConfirmationFrames;
            if (runLength >= MinimumSilenceFrames && middleSeconds >= minChunkSeconds
                && middleSeconds <= maxChunkSeconds && (confirmed || endOfAudio))
            {
                candidates.Add((middle * Hop, runLength));
            }
            runStart = -1;
        }

        candidates.Sort(realtime
            ? (left, right) => left.Cut.CompareTo(right.Cut)
            : (left, right) => right.Run.CompareTo(left.Run));
        if (candidates.Count > 0)
        {
            if (vad == null)
                return Math.Min(candidates[0].Cut, samples.Count);
            foreach (var candidate in candidates.Take(2))
            {
                if (VadHasGapAround(samples, candidate.Cut))
                    return Math.Min(candidate.Cut, samples.Count);
            }
            return Math.Min(candidates[0].Cut, samples.Count);
        }

        if (endOfAudio || duration >= maxChunkSeconds)
            return Math.Min(samples.Count, (int)(maxChunkSeconds * SampleRate));
        return 0;
    }

    public (int Start, int End)? SpeechBounds(IReadOnlyList<float> samples)
    {
        var frames = FrameRms(samples);
        if (frames.Length == 0)
            return null;
        var noise = Math.Clamp(Percentile(frames, 0.15f), 0.006f, 0.025f);
        var speech = Math.Max(0.02f, noise * 1.5f);
        var first = -1;
        var last = -1;
        var count = 0;
        for (var frame = 0; frame < frames.Length; frame++)
        {
            if (frames[frame] < speech)
                continue;
            count++;
            if (first < 0)
                first = frame;
            last = frame;
        }
        if (first < 0 || count < Math.Max(2, frames.Length / 50))
            return null;
        var start = Math.Max(0, first * Hop - 1600);
        var end = Math.Min(samples.Count, last * Hop + Window + 3200);
        return end > start ? (start, end) : null;
    }

    private bool VadHasGapAround(List<float> samples, int cutSample)
    {
        if (vad == null)
            return true;
        var from = Math.Max(0, cutSample - SampleRate * 6 / 5);
        var to = Math.Min(samples.Count, cutSample + SampleRate * 4 / 5);
        if (to - from < SampleRate / 2)
            return true;
        var window = new float[to - from];
        for (var index = 0; index < window.Length; index++)
            window[index] = samples[from + index];
        var segments = VadSegments(window);
        if (segments.Count == 0)
            return true;
        var cutSeconds = (cutSample - from) / (double)SampleRate;
        var previousEnd = 0.0;
        foreach (var (start, end) in segments)
        {
            if (cutSeconds >= previousEnd && cutSeconds <= start)
                return true;
            previousEnd = Math.Max(previousEnd, end);
        }
        return cutSeconds >= previousEnd;
    }

    private List<(double Start, double End)> VadSegments(IReadOnlyList<float> samples)
    {
        var result = new List<(double Start, double End)>();
        if (vad == null)
            return result;
        try
        {
            vad.Reset();
            var total = 0;
            const int block = 8000;
            while (total < samples.Count)
            {
                var count = Math.Min(block, samples.Count - total);
                var part = new float[count];
                for (var index = 0; index < count; index++)
                    part[index] = samples[total + index];
                vad.AcceptWaveform(part);
                total += count;
                Drain();
            }
            vad.Flush();
            Drain();
        }
        catch (Exception)
        {
            result.Clear();
        }
        return result;

        void Drain()
        {
            while (!vad.IsEmpty())
            {
                var segment = vad.Front();
                vad.Pop();
                var start = segment.Start / (double)SampleRate;
                var end = (segment.Start + segment.Samples.Length) / (double)SampleRate;
                if (end > start)
                    result.Add((start, end));
            }
        }
    }

    private static float[] FrameRms(IReadOnlyList<float> samples)
    {
        if (samples.Count < Window)
            return [];
        var count = (samples.Count - Window) / Hop + 1;
        var values = new float[count];
        for (var frame = 0; frame < count; frame++)
        {
            double sum = 0;
            var offset = frame * Hop;
            for (var index = 0; index < Window; index++)
                sum += samples[offset + index] * samples[offset + index];
            values[frame] = (float)Math.Sqrt(sum / Window);
        }
        return values;
    }

    private static float Percentile(float[] values, float percentile)
    {
        if (values.Length == 0)
            return 0.01f;
        var sorted = (float[])values.Clone();
        Array.Sort(sorted);
        return sorted[(int)Math.Clamp(percentile * (sorted.Length - 1), 0, sorted.Length - 1)];
    }
}
