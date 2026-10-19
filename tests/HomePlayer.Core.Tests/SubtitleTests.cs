using HomePlayer.Core;

namespace HomePlayer.Core.Tests;

public class SubtitleFilesTests
{
    [Fact]
    public void Find_matches_stem_and_language_tag_sidecars()
    {
        var root = Path.Combine(Path.GetTempPath(), "HomePlayer-subtitles-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var video = Path.Combine(root, "movie.mkv");
            File.WriteAllBytes(video, [0]);
            Touch(root, "movie.srt");
            Touch(root, "movie.en.srt");
            Touch(root, "movie.zh-CN.ass");
            Touch(root, "movie.ssa");
            Touch(root, "movie.vtt");
            Touch(root, "other.srt");
            Touch(root, "movie.txt");
            Touch(root, "movie.en.forced.srt");

            var found = SubtitleFiles.Find(video);

            Assert.Contains(found, path => NameIs(path, "movie.srt"));
            Assert.Contains(found, path => NameIs(path, "movie.en.srt"));
            Assert.Contains(found, path => NameIs(path, "movie.zh-CN.ass"));
            Assert.Contains(found, path => NameIs(path, "movie.ssa"));
            Assert.Contains(found, path => NameIs(path, "movie.vtt"));
            Assert.DoesNotContain(found, path => NameIs(path, "other.srt"));
            Assert.DoesNotContain(found, path => NameIs(path, "movie.txt"));
            Assert.DoesNotContain(found, path => NameIs(path, "movie.en.forced.srt"));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void IsSidecar_is_case_insensitive()
    {
        Assert.True(SubtitleFiles.IsSidecar(@"C:\Films\Movie.MKV", @"C:\Films\MOVIE.SRT"));
        Assert.True(SubtitleFiles.IsSidecar(@"C:\Films\Movie.MKV", @"C:\Films\Movie.EN.VTT"));
        Assert.False(SubtitleFiles.IsSidecar(@"C:\Films\Movie.MKV", @"C:\Films\Movie.backup.srt"));
    }

    private static void Touch(string root, string name) => File.WriteAllBytes(Path.Combine(root, name), [0]);

    private static bool NameIs(string path, string name) =>
        Path.GetFileName(path).Equals(name, StringComparison.OrdinalIgnoreCase);
}

public class SubtitleTextTests
{
    [Fact]
    public void Parse_srt_two_cues()
    {
        const string srt = """
            1
            00:00:01,000 --> 00:00:04,000
            Hello

            2
            00:00:05,500 --> 00:00:08,000
            World
            line 2
            """;

        var cues = SubtitleText.Parse(srt, ".srt");

        Assert.Equal(2, cues.Count);
        Assert.Equal(TimeSpan.FromSeconds(1), cues[0].Start);
        Assert.Equal(TimeSpan.FromSeconds(4), cues[0].End);
        Assert.Equal("Hello", cues[0].Text);
        Assert.Equal(TimeSpan.FromSeconds(5.5), cues[1].Start);
        Assert.Equal(TimeSpan.FromSeconds(8), cues[1].End);
        Assert.Equal("World\nline 2", cues[1].Text);
    }

    [Fact]
    public void Parse_ass_strips_override_tags_and_newlines()
    {
        const string ass = """
            [Events]
            Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text
            Dialogue: 0,0:00:01.00,0:00:02.50,Default,,0,0,0,,{\b1}Hello{\b0}\NWorld
            """;

        var cues = SubtitleText.Parse(ass, ".ass");

        Assert.Single(cues);
        Assert.Equal(TimeSpan.FromSeconds(1), cues[0].Start);
        Assert.Equal(TimeSpan.FromSeconds(2.5), cues[0].End);
        Assert.Equal("Hello\nWorld", cues[0].Text);
    }
}

public class SubtitleCuesTests
{
    private static readonly SubtitleCue[] Cues =
    [
        new(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3), "A"),
        new(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(7), "B")
    ];

    [Fact]
    public void At_returns_cue_inside_range()
    {
        Assert.Equal("A", SubtitleCues.At(Cues, TimeSpan.FromSeconds(1))?.Text);
        Assert.Equal("A", SubtitleCues.At(Cues, TimeSpan.FromSeconds(2))?.Text);
        Assert.Equal("B", SubtitleCues.At(Cues, TimeSpan.FromSeconds(5))?.Text);
        Assert.Equal("B", SubtitleCues.At(Cues, TimeSpan.FromSeconds(6.9))?.Text);
    }

    [Fact]
    public void At_returns_null_between_cues()
    {
        Assert.Null(SubtitleCues.At(Cues, TimeSpan.FromSeconds(3)));
        Assert.Null(SubtitleCues.At(Cues, TimeSpan.FromSeconds(4)));
    }

    [Fact]
    public void At_returns_null_after_last()
    {
        Assert.Null(SubtitleCues.At(Cues, TimeSpan.FromSeconds(7)));
        Assert.Null(SubtitleCues.At(Cues, TimeSpan.FromSeconds(10)));
    }
}
