using HomePlayer.Core;

namespace HomePlayer.Core.Tests;

public class AiSubtitleFileTests
{
    [Fact]
    public void Save_and_attach_roundtrip_preserves_translations()
    {
        var root = Path.Combine(Path.GetTempPath(), "HomePlayer-ai-json-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "movie.ai.srt");
            SubtitleCue[] cues =
            [
                new(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), "original one", Translation: "译文一"),
                new(TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(4), "original two")
            ];

            SubtitleFileWriter.WriteSrt(path, cues, bilingual: true);
            AiSubtitleFile.Save(path, cues);

            var json = Path.Combine(root, "movie.ai.json");
            Assert.True(File.Exists(json));

            var attached = AiSubtitleFile.AttachTranslations(SubtitleText.ParseFile(path), path);

            Assert.Equal(2, attached.Count);
            Assert.Equal("译文一", attached[0].Translation);
            Assert.Null(attached[1].Translation);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void Attach_keeps_srt_text_when_it_was_edited()
    {
        var root = Path.Combine(Path.GetTempPath(), "HomePlayer-ai-json-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "movie.ai.srt");
            AiSubtitleFile.Save(path, [new(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), "旧原文", Translation: "译文")]);
            SubtitleFileWriter.WriteSrt(path, [new SubtitleCue(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), "手工修正的原文")]);

            var cue = Assert.Single(AiSubtitleFile.AttachTranslations(SubtitleText.ParseFile(path), path));

            Assert.Equal("手工修正的原文", cue.Text);
            Assert.Equal("译文", cue.Translation);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void Attach_restores_original_when_srt_contains_translation_only()
    {
        var root = Path.Combine(Path.GetTempPath(), "HomePlayer-ai-json-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "movie.ai.srt");
            var cue = new SubtitleCue(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), "原文", Translation: "译文");
            AiSubtitleFile.Save(path, [cue]);
            SubtitleFileWriter.WriteSrt(path, [cue with { Text = "译文", Translation = null }]);

            var attached = Assert.Single(AiSubtitleFile.AttachTranslations(SubtitleText.ParseFile(path), path));

            Assert.Equal("原文", attached.Text);
            Assert.Equal("译文", attached.Translation);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void Attach_ignores_plain_sidecars()
    {
        var root = Path.Combine(Path.GetTempPath(), "HomePlayer-ai-json-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "movie.srt");
            SubtitleFileWriter.WriteSrt(path, [new SubtitleCue(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), "原文")]);
            File.WriteAllText(Path.Combine(root, "movie.json"), "{\"version\":1,\"cues\":[{\"startMs\":1000,\"endMs\":2000,\"text\":\"x\",\"translation\":\"不该出现\"}]}");

            var cue = Assert.Single(AiSubtitleFile.AttachTranslations(SubtitleText.ParseFile(path), path));

            Assert.Null(cue.Translation);
            Assert.Null(AiSubtitleFile.PathFor(path));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void Attach_ignores_unreadable_json()
    {
        var root = Path.Combine(Path.GetTempPath(), "HomePlayer-ai-json-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "movie.ai.srt");
            SubtitleFileWriter.WriteSrt(path, [new SubtitleCue(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), "原文")]);
            File.WriteAllText(Path.Combine(root, "movie.ai.json"), "not json");

            var cue = Assert.Single(AiSubtitleFile.AttachTranslations(SubtitleText.ParseFile(path), path));

            Assert.Null(cue.Translation);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }
}
