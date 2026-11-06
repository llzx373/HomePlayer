using HomePlayer.Core;

namespace HomePlayer.Core.Tests;

public class LiveDubbingTrackTests
{
    [Fact]
    public void Speech_spans_merge_when_close_or_overlapping()
    {
        var track = new LiveDubbingTrack();
        track.AddSpeech(2, 4);
        track.AddSpeech(4.1, 6);
        track.AddSpeech(8, 9);

        var spans = track.SpeechSnapshot();

        Assert.Equal(2, spans.Length);
        Assert.Equal(2, spans[0].StartSeconds, 3);
        Assert.Equal(6, spans[0].EndSeconds, 3);
        Assert.Equal(8, spans[1].StartSeconds, 3);
    }

    [Fact]
    public void Speech_spans_insert_before_existing()
    {
        var track = new LiveDubbingTrack();
        track.AddSpeech(5, 7);
        track.AddSpeech(1, 2);

        var spans = track.SpeechSnapshot();

        Assert.Equal(2, spans.Length);
        Assert.Equal(1, spans[0].StartSeconds, 3);
        Assert.Equal(5, spans[1].StartSeconds, 3);
    }

    [Fact]
    public void Remove_speech_splits_the_span()
    {
        var track = new LiveDubbingTrack();
        track.AddSpeech(1, 9);

        track.RemoveSpeech(3, 5);

        var spans = track.SpeechSnapshot();
        Assert.Equal(2, spans.Length);
        Assert.Equal(1, spans[0].StartSeconds, 3);
        Assert.Equal(3, spans[0].EndSeconds, 3);
        Assert.Equal(5, spans[1].StartSeconds, 3);
        Assert.Equal(9, spans[1].EndSeconds, 3);
    }

    [Fact]
    public void Evict_before_drops_old_speech_and_segments()
    {
        var track = new LiveDubbingTrack();
        track.AddSpeech(0, 2);
        track.AddSpeech(10, 12);
        track.Add(new LiveDubbingSegment(1, 0, 2, 24000, new short[48000]));

        track.EvictBefore(5);

        Assert.Empty(track.Snapshot());
        Assert.Single(track.SpeechSnapshot());
        Assert.Equal(10, track.SpeechSnapshot()[0].StartSeconds, 3);
    }
}
