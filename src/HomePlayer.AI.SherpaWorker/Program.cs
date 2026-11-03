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

var segmenter = new SilenceSegmenter(job.MinChunkSeconds, job.MaxChunkSeconds);
var pending = new List<float>(16000 * 32);
var pendingStart = 0L;
var chunkIndex = 0;

while (true)
{
    if (File.Exists(job.AudioPath + ".error"))
        throw new InvalidOperationException("音频提取失败。");
    var complete = File.Exists(job.AudioPath + ".done");
    var block = ReadChunk(audio, 16000);
    if (block.Length > 0)
        pending.AddRange(block);
    var endOfAudio = complete && block.Length == 0;

    while (true)
    {
        var cut = segmenter.FindCut(pending, endOfAudio);
        if (cut <= 0)
            break;
        Emit(pending, cut, pendingStart);
        pending.RemoveRange(0, cut);
        pendingStart += cut;
    }

    if (block.Length == 0)
    {
        if (complete)
            break;
        await Task.Delay(100);
    }
}

if (pending.Count > 0)
    Emit(pending, pending.Count, pendingStart);

void Emit(List<float> buffer, int count, long startSample)
{
    var samples = new float[count];
    buffer.CopyTo(0, samples, 0, count);
    if (segmenter.SpeechBounds(samples) is not { } speech)
        return;
    var audio = new float[speech.End - speech.Start];
    Array.Copy(samples, speech.Start, audio, 0, audio.Length);
    using var stream = recognizer.CreateStream();
    stream.AcceptWaveform(16000, audio);
    recognizer.Decode(stream);
    var text = Sanitize(stream.Result.Text);
    Console.WriteLine(JsonSerializer.Serialize(new SherpaResult(
        chunkIndex++,
        job.StartSeconds + (double)(startSample + speech.Start) / 16000,
        job.StartSeconds + (double)(startSample + speech.End) / 16000,
        text)));
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

public sealed record SherpaJob(string ModelDirectory, string AudioPath, double StartSeconds, double MinChunkSeconds, double MaxChunkSeconds, bool UseCuda);
public sealed record SherpaResult(int Id, double StartSeconds, double EndSeconds, string Text);

internal sealed class SilenceSegmenter(double minChunkSeconds, double maxChunkSeconds)
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

        var bestCut = -1;
        var longest = 0;
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
                if (runLength > longest)
                {
                    longest = runLength;
                    bestCut = middle * Hop;
                }
            }
            runStart = -1;
        }

        if (bestCut > 0)
            return Math.Min(bestCut, samples.Count);
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
