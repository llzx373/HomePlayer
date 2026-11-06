using System.Text;
using System.Text.Json;
using HomePlayer.Core;
using SherpaOnnx;

internal static class LiveDiarizationRunner
{
    public static void Run(DiarizeLiveJob job)
    {
        var config = new OfflineSpeakerDiarizationConfig();
        config.Segmentation.Pyannote.Model = job.SegmentationModel;
        config.Segmentation.Provider = job.UseCuda ? "cuda" : "cpu";
        config.Segmentation.NumThreads = Math.Clamp(Environment.ProcessorCount / 2, 2, 8);
        config.Embedding.Model = job.EmbeddingModel;
        config.Embedding.Provider = "cpu";
        config.Embedding.NumThreads = Math.Clamp(Environment.ProcessorCount / 2, 2, 8);
        config.Clustering.Threshold = 0.65f;
        config.MinDurationOn = 0.4f;
        config.MinDurationOff = 0.5f;

        using var diarization = new OfflineSpeakerDiarization(config);
        using var extractor = new SpeakerEmbeddingExtractor(new SpeakerEmbeddingExtractorConfig
        {
            Model = job.EmbeddingModel,
            Provider = "cpu",
            NumThreads = Math.Clamp(Environment.ProcessorCount / 2, 2, 8)
        });

        var rate = diarization.SampleRate;
        using var audio = new GrowingWav(job.AudioPath);
        var tracker = new SpeakerProfileTracker();
        var blockSamples = (int)Math.Round(Math.Max(2, job.BlockSeconds) * rate);
        var overlapSamples = (int)Math.Round(Math.Max(0, job.OverlapSeconds) * rate);
        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        long processed = 0;
        var emittedEnd = job.StartSeconds;

        while (true)
        {
            if (audio.Failed)
                throw new InvalidOperationException("音频提取失败。");

            var available = audio.AvailableSamples;
            var complete = audio.Complete;
            var target = processed + blockSamples;
            if (available < target && !complete)
            {
                Thread.Sleep(100);
                continue;
            }

            var readEnd = Math.Min(available, target);
            if (readEnd <= processed)
            {
                if (complete)
                    break;
                Thread.Sleep(100);
                continue;
            }

            var readStart = Math.Max(0, processed - overlapSamples);
            var samples = audio.Read(readStart, (int)(readEnd - readStart));
            if (samples.Length == 0)
            {
                if (complete)
                    break;
                Thread.Sleep(100);
                continue;
            }

            var block = diarization.Process(samples);
            var probes = new Dictionary<int, float[]>();
            foreach (var segment in block)
            {
                if (!probes.ContainsKey(segment.Speaker))
                    probes[segment.Speaker] = SpeakerProbe.Extract(samples, rate, block, segment.Speaker, extractor);
            }

            var blockStart = job.StartSeconds + readStart / (double)rate;
            foreach (var segment in block)
            {
                var speaker = tracker.Match(probes[segment.Speaker], SpeakerProbe.TotalSeconds(block, segment.Speaker));
                if (speaker < 0)
                    continue;
                var start = blockStart + segment.Start;
                var end = blockStart + segment.End;
                var emitStart = Math.Max(start, emittedEnd);
                if (end <= emitStart + 0.02)
                    continue;
                Console.WriteLine(JsonSerializer.Serialize(new
                {
                    type = "segment",
                    speaker,
                    start = emitStart,
                    end
                }, options));
                emittedEnd = end;
            }

            processed = readStart + samples.Length;
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                type = "through",
                seconds = job.StartSeconds + processed / (double)rate
            }, options));

            if (complete && readEnd >= available)
                break;
        }

        Console.WriteLine(JsonSerializer.Serialize(new { type = "done", speakers = tracker.Count }, options));
    }

    /// <summary>
    /// 读取还在增长的 WAV。FFmpeg 写文件时 data 长度可能是占位值，
    /// 所以只信任 data 段起点，按文件实际长度计算可用样本。
    /// </summary>
    private sealed class GrowingWav : IDisposable
    {
        private readonly string _path;
        private readonly FileStream _stream;
        private readonly long _dataOffset;

        public GrowingWav(string path)
        {
            _path = path;
            while (!File.Exists(path))
            {
                if (File.Exists(path + ".error"))
                    throw new InvalidOperationException("音频提取失败。");
                Thread.Sleep(50);
            }

            _stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 64 * 1024);
            _dataOffset = FindDataOffset(_stream, path);
        }

        public long AvailableSamples => Math.Max(0, (_stream.Length - _dataOffset) / 2);

        public bool Complete => File.Exists(_path + ".done");

        public bool Failed => File.Exists(_path + ".error");

        public float[] Read(long firstSample, int count)
        {
            _stream.Position = _dataOffset + firstSample * 2;
            var bytes = new byte[count * 2];
            var offset = 0;
            while (offset < bytes.Length)
            {
                var read = _stream.Read(bytes, offset, bytes.Length - offset);
                if (read == 0)
                    break;
                offset += read;
            }

            var actual = offset / 2;
            var samples = new float[actual];
            for (var index = 0; index < actual; index++)
                samples[index] = BitConverter.ToInt16(bytes, index * 2) / 32768f;
            return samples;
        }

        public void Dispose() => _stream.Dispose();

        private static long FindDataOffset(FileStream stream, string path)
        {
            while (true)
            {
                if (stream.Length >= 12)
                {
                    stream.Position = 0;
                    var header = new byte[12];
                    if (TryRead(stream, header, header.Length)
                        && Encoding.ASCII.GetString(header, 0, 4) == "RIFF"
                        && Encoding.ASCII.GetString(header, 8, 4) == "WAVE")
                    {
                        while (true)
                        {
                            var chunk = new byte[8];
                            if (!TryRead(stream, chunk, chunk.Length))
                                break;
                            var id = Encoding.ASCII.GetString(chunk, 0, 4);
                            var length = BitConverter.ToUInt32(chunk, 4);
                            if (id == "data")
                                return stream.Position;
                            if (length > int.MaxValue)
                                break;
                            var next = stream.Position + length + (length & 1);
                            if (next > stream.Length)
                                break;
                            stream.Position = next;
                        }
                    }
                }

                if (File.Exists(path + ".error"))
                    throw new InvalidOperationException("音频提取失败。");
                if (File.Exists(path + ".done"))
                    throw new InvalidDataException("WAV 缺少 data 段。");
                Thread.Sleep(50);
            }
        }

        private static bool TryRead(Stream stream, byte[] buffer, int count)
        {
            var offset = 0;
            while (offset < count)
            {
                var read = stream.Read(buffer, offset, count - offset);
                if (read == 0)
                    return false;
                offset += read;
            }

            return true;
        }
    }
}

public sealed record DiarizeLiveJob(
    string AudioPath,
    string SegmentationModel,
    string EmbeddingModel,
    bool UseCuda,
    double BlockSeconds,
    double OverlapSeconds,
    double StartSeconds);
