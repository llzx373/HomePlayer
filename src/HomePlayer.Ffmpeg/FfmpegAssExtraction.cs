using System.Runtime.InteropServices;
using System.Text;
using FFmpeg.AutoGen;

namespace HomePlayer.Ffmpeg;

public static unsafe class FfmpegAssExtraction
{
    public static void Extract(string path, int streamIndex, string destination, string fontDirectory)
    {
        FfmpegNative.Ensure();
        AVFormatContext* format = null;
        AVPacket* packet = null;
        try
        {
            var open = ffmpeg.avformat_open_input(&format, path, null, null);
            FfmpegError.Check(open, "打开内嵌 ASS");
            FfmpegError.Check(ffmpeg.avformat_find_stream_info(format, null), "读取内嵌 ASS 信息");
            if (streamIndex < 0 || streamIndex >= (int)format->nb_streams)
                throw new InvalidOperationException("内嵌 ASS 轨道不存在。");
            var stream = format->streams[streamIndex];
            if (stream->codecpar == null || stream->codecpar->codec_id is not (AVCodecID.AV_CODEC_ID_ASS or AVCodecID.AV_CODEC_ID_SSA))
                throw new InvalidOperationException("所选轨道不是 ASS/SSA。");
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            Directory.CreateDirectory(fontDirectory);
            using (var writer = new StreamWriter(destination, false, new UTF8Encoding(false)))
            {
                var header = stream->codecpar->extradata != null && stream->codecpar->extradata_size > 0
                    ? Encoding.UTF8.GetString(stream->codecpar->extradata, stream->codecpar->extradata_size).TrimEnd('\0')
                    : "";
                writer.WriteLine(header);
                if (!header.Contains("[Events]", StringComparison.OrdinalIgnoreCase))
                {
                    writer.WriteLine("[Events]");
                    writer.WriteLine("Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text");
                }
                packet = ffmpeg.av_packet_alloc();
                while (ffmpeg.av_read_frame(format, packet) >= 0)
                {
                    if (packet->stream_index == streamIndex && packet->data != null && packet->size > 0)
                    {
                        var text = Encoding.UTF8.GetString(packet->data, packet->size).TrimEnd('\0', '\r', '\n');
                        if (!string.IsNullOrWhiteSpace(text))
                            writer.WriteLine(PacketDialogue(packet, stream->time_base, text));
                    }
                    ffmpeg.av_packet_unref(packet);
                }
            }
            ExtractFonts(format, fontDirectory);
        }
        finally
        {
            if (packet != null)
                ffmpeg.av_packet_free(&packet);
            if (format != null)
                ffmpeg.avformat_close_input(&format);
        }
    }

    private static string PacketDialogue(AVPacket* packet, AVRational timeBase, string text)
    {
        var payload = text.StartsWith("Dialogue:", StringComparison.OrdinalIgnoreCase)
            ? text["Dialogue:".Length..].TrimStart()
            : text;
        var parts = payload.Split(',', 3);
        if (parts.Length < 3 || parts[1].Contains(':'))
            return "Dialogue: " + payload;
        var scale = timeBase.den == 0 ? 0 : timeBase.num / (double)timeBase.den;
        var start = packet->pts == ffmpeg.AV_NOPTS_VALUE ? 0 : Math.Max(0, packet->pts * scale);
        var end = packet->duration > 0 ? start + packet->duration * scale : start + 3;
        return $"Dialogue: {parts[0]},{AssTime(start)},{AssTime(end)},{parts[2]}";
    }

    private static string AssTime(double seconds)
    {
        var value = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return $"{(int)value.TotalHours}:{value.Minutes:00}:{value.Seconds:00}.{value.Milliseconds / 10:00}";
    }

    private static void ExtractFonts(AVFormatContext* format, string fontDirectory)
    {
        for (var i = 0; i < (int)format->nb_streams; i++)
        {
            var stream = format->streams[i];
            if (stream->codecpar == null || stream->codecpar->codec_type != AVMediaType.AVMEDIA_TYPE_ATTACHMENT
                || stream->codecpar->extradata == null || stream->codecpar->extradata_size <= 0)
                continue;
            var entry = ffmpeg.av_dict_get(stream->metadata, "filename", null, 0);
            var name = entry == null ? $"font-{i}.ttf" : Marshal.PtrToStringUTF8((IntPtr)entry->value);
            name = Path.GetFileName(name);
            if (string.IsNullOrWhiteSpace(name))
                name = $"font-{i}.ttf";
            File.WriteAllBytes(
                Path.Combine(fontDirectory, name),
                new ReadOnlySpan<byte>(stream->codecpar->extradata, stream->codecpar->extradata_size).ToArray());
        }
    }
}
