using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using FFmpeg.AutoGen;
using HomePlayer.Core;

namespace HomePlayer.Ffmpeg;

public sealed record FfmpegSubtitleStream(int Index, string Title, string? Language, string Codec, bool IsText)
{
    public string? StableId { get; init; }
    public MediaTrackDisposition Disposition { get; init; }
}

public static unsafe class FfmpegSubtitles
{
    private const int MaxCues = 20_000;

    public static IReadOnlyList<FfmpegSubtitleStream> List(string path)
    {
        try
        {
            return WithFormat(path, format => ListStreams(format));
        }
        catch
        {
            return [];
        }
    }

    public static IReadOnlyList<SubtitleCue> ReadCues(string path, int streamIndex)
        => WithFormat(path, format => ReadStream(format, streamIndex));

    private static T WithFormat<T>(string path, Func<IntPtr, T> use)
    {
        FfmpegNative.Ensure();
        AVFormatContext* format = ffmpeg.avformat_alloc_context();
        var opened = format;
        var openResult = ffmpeg.avformat_open_input(&opened, path, null, null);
        format = opened;
        try
        {
            FfmpegError.Check(openResult, "FFmpeg 打开字幕");
            FfmpegError.Check(ffmpeg.avformat_find_stream_info(format, null), "FFmpeg 读取字幕信息");
            return use((IntPtr)format);
        }
        finally
        {
            if (format != null)
                ffmpeg.avformat_close_input(&format);
        }
    }

    private static IReadOnlyList<FfmpegSubtitleStream> ListStreams(IntPtr formatPtr)
    {
        var format = (AVFormatContext*)formatPtr;
        var tracks = new List<FfmpegSubtitleStream>();
        for (var i = 0; i < (int)format->nb_streams; i++)
        {
            var stream = format->streams[i];
            if (stream->codecpar == null || stream->codecpar->codec_type != AVMediaType.AVMEDIA_TYPE_SUBTITLE)
                continue;
            var codec = ffmpeg.avcodec_get_name(stream->codecpar->codec_id) ?? "";
            var title = DictGet(stream->metadata, "title") ?? "";
            var language = DictLanguage(stream->metadata);
            tracks.Add(new FfmpegSubtitleStream(i, title, language, codec, IsText(stream->codecpar->codec_id))
            {
                StableId = FfmpegTrackIdentity.StableId(stream, MediaTrackKind.Subtitle, title, language),
                Disposition = FfmpegTrackIdentity.Disposition(stream)
            });
        }

        return tracks;
    }

    private static IReadOnlyList<SubtitleCue> ReadStream(IntPtr formatPtr, int streamIndex)
    {
        var format = (AVFormatContext*)formatPtr;
        if (streamIndex < 0 || streamIndex >= (int)format->nb_streams)
            return [];
        var stream = format->streams[streamIndex];
        if (stream->codecpar == null || stream->codecpar->codec_type != AVMediaType.AVMEDIA_TYPE_SUBTITLE)
            return [];
        AVCodecContext* codec = null;
        AVPacket* packet = null;
        try
        {
            var decoder = ffmpeg.avcodec_find_decoder(stream->codecpar->codec_id);
            if (decoder != null)
            {
                codec = ffmpeg.avcodec_alloc_context3(decoder);
                FfmpegError.Check(ffmpeg.avcodec_parameters_to_context(codec, stream->codecpar), "复制字幕参数");
                codec->pkt_timebase = stream->time_base;
                FfmpegError.Check(ffmpeg.avcodec_open2(codec, decoder, null), "打开字幕解码器");
            }

            packet = ffmpeg.av_packet_alloc();
            var cues = new List<SubtitleCue>();
            var timeBase = stream->time_base;
            var codecId = stream->codecpar->codec_id;
            while (cues.Count < MaxCues && ffmpeg.av_read_frame(format, packet) >= 0)
            {
                if (packet->stream_index == streamIndex)
                    Collect(codec, packet, timeBase, codecId, cues);
                ffmpeg.av_packet_unref(packet);
            }

            if (codec != null && cues.Count < MaxCues)
            {
                ffmpeg.av_packet_unref(packet);
                Collect(codec, packet, timeBase, codecId, cues);
            }

            return cues;
        }
        finally
        {
            if (packet != null)
                ffmpeg.av_packet_free(&packet);
            if (codec != null)
                ffmpeg.avcodec_free_context(&codec);
        }
    }

