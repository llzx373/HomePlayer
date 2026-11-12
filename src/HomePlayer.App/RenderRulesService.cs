using System.Security.Cryptography;
using System.Text;
using HomePlayer.Core;
using HomePlayer.Playback;

namespace HomePlayer;

public sealed class RenderRulesService
{
    private readonly object _gate = new();
    private readonly AppSettings _settings;
    private readonly RenderRuleStore _store;
    private readonly RenderRuleStore? _builtInStore;
    private RenderRulesDocument _document = new();
    private string _revision = "";
    private string _builtInRevision = "";

    public RenderRulesService(AppSettings settings, RenderRuleStore store, RenderRuleStore? builtInStore = null)
    {
        _settings = settings;
        _store = store;
        _builtInStore = builtInStore;
        Reload();
    }

    public event Action? Changed;

    public string? Warning { get; private set; }
    public string? LastError { get; private set; }
    public LibplaceboRenderStatus? ActiveStatus { get; set; }
    public RenderRuleStore Store => _store;

    public IReadOnlyList<RenderProfileDefinition> Profiles
    {
        get
        {
            lock (_gate)
                return _builtInStore == null || _builtInRevision.Length == 0
                    ? _document.Profiles.ToArray()
                    : [.. BuiltInRenderPresets.Profiles, .. _document.Profiles];
        }
    }

    public string? Reload()
    {
        try
        {
            var loaded = _store.Load();
            var revision = ComputeRevision(loaded.Document, loaded.Revision);
            var builtInRevision = ComputeBuiltInRevision();
            lock (_gate)
            {
                _document = loaded.Document;
                _revision = revision;
                _builtInRevision = builtInRevision;
                Warning = null;
            }
            LastError = null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            lock (_gate)
                Warning = ex.Message;
            LastError = ex.Message;
        }
        ActiveStatus = null;
        Changed?.Invoke();
        return Warning;
    }

    public ResolvedRenderProfile Resolve(RenderRuleContext context, VideoRenderQuality fallbackQuality)
    {
        lock (_gate)
        {
            if (!_settings.RenderRulesEnabled)
                return ResolvedRenderProfile.Fallback(fallbackQuality);
            var manual = _settings.RenderProfileOverride;
            if (BuiltInRenderPresets.IsBuiltIn(manual))
            {
                if (_builtInStore == null || _builtInRevision.Length == 0)
                    return ResolvedRenderProfile.Fallback(fallbackQuality);
                var document = new RenderRulesDocument { Profiles = [.. BuiltInRenderPresets.Profiles] };
                return RenderRuleEvaluator.Resolve(document, context, fallbackQuality, manual) with { Revision = _builtInRevision };
            }
            return RenderRuleEvaluator.Resolve(_document, context, fallbackQuality, manual) with { Revision = _revision };
        }
    }

    public string ReadShader(string relativePath)
    {
        if (BuiltInRenderPresets.IsBuiltInShader(relativePath) && _builtInStore != null)
            return _builtInStore.ReadShader(BuiltInRenderPresets.ShaderPath(relativePath));
        return _store.ReadShader(relativePath);
    }

    public string ImportShader(string sourcePath) => _store.ImportShader(sourcePath);

    public void UpdateParameter(string profileId, string shaderFile, string name, double value)
    {
        lock (_gate)
        {
            if (BuiltInRenderPresets.IsBuiltIn(profileId))
                throw new InvalidDataException("内置快速方案为只读，请复制为用户配置后再修改参数。");
            var profile = _document.Profiles.FirstOrDefault(candidate =>
                string.Equals(candidate.Id, profileId, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidDataException("找不到当前渲染配置。");
            var shader = profile.Shaders.FirstOrDefault(candidate =>
                string.Equals(candidate.File, shaderFile, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidDataException("找不到当前 Shader。");
            var hadPrevious = shader.Parameters.TryGetValue(name, out var previous);
            shader.Parameters[name] = value;
            try
            {
                _store.Save(_document);
                _revision = ComputeRevision(_document, "");
            }
            catch
            {
                if (hadPrevious)
                    shader.Parameters[name] = previous;
                else
                    shader.Parameters.Remove(name);
                throw;
            }
        }
        Changed?.Invoke();
    }

    private string ComputeRevision(RenderRulesDocument document, string documentRevision)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(Encoding.UTF8.GetBytes(documentRevision));
        foreach (var shader in document.Profiles.SelectMany(profile => profile.Shaders).Where(shader => shader.Enabled))
        {
            hash.AppendData(Encoding.UTF8.GetBytes(shader.File));
            hash.AppendData(Encoding.UTF8.GetBytes(ReadShader(shader.File)));
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private string ComputeBuiltInRevision()
    {
        if (_builtInStore == null)
            return "";
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var profile in BuiltInRenderPresets.Profiles)
        {
            hash.AppendData(Encoding.UTF8.GetBytes(profile.Id));
            foreach (var shader in profile.Shaders)
            {
                var path = BuiltInRenderPresets.ShaderPath(shader.File);
                hash.AppendData(Encoding.UTF8.GetBytes(shader.File));
                hash.AppendData(Encoding.UTF8.GetBytes(_builtInStore.ReadShader(path)));
            }
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }
}
