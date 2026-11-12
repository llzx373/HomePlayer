using System.Text.Json;
using HomePlayer.Core;

namespace HomePlayer.Core.Tests;

public sealed class RenderRuleEvaluatorTests
{
    [Fact]
    public void First_matching_rule_wins_and_preserves_shader_order()
    {
        var document = Document();
        document.Rules.Insert(0, new RenderRuleDefinition
        {
            Name = "first",
            Profile = "anime",
            All = [Condition("source.height", "lte", 1080)]
        });
        document.Rules.Add(new RenderRuleDefinition
        {
            Name = "second",
            Profile = "film"
        });

        var selected = RenderRuleEvaluator.Resolve(document, Context(), VideoRenderQuality.Performance);

        Assert.Equal("anime", selected.ProfileId);
        Assert.Equal("first", selected.RuleName);
        Assert.Equal(VideoRenderQuality.HighQuality, selected.Quality);
        Assert.Equal(["shaders/a.glsl", "shaders/b.hook"], selected.Shaders.Select(shader => shader.File));
        Assert.Equal(1.2, selected.Shaders[0].Parameters["strength"]);
    }

    [Fact]
    public void Manual_profile_has_priority_and_fallback_has_no_hooks()
    {
        var document = Document();
        var manual = RenderRuleEvaluator.Resolve(document, Context(), VideoRenderQuality.Balanced, "film");
        var fallback = RenderRuleEvaluator.Resolve(document, Context(SourceHeight: 2160), VideoRenderQuality.Balanced);

        Assert.Equal("film", manual.ProfileId);
        Assert.Null(manual.RuleName);
        Assert.Null(fallback.ProfileId);
        Assert.Empty(fallback.Shaders);
        Assert.Equal(VideoRenderQuality.Balanced, fallback.Quality);
    }

    [Fact]
    public void Conditions_cover_any_not_glob_scale_hdr_and_unknown_values()
    {
        var rule = new RenderRuleDefinition
        {
            Profile = "anime",
            Any =
            [
                Condition("source.codec", "in", new[] { "hevc", "av1" }),
                Condition("source.fps", "gt", 60)
            ],
            Not = Condition("file.path", "glob", "**/samples/*"),
            All =
            [
                Condition("scale.height", "gte", 2),
                Condition("target.hdr", "eq", false),
                Condition("source.transfer", "eq", "sdr")
            ]
        };

        Assert.True(RenderRuleEvaluator.Matches(rule, Context(VideoCodec: "hevc", SourceHeight: 720, TargetHeight: 1440)));
        Assert.False(RenderRuleEvaluator.Matches(rule, Context(VideoCodec: "h264", SourceHeight: 720, TargetHeight: 1440)));
        Assert.False(RenderRuleEvaluator.Matches(rule, Context(FilePath: @"D:\samples\a.mkv", VideoCodec: "hevc", SourceHeight: 720, TargetHeight: 1440)));
        Assert.False(RenderRuleEvaluator.Matches(rule, Context(VideoCodec: "hevc", Transfer: VideoTransferFunction.Unknown,
            SourceHeight: 720, TargetHeight: 1440)));
    }

    [Fact]
    public void Validation_rejects_unknown_fields_bad_profiles_and_non_finite_parameters()
    {
        var document = Document();
        document.Profiles[0].Base = "basic";
        document.Profiles[0].Shaders[0].Parameters["bad"] = double.NaN;
        document.Rules.Add(new RenderRuleDefinition
        {
            Name = "bad",
            Profile = "missing",
            All = [Condition("source.typo", "eq", 1)]
        });

        var errors = RenderRuleEvaluator.Validate(document);

        Assert.Contains(errors, error => error.Contains("Basic", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(errors, error => error.Contains("bad", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(errors, error => error.Contains("missing", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(errors, error => error.Contains("source.typo", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Missing_manual_profile_uses_safe_fallback_instead_of_an_unrelated_rule()
    {
        var selected = RenderRuleEvaluator.Resolve(Document(), Context(), VideoRenderQuality.Performance, "removed");

        Assert.Null(selected.ProfileId);
        Assert.Empty(selected.Shaders);
        Assert.Equal(VideoRenderQuality.Performance, selected.Quality);
    }

    [Fact]
    public void Built_in_presets_are_valid_and_have_distinct_ordered_chains()
    {
        var document = new RenderRulesDocument { Profiles = [.. BuiltInRenderPresets.Profiles] };

        Assert.Empty(RenderRuleEvaluator.Validate(document, allowBuiltInProfiles: true));
        Assert.Equal([BuiltInRenderPresets.AnimeId, BuiltInRenderPresets.LiveActionId, BuiltInRenderPresets.LowBitrateId],
            document.Profiles.Select(profile => profile.Id));
        Assert.All(document.Profiles, profile => Assert.NotEmpty(profile.Shaders));
        Assert.All(document.Profiles.SelectMany(profile => profile.Shaders),
            shader => Assert.True(BuiltInRenderPresets.IsBuiltInShader(shader.File)));

        var selected = RenderRuleEvaluator.Resolve(document, Context(), VideoRenderQuality.Basic,
            BuiltInRenderPresets.LowBitrateId);
        Assert.Equal(VideoRenderQuality.HighQuality, selected.Quality);
        Assert.Equal("builtin/shaders/fsrcnnx/FSRCNNX_x2_8-0-4-1.glsl", selected.Shaders[2].File);
    }

    [Fact]
    public void Built_in_profile_prefix_is_reserved_for_packaged_presets()
    {
        var document = Document();
        document.Profiles[0].Id = BuiltInRenderPresets.AnimeId;

        Assert.Contains(RenderRuleEvaluator.Validate(document), error => error.Contains("builtin."));
    }

    private static RenderRulesDocument Document() => new()
    {
        Profiles =
        [
            new RenderProfileDefinition
            {
                Id = "anime",
                Base = "highQuality",
                Shaders =
                [
                    new RenderShaderDefinition { File = "shaders/a.glsl", Parameters = { ["strength"] = 1.2 } },
                    new RenderShaderDefinition { File = "shaders/b.hook" }
                ]
            },
            new RenderProfileDefinition { Id = "film", Base = "balanced" }
        ],
        Rules =
        [
            new RenderRuleDefinition
            {
                Name = "upscale",
                Profile = "anime",
                All = [Condition("source.height", "lte", 1080)]
            }
        ]
    };

    private static RenderCondition Condition(string field, string op, object value) => new()
    {
        Field = field,
        Op = op,
        Value = JsonSerializer.SerializeToElement(value)
    };

    private static RenderRuleContext Context(
        string FilePath = @"D:\media\episode.mkv",
        string VideoCodec = "hevc",
        VideoTransferFunction Transfer = VideoTransferFunction.Sdr,
        int SourceHeight = 1080,
        int TargetHeight = 2160) => new(
            FilePath: FilePath,
            VideoCodec: VideoCodec,
            Transfer: Transfer,
            SourceWidth: SourceHeight * 16 / 9,
            SourceHeight: SourceHeight,
            SourceFrameRate: 23.976,
            PixelFormat: VideoPixelFormat.Nv12,
            TargetWidth: TargetHeight * 16 / 9,
            TargetHeight: TargetHeight);
}
