using HomePlayer.Core;

namespace HomePlayer.AI.Tests;

public class AiSubtitleServiceTests
{
    [Theory]
    [InlineData("cpu", true, 24576, true)]
    [InlineData("auto", false, 0, true)]
    [InlineData("auto", true, 6144, true)]
    [InlineData("auto", true, 8192, false)]
    [InlineData("auto", true, 24576, false)]
    [InlineData("cuda", true, 0, false)]
    public void RecommendLightweightTranslation_selects_by_device_and_vram(
        string device, bool hasNvidiaGpu, int memoryMiB, bool expected)
    {
        Assert.Equal(expected, AiSubtitleService.RecommendLightweightTranslation(device, hasNvidiaGpu, memoryMiB));
    }

    [Fact]
    public void Installed_model_requires_matching_size_and_verified_marker()
    {
        var root = Path.Combine(Path.GetTempPath(), "HomePlayer-model-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var content = new byte[] { 1, 2, 3, 4 };
            var model = new AiModelInfo("test", AiModelKind.Translation, "test", "repo", "rev", "test.gguf",
                content.Length, "ABC123", "MIT");
            var manager = new AiModelManager(root, () => "");
            var path = manager.PathFor(model);
            File.WriteAllBytes(path, content);

            Assert.False(manager.IsInstalled(model));
            File.WriteAllText(path + ".verified", "wrong");
            Assert.False(manager.IsInstalled(model));
            File.WriteAllText(path + ".verified", "abc123");
            Assert.True(manager.IsInstalled(model));
            File.WriteAllBytes(path, new byte[] { 1, 2, 3 });
            Assert.False(manager.IsInstalled(model));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void ChangeModelDirectory_switches_new_model_paths()
    {
        var root = Path.Combine(Path.GetTempPath(), "HomePlayer-model-dir-" + Guid.NewGuid().ToString("N"));
        try
        {
            var first = Path.Combine(root, "first");
            var second = Path.Combine(root, "second");
            var service = new AiSubtitleService(first, () => "", () => "");

            Assert.Equal(first, service.ModelDirectory);

            service.ChangeModelDirectory(second);

            Assert.Equal(second, service.ModelDirectory);
            Assert.True(Directory.Exists(second));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, true);
        }
    }

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
