using HomePlayer.Core;

namespace HomePlayer.AI;

internal static class DubbingAudio
{
    private const double MaxCompression = 1.25;
    private const double FitTolerance = 1.02;

    public static (short[] Samples, bool Truncated) FitSamples(short[] samples, int sampleRate, double targetSeconds)
    {
        if (sampleRate <= 0 || targetSeconds <= 0)
            return (samples, false);

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

        return (samples, truncated);
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
}
