using System.Text.Encodings.Web;
using System.Text.Json;

namespace HomePlayer.Core;

/// <summary>
/// AI 生成字幕的结构化附带文件。SRT 只能表达一段文本，无法区分原文和译文，
/// 这里把两条文本按原始时间轴单独保存，播放时按显示模式挑选。
/// </summary>
public static class AiSubtitleFile
{
    public const int FormatVersion = 1;

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static string? PathFor(string subtitlePath)
    {
        if (!subtitlePath.EndsWith(".ai.srt", StringComparison.OrdinalIgnoreCase))
            return null;
        return subtitlePath[..^4] + ".json";
    }

    public static void Save(string subtitlePath, IReadOnlyList<SubtitleCue> cues)
    {
        var path = PathFor(subtitlePath);
        if (path == null)
            return;
        if (!cues.Any(cue => cue.HasTranslation))
        {
            if (File.Exists(path))
                File.Delete(path);
            return;
        }

        var payload = new Payload(
            FormatVersion,
            cues.Select(cue => new Entry(
                (long)Math.Round(cue.Start.TotalMilliseconds),
                (long)Math.Round(cue.End.TotalMilliseconds),
                cue.Text,
                cue.HasTranslation ? cue.Translation : null)).ToArray());
        WriteAtomic(path, JsonSerializer.Serialize(payload, Options));
    }

    /// <summary>
    /// 按开始时间把译文挂回从 SRT 解析出的 cue。SRT 仍是文本和时间的权威来源，
    /// 用户手工改过 SRT 也不会被旧译文覆盖。
    /// </summary>
    public static IReadOnlyList<SubtitleCue> AttachTranslations(IReadOnlyList<SubtitleCue> cues, string subtitlePath)
    {
        var path = PathFor(subtitlePath);
        if (path == null || !File.Exists(path) || cues.Count == 0)
            return cues;

        Payload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<Payload>(File.ReadAllText(path), Options);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return cues;
        }

        if (payload?.Cues is not { Length: > 0 })
            return cues;

        var entries = new Dictionary<long, Entry>();
        foreach (var entry in payload.Cues)
        {
            if (!string.IsNullOrWhiteSpace(entry.Translation))
                entries[entry.StartMs] = entry;
        }

        if (entries.Count == 0)
            return cues;

        var result = new SubtitleCue[cues.Count];
        var changed = false;
        for (var i = 0; i < cues.Count; i++)
        {
            var cue = cues[i];
            if (cue.HasTranslation || !entries.TryGetValue((long)Math.Round(cue.Start.TotalMilliseconds), out var entry))
            {
                result[i] = cue;
                continue;
            }

            var translation = entry.Translation!.Trim();
            var text = cue.Text;
            // 双语 SRT 把译文写在原文下一行；结构化数据已经在手，去掉重复的那一行。
            var suffix = "\n" + translation;
            if (text.EndsWith(suffix, StringComparison.Ordinal))
                text = text[..^suffix.Length];
            // 只导出译文的 SRT 里没有原文，用结构化数据补回来。
            if (string.Equals(text.Trim(), translation, StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(entry.Text))
                text = entry.Text.Trim();

            result[i] = cue with { Text = text, Translation = translation };
            changed = true;
        }

        return changed ? result : cues;
    }

    private static void WriteAtomic(string path, string text)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);
        var temp = path + ".tmp";
        try
        {
            File.WriteAllText(temp, text, new System.Text.UTF8Encoding(false));
            File.Move(temp, path, true);
        }
        finally
        {
            if (File.Exists(temp))
                File.Delete(temp);
        }
    }

    private sealed record Payload(int Version, Entry[] Cues);

    private sealed record Entry(long StartMs, long EndMs, string Text, string? Translation);
}
