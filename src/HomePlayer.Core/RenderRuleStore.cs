using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace HomePlayer.Core;

public sealed record RenderRulesLoadResult(
    RenderRulesDocument Document,
    string Revision,
    string? Warning = null);

public sealed class RenderRuleStore
{
    public const int MaxDocumentBytes = 1024 * 1024;
    public const int MaxShaderBytes = 1024 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    private readonly string _root;

    public RenderRuleStore(string root)
    {
        _root = Path.GetFullPath(root);
    }

    public static RenderRuleStore Default => new(Path.Combine(AppPaths.Root, "render"));

    public string Root => _root;
    public string DocumentPath => Path.Combine(_root, "rules.json");
    public string ShaderDirectory => Path.Combine(_root, "shaders");

    public RenderRulesLoadResult Load()
    {
        if (!File.Exists(DocumentPath))
            return new RenderRulesLoadResult(new RenderRulesDocument(), "");
        var bytes = ReadBounded(DocumentPath, MaxDocumentBytes);
        RenderRulesDocument document;
        try
        {
            document = JsonSerializer.Deserialize<RenderRulesDocument>(bytes, JsonOptions)
                ?? throw new InvalidDataException("规则文件为空。");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("规则 JSON 无法解析：" + ex.Message, ex);
        }
        Normalize(document);
        var errors = RenderRuleEvaluator.Validate(document);
        if (errors.Count > 0)
            throw new InvalidDataException(string.Join(Environment.NewLine, errors));
        return new RenderRulesLoadResult(document, Convert.ToHexString(SHA256.HashData(bytes)));
    }

    public void Save(RenderRulesDocument document)
    {
        Normalize(document);
        var errors = RenderRuleEvaluator.Validate(document);
        if (errors.Count > 0)
            throw new InvalidDataException(string.Join(Environment.NewLine, errors));
        Directory.CreateDirectory(_root);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(document, JsonOptions);
        if (bytes.Length > MaxDocumentBytes)
            throw new InvalidDataException($"规则文件不能超过 {MaxDocumentBytes} 字节。");
        var temporary = DocumentPath + ".tmp";
        try
        {
            File.WriteAllBytes(temporary, bytes);
            File.Move(temporary, DocumentPath, true);
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }

    public string ReadShader(string relativePath)
    {
        var path = ResolveShaderPath(relativePath);
        var bytes = ReadBounded(path, MaxShaderBytes);
        try
        {
            return new UTF8Encoding(false, true).GetString(bytes);
        }
        catch (DecoderFallbackException ex)
        {
            throw new InvalidDataException($"Shader 不是有效 UTF-8：{relativePath}", ex);
        }
    }

    public string ImportShader(string sourcePath)
    {
        var source = Path.GetFullPath(sourcePath);
        ValidateExtension(source);
        var bytes = ReadBounded(source, MaxShaderBytes);
        try
        {
            _ = new UTF8Encoding(false, true).GetString(bytes);
        }
        catch (DecoderFallbackException ex)
        {
            throw new InvalidDataException("Shader 不是有效 UTF-8。", ex);
        }
        Directory.CreateDirectory(ShaderDirectory);
        var name = Path.GetFileName(source);
        var destination = Path.Combine(ShaderDirectory, name);
        var extension = Path.GetExtension(name);
        var stem = Path.GetFileNameWithoutExtension(name);
        for (var suffix = 2; File.Exists(destination); suffix++)
            destination = Path.Combine(ShaderDirectory, $"{stem}-{suffix}{extension}");
        File.WriteAllBytes(destination, bytes);
        return Path.GetRelativePath(_root, destination).Replace('\\', '/');
    }

    public string ResolveShaderPath(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
            throw new InvalidDataException("Shader 必须是 render 目录内的相对路径。");
        try
        {
            if (Path.IsPathRooted(relativePath))
                throw new InvalidDataException("Shader 必须是 render 目录内的相对路径。");
            ValidateExtension(relativePath);
            var path = Path.GetFullPath(Path.Combine(_root, relativePath));
            var prefix = _root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Shader 路径不能离开 render 目录。");
            return path;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new InvalidDataException("Shader 路径无效。", ex);
        }
    }

    private static void Normalize(RenderRulesDocument document)
    {
        if (document.Profiles == null || document.Rules == null)
            throw new InvalidDataException("Profiles 和 Rules 不能为 null。");
        foreach (var profile in document.Profiles)
        {
            if (profile == null || profile.Shaders == null)
                throw new InvalidDataException("Profile 及其 Shaders 不能为 null。");
            profile.Id ??= "";
            profile.Base ??= "";
            foreach (var shader in profile.Shaders)
            {
                if (shader == null || shader.Parameters == null)
                    throw new InvalidDataException("Shader 及其 Parameters 不能为 null。");
                shader.File ??= "";
            }
        }
        foreach (var rule in document.Rules)
        {
            if (rule == null || rule.All == null || rule.Any == null)
                throw new InvalidDataException("Rule 及其 All/Any 不能为 null。");
            rule.Name ??= "";
            rule.Profile ??= "";
            foreach (var condition in rule.All.Concat(rule.Any).Append(rule.Not))
            {
                if (condition == null)
                    throw new InvalidDataException("规则条件不能为 null。");
                condition.Field ??= "";
                condition.Op ??= "";
            }
        }
    }

    private static void ValidateExtension(string path)
    {
        var extension = Path.GetExtension(path);
        if (!extension.Equals(".glsl", StringComparison.OrdinalIgnoreCase)
            && !extension.Equals(".hook", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("仅支持 .glsl 和 .hook Shader 文件。");
    }

    private static byte[] ReadBounded(string path, int maximum)
    {
        var info = new FileInfo(path);
        if (!info.Exists)
            throw new FileNotFoundException("文件不存在。", path);
        if (info.Length > maximum)
            throw new InvalidDataException($"文件 {path} 超过 {maximum} 字节限制。");
        return File.ReadAllBytes(path);
    }
}
