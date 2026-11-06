namespace HomePlayer.Core.Tests;

public class SpeakerProfileTrackerTests
{
    [Fact]
    public void Same_embedding_maps_to_same_speaker()
    {
        var tracker = new SpeakerProfileTracker();
        var first = tracker.Match([1f, 0f, 0f], 3);
        var second = tracker.Match([1f, 0f, 0f], 3);

        Assert.Equal(0, first);
        Assert.Equal(0, second);
        Assert.Equal(1, tracker.Count);
    }

    [Fact]
    public void Different_long_embedding_creates_new_speaker()
    {
        var tracker = new SpeakerProfileTracker();
        tracker.Match([1f, 0f, 0f], 3);

        Assert.Equal(1, tracker.Match([0f, 1f, 0f], 3));
        Assert.Equal(2, tracker.Count);
    }

    [Fact]
    public void Short_unknown_embedding_reuses_nearest_speaker()
    {
        var tracker = new SpeakerProfileTracker();
        tracker.Match([1f, 0f, 0f], 3);

        Assert.Equal(0, tracker.Match([0f, 1f, 0f], 0.5));
        Assert.Equal(1, tracker.Count);
    }

    [Fact]
    public void Speaker_limit_reuses_nearest()
    {
        var tracker = new SpeakerProfileTracker(maxSpeakers: 1);
        tracker.Match([1f, 0f, 0f], 3);

        Assert.Equal(0, tracker.Match([0f, 1f, 0f], 3));
        Assert.Equal(1, tracker.Count);
    }

    [Fact]
    public void Empty_embedding_falls_back_to_first_speaker()
    {
        var tracker = new SpeakerProfileTracker();
        Assert.Equal(-1, tracker.Match([], 3));

        tracker.Match([1f, 0f, 0f], 3);
        Assert.Equal(0, tracker.Match([], 3));
    }
}
