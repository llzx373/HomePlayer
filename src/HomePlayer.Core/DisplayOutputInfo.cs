namespace HomePlayer.Core;

public enum DisplayColorMode
{
    Unknown,
    Sdr,
    Wcg,
    Hdr
}

public enum VideoOutputTransform
{
    None,
    SdrToPq,
    PqToSdr,
    HlgToPq,
    HlgToSdr
}

public sealed record DisplayOutputInfo(
    string DeviceName,
    DisplayColorMode Mode,
    string SwapChainFormat,
    string SwapChainColorSpace,
    long DisplayGeneration,
    double? MaximumLuminance = null,
    string? Reason = null);

public readonly record struct DisplayOutputSelection(bool HdrOutput, VideoOutputTransform Transform);

public static class DisplayOutputPolicy
{
    public static DisplayOutputSelection Select(bool hdrDisplay, VideoTransferFunction input)
    {
        if (hdrDisplay)
        {
            return new DisplayOutputSelection(true, input switch
            {
                VideoTransferFunction.Pq => VideoOutputTransform.None,
                VideoTransferFunction.Hlg => VideoOutputTransform.HlgToPq,
                _ => VideoOutputTransform.SdrToPq
            });
        }
        return new DisplayOutputSelection(false, input switch
        {
            VideoTransferFunction.Pq => VideoOutputTransform.PqToSdr,
            VideoTransferFunction.Hlg => VideoOutputTransform.HlgToSdr,
            _ => VideoOutputTransform.None
        });
    }
}
