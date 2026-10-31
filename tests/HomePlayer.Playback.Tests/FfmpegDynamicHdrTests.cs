using FFmpeg.AutoGen;
using HomePlayer.Core;
using HomePlayer.Ffmpeg;

namespace HomePlayer.Playback.Tests;

public sealed unsafe class FfmpegDynamicHdrTests
{
    [Theory]
    [InlineData(AVFrameSideDataType.AV_FRAME_DATA_DYNAMIC_HDR_PLUS, VideoDynamicHdrFormat.Hdr10Plus)]
    [InlineData(AVFrameSideDataType.AV_FRAME_DATA_DOVI_RPU_BUFFER, VideoDynamicHdrFormat.DolbyVision)]
    [InlineData(AVFrameSideDataType.AV_FRAME_DATA_DOVI_METADATA, VideoDynamicHdrFormat.DolbyVision)]
    public void Dynamic_side_data_is_identified(AVFrameSideDataType type, VideoDynamicHdrFormat expected)
    {
        FfmpegNative.Ensure();
        var frame = ffmpeg.av_frame_alloc();
        Assert.False(frame == null);
        var side = ffmpeg.av_frame_side_data_new(&frame->side_data, &frame->nb_side_data, type, 0, 0);
        Assert.False(side == null);
        try
        {
            Assert.Equal(type, frame->side_data[0]->type);
            Assert.Equal(expected, FfmpegPlayer.DynamicHdrOf(frame));
        }
        finally
        {
            var sides = frame->side_data;
            var count = frame->nb_side_data;
            ffmpeg.av_frame_side_data_free(&sides, &count);
            frame->side_data = null;
            frame->nb_side_data = 0;
            ffmpeg.av_frame_free(&frame);
        }
    }
}
