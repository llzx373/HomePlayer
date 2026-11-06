using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

using SherpaOnnx;

if (args.Length < 4 || args[0] != "--serve")
    throw new ArgumentException("Usage: HomePlayer.AI.TtsWorker --serve <modelDir> <vocoder.onnx> <voicesDir> [--gpu]");

await TtsWorker.RunAsync(args[1], args[2], args[3], args.Length >= 5 && args[4] == "--gpu");

internal static class TtsWorker
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    public static async Task RunAsync(string modelDirectory, string vocoder, string voicesDirectory, bool useGpu)
    {
        Console.OutputEncoding = new UTF8Encoding(false);
        var config = new OfflineTtsConfig();
        config.Model.ZipVoice.Encoder = Path.Combine(modelDirectory, "encoder.int8.onnx");
        config.Model.ZipVoice.Decoder = Path.Combine(modelDirectory, "decoder.int8.onnx");
        config.Model.ZipVoice.Vocoder = vocoder;
        config.Model.ZipVoice.Tokens = Path.Combine(modelDirectory, "tokens.txt");
        config.Model.ZipVoice.Lexicon = Path.Combine(modelDirectory, "lexicon.txt");
        config.Model.Provider = useGpu ? "cuda" : "cpu";
        config.Model.NumThreads = Math.Clamp(Environment.ProcessorCount - 2, 2, 16);
        config.Model.Debug = 0;
        config.MaxNumSentences = 1;

        using var tts = new OfflineTts(config);
        var handle = GetHandle(tts);
        var voices = new Dictionary<string, Voice>(StringComparer.OrdinalIgnoreCase);

        using var reader = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
        using var writer = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true };
        await writer.WriteLineAsync(JsonSerializer.Serialize(new Response(-1, true, null, null, 0, 0, 0, Ready: true), Json));

        string? line;
        while ((line = await reader.ReadLineAsync()) != null)
        {
            Request? request;
            try
            {
                request = JsonSerializer.Deserialize<Request>(line, Json);
            }
            catch (JsonException)
            {
                continue;
            }

            if (request == null || request.Id < 0 || string.IsNullOrWhiteSpace(request.Text))
                continue;

            var watch = Stopwatch.StartNew();
            try
            {
                var voice = LoadVoice(voicesDirectory, request.Voice, voices);
                var audio = Synthesize(handle, request.Text, voice, request.Speed, request.Steps);
                var directory = Path.GetDirectoryName(request.Output);
                if (!string.IsNullOrEmpty(directory))
                    Directory.CreateDirectory(directory);
                audio.SaveToWaveFile(request.Output);
                watch.Stop();
                await writer.WriteLineAsync(JsonSerializer.Serialize(new Response(
                    request.Id, true, request.Output, null, audio.SampleRate, audio.NumSamples, watch.ElapsedMilliseconds), Json));
            }
            catch (Exception ex)
            {
                watch.Stop();
                await writer.WriteLineAsync(JsonSerializer.Serialize(new Response(
                    request.Id, false, null, ex.GetType().Name + ": " + ex.Message, 0, 0, watch.ElapsedMilliseconds), Json));
            }
        }
    }

    private static Voice LoadVoice(string voicesDirectory, string? id, Dictionary<string, Voice> cache)
    {
        var name = string.IsNullOrWhiteSpace(id) ? "default" : id;
        if (cache.TryGetValue(name, out var cached))
            return cached;

        var wav = Path.Combine(voicesDirectory, name + ".wav");
        var text = Path.Combine(voicesDirectory, name + ".txt");
        if (!File.Exists(wav))
            throw new FileNotFoundException("找不到音色参考音频：" + wav);
        if (!File.Exists(text))
            throw new FileNotFoundException("找不到音色参考文本：" + text);

        var (samples, sampleRate) = ReadWav(wav);
        var voice = new Voice(samples, sampleRate, File.ReadAllText(text, Encoding.UTF8).Trim());
        if (string.IsNullOrWhiteSpace(voice.ReferenceText))
            throw new InvalidDataException("音色参考文本为空：" + text);
        cache[name] = voice;
        return voice;
    }

    private static OfflineTtsGeneratedAudio Synthesize(IntPtr tts, string text, Voice voice, float speed, int steps)
    {
        var audioHandle = GCHandle.Alloc(voice.Samples, GCHandleType.Pinned);
        var referenceText = Marshal.StringToCoTaskMemUTF8(voice.ReferenceText + "\0");
        var extra = Marshal.StringToCoTaskMemUTF8("{}\0");
        try
        {
            var config = new GenerationConfigNative
            {
                SilenceScale = 0.2f,
                Speed = speed <= 0 ? 1f : speed,
                Sid = 0,
                ReferenceAudio = audioHandle.AddrOfPinnedObject(),
                ReferenceAudioLen = voice.Samples.Length,
                ReferenceSampleRate = voice.SampleRate,
                ReferenceText = referenceText,
                NumSteps = steps <= 0 ? 2 : steps,
                Extra = extra
            };

            var handle = GenerateWithConfig(
                tts,
                Encoding.UTF8.GetBytes(text + "\0"),
                ref config,
                IntPtr.Zero,
                IntPtr.Zero);
            if (handle == IntPtr.Zero)
                throw new InvalidOperationException("语音合成没有返回音频。");
            return new OfflineTtsGeneratedAudio(handle);
        }
        finally
        {
            audioHandle.Free();
            Marshal.FreeCoTaskMem(referenceText);
            Marshal.FreeCoTaskMem(extra);
        }
    }

    private static IntPtr GetHandle(OfflineTts tts)
    {
        var field = typeof(OfflineTts).GetField("_handle", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("无法获取语音合成句柄。");
        return ((HandleRef)field.GetValue(tts)!).Handle;
    }

    private static (float[] Samples, int SampleRate) ReadWav(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var channels = 1;
        var rate = 16000;
        var bits = 16;
        var dataOffset = -1;
        var dataLength = 0;

        for (var i = 12; i + 8 <= bytes.Length;)
        {
            var id = Encoding.ASCII.GetString(bytes, i, 4);
            var size = BitConverter.ToInt32(bytes, i + 4);
            var body = i + 8;
            if (id == "fmt ")
            {
                channels = BitConverter.ToInt16(bytes, body + 2);
                rate = BitConverter.ToInt32(bytes, body + 4);
                bits = BitConverter.ToInt16(bytes, body + 14);
            }
            else if (id == "data")
            {
                dataOffset = body;
                dataLength = Math.Min(size, bytes.Length - body);
            }

            i = body + size + (size & 1);
        }

        if (dataOffset < 0)
            throw new InvalidDataException("音频缺少 data 段：" + path);
        if (bits != 16)
            throw new InvalidDataException("参考音频只支持 16 位 PCM：" + path);
        if (channels < 1)
            throw new InvalidDataException("参考音频声道数无效：" + path);

        var frames = dataLength / (2 * channels);
        var samples = new float[frames];
        for (var frame = 0; frame < frames; frame++)
        {
            var sum = 0f;
            for (var channel = 0; channel < channels; channel++)
                sum += BitConverter.ToInt16(bytes, dataOffset + (frame * channels + channel) * 2) / 32768f;
            samples[frame] = sum / channels;
        }

        return (samples, rate);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct GenerationConfigNative
    {
        public float SilenceScale;
        public float Speed;
        public int Sid;
        public IntPtr ReferenceAudio;
        public int ReferenceAudioLen;
        public int ReferenceSampleRate;
        public IntPtr ReferenceText;
        public int NumSteps;
        public IntPtr Extra;
        public IntPtr PhonemeCodepoints;
        public IntPtr PhonemeCodepointsLens;
        public int PhonemeCodepointsNumSentences;
        public IntPtr Tokens;
        public IntPtr TokensLens;
        public int TokensNumSentences;
    }

    [DllImport("sherpa-onnx-c-api.dll", CallingConvention = CallingConvention.Cdecl, EntryPoint = "SherpaOnnxOfflineTtsGenerateWithConfig")]
    private static extern IntPtr GenerateWithConfig(
        IntPtr handle,
        byte[] text,
        ref GenerationConfigNative config,
        IntPtr callback,
        IntPtr arg);

    private sealed record Voice(float[] Samples, int SampleRate, string ReferenceText);

    private sealed record Request(int Id, string? Text, string? Voice, float Speed, int Steps, string Output);

    private sealed record Response(
        int Id,
        bool Ok,
        string? Path,
        string? Error,
        int SampleRate,
        int Samples,
        long ElapsedMs,
        bool Ready = false);
}
