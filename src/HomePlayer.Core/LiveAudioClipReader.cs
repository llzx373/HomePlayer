namespace HomePlayer.Core;

/// <summary>
/// 从还在增长的 WAV 中按绝对时间切片，用于实时配音的参考音频。
/// 不依赖 WAV 头里的 data 大小，按文件实际长度计算可用样本。
/// </summary>
public static class LiveAudioClipReader
{
    public static bool TryCut(
        string wavPath,
        double dataStartSeconds,
        double fromSeconds,
        double toSeconds,
        string outputPath,
        double maxSeconds = 10,
        double minSeconds = 1.5)
    {
        try
        {
            var info = WavFile.ReadInfo(wavPath);
            if (info.Format != 1 || info.BitsPerSample != 16 || info.Channels != 1 || info.SampleRate < 8000)
                return false;

            var rate = info.SampleRate;
            var first = Math.Max(0L, (long)Math.Round((fromSeconds - dataStartSeconds) * rate));
            var last = Math.Max(0L, (long)Math.Round((toSeconds - dataStartSeconds) * rate));
            if (last <= first)
                return false;
            last = Math.Min(last, first + (long)(maxSeconds * rate));

            using var stream = new FileStream(wavPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var available = Math.Max(0, (stream.Length - info.DataOffset) / 2);
            first = Math.Min(first, available);
            last = Math.Min(last, available);
            if (last - first < minSeconds * rate)
                return false;

            stream.Position = info.DataOffset + first * 2;
            var count = checked((int)(last - first));
            var samples = new short[count];
            var bytes = new byte[count * 2];
            stream.ReadExactly(bytes);
            Buffer.BlockCopy(bytes, 0, samples, 0, bytes.Length);
            WavFile.WritePcm16(outputPath, samples, rate, 1);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or EndOfStreamException)
        {
            return false;
        }
    }
}