    private static void Collect(
        AVCodecContext* codec,
        AVPacket* packet,
        AVRational timeBase,
        AVCodecID codecId,
        List<SubtitleCue> cues)
    {
        if (codec != null)
        {
            AVSubtitle sub = default;
            var got = 0;
            if (ffmpeg.avcodec_decode_subtitle2(codec, &sub, &got, packet) < 0 || got == 0)
                return;
            try
            {
                var pts = Pts(packet, &sub, timeBase);
                var start = TimeSpan.FromSeconds(Math.Max(0, pts + sub.start_display_time / 1000d));
                var end = TimeSpan.FromSeconds(Math.Max(0, pts + sub.end_display_time / 1000d));
                if (end <= start && packet->duration > 0)
                    end = start + TimeSpan.FromSeconds(packet->duration * Q2d(timeBase));
                if (end <= start)
                    end = start + TimeSpan.FromSeconds(3);
                var text = RectText(&sub);
                var bitmaps = RectBitmaps(&sub);
                var assEvent = FirstAssEvent(&sub);
                if (!string.IsNullOrWhiteSpace(text) || bitmaps.Count > 0)
                    cues.Add(new SubtitleCue(start, end, text, bitmaps, assEvent));
            }
            finally
            {
                ffmpeg.avsubtitle_free(&sub);
            }

            return;
        }

        if (packet->data == null || packet->size <= 0)
            return;
        var raw = Encoding.UTF8.GetString(packet->data, packet->size).TrimEnd('\0', '\r', '\n');
        if (string.IsNullOrWhiteSpace(raw))
            return;
        if (codecId is AVCodecID.AV_CODEC_ID_ASS or AVCodecID.AV_CODEC_ID_SSA)
        {
            var source = raw.Contains("Dialogue:", StringComparison.OrdinalIgnoreCase)
                ? raw
                : "Dialogue: 0,0:00:00.00,0:00:00.00,Default,,0,0,0,," + raw;
            var parsed = SubtitleText.Parse(source, ".ass");
            if (parsed.Count > 0)
            {
                cues.AddRange(parsed);
                return;
            }
        }

        var ptsFallback = packet->pts != ffmpeg.AV_NOPTS_VALUE ? packet->pts * Q2d(timeBase) : 0;
        var startFallback = TimeSpan.FromSeconds(Math.Max(0, ptsFallback));
        var endFallback = packet->duration > 0
            ? startFallback + TimeSpan.FromSeconds(packet->duration * Q2d(timeBase))
            : startFallback + TimeSpan.FromSeconds(3);
        cues.Add(new SubtitleCue(startFallback, endFallback, SubtitleText.Plain(raw)));
    }

    private static IReadOnlyList<SubtitleBitmap> RectBitmaps(AVSubtitle* sub)
    {
        var bitmaps = new List<SubtitleBitmap>();
        for (uint i = 0; i < sub->num_rects; i++)
        {
            var rect = sub->rects[i];
            if (rect == null || rect->w <= 0 || rect->h <= 0 || rect->data[0] == null || rect->data[1] == null || rect->nb_colors <= 0)
                continue;
            var length = checked(rect->w * rect->h);
            var bgra = new byte[length * 4];
            for (var pixel = 0; pixel < length; pixel++)
            {
                var color = rect->data[0][pixel] * 4;
                bgra[pixel * 4] = rect->data[1][color + 2];
                bgra[pixel * 4 + 1] = rect->data[1][color + 1];
                bgra[pixel * 4 + 2] = rect->data[1][color];
                bgra[pixel * 4 + 3] = rect->data[1][color + 3];
            }
            bitmaps.Add(new SubtitleBitmap(rect->x, rect->y, rect->w, rect->h, bgra));
        }
        return bitmaps;
    }

