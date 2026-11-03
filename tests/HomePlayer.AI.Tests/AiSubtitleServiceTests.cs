using HomePlayer.Core;

namespace HomePlayer.AI.Tests;

public class AiSubtitleServiceTests
{
    [Fact]
    public void SaveBesideMedia_replaces_complete_sidecar_without_leaving_temporary_files()
    {
        var root = Path.Combine(Path.GetTempPath(), "HomePlayer-ai-subtitles-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var media = Path.Combine(root, "movie.mkv");
            File.WriteAllBytes(media, [0]);
            var output = Path.Combine(root, "movie.ai.srt");
            File.WriteAllText(output, "previous complete subtitle");
            var service = new AiSubtitleService(Path.Combine(root, "models"), () => "", () => "");
            IReadOnlyList<SubtitleCue> cues =
            [
                new(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3), "识别完成的字幕")
            ];

            var result = service.SaveBesideMedia(media, cues, "test");

            Assert.Equal(output, result.Path);
            var saved = Assert.Single(SubtitleText.ParseFile(output));
            Assert.Equal("识别完成的字幕", saved.Text);
            Assert.Equal(output, Assert.Single(SubtitleFiles.Find(media)));
            Assert.Empty(Directory.GetFiles(root, "*.tmp"));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }
}
