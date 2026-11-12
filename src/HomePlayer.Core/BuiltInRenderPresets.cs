namespace HomePlayer.Core;

public static class BuiltInRenderPresets
{
    public const string Prefix = "builtin/";
    public const string AnimeId = "builtin.anime";
    public const string LiveActionId = "builtin.live-action";
    public const string LowBitrateId = "builtin.low-bitrate";

    public static IReadOnlyList<RenderProfileDefinition> Profiles { get; } =
    [
        new RenderProfileDefinition
        {
            Id = AnimeId,
            Base = "highQuality",
            Shaders =
            [
                Shader("shaders/chroma/CfL_Prediction.glsl"),
                Shader("shaders/anime4k/Anime4K_Clamp_Highlights.glsl"),
                Shader("shaders/anime4k/Anime4K_Restore_CNN_M.glsl"),
                Shader("shaders/anime4k/Anime4K_Upscale_CNN_x2_M.glsl"),
                Shader("shaders/sharpening/adaptive-sharpen.glsl")
            ]
        },
        new RenderProfileDefinition
        {
            Id = LiveActionId,
            Base = "highQuality",
            Shaders =
            [
                Shader("shaders/chroma/CfL_Prediction.glsl"),
                Shader("shaders/ravu/ravu-lite-ar-r3.hook"),
                Shader("shaders/ssim/SSimSuperRes.glsl"),
                Shader("shaders/sharpening/adaptive-sharpen.glsl")
            ]
        },
        new RenderProfileDefinition
        {
            Id = LowBitrateId,
            Base = "highQuality",
            Shaders =
            [
                Shader("shaders/restoration/hdeband.glsl"),
                Shader("shaders/restoration/nlmeans.glsl"),
                Shader("shaders/fsrcnnx/FSRCNNX_x2_8-0-4-1.glsl"),
                Shader("shaders/sharpening/adaptive-sharpen.glsl")
            ]
        }
    ];

    public static bool IsBuiltIn(string? profileId) =>
        profileId?.StartsWith("builtin.", StringComparison.OrdinalIgnoreCase) == true;

    public static string DisplayName(string profileId) => profileId switch
    {
        AnimeId => "动画增强",
        LiveActionId => "真人增强",
        LowBitrateId => "低码率修复",
        _ => profileId
    };

    public static bool IsBuiltInShader(string file) =>
        file.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase);

    public static string ShaderPath(string file) => file[Prefix.Length..];

    private static RenderShaderDefinition Shader(string file) => new() { File = Prefix + file };
}
