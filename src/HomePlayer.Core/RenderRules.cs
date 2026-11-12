using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace HomePlayer.Core;

public sealed class RenderRulesDocument
{
    public int Version { get; set; } = 1;
    public List<RenderProfileDefinition> Profiles { get; set; } = [];
    public List<RenderRuleDefinition> Rules { get; set; } = [];
}

public sealed class RenderProfileDefinition
{
    public string Id { get; set; } = "";
    public string Base { get; set; } = "balanced";
    public List<RenderShaderDefinition> Shaders { get; set; } = [];
}

public sealed class RenderShaderDefinition
{
    public string File { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public Dictionary<string, double> Parameters { get; set; } = [];
}

public sealed class RenderRuleDefinition
{
    public string Name { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public string Profile { get; set; } = "";
    public List<RenderCondition> All { get; set; } = [];
    public List<RenderCondition> Any { get; set; } = [];
    public RenderCondition? Not { get; set; }
}

public sealed class RenderCondition
{
    public string Field { get; set; } = "";
    public string Op { get; set; } = "eq";
    public JsonElement Value { get; set; }
}

public sealed record RenderRuleContext(
    string FilePath = "",
    string Container = "",
    string VideoCodec = "",
    double? DurationSeconds = null,
    int SourceWidth = 0,
    int SourceHeight = 0,
    double? SourceFrameRate = null,
    VideoPixelFormat PixelFormat = VideoPixelFormat.Unknown,
    VideoTransferFunction Transfer = VideoTransferFunction.Unknown,
    bool Interlaced = false,
    int TargetWidth = 0,
    int TargetHeight = 0,
    bool DisplayHdr = false);

public sealed record ResolvedRenderShader(
    string File,
    IReadOnlyDictionary<string, double> Parameters);

public sealed record ResolvedRenderProfile(
    string? ProfileId,
    string? RuleName,
    VideoRenderQuality Quality,
    IReadOnlyList<ResolvedRenderShader> Shaders,
    string Revision = "")
{
    public static ResolvedRenderProfile Fallback(VideoRenderQuality quality) =>
        new(null, null, quality, []);
}

public static class RenderRuleEvaluator
{
    public const int MaxProfiles = 64;
    public const int MaxRules = 256;
    public const int MaxShadersPerProfile = 16;

    private static readonly HashSet<string> Fields = new(StringComparer.OrdinalIgnoreCase)
    {
        "file.path", "file.name", "file.extension", "file.container", "file.duration",
        "source.codec", "source.width", "source.height", "source.fps", "source.pixelFormat",
        "source.transfer", "source.interlaced", "target.width", "target.height", "target.hdr",
        "scale.width", "scale.height"
    };

    private static readonly HashSet<string> Operators = new(StringComparer.OrdinalIgnoreCase)
    {
        "eq", "ne", "gt", "gte", "lt", "lte", "in", "glob"
    };

    public static IReadOnlyList<string> Validate(RenderRulesDocument document, bool allowBuiltInProfiles = false)
    {
        var errors = new List<string>();
        if (document.Version != 1)
            errors.Add($"不支持的规则版本 {document.Version}；当前仅支持 1。");
        if (document.Profiles.Count > MaxProfiles)
            errors.Add($"渲染配置不能超过 {MaxProfiles} 个。");
        if (document.Rules.Count > MaxRules)
            errors.Add($"规则不能超过 {MaxRules} 条。");
        var profiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var profile in document.Profiles)
        {
            if (string.IsNullOrWhiteSpace(profile.Id) || !profiles.Add(profile.Id))
                errors.Add($"渲染配置标识为空或重复：{profile.Id}");
            if (!allowBuiltInProfiles && BuiltInRenderPresets.IsBuiltIn(profile.Id))
                errors.Add($"渲染配置 {profile.Id} 使用了保留的 builtin. 前缀。");
            if (!TryQuality(profile.Base, out var quality))
                errors.Add($"渲染配置 {profile.Id} 的基础质量无效：{profile.Base}");
            if (profile.Shaders.Count > MaxShadersPerProfile)
                errors.Add($"渲染配置 {profile.Id} 的 Shader 不能超过 {MaxShadersPerProfile} 个。");
            if (quality == VideoRenderQuality.Basic && profile.Shaders.Any(shader => shader.Enabled))
                errors.Add($"Basic 配置 {profile.Id} 不能包含启用的 Shader。");
            foreach (var shader in profile.Shaders)
            {
                if (string.IsNullOrWhiteSpace(shader.File))
                    errors.Add($"渲染配置 {profile.Id} 包含空 Shader 路径。");
                foreach (var parameter in shader.Parameters)
                {
                    if (string.IsNullOrWhiteSpace(parameter.Key) || !double.IsFinite(parameter.Value))
                        errors.Add($"Shader {shader.File} 的参数 {parameter.Key} 无效。");
                }
            }
        }
        foreach (var rule in document.Rules)
        {
            if (!profiles.Contains(rule.Profile))
                errors.Add($"规则 {rule.Name} 引用了不存在的渲染配置：{rule.Profile}");
            ValidateConditions(rule.All, rule.Name, errors);
            ValidateConditions(rule.Any, rule.Name, errors);
            if (rule.Not != null)
                ValidateConditions([rule.Not], rule.Name, errors);
        }
        return errors;
    }

