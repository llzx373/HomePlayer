using FFmpeg.AutoGen;
using HomePlayer.Core;

namespace HomePlayer.Ffmpeg;

public static unsafe class FfmpegTrackIdentity
{
    public static string StableId(AVStream* stream, MediaTrackKind kind, string title, string? language) =>
        MediaTrackIdentity.Create(kind, stream->id > 0 ? stream->id : stream->index, title, language, Disposition(stream));

    public static MediaTrackDisposition Disposition(AVStream* stream)
    {
        var result = MediaTrackDisposition.None;
        if ((stream->disposition & ffmpeg.AV_DISPOSITION_DEFAULT) != 0)
            result |= MediaTrackDisposition.Default;
        if ((stream->disposition & ffmpeg.AV_DISPOSITION_FORCED) != 0)
            result |= MediaTrackDisposition.Forced;
        if ((stream->disposition & ffmpeg.AV_DISPOSITION_COMMENT) != 0)
            result |= MediaTrackDisposition.Commentary;
        if ((stream->disposition & ffmpeg.AV_DISPOSITION_HEARING_IMPAIRED) != 0)
            result |= MediaTrackDisposition.HearingImpaired;
        if ((stream->disposition & ffmpeg.AV_DISPOSITION_VISUAL_IMPAIRED) != 0)
            result |= MediaTrackDisposition.VisualImpaired;
        if ((stream->disposition & ffmpeg.AV_DISPOSITION_DESCRIPTIONS) != 0)
            result |= MediaTrackDisposition.Descriptions;
        if ((stream->disposition & ffmpeg.AV_DISPOSITION_LYRICS) != 0)
            result |= MediaTrackDisposition.Lyrics;
        return result;
    }
}
