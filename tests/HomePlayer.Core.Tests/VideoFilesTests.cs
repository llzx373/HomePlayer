using HomePlayer.Core;

namespace HomePlayer.Core.Tests;

public class VideoFilesTests
{
    [Theory]
    [InlineData(".rm")]
    [InlineData(".3gp")]
    [InlineData(".3g2")]
    [InlineData(".ogv")]
    [InlineData(".mts")]
    [InlineData(".vob")]
    [InlineData(".mxf")]
    [InlineData(".dv")]
    [InlineData(".wtv")]
    [InlineData(".y4m")]
    public void IsVideo_accepts_registered_extensions_regardless_of_case(string extension)
    {
        Assert.True(VideoFiles.IsVideo($"movie{extension}"));
        Assert.True(VideoFiles.IsVideo($"movie{extension.ToUpperInvariant()}"));
    }
}
