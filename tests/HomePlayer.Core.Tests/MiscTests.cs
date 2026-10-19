using HomePlayer.Core;

namespace HomePlayer.Core.Tests;

public class HotkeyCatalogTests
{
    [Fact]
    public void Merge_returns_defaults_when_nothing_saved()
    {
        var merged = HotkeyCatalog.Merge(null);

        Assert.Equal(HotkeyCatalog.Actions.Length, merged.Count);
        Assert.Equal("Space", merged[HotkeyCatalog.PlayPause]);
        Assert.Equal("Escape", merged[HotkeyCatalog.Escape]);
    }

    [Fact]
    public void Merge_keeps_saved_values_and_ignores_unknown_actions()
    {
        var merged = HotkeyCatalog.Merge(new Dictionary<string, string>
        {
            [HotkeyCatalog.PlayPause] = "Ctrl+P",
            ["UnknownAction"] = "Ctrl+U"
        });

        Assert.Equal("Ctrl+P", merged[HotkeyCatalog.PlayPause]);
        Assert.False(merged.ContainsKey("UnknownAction"));
        Assert.Equal("Left", merged[HotkeyCatalog.SeekBack]);
    }
}

public class ClockTextTests
{
    [Theory]
    [InlineData(5, "00:05")]
    [InlineData(65, "01:05")]
    [InlineData(3661, "1:01:01")]
    public void Format_uses_minutes_or_hours(int seconds, string expected)
    {
        Assert.Equal(expected, ClockText.Format(TimeSpan.FromSeconds(seconds)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0d)]
    [InlineData(-1d)]
    public void FormatSeconds_reports_unknown_for_missing_duration(double? seconds)
    {
        Assert.Equal("未知时长", ClockText.FormatSeconds(seconds));
    }

    [Fact]
    public void FormatSeconds_formats_positive_duration()
    {
        Assert.Equal("01:30", ClockText.FormatSeconds(90));
    }
}

public class WindowFitTests
{
    [Fact]
    public void ForVideo_returns_null_when_aspect_already_matches()
    {
        var size = WindowFit.ForVideo(1920, 1080, 1280, 720, 16, 40, 400, 300, 1920, 1080);

        Assert.Null(size);
    }

    [Fact]
    public void ForVideo_shrinks_width_when_window_is_wider_than_video()
    {
        var size = WindowFit.ForVideo(1920, 1080, 1920, 800, 20, 60, 400, 300, 3840, 2160);

        Assert.NotNull(size);
        Assert.Equal(1422 + 20, size.Value.Width);
        Assert.Equal(800 + 60, size.Value.Height);
    }

    [Fact]
    public void ForVideo_shrinks_height_when_window_is_taller_than_video()
    {
        var size = WindowFit.ForVideo(1920, 1080, 1280, 900, 8, 50, 400, 300, 3840, 2160);

        Assert.NotNull(size);
        Assert.Equal(1280 + 8, size.Value.Width);
        Assert.Equal(720 + 50, size.Value.Height);
    }

    [Fact]
    public void ForVideo_returns_null_for_invalid_video_size()
    {
        Assert.Null(WindowFit.ForVideo(0, 1080, 1280, 720, 0, 0, 400, 300, 1920, 1080));
        Assert.Null(WindowFit.ForVideo(1920, 0, 1280, 720, 0, 0, 400, 300, 1920, 1080));
    }

    [Fact]
    public void ForVideo_clamps_to_minimum_window_size()
    {
        var size = WindowFit.ForVideo(16, 9, 400, 90, 0, 0, 320, 240, 1920, 1080);

        Assert.NotNull(size);
        Assert.Equal(320, size.Value.Width);
        Assert.Equal(240, size.Value.Height);
    }
}

public class VideoFrameTests
{
    [Fact]
    public void ReturnBuffer_releases_only_once()
    {
        var released = 0;
        var frame = new VideoFrame
        {
            Width = 1,
            Height = 1,
            Stride = 4,
            Data = new byte[4],
            ReleaseBuffer = () => released++
        };

        frame.ReturnBuffer();
        frame.ReturnBuffer();

        Assert.Equal(1, released);
    }
}
