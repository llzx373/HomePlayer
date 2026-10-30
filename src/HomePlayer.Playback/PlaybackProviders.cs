using HomePlayer.Core;

namespace HomePlayer.Playback;

public enum PlaybackRoute
{
    Auto,
    NativeFfmpeg,
    DirectShow
}

public interface IPlaybackProvider
{
    string Id { get; }
    string DisplayName { get; }
    string Maturity { get; }
    IReadOnlyList<PlaybackCapability> DeclaredCapabilities { get; }
    IPlaybackSession Open(string path, string? audioOutputId);
}

public interface IPlaybackSession : IPlaybackEngine
{
    Guid SessionId { get; }
    string MediaId { get; }
    long Epoch { get; }
    string TimelineId { get; }
    PlaybackSessionSnapshot Snapshot { get; }
    event Action<PlaybackCommandResult>? CommandCompleted;
    Task<PlaybackCommandResult> ExecuteAsync(PlaybackCommandType command, TimeSpan? position = null, CancellationToken cancellationToken = default);
}

public sealed class EngineSelectionPolicy
{
    public IPlaybackProvider Select(IEnumerable<IPlaybackProvider> providers, PlaybackRoute route, bool directShowAvailable)
    {
        var available = providers.ToArray();
        if (route == PlaybackRoute.NativeFfmpeg)
            return available.Single(provider => provider.Id == NativeFfmpegProvider.StableId);
        if (route == PlaybackRoute.DirectShow || directShowAvailable)
            return available.Single(provider => provider.Id == DirectShowProvider.StableId);
        return available.Single(provider => provider.Id == NativeFfmpegProvider.StableId);
    }
}

public sealed class PlaybackProviderRegistry(IEnumerable<IPlaybackProvider> providers)
{
    private readonly IPlaybackProvider[] _providers = providers.ToArray();
    private readonly EngineSelectionPolicy _policy = new();

    public IReadOnlyList<IPlaybackProvider> Providers => _providers;

    public OpenedPlaybackSession Open(string path, string? audioOutputId, Func<CodecPlan> codecs, PlaybackRoute route)
    {
        Exception? directShowError = null;
        if (route != PlaybackRoute.NativeFfmpeg)
        {
            var plan = codecs();
            if (plan.Splitters.Count > 0 && plan.VideoDecoders.Count > 0)
            {
                try
                {
                    var provider = (DirectShowProvider)_policy.Select(_providers, PlaybackRoute.DirectShow, true);
                    var session = provider.Open(path, audioOutputId);
                    var engine = (LavPlaybackEngine)((PlaybackSession)session).InnerEngine;
                    var reason = engine.AudioMissing
                        ? "画面已打开，音频没有接上：" + engine.AudioWarning
                        : string.IsNullOrWhiteSpace(engine.AudioWarning) ? "已按设置的顺序建立滤镜图。" : engine.AudioWarning;
                    return new OpenedPlaybackSession(session, reason);
                }
                catch (Exception ex)
                {
                    directShowError = ex;
                    if (route == PlaybackRoute.DirectShow)
                        throw;
                }
            }
            else if (route == PlaybackRoute.DirectShow)
            {
                throw new InvalidOperationException("没有可用的 64 位 DirectShow 分离器或视频解码器。");
            }
        }

        try
        {
            var provider = (NativeFfmpegProvider)_policy.Select(_providers, PlaybackRoute.NativeFfmpeg, false);
            var session = provider.Open(path, audioOutputId);
            var reason = directShowError == null
                ? route == PlaybackRoute.NativeFfmpeg ? "已按指定改用 FFmpeg。" : "没有可用的 64 位分离器或视频解码器，已改用 FFmpeg。"
                : "DirectShow 滤镜无法播放该文件，已改用 FFmpeg：" + directShowError.Message;
            return new OpenedPlaybackSession(session, reason);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(directShowError == null ? ex.Message : $"{directShowError.Message}；FFmpeg 也失败：{ex.Message}", ex);
        }
    }
}

public sealed record OpenedPlaybackSession(IPlaybackSession Session, string Reason);

public sealed class NativeFfmpegProvider : IPlaybackProvider
{
    public const string StableId = "native-ffmpeg";
    public string Id => StableId;
    public string DisplayName => "HomePlayer 原生（FFmpeg）";
    public string Maturity => "stable";
    public IReadOnlyList<PlaybackCapability> DeclaredCapabilities { get; } =
    [
        new("playback.seek", CapabilityStatus.Supported, CapabilityOwner.Engine),
        new("playback.rate", CapabilityStatus.Supported, CapabilityOwner.Engine),
        new("video.frame-cpu", CapabilityStatus.Supported, CapabilityOwner.Renderer),
        new("video.zero-copy", CapabilityStatus.Unsupported, CapabilityOwner.Renderer, "BGRA_COMPAT", "当前保留 BGRA 兼容渲染路径。")
    ];

    public IPlaybackSession Open(string path, string? audioOutputId)
    {
        var engine = new FfmpegPlaybackEngine(audioOutputId);
        try
        {
            engine.Open(path);
            return new PlaybackSession(engine, path);
        }
        catch
        {
            engine.Dispose();
            throw;
        }
    }
}

public sealed class DirectShowProvider : IPlaybackProvider
{
    public const string StableId = "directshow-lav";
    private readonly Func<CodecPlan> _codecs;
    public DirectShowProvider(Func<CodecPlan> codecs) => _codecs = codecs;
    public string Id => StableId;
    public string DisplayName => "LAV + HomePlayer";
    public string Maturity => "compatible";
    public IReadOnlyList<PlaybackCapability> DeclaredCapabilities { get; } =
    [
        new("playback.seek", CapabilityStatus.Supported, CapabilityOwner.Engine),
        new("playback.rate", CapabilityStatus.Unknown, CapabilityOwner.Engine),
        new("video.frame-cpu", CapabilityStatus.Supported, CapabilityOwner.Renderer),
        new("video.zero-copy", CapabilityStatus.Unsupported, CapabilityOwner.Renderer, "BGRA_COMPAT", "当前保留 RGB32/BGRA 兼容渲染路径。")
    ];

    public IPlaybackSession Open(string path, string? audioOutputId)
    {
        var engine = new LavPlaybackEngine(_codecs(), audioOutputId);
        try
        {
            engine.Open(path);
            return new PlaybackSession(engine, path);
        }
        catch
        {
            engine.Dispose();
            throw;
        }
    }
}
