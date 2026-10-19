using System.Globalization;
using System.Text;

namespace HomePlayer.Core;

public sealed class PipelineInfo
{
    public string FilePath { get; init; } = "";
    public string Engine { get; init; } = "";
    public string? EngineReason { get; set; }
    public string? Container { get; init; }
    public double? DurationSeconds { get; init; }
    public long? FileSize { get; init; }
    public double? BitrateMbps { get; init; }
    public string? VideoCodec { get; init; }
    public string? VideoDecoder { get; init; }
    public int? Width { get; init; }
    public int? Height { get; init; }
    public double? FrameRate { get; init; }
    public string? PixelFormat { get; init; }
    public string? Hardware { get; init; }
    public string? AudioCodec { get; init; }
    public string? AudioDecoder { get; init; }
    public int? Channels { get; init; }
    public int? SampleRate { get; init; }
    public IReadOnlyList<string> AudioTracks { get; init; } = [];
    public IReadOnlyList<string> SubtitleTracks { get; init; } = [];
    public IReadOnlyList<string> Filters { get; init; } = [];
    public IReadOnlyList<string> Connections { get; init; } = [];
    public IReadOnlyList<string> Usage { get; init; } = [];

    public string Format()
    {
        var text = new StringBuilder();
        text.AppendLine("来源");
        text.AppendLine($"文件：{FilePath}");
        text.AppendLine($"引擎：{Engine}");
        if (!string.IsNullOrWhiteSpace(EngineReason))
            text.AppendLine($"说明：{EngineReason}");
        if (Usage.Count > 0)
        {
            text.AppendLine();
            text.AppendLine("实际使用");
            foreach (var line in Usage)
                text.AppendLine(line);
        }

        text.AppendLine();
        text.AppendLine("容器");
        text.AppendLine($"格式：{Value(Container)}");
        text.AppendLine($"时长：{ClockText.FormatSeconds(DurationSeconds)}");
        text.AppendLine($"大小：{(FileSize is long size ? FormatSize(size) : "未知")}");
        text.AppendLine($"码率：{(BitrateMbps is double rate ? $"{rate.ToString("0.##", CultureInfo.InvariantCulture)} Mbps（约）" : "未知")}");

        text.AppendLine();
        text.AppendLine("视频");
        text.AppendLine($"编码：{Value(VideoCodec)}");
        text.AppendLine($"分辨率：{(Width is int w && Height is int h ? $"{w}×{h}" : "未知")}");
        text.AppendLine($"帧率：{(FrameRate is double fps ? $"{fps.ToString("0.###", CultureInfo.InvariantCulture)} fps" : "未知")}");
        text.AppendLine($"像素格式：{Value(PixelFormat)}");
        text.AppendLine($"解码器：{Value(VideoDecoder)}");
        text.AppendLine($"硬件加速：{Value(Hardware)}");

        text.AppendLine();
        text.AppendLine("音频");
        text.AppendLine($"编码：{Value(AudioCodec)}");
        text.AppendLine($"解码器：{Value(AudioDecoder)}");
        text.AppendLine($"声道：{(Channels is int channels ? channels.ToString(CultureInfo.InvariantCulture) : "未知")}");
        text.AppendLine($"采样率：{(SampleRate is int hertz ? $"{hertz} Hz" : "未知")}");
        if (AudioTracks.Count > 0)
        {
            text.AppendLine("音轨：");
            foreach (var track in AudioTracks)
                text.AppendLine($"  {track}");
        }

        text.AppendLine();
        text.AppendLine("字幕");
        if (SubtitleTracks.Count == 0)
            text.AppendLine("无（第一版只列出，不渲染）");
        else
        {
            foreach (var track in SubtitleTracks)
                text.AppendLine($"  {track}");
            text.AppendLine("第一版只列出，不渲染。");
        }

        text.AppendLine();
        text.AppendLine("滤镜链");
        if (Filters.Count == 0)
            text.AppendLine("无");
        else
        {
            for (var i = 0; i < Filters.Count; i++)
                text.AppendLine($"{i + 1}. {Filters[i]}");
        }

        if (Connections.Count > 0)
        {
            text.AppendLine();
            text.AppendLine("连接的媒体类型");
            foreach (var connection in Connections)
                text.AppendLine(connection);
        }

        return text.ToString().TrimEnd();
    }

    private static string Value(string? text) => string.IsNullOrWhiteSpace(text) ? "未知" : text;

    private static string FormatSize(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double size = bytes;
        var unit = 0;
        while (size >= 1024 && unit < units.Length - 1)
        {
            size /= 1024;
            unit++;
        }

        return $"{size.ToString("0.##", CultureInfo.InvariantCulture)} {units[unit]}";
    }
}
