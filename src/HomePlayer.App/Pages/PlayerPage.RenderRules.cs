using HomePlayer.Core;
using HomePlayer.Playback;
using Microsoft.UI.Xaml.Controls;

namespace HomePlayer;

public sealed partial class PlayerPage
{
    private readonly object _renderFrameGate = new();
    private VideoColorMetadata _renderFrameColor;
    private VideoFrameGeometry _renderFrameGeometry;
    private VideoPixelFormat _renderPixelFormat;
    private int _renderSourceWidth;
    private int _renderSourceHeight;
    private string _renderContextKey = "";
    private ResolvedRenderProfile _requestedRenderProfile = ResolvedRenderProfile.Fallback(VideoRenderQuality.Basic);
    private PipelineInfo? _pipelineCache;
    private string? _pipelinePath;
    private int _pipelineRefresh;

    private void InitializeRenderRules()
    {
        _presenter.ShaderReader = App.Host.RenderRules.ReadShader;
        _presenter.RenderStatusChanged += status => App.Host.RenderRules.ActiveStatus = status;
        App.Host.RenderRules.Changed += () => DispatcherQueue.TryEnqueue(() => ApplyRenderProfile(true));
        ApplyRenderProfile(true);
    }

    private void ApplyRenderProfile(bool force = false)
    {
        var target = _presenter.CurrentRenderTarget;
        VideoColorMetadata color;
        VideoFrameGeometry geometry;
        VideoPixelFormat pixelFormat;
        int sourceWidth;
        int sourceHeight;
        lock (_renderFrameGate)
        {
            color = _renderFrameColor;
            geometry = _renderFrameGeometry;
            pixelFormat = _renderPixelFormat;
            sourceWidth = _renderSourceWidth;
            sourceHeight = _renderSourceHeight;
        }
        var path = Playback.CurrentPath ?? "";
        var pipeline = CachedPipeline(path);
        sourceWidth = sourceWidth > 0 ? sourceWidth : pipeline?.Width ?? 0;
        sourceHeight = sourceHeight > 0 ? sourceHeight : pipeline?.Height ?? 0;
        var key = string.Join("|", path, pipeline?.VideoCodec, pipeline?.FrameRate,
            sourceWidth, sourceHeight, pixelFormat, color.Transfer, geometry.Interlaced,
            target.Width, target.Height, target.Hdr, target.Generation,
            App.Host.Settings.RenderRulesEnabled, App.Host.Settings.RenderProfileOverride,
            App.Host.Settings.RenderQuality);
        if (!force && key == _renderContextKey)
            return;
        _renderContextKey = key;
        var context = new RenderRuleContext(
            path,
            pipeline?.Container ?? "",
            pipeline?.VideoCodec ?? "",
            pipeline?.DurationSeconds,
            sourceWidth,
            sourceHeight,
            pipeline?.FrameRate,
            pixelFormat,
            color.Transfer,
            geometry.Interlaced,
            target.Width,
            target.Height,
            target.Hdr);
        _requestedRenderProfile = App.Host.RenderRules.Resolve(context, App.Host.Settings.RenderQuality);
        _presenter.RenderProfile = _requestedRenderProfile;
        _presenter.RenderQuality = _requestedRenderProfile.Quality;
        App.Host.RenderRules.ActiveStatus = null;
        _presenter.Refresh();
        if (force)
            _presenter.Prewarm();
    }

    private PipelineInfo? CachedPipeline(string path)
    {
        if (string.IsNullOrEmpty(path)
            || !string.Equals(_pipelinePath, path, StringComparison.OrdinalIgnoreCase))
            return null;
        return _pipelineCache;
    }

    private void RefreshPipelineCache()
    {
        var path = Playback.CurrentPath;
        if (string.IsNullOrEmpty(path) || Interlocked.Exchange(ref _pipelineRefresh, 1) == 1)
            return;
        _ = Task.Run(() =>
        {
            PipelineInfo? pipeline = null;
            try
            {
                pipeline = Playback.Pipeline();
            }
            catch
            {
            }

            DispatcherQueue.TryEnqueue(() =>
            {
                Interlocked.Exchange(ref _pipelineRefresh, 0);
                if (!string.Equals(path, Playback.CurrentPath, StringComparison.OrdinalIgnoreCase))
                {
                    RefreshPipelineCache();
                    return;
                }

                _pipelinePath = path;
                _pipelineCache = pipeline;
                ApplyRenderProfile(true);
            });
        });
    }

    private void ClearPipelineCache()
    {
        _pipelinePath = null;
        _pipelineCache = null;
        Interlocked.Exchange(ref _pipelineRefresh, 0);
    }