    public static ResolvedRenderProfile Resolve(
        RenderRulesDocument document,
        RenderRuleContext context,
        VideoRenderQuality fallbackQuality,
        string? manualProfileId = null)
    {
        if (!string.IsNullOrWhiteSpace(manualProfileId))
        {
            var manual = document.Profiles.FirstOrDefault(profile =>
                string.Equals(profile.Id, manualProfileId, StringComparison.OrdinalIgnoreCase));
            return manual != null ? ResolveProfile(manual, null) : ResolvedRenderProfile.Fallback(fallbackQuality);
        }
        foreach (var rule in document.Rules)
        {
            if (!rule.Enabled || !Matches(rule, context))
                continue;
            var profile = document.Profiles.FirstOrDefault(candidate =>
                string.Equals(candidate.Id, rule.Profile, StringComparison.OrdinalIgnoreCase));
            if (profile != null)
                return ResolveProfile(profile, rule.Name);
        }
        return ResolvedRenderProfile.Fallback(fallbackQuality);
    }

    public static bool Matches(RenderRuleDefinition rule, RenderRuleContext context) =>
        rule.All.All(condition => Matches(condition, context))
        && (rule.Any.Count == 0 || rule.Any.Any(condition => Matches(condition, context)))
        && (rule.Not == null || !Matches(rule.Not, context));

    private static ResolvedRenderProfile ResolveProfile(RenderProfileDefinition profile, string? ruleName)
    {
        TryQuality(profile.Base, out var quality);
        return new ResolvedRenderProfile(profile.Id, ruleName, quality,
            profile.Shaders.Where(shader => shader.Enabled)
                .Select(shader => new ResolvedRenderShader(shader.File,
                    new Dictionary<string, double>(shader.Parameters, StringComparer.OrdinalIgnoreCase)))
                .ToArray());
    }

    private static void ValidateConditions(IEnumerable<RenderCondition> conditions, string rule, List<string> errors)
    {
        foreach (var condition in conditions)
        {
            if (!Fields.Contains(condition.Field))
                errors.Add($"规则 {rule} 包含未知字段：{condition.Field}");
            if (!Operators.Contains(condition.Op))
                errors.Add($"规则 {rule} 包含未知操作符：{condition.Op}");
            if (condition.Value.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
                errors.Add($"规则 {rule} 的条件 {condition.Field} 缺少值。");
        }
    }

    private static bool TryQuality(string value, out VideoRenderQuality quality)
    {
        quality = value?.Trim().ToLowerInvariant() switch
        {
            "basic" => VideoRenderQuality.Basic,
            "performance" or "fast" => VideoRenderQuality.Performance,
            "balanced" or "default" => VideoRenderQuality.Balanced,
            "highquality" or "high_quality" or "high-quality" => VideoRenderQuality.HighQuality,
            _ => (VideoRenderQuality)(-1)
        };
        return Enum.IsDefined(quality);
    }

    private static bool Matches(RenderCondition condition, RenderRuleContext context)
    {
        if (!Fields.Contains(condition.Field) || !Operators.Contains(condition.Op))
            return false;
        if (IsNumericField(condition.Field))
            return MatchNumber(Number(condition.Field, context), condition);
        if (IsBooleanField(condition.Field))
            return MatchBoolean(Boolean(condition.Field, context), condition);
        return MatchText(Text(condition.Field, context), condition);
    }

    private static bool MatchNumber(double? actual, RenderCondition condition)
    {
        if (actual == null)
            return false;
        if (condition.Op.Equals("in", StringComparison.OrdinalIgnoreCase))
            return condition.Value.ValueKind == JsonValueKind.Array
                && condition.Value.EnumerateArray().Any(value => TryNumber(value, out var expected)
                    && actual.Value.Equals(expected));
        if (!TryNumber(condition.Value, out var number))
            return false;
        return condition.Op.ToLowerInvariant() switch
        {
            "eq" => actual.Value.Equals(number),
            "ne" => !actual.Value.Equals(number),
            "gt" => actual.Value > number,
            "gte" => actual.Value >= number,
            "lt" => actual.Value < number,
            "lte" => actual.Value <= number,
            _ => false
        };
    }

    private static bool MatchBoolean(bool? actual, RenderCondition condition)
    {
        if (actual == null || !TryBoolean(condition.Value, out var expected))
            return false;
        return condition.Op.Equals("eq", StringComparison.OrdinalIgnoreCase) ? actual == expected
            : condition.Op.Equals("ne", StringComparison.OrdinalIgnoreCase) && actual != expected;
    }

    private static bool MatchText(string? actual, RenderCondition condition)
    {
        if (actual == null)
            return false;
        if (condition.Op.Equals("glob", StringComparison.OrdinalIgnoreCase))
            return TryText(condition.Value, out var pattern) && Glob(actual, pattern);
        if (condition.Op.Equals("in", StringComparison.OrdinalIgnoreCase))
            return condition.Value.ValueKind == JsonValueKind.Array
                && condition.Value.EnumerateArray().Any(value => TryText(value, out var expected)
                    && string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase));
        if (!TryText(condition.Value, out var text))
            return false;
        return condition.Op.Equals("eq", StringComparison.OrdinalIgnoreCase)
            ? string.Equals(actual, text, StringComparison.OrdinalIgnoreCase)
            : condition.Op.Equals("ne", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(actual, text, StringComparison.OrdinalIgnoreCase);
    }

    private static bool Glob(string actual, string pattern)
    {
        var expression = "^" + Regex.Escape(pattern.Replace('\\', '/'))
            .Replace(@"\*\*", ".*").Replace(@"\*", "[^/]*").Replace(@"\?", "[^/]") + "$";
        return Regex.IsMatch(actual.Replace('\\', '/'), expression,
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50));
    }

