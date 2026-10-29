using HomePlayer.Core;

namespace HomePlayer.Core.Tests;

public class UpdateCheckerTests
{
    [Fact]
    public void Stable_channel_ignores_prereleases()
    {
        const string json = """
            [
              {"tag_name":"v0.3.0-1","html_url":"beta","prerelease":true},
              {"tag_name":"v0.2.0","html_url":"stable","prerelease":false}
            ]
            """;

        var result = UpdateChecker.Parse(json, "0.2.0-1", beta: false);

        Assert.True(result.HasUpdate);
        Assert.Equal("v0.2.0", result.Latest?.Version);
        Assert.Equal("stable", result.Latest?.Url);
    }

    [Fact]
    public void Beta_channel_compares_numeric_prerelease_suffix()
    {
        const string json = """{"tag_name":"v0.2.0-2","html_url":"beta","prerelease":true}""";

        var result = UpdateChecker.Parse(json, "0.2.0-1", beta: true);

        Assert.True(result.HasUpdate);
        Assert.True(UpdateChecker.Compare("0.2.0", "0.2.0-2") > 0);
        Assert.True(UpdateChecker.Compare("0.2.0-10", "0.2.0-2") > 0);
    }
}