    private void ObserveRenderFrame(FrameLease frame)
    {
        var changed = false;
        lock (_renderFrameGate)
        {
            if (_renderSourceWidth != frame.Width || _renderSourceHeight != frame.Height
                || _renderPixelFormat != frame.PixelFormat || _renderFrameColor.Transfer != frame.Color.Transfer
                || _renderFrameGeometry.Interlaced != frame.Geometry.Interlaced)
                changed = true;
            _renderSourceWidth = frame.Width;
            _renderSourceHeight = frame.Height;
            _renderPixelFormat = frame.PixelFormat;
            _renderFrameColor = frame.Color;
            _renderFrameGeometry = frame.Geometry;
        }
        if (changed)
            DispatcherQueue.TryEnqueue(() => ApplyRenderProfile(true));
    }

    private void QuickRenderFlyout_Opening(object sender, object e)
    {
        var available = App.Host.RenderRules.Profiles.Select(profile => profile.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var item in QuickRenderFlyout.Items.OfType<RadioMenuFlyoutItem>())
        {
            var profileId = item.Tag as string ?? "";
            item.IsChecked = string.Equals(profileId, App.Host.Settings.RenderProfileOverride,
                StringComparison.OrdinalIgnoreCase);
            item.IsEnabled = profileId.Length == 0 || available.Contains(profileId);
        }
    }

    private void QuickRenderItem_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        if (sender is RadioMenuFlyoutItem item)
            SelectRenderProfile(item.Tag as string ?? "");
    }

    private void RenderProfileMenu_Opening()
    {
        RenderProfileMenu.Items.Clear();
        RenderProfileMenu.Items.Add(new RadioMenuFlyoutItem
        {
            Text = "自动（按规则）",
            GroupName = "RenderProfile",
            IsChecked = string.IsNullOrWhiteSpace(App.Host.Settings.RenderProfileOverride),
            IsEnabled = App.Host.Settings.RenderRulesEnabled
        });
        foreach (var profile in App.Host.RenderRules.Profiles)
        {
            var item = new RadioMenuFlyoutItem
            {
                Text = BuiltInRenderPresets.DisplayName(profile.Id),
                GroupName = "RenderProfile",
                IsChecked = string.Equals(profile.Id, App.Host.Settings.RenderProfileOverride,
                    StringComparison.OrdinalIgnoreCase),
                IsEnabled = App.Host.Settings.RenderRulesEnabled
            };
            item.Click += (_, _) => SelectRenderProfile(profile.Id);
            RenderProfileMenu.Items.Add(item);
        }
        if (RenderProfileMenu.Items.Count == 1)
            RenderProfileMenu.Items.Add(new MenuFlyoutItem { Text = "尚无自定义配置", IsEnabled = false });
        var automatic = (RadioMenuFlyoutItem)RenderProfileMenu.Items[0];
        automatic.Click += (_, _) => SelectRenderProfile("");
    }

    private void SelectRenderProfile(string profileId)
    {
        App.Host.Settings.RenderRulesEnabled = true;
        App.Host.Settings.RenderProfileOverride = profileId;
        App.Host.SaveSettings();
        ApplyRenderProfile(true);
    }

    private string RenderDiagnostics()
    {
        var settings = App.Host.Settings;
        var requested = _requestedRenderProfile;
        var status = App.Host.RenderRules.ActiveStatus;
        var lines = new List<string>
        {
            "",
            "自定义渲染",
            $"功能：{(settings.RenderRulesEnabled ? "已启用" : "已停用；使用基础渲染质量")}",
            $"请求：{requested.ProfileId ?? "无自定义配置"} / {requested.Quality}",
            $"命中规则：{requested.RuleName ?? (string.IsNullOrWhiteSpace(settings.RenderProfileOverride) ? "无" : "手动优先")}",
            $"实际：{status?.ProfileId ?? "libplacebo 自定义配置未生效"}"
        };
        if (status?.Shaders.Count > 0)
            lines.Add("Shader：" + string.Join(" → ", status.Shaders.Select(shader => shader.File)));
        if (status?.CandidatePending == true)
            lines.Add("状态：候选配置正在等待首次成功渲染。");
        lines.Add(status?.GpuMilliseconds is double gpu
            ? $"GPU：最近 {gpu:0.###} ms；P95 {status.GpuP95Milliseconds:0.###} ms；{status.GpuSampleCount} 次采样"
            : "GPU：Unknown");
        if (!string.IsNullOrWhiteSpace(status?.Warning))
            lines.Add("回退：" + status.Warning);
        if (!string.IsNullOrWhiteSpace(App.Host.RenderRules.Warning))
            lines.Add("配置：" + App.Host.RenderRules.Warning);
        if (Playback.UsesExternalVideoOutput)
            lines.Add("说明：外部原生输出不执行 HomePlayer Shader。");
        else if (requested.Quality == VideoRenderQuality.Basic)
            lines.Add("说明：Basic 路线不执行 Shader。");
        return string.Join(Environment.NewLine, lines);
    }
}
