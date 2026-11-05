using System.Text;

namespace HomePlayer.Core;

/// <summary>
/// 配音产物只使用 16 位 PCM，这里只实现这一种格式，避免引入额外依赖。
/// </summary>
public static class WavFile
{
    public static (short[] Samples, int SampleRate, int Channels) ReadPcm16(string path)
    {
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length < 44 || Encoding.ASCII.GetString(bytes, 0, 4) != "RIFF" || Encoding.ASCII.GetString(bytes, 8, 4) != "WAVE")
            throw new InvalidDataException("不是有效的 WAV 文件：" + path);

        var channels = 1;
        var sampleRate = 16000;
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
                sampleRate = BitConverter.ToInt32(bytes, body + 4);
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
            throw new InvalidDataException("WAV 缺少 data 段：" + path);
        if (bits != 16 || channels < 1)
            throw new InvalidDataException("只支持 16 位 PCM WAV：" + path);

        var samples = new short[dataLength / 2];
        Buffer.BlockCopy(bytes, dataOffset, samples, 0, samples.Length * 2);
        return (samples, sampleRate, channels);
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