    private static string? FirstAssEvent(AVSubtitle* sub)
    {
        for (uint i = 0; i < sub->num_rects; i++)
        {
            var rect = sub->rects[i];
            if (rect != null && rect->type == AVSubtitleType.SUBTITLE_ASS && rect->ass != null)
                return Ptr(rect->ass);
        }
        return null;
    }

    private static string RectText(AVSubtitle* sub)
    {
        var builder = new StringBuilder();
        for (uint i = 0; i < sub->num_rects; i++)
        {
            var rect = sub->rects[i];
            if (rect == null)
                continue;
            string? piece = null;
            if (rect->type == AVSubtitleType.SUBTITLE_ASS && rect->ass != null)
                piece = SubtitleText.FromAssEvent(Ptr(rect->ass) ?? "");
            else if (rect->text != null)
                piece = SubtitleText.Plain(Ptr(rect->text) ?? "");
            else if (rect->ass != null)
                piece = SubtitleText.FromAssEvent(Ptr(rect->ass) ?? "");
            if (string.IsNullOrWhiteSpace(piece))
                continue;
            if (builder.Length > 0)
                builder.Append('\n');
            builder.Append(piece.Trim());
        }

        return builder.ToString();
    }

    private static double Pts(AVPacket* packet, AVSubtitle* sub, AVRational timeBase)
    {
        if (sub->pts != ffmpeg.AV_NOPTS_VALUE)
            return sub->pts / (double)ffmpeg.AV_TIME_BASE;
        if (packet->pts != ffmpeg.AV_NOPTS_VALUE)
            return packet->pts * Q2d(timeBase);
        return 0;
    }

    private static bool IsText(AVCodecID id) => id is
        AVCodecID.AV_CODEC_ID_SUBRIP
        or AVCodecID.AV_CODEC_ID_SRT
        or AVCodecID.AV_CODEC_ID_ASS
        or AVCodecID.AV_CODEC_ID_SSA
        or AVCodecID.AV_CODEC_ID_WEBVTT
        or AVCodecID.AV_CODEC_ID_MOV_TEXT
        or AVCodecID.AV_CODEC_ID_TEXT
        or AVCodecID.AV_CODEC_ID_HDMV_TEXT_SUBTITLE;

    private static double Q2d(AVRational rational) => rational.den == 0 ? 0 : rational.num / (double)rational.den;

    private static string? Ptr(byte* value) => value == null ? null : Marshal.PtrToStringUTF8((IntPtr)value);

    private static string? DictGet(AVDictionary* dict, string key)
    {
        if (dict == null)
            return null;
        var entry = ffmpeg.av_dict_get(dict, key, null, 0);
        if (entry == null)
            return null;
        var text = Marshal.PtrToStringUTF8((IntPtr)entry->value);
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    private static string? DictLanguage(AVDictionary* metadata)
    {
        var code = DictGet(metadata, "language");
        if (code == null || code.Equals("und", StringComparison.OrdinalIgnoreCase) || code.Equals("unk", StringComparison.OrdinalIgnoreCase))
            return null;
        try
        {
            foreach (var culture in CultureInfo.GetCultures(CultureTypes.NeutralCultures))
            {
                if (culture.TwoLetterISOLanguageName.Equals(code, StringComparison.OrdinalIgnoreCase)
                    || culture.ThreeLetterISOLanguageName.Equals(code, StringComparison.OrdinalIgnoreCase)
                    || culture.ThreeLetterWindowsLanguageName.Equals(code, StringComparison.OrdinalIgnoreCase))
                    return culture.DisplayName;
            }
        }
        catch (CultureNotFoundException)
        {
        }

        return code;
    }
}