    private static bool IsNumericField(string field) => field.Equals("file.duration", StringComparison.OrdinalIgnoreCase)
        || field.StartsWith("source.", StringComparison.OrdinalIgnoreCase)
            && (field.EndsWith("width", StringComparison.OrdinalIgnoreCase)
                || field.EndsWith("height", StringComparison.OrdinalIgnoreCase)
                || field.EndsWith("fps", StringComparison.OrdinalIgnoreCase))
        || field.StartsWith("target.", StringComparison.OrdinalIgnoreCase)
            && (field.EndsWith("width", StringComparison.OrdinalIgnoreCase)
                || field.EndsWith("height", StringComparison.OrdinalIgnoreCase))
        || field.StartsWith("scale.", StringComparison.OrdinalIgnoreCase);

    private static bool IsBooleanField(string field) =>
        field.Equals("source.interlaced", StringComparison.OrdinalIgnoreCase)
        || field.Equals("target.hdr", StringComparison.OrdinalIgnoreCase);

    private static double? Number(string field, RenderRuleContext context) => field.ToLowerInvariant() switch
    {
        "file.duration" => context.DurationSeconds,
        "source.width" => context.SourceWidth > 0 ? context.SourceWidth : null,
        "source.height" => context.SourceHeight > 0 ? context.SourceHeight : null,
        "source.fps" => context.SourceFrameRate,
        "target.width" => context.TargetWidth > 0 ? context.TargetWidth : null,
        "target.height" => context.TargetHeight > 0 ? context.TargetHeight : null,
        "scale.width" => context.SourceWidth > 0 && context.TargetWidth > 0
            ? (double)context.TargetWidth / context.SourceWidth : null,
        "scale.height" => context.SourceHeight > 0 && context.TargetHeight > 0
            ? (double)context.TargetHeight / context.SourceHeight : null,
        _ => null
    };

    private static bool? Boolean(string field, RenderRuleContext context) => field.ToLowerInvariant() switch
    {
        "source.interlaced" => context.Interlaced,
        "target.hdr" => context.DisplayHdr,
        _ => null
    };

    private static string? Text(string field, RenderRuleContext context) => field.ToLowerInvariant() switch
    {
        "file.path" => NullIfEmpty(context.FilePath),
        "file.name" => NullIfEmpty(Path.GetFileName(context.FilePath)),
        "file.extension" => NullIfEmpty(Path.GetExtension(context.FilePath)),
        "file.container" => NullIfEmpty(context.Container),
        "source.codec" => NullIfEmpty(context.VideoCodec),
        "source.pixelformat" => context.PixelFormat == VideoPixelFormat.Unknown ? null : context.PixelFormat.ToString().ToLowerInvariant(),
        "source.transfer" => context.Transfer == VideoTransferFunction.Unknown ? null : context.Transfer.ToString().ToLowerInvariant(),
        _ => null
    };

    private static string? NullIfEmpty(string value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private static bool TryNumber(JsonElement value, out double number)
    {
        if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out number))
            return double.IsFinite(number);
        if (value.ValueKind == JsonValueKind.String
            && double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out number))
            return double.IsFinite(number);
        number = 0;
        return false;
    }

    private static bool TryBoolean(JsonElement value, out bool result)
    {
        if (value.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            result = value.GetBoolean();
            return true;
        }
        if (value.ValueKind == JsonValueKind.String && bool.TryParse(value.GetString(), out result))
            return true;
        result = false;
        return false;
    }

    private static bool TryText(JsonElement value, out string text)
    {
        if (value.ValueKind == JsonValueKind.String)
        {
            text = value.GetString() ?? "";
            return true;
        }
        if (value.ValueKind == JsonValueKind.Number)
        {
            text = value.GetRawText();
            return true;
        }
        text = "";
        return false;
    }
}
