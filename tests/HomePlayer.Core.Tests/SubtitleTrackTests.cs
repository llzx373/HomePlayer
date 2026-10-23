namespace HomePlayer.Core.Tests;

public class SubtitleTrackTests
{
    [Fact]
    public void Empty_track_has_no_cue()
    {
        Assert.Null(SubtitleTrack.Empty.At(TimeSpan.Zero));
        Assert.Null(new SubtitleTrack([]).At(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void Matches_linear_scan_at_boundaries()
    {
        var cues = new[]
        {
            new SubtitleCue(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3), "A"),
            new SubtitleCue(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(7), "B")
        };
        var track = new SubtitleTrack(cues);

        Assert.Equal("A", track.At(TimeSpan.FromSeconds(1))?.Text);
        Assert.Equal("A", track.At(TimeSpan.FromSeconds(2.9))?.Text);
        Assert.Null(track.At(TimeSpan.FromSeconds(3)));
        Assert.Null(track.At(TimeSpan.FromSeconds(4)));
        Assert.Equal("B", track.At(TimeSpan.FromSeconds(5))?.Text);
        Assert.Null(track.At(TimeSpan.FromSeconds(7)));
    }

    [Fact]
    public void Overlapping_cues_still_return_the_first_match()
    {
        var cues = new[]
        {
            new SubtitleCue(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(10), "long"),
            new SubtitleCue(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(3), "short"),
            new SubtitleCue(TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(5), "later")
        };
        var track = new SubtitleTrack(cues);

        Assert.Equal("long", track.At(TimeSpan.FromSeconds(2.5))?.Text);
        Assert.Equal("long", track.At(TimeSpan.FromSeconds(4.5))?.Text);
        Assert.Equal("long", track.At(TimeSpan.FromSeconds(9.9))?.Text);
    }

    [Fact]
    public void Unsorted_cues_fall_back_to_linear_scan()
    {
        var cues = new[]
        {
            new SubtitleCue(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(7), "B"),
            new SubtitleCue(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3), "A")
        };
        var track = new SubtitleTrack(cues);

        Assert.Equal("A", track.At(TimeSpan.FromSeconds(2))?.Text);
        Assert.Equal("B", track.At(TimeSpan.FromSeconds(6))?.Text);
        Assert.Null(track.At(TimeSpan.FromSeconds(4)));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(42)]
    public void Matches_linear_scan_for_generated_tracks(int seed)
    {
        var random = new Random(seed);
        for (var iteration = 0; iteration < 100; iteration++)
        {
            var cues = new List<SubtitleCue>();
            var seconds = 0.0;
            var count = random.Next(0, 40);
            for (var i = 0; i < count; i++)
            {
                seconds += random.NextDouble() * 2;
                var start = TimeSpan.FromSeconds(seconds);
                var end = start + TimeSpan.FromSeconds(random.NextDouble() * 4);
                cues.Add(new SubtitleCue(start, end, i.ToString()));
            }

            var track = new SubtitleTrack(cues);
            for (var probe = 0; probe < 80; probe++)
            {
                var time = TimeSpan.FromSeconds(random.NextDouble() * (seconds + 5));
                Assert.Equal(SubtitleCues.At(cues, time), track.At(time));
            }
        }
    }
}
