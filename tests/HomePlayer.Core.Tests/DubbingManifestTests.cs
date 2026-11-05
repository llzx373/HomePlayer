using HomePlayer.Core;

namespace HomePlayer.Core.Tests;

public class DubbingManifestTests
{
    [Fact]
    public void Manifest_roundtrip_keeps_timeline_and_audio_paths()
    {
        var root = Path.Combine(Path.GetTempPath(), "HomePlayer-dub-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var media = Path.Combine(root, "movie.mkv");
            var segmentAudio = Path.Combine(root, "00000.wav");
            var manifest = new DubbingManifest(
                1,
                media,
                "preset-female",
                DubbingFile.AudioPathFor(media),
                24000,
                [new DubbingSegment(0, 1500, 4200, "Hello", "你好", "preset-female", segmentAudio, 24000, 64800)]);

            DubbingFile.Save(manifest);
            var loaded = DubbingFile.TryLoad(DubbingFile.ManifestPathFor(media));

            Assert.NotNull(loaded);
            Assert.Equal("preset-female", loaded!.VoiceId);
            Assert.Equal(24000, loaded.SampleRate);
            var segment = Assert.Single(loaded.Segments);
            Assert.Equal(1500, segment.StartMs);
            Assert.Equal(4200, segment.EndMs);
            Assert.Equal("你好", segment.Translation);
            Assert.Equal(segmentAudio, segment.AudioPath);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void Missing_manifest_returns_null()
    {
        Assert.Null(DubbingFile.TryLoad(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".json")));
    }
}
