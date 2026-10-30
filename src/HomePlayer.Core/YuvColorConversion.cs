namespace HomePlayer.Core;

public readonly record struct YuvColorCoefficients(
    float YOffset,
    float YScale,
    float ChromaOffset,
    float RedV,
    float GreenU,
    float GreenV,
    float BlueU);

public static class YuvColorConversion
{
    public static YuvColorCoefficients For(VideoColorMetadata color, int frameHeight)
    {
        var limited = color.Range != VideoColorRange.Full;
        var matrix = color.Matrix == VideoColorMatrix.Unknown ? VideoColorMatrix.Bt601 : color.Matrix;
        var (redV, greenU, greenV, blueU) = matrix switch
        {
            VideoColorMatrix.Bt709 => (1.79274107f, 0.213248614f, 0.532909328f, 2.11240179f),
            VideoColorMatrix.Bt2020 => (1.4746f, 0.164553127f, 0.571353126f, 1.8814f),
            _ => (1.59602717f, 0.391762290f, 0.812967647f, 2.01723214f)
        };
        return new YuvColorCoefficients(
            limited ? 16f / 255f : 0,
            limited ? 255f / 219f : 1,
            0.5f,
            redV,
            greenU,
            greenV,
            blueU);
    }
}
