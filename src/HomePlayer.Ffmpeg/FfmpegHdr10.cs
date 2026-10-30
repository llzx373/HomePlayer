using FFmpeg.AutoGen;
using HomePlayer.Core;

namespace HomePlayer.Ffmpeg;

public sealed unsafe partial class FfmpegPlayer
{
    private static VideoHdr10Metadata? Hdr10Of(AVFrame* source)
    {
        AVMasteringDisplayMetadata* mastering = null;
        AVContentLightMetadata* content = null;
        for (var i = 0; i < source->nb_side_data; i++)
        {
            var sideData = source->side_data[i];
            if (sideData == null)
                continue;
            if (sideData->type == AVFrameSideDataType.AV_FRAME_DATA_MASTERING_DISPLAY_METADATA)
                mastering = (AVMasteringDisplayMetadata*)sideData->data;
            else if (sideData->type == AVFrameSideDataType.AV_FRAME_DATA_CONTENT_LIGHT_LEVEL)
                content = (AVContentLightMetadata*)sideData->data;
        }
        if (mastering == null && content == null)
            return null;

        var result = new VideoHdr10Metadata();
        if (mastering != null)
        {
            if (mastering->has_primaries != 0)
            {
                result = result with
                {
                    RedPrimaryX = Primary(mastering->display_primaries[0][0], result.RedPrimaryX),
                    RedPrimaryY = Primary(mastering->display_primaries[0][1], result.RedPrimaryY),
                    GreenPrimaryX = Primary(mastering->display_primaries[1][0], result.GreenPrimaryX),
                    GreenPrimaryY = Primary(mastering->display_primaries[1][1], result.GreenPrimaryY),
                    BluePrimaryX = Primary(mastering->display_primaries[2][0], result.BluePrimaryX),
                    BluePrimaryY = Primary(mastering->display_primaries[2][1], result.BluePrimaryY),
                    WhitePointX = Primary(mastering->white_point[0], result.WhitePointX),
                    WhitePointY = Primary(mastering->white_point[1], result.WhitePointY)
                };
            }
            if (mastering->has_luminance != 0)
            {
                result = result with
                {
                    MaxMasteringLuminance = Luminance(mastering->max_luminance, 1, result.MaxMasteringLuminance),
                    MinMasteringLuminance = Luminance(mastering->min_luminance, 10_000, result.MinMasteringLuminance)
                };
            }
        }
        if (content != null)
        {
            result = result with
            {
                MaxContentLightLevel = content->MaxCLL,
                MaxFrameAverageLightLevel = content->MaxFALL
            };
        }
        return result;
    }

    private static ushort Primary(AVRational value, ushort fallback) =>
        (ushort)Math.Clamp(Math.Round(Rational(value, 50_000, fallback), MidpointRounding.AwayFromZero), 0, ushort.MaxValue);

    private static uint Luminance(AVRational value, double scale, uint fallback) =>
        (uint)Math.Clamp(Math.Round(Rational(value, scale, fallback), MidpointRounding.AwayFromZero), 0, uint.MaxValue);

    private static double Rational(AVRational value, double scale, double fallback) =>
        value.den == 0 ? fallback : value.num / (double)value.den * scale;
}
