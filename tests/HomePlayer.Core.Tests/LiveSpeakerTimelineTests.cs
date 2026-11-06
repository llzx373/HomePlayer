namespace HomePlayer.Core.Tests;

public class LiveSpeakerTimelineTests
{
    [Fact]
    public void Dominant_speaker_uses_largest_overlap()
    {
        var timeline = new LiveSpeakerTimeline();
        timeline.Add(new LiveSpeakerSegment(0, 0, 4.8));
        timeline.Add(new LiveSpeakerSegment(1, 5, 10));

        Assert.Equal(1, timeline.DominantSpeaker(4, 6));
        Assert.Equal(0, timeline.DominantSpeaker(0, 2));
        Assert.Equal(-1, timeline.DominantSpeaker(12, 14));
    }

    [Fact]
    public void Adjacent_same_speaker_segments_merge()
    {
        var timeline = new LiveSpeakerTimeline();
        timeline.Add(new LiveSpeakerSegment(0, 0, 2));
        timeline.Add(new LiveSpeakerSegment(0, 2.2, 4));
        timeline.Add(new LiveSpeakerSegment(1, 4.5, 6));

        var snapshot = timeline.Snapshot();
        Assert.Equal(2, snapshot.Count);
        Assert.Equal(0, snapshot[0].Speaker);
        Assert.Equal(0, snapshot[0].StartSeconds);
        Assert.Equal(4, snapshot[0].EndSeconds);
    }

    [Fact]
    public void Diarized_through_only_moves_forward()
    {
        var timeline = new LiveSpeakerTimeline();
        timeline.SetThrough(10);
        timeline.SetThrough(5);
        timeline.Add(new LiveSpeakerSegment(0, 12, 14));

        Assert.Equal(14, timeline.DiarizedThrough);
    }

    [Fact]
    public void Reset_clears_segments_and_frontier()
    {
        var timeline = new LiveSpeakerTimeline();
        timeline.Add(new LiveSpeakerSegment(0, 0, 2));
        timeline.Reset();

        Assert.Empty(timeline.Snapshot());
        Assert.Equal(0, timeline.DiarizedThrough);
    }
}
