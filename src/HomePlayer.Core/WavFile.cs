using System.Text;

namespace HomePlayer.Core;

public sealed record WavInfo(int SampleRate, int Channels, int BitsPerSample, int DataOffset, long Frames, int Format = 1);

/// <summary>
/// 配音产物只使用 16 位 PCM，这里只实现这一种格式，避免引入额外依赖。
/// </summary>
public static class WavFile
{
    public static WavInfo ReadInfo(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var reader = new BinaryReader(stream, Encoding.ASCII, leaveOpen: true);
        if (stream.Length < 44 || Encoding.ASCII.GetString(reader.ReadBytes(4)) != "RIFF")
            throw new InvalidDataException("不是有效的 WAV 文件：" + path);
        stream.Position = 8;
        if (Encoding.ASCII.GetString(reader.ReadBytes(4)) != "WAVE")
            throw new InvalidDataException("不是有效的 WAV 文件：" + path);

        var channels = 1;
        var sampleRate = 16000;
        var bits = 16;
        var blockAlign = 2;
        var format = 1;
        while (stream.Position + 8 <= stream.Length)
        {
            var id = Encoding.ASCII.GetString(reader.ReadBytes(4));
            var size = reader.ReadInt32();
            var body = stream.Position;
            if (id == "fmt ")
            {
                format = reader.ReadInt16();
                channels = reader.ReadInt16();
                sampleRate = reader.ReadInt32();
                reader.ReadInt32();
                blockAlign = reader.ReadInt16();
                bits = reader.ReadInt16();
            }
            else if (id == "data")
            {
                if (channels < 1 || blockAlign <= 0)
                    throw new InvalidDataException("WAV 声道信息无效：" + path);
                var frames = Math.Min(size, stream.Length - body) / blockAlign;
                return new WavInfo(sampleRate, channels, bits, (int)body, frames, format);
            }

            stream.Position = body + size + (size & 1);
        }

        throw new InvalidDataException("WAV 缺少 data 段：" + path);
    }

    public static (short[] Samples, int SampleRate, int Channels) ReadPcm16(string path)
    {
        var info = ReadInfo(path);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        stream.Position = info.DataOffset;
        var bytes = new byte[info.Frames * info.Channels * info.BitsPerSample / 8];
        stream.ReadExactly(bytes);
        return (ToPcm16(bytes, info), info.SampleRate, info.Channels);
    }

    private static short[] ToPcm16(byte[] bytes, WavInfo info)
    {
        var count = info.Frames * info.Channels;
        var samples = new short[count];
        if (info.Format == 3 && info.BitsPerSample == 32)
        {
            for (var index = 0; index < count; index++)
            {
                var value = BitConverter.ToSingle(bytes, index * 4);
                samples[index] = (short)Math.Clamp(Math.Round(value * 32767.0), short.MinValue, short.MaxValue);
            }

            return samples;
        }

        if (info.Format == 1 && info.BitsPerSample == 24)
        {
            for (var index = 0; index < count; index++)
                samples[index] = (short)(bytes[index * 3 + 1] | (bytes[index * 3 + 2] << 8));
            return samples;
        }

        if (info.Format == 1 && info.BitsPerSample == 16)
        {
            Buffer.BlockCopy(bytes, 0, samples, 0, bytes.Length);
            return samples;
        }

        throw new InvalidDataException($"只支持 16 位 PCM 或 32 位浮点 WAV（格式 {info.Format}，{info.BitsPerSample} 位）。");
    }

    public static void WritePcm16(string path, short[] samples, int sampleRate, int channels)
    {
        using var writer = CreatePcm16Writer(path, samples.Length * 2, sampleRate, channels);
        foreach (var sample in samples)
            writer.Write(sample);
    }

    public static BinaryWriter CreatePcm16Writer(string path, int dataLength, int sampleRate, int channels)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024);
        var writer = new BinaryWriter(stream, Encoding.ASCII);
        writer.Write("RIFF"u8.ToArray());
        writer.Write(36 + dataLength);
        writer.Write("WAVE"u8.ToArray());
        writer.Write("fmt "u8.ToArray());
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)channels);
        writer.Write(sampleRate);
        writer.Write(sampleRate * channels * 2);
        writer.Write((short)(channels * 2));
        writer.Write((short)16);
        writer.Write("data"u8.ToArray());
        writer.Write(dataLength);
        return writer;
    }
}
