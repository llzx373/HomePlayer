using HomePlayer.Core;

namespace HomePlayer.AI;

/// <summary>
/// 音色目录：每个音色是 &lt;id&gt;.wav 和同名的 &lt;id&gt;.txt（参考音频逐字稿）。
/// </summary>
public sealed class VoiceLibrary(string directory)
{
    public string Directory { get; } = directory;

    public IReadOnlyList<VoiceProfile> List()
    {
        System.IO.Directory.CreateDirectory(Directory);
        var voices = new List<VoiceProfile>();
        foreach (var audio in System.IO.Directory.EnumerateFiles(Directory, "*.wav").OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            var id = Path.GetFileNameWithoutExtension(audio);
            var text = Path.Combine(Directory, id + ".txt");
            if (!File.Exists(text))
                continue;
            try
            {
                var reference = File.ReadAllText(text).Trim();
                if (reference.Length > 0)
                    voices.Add(new VoiceProfile(id, DisplayName(id), audio, reference));
            }
            catch (IOException)
            {
            }
        }

        return voices;
    }

    public VoiceProfile Add(string id, string sourceAudioPath, string referenceText)
    {
        if (string.IsNullOrWhiteSpace(referenceText))
            throw new InvalidOperationException("音色参考文本不能为空。");
        if (!File.Exists(sourceAudioPath))
            throw new FileNotFoundException("找不到音色参考音频。", sourceAudioPath);

        System.IO.Directory.CreateDirectory(Directory);
        var name = Normalize(id);
        var audio = Path.Combine(Directory, name + ".wav");
        var text = Path.Combine(Directory, name + ".txt");
        File.Copy(sourceAudioPath, audio, true);
        File.WriteAllText(text, referenceText.Trim(), new System.Text.UTF8Encoding(false));
        return new VoiceProfile(name, DisplayName(name), audio, referenceText.Trim());
    }

    public void Remove(string id)
    {
        foreach (var path in new[] { Path.Combine(Directory, id + ".wav"), Path.Combine(Directory, id + ".txt") })
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    private static string Normalize(string id)
    {
        var name = new string((id ?? "").Where(character => char.IsLetterOrDigit(character) || character is '-' or '_').ToArray());
        return name.Length == 0 ? "voice-" + Guid.NewGuid().ToString("N")[..8] : name;
    }

    private static string DisplayName(string id) => id switch
    {
        "preset-female" => "内置女声（系统语音）",
        "preset-male" => "内置男声（系统语音）",
        _ => id
    };
}
