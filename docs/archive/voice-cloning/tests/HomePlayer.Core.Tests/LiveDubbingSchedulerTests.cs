using HomePlayer.Core;

namespace HomePlayer.Core.Tests;

public class LiveDubbingSchedulerTests
{
    [Fact]
    public void Coverage_waits_for_translation_and_synthesis()
    {
        var scheduler = new LiveDubbingScheduler();
        scheduler.Reset(0);
        scheduler.SetRecognizedThrough(10);
        scheduler.Track(1, 2, 4);
        scheduler.Track(2, 6, 8);

        Assert.Equal(2, scheduler.CoveredThrough);

        scheduler.Translate(1, "你好");
        scheduler.AssignSpeaker(1, 0);
        Assert.Equal(2, scheduler.CoveredThrough);

        var job = scheduler.Next(0, lookaheadSeconds: 20);
        Assert.NotNull(job);
        Assert.Equal(1, job!.Key);
        Assert.Equal("你好", job.Text);
        Assert.Equal(0, job.Speaker);

        scheduler.Complete(1);
        Assert.Equal(6, scheduler.CoveredThrough);

        scheduler.Translate(2, "");
        Assert.Equal(10, scheduler.CoveredThrough);
    }

    [Fact]
    public void Coverage_waits_for_speaker()
    {
        var scheduler = new LiveDubbingScheduler();
        scheduler.Reset(0);
        scheduler.SetRecognizedThrough(10);
        scheduler.Track(1, 2, 4);
        scheduler.Translate(1, "你好");

        Assert.Equal(2, scheduler.CoveredThrough);
        Assert.Null(scheduler.Next(0, lookaheadSeconds: 20));

        scheduler.AssignSpeaker(1, 3);
        var job = scheduler.Next(0, lookaheadSeconds: 20);
        Assert.NotNull(job);
        Assert.Equal(3, job!.Speaker);
    }

    [Fact]
    public void Expire_speakers_falls_back_to_default_voice()
    {
        var scheduler = new LiveDubbingScheduler();
        scheduler.Reset(0);
        scheduler.SetRecognizedThrough(10);
        scheduler.Track(1, 2, 4);
        scheduler.Translate(1, "你好");

        Assert.Equal(2, scheduler.CoveredThrough, 3);
        Assert.Equal(1, scheduler.ExpireSpeakers(diarizedThrough: 7, graceSeconds: 2));
        Assert.Equal(2, scheduler.CoveredThrough, 3);

        var job = scheduler.Next(0, lookaheadSeconds: 20);
        Assert.NotNull(job);
        Assert.Equal(-1, job!.Speaker);

        scheduler.Complete(1);
        Assert.Equal(10, scheduler.CoveredThrough, 3);
    }

    [Fact]
    public void Next_skips_late_cues_and_respects_lookahead()
    {
        var scheduler = new LiveDubbingScheduler();
        scheduler.Reset(0);
        scheduler.SetRecognizedThrough(40);
        scheduler.Track(1, 1, 2);
        scheduler.Track(2, 30, 31);
        scheduler.Translate(1, "第一句");
        scheduler.Translate(2, "第二句");
        scheduler.AssignSpeaker(1, 0);
        scheduler.AssignSpeaker(2, 1);

        Assert.Null(scheduler.Next(5, lookaheadSeconds: 20));
        Assert.Equal(30, scheduler.CoveredThrough, 3);

        var job = scheduler.Next(5, lookaheadSeconds: 30);
        Assert.NotNull(job);
        Assert.Equal(2, job!.Key);
        Assert.Equal(1, job.Speaker);
    }

    [Fact]
    public void Reset_clears_cues_and_advances_recognized_frontier()
    {
        var scheduler = new LiveDubbingScheduler();
        scheduler.SetRecognizedThrough(50);
        scheduler.Track(1, 10, 12);
        scheduler.Reset(100);

        Assert.Equal(100, scheduler.CoveredThrough, 3);
        Assert.Null(scheduler.Next(0, lookaheadSeconds: 20));
    }

    [Fact]
    public void Skip_releases_coverage_without_synthesis()
    {
        var scheduler = new LiveDubbingScheduler();
        scheduler.Reset(0);
        scheduler.SetRecognizedThrough(5);
        scheduler.Track(1, 1, 2);
        scheduler.Skip(1);

        Assert.Equal(5, scheduler.CoveredThrough, 3);
    }
}
