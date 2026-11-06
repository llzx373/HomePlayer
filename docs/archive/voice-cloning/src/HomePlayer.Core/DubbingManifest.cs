using System.Text.Encodings.Web;
using System.Text.Json;

namespace HomePlayer.Core;

public sealed record DubbingSegment(
    int Index,
    int StartMs,
    int EndMs,
    string Text,
    string Translation,
    string VoiceId,
    string AudioPath,
    int SampleRate,
    long Samples);

public sealed record DubbingManifest(
    int Version,
    string MediaPath,
    string VoiceId,
    string AudioPath,
    int SampleRate,
    IReadOnlyList<DubbingSegment> Segments);

public static class DubbingFile
{
    public const int FormatVersion = 1;

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static string ManifestPathFor(string mediaPath) => Path.ChangeExtension(mediaPath, ".ai.dub.json");

    public static string AudioPathFor(string mediaPath) => Path.ChangeExtension(mediaPath, ".ai.dub.wav");

    public static void Save(DubbingManifest manifest)
    {
        var path = ManifestPathFor(manifest.MediaPath);
        File.WriteAllText(path, JsonSerializer.Serialize(manifest, Options), new System.Text.UTF8Encoding(false));
    }

    public static DubbingManifest? TryLoad(string manifestPath)
    {
        try
        {
            if (!File.Exists(manifestPath))
                return null;
            var manifest = JsonSerializer.Deserialize<DubbingManifest>(File.ReadAllText(manifestPath), Options);
            return manifest is { Version: > 0, Segments.Count: > 0 } ? manifest : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }
}
