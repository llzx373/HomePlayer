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
using var audio = new FileStream(job.AudioPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
var header = await ReadExactAsync(audio, 12, job.AudioPath);
if (System.Text.Encoding.ASCII.GetString(header, 0, 4) != "RIFF"
    || System.Text.Encoding.ASCII.GetString(header, 8, 4) != "WAVE")
    throw new InvalidDataException("识别音频不是有效 WAV。");
while (true)
{
    var chunkHeader = await ReadExactAsync(audio, 8, job.AudioPath);
    var length = BitConverter.ToUInt32(chunkHeader, 4);
    if (System.Text.Encoding.ASCII.GetString(chunkHeader, 0, 4) == "data")
        break;
    if (audio.Position + length + length % 2 > audio.Length)
        throw new InvalidDataException("WAV 元数据块不完整。");
    audio.Position += length + length % 2;
}
var dataStart = audio.Position;
var chunkSamples = 16000 * job.ChunkSeconds;
var overlapSamples = 16000 * job.OverlapSeconds;
var stride = job.ChunkSeconds - job.OverlapSeconds;
if (stride <= 0)
    throw new InvalidDataException("识别窗口重叠不能大于窗口长度。");
var chunkIndex = 0;
while (true)
{
    var chunkPosition = audio.Position;
    var buffer = ReadChunk(audio, chunkSamples);
    var complete = File.Exists(job.AudioPath + ".done");
    if (File.Exists(job.AudioPath + ".error"))
        throw new InvalidOperationException("音频提取失败。");
    if (buffer.Length < chunkSamples && !complete)
    {
        audio.Position = chunkPosition;
        await Task.Delay(100);
        continue;
    }
    if (buffer.Length == 0)
    {
        if (complete)
            break;
        audio.Position = chunkPosition;
        await Task.Delay(100);
        continue;
    }
    if (complete && chunkIndex > 0 && buffer.Length <= overlapSamples)
        break;
    var text = "";
    if (HasSpeech(buffer))
    {
        using var stream = recognizer.CreateStream();
        stream.AcceptWaveform(16000, buffer);
        recognizer.Decode(stream);
        text = stream.Result.Text?.Trim() ?? "";
    }
    Console.WriteLine(JsonSerializer.Serialize(new SherpaResult(
        chunkIndex,
        job.StartSeconds + (double)chunkIndex * stride,
        job.StartSeconds + (double)chunkIndex * stride + (double)buffer.Length / 16000,
        text)));
    chunkIndex++;
    if (overlapSamples > 0 && audio.Position - overlapSamples * 2 >= dataStart)
        audio.Position -= overlapSamples * 2;
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

static bool HasSpeech(float[] samples)
{
    if (samples.Length == 0)
        return false;
    double sum = 0;
    for (var index = 0; index < samples.Length; index++)
        sum += samples[index] * samples[index];
    return Math.Sqrt(sum / samples.Length) >= 0.0001;
}

public sealed record SherpaJob(string ModelDirectory, string AudioPath, double StartSeconds, int ChunkSeconds, int OverlapSeconds, bool UseCuda);
public sealed record SherpaResult(int Id, double StartSeconds, double EndSeconds, string Text);
