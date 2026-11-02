namespace HomePlayer.Core.Tests;

public sealed class DisplayRefreshRateOptionsTests
{
    [Fact]
    public void System_reported_rate_is_kept_even_when_driver_list_omits_it()
    {
        var rates = DisplayRefreshRateOptions.Merge([60], 59);
        Assert.Equal([59, 60], rates);
    }

    [Fact]
    public void Rates_are_sorted_deduplicated_and_positive()
    {
        var rates = DisplayRefreshRateOptions.Merge([60, 24, 60, 0, -1], 24);
        Assert.Equal([24, 60], rates);
    }

    [Fact]
    public void Default_setting_does_not_change_windows()
    {
        var settings = new AppSettings();
        Assert.Equal(0, settings.DisplayRefreshRate);
        Assert.Equal(VideoOutputRange.Full, settings.RgbOutputRange);
    }
}
