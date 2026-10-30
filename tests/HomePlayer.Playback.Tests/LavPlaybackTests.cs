using HomePlayer.Core;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace HomePlayer.Playback.Tests;

public sealed class LavPlaybackTests
{
    [Fact]
    public async Task Installed_lav_opens_bgra_reference_pipeline()
    {
        var media = Environment.GetEnvironmentVariable("HOMEPLAYER_TEST_LAV_MEDIA");
        if (string.IsNullOrWhiteSpace(media))
            return;
        var plan = CodecCatalog.Plan(new AppSettings());
        if (plan.Splitters.Count == 0 || plan.VideoDecoders.Count == 0)
            return;

        using var engine = new LavPlaybackEngine(plan);
        var signal = new TaskCompletionSource<FrameLease>(TaskCreationOptions.RunContinuationsAsynchronously);
        engine.FrameArrived += frame =>
        {
            if (!signal.TrySetResult(frame))
                frame.ReturnBuffer();
        };
        engine.Open(media);
        engine.Play();
        var frame = await signal.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            var bgra = Assert.IsType<VideoFrame>(frame);
            Assert.Equal(VideoPixelFormat.Bgra32, bgra.PixelFormat);
            Assert.Equal("RGB32", engine.GetPipeline().PixelFormat);
        }
        finally
        {
            frame.ReturnBuffer();
            engine.Pause();
        }
    }

    [Fact]
    public async Task Lav_d3d11_native_delivers_texture_and_array_slice()
    {
        var media = Environment.GetEnvironmentVariable("HOMEPLAYER_TEST_LAV_MEDIA");
        if (string.IsNullOrWhiteSpace(media))
            return;
        var plan = CodecCatalog.Plan(new AppSettings());
        if (plan.Splitters.Count == 0 || plan.VideoDecoders.Count == 0)
            return;

        var result = D3D11.D3D11CreateDevice(
            null,
            DriverType.Hardware,
            DeviceCreationFlags.BgraSupport,
            new[] { FeatureLevel.Level_11_0 },
            out ID3D11Device outputDevice,
            out ID3D11DeviceContext outputContext);
        Assert.True(result.Success);
        using var _ = outputDevice;
        using var __ = outputContext;
        using var dxgi = outputDevice.QueryInterface<IDXGIDevice>();
        using var adapter = dxgi.GetAdapter();
        var luid = (uint)adapter.Description.Luid.LowPart | ((long)adapter.Description.Luid.HighPart << 32);
        var shared = new D3D11SharedDevice(outputDevice.NativePointer, outputContext.NativePointer, luid, 0, new object());
        using var engine = new LavPlaybackEngine(plan) { UseD3D11Sink = true, D3D11Device = shared };
        var signal = new TaskCompletionSource<FrameLease>(TaskCreationOptions.RunContinuationsAsynchronously);
        engine.FrameArrived += frame =>
        {
            if (!signal.TrySetResult(frame))
                frame.ReturnBuffer();
        };
        engine.Open(media);
        engine.Play();
        var frame = await signal.Task.WaitAsync(TimeSpan.FromSeconds(15));
        try
        {
            var gpu = Assert.IsType<D3D11FrameLease>(frame);
            Assert.Equal(ZeroCopyLevel.Z4, gpu.ZeroCopyLevel);
            Assert.Equal(VideoPixelFormat.Nv12, gpu.PixelFormat);
            Assert.NotEqual(0, gpu.Resource.Texture);
            Assert.NotEqual(0, engine.DecoderDevice);
            Assert.Equal("Z4", engine.GetPipeline().ZeroCopyLevel);
            Assert.Equal(luid, gpu.Resource.AdapterLuid);
            Assert.Equal(engine.DecoderDevice, gpu.Resource.SourceDevice);
            Assert.NotEqual(0, gpu.Resource.SharedHandle);
            using var texture = outputDevice.OpenSharedResource<ID3D11Texture2D>(gpu.Resource.SharedHandle);
            var description = texture.Description;
            Assert.Equal(Format.NV12, description.Format);
            Assert.True((description.BindFlags & BindFlags.ShaderResource) != 0);
            Assert.InRange(gpu.Resource.Subresource, 0, (int)description.ArraySize - 1);
            using var view = description.ArraySize > 1
                ? outputDevice.CreateShaderResourceView(texture, new ShaderResourceViewDescription
                {
                    Format = Format.R8_UNorm,
                    ViewDimension = ShaderResourceViewDimension.Texture2DArray,
                    Texture2DArray = new Texture2DArrayShaderResourceView
                    {
                        MostDetailedMip = 0,
                        MipLevels = 1,
                        FirstArraySlice = (uint)gpu.Resource.Subresource,
                        ArraySize = 1
                    }
                })
                : outputDevice.CreateShaderResourceView(texture, new ShaderResourceViewDescription
                {
                    Format = Format.R8_UNorm,
                    ViewDimension = ShaderResourceViewDimension.Texture2D,
                    Texture2D = new Texture2DShaderResourceView { MostDetailedMip = 0, MipLevels = 1 }
                });
        }
        finally
        {
            frame.ReturnBuffer();
            engine.Pause();
        }
    }

    [Fact]
    public async Task Lav_d3d11_sink_delivers_p010_or_cpu_fallback()
    {
        var media = Environment.GetEnvironmentVariable("HOMEPLAYER_TEST_LAV_HDR_MEDIA");
        if (string.IsNullOrWhiteSpace(media))
            return;
        var plan = CodecCatalog.Plan(new AppSettings());
        if (plan.Splitters.Count == 0 || plan.VideoDecoders.Count == 0)
            return;

        using var engine = new LavPlaybackEngine(plan) { UseD3D11Sink = true };
        var signal = new TaskCompletionSource<FrameLease>(TaskCreationOptions.RunContinuationsAsynchronously);
        engine.FrameArrived += frame =>
        {
            if (!signal.TrySetResult(frame))
                frame.ReturnBuffer();
        };
        engine.Open(media);
        engine.Play();
        FrameLease frame;
        try
        {
            frame = await signal.Task.WaitAsync(TimeSpan.FromSeconds(15));
        }
        catch (TimeoutException ex)
        {
            throw new TimeoutException("等待 LAV P010 超时，当前管线：" + engine.GetPipeline().Format(), ex);
        }
        try
        {
            if (frame is D3D11FrameLease gpu)
            {
                Assert.Equal(VideoPixelFormat.P010, gpu.PixelFormat);
                Assert.Equal(ZeroCopyLevel.Z4, gpu.ZeroCopyLevel);
                Assert.NotEqual(0, gpu.Resource.Texture);
            }
            else
            {
                var cpu = Assert.IsType<CpuFrameLease>(frame);
                Assert.Equal(VideoPixelFormat.Nv12, cpu.PixelFormat);
                Assert.Equal(ZeroCopyLevel.Z2, cpu.ZeroCopyLevel);
            }
            Assert.Equal(frame.ZeroCopyLevel.ToString(), engine.GetPipeline().ZeroCopyLevel);
        }
        finally
        {
            frame.ReturnBuffer();
            engine.Pause();
        }
    }

    [Fact]
    public async Task Madvr_owns_native_output_and_directshow_clock()
    {
        var media = Environment.GetEnvironmentVariable("HOMEPLAYER_TEST_LAV_MEDIA");
        if (string.IsNullOrWhiteSpace(media) || !ExternalRendererCatalog.Detect(ExternalRendererKind.MadVR).Available)
            return;
        var plan = CodecCatalog.Plan(new AppSettings());
        if (plan.Splitters.Count == 0 || plan.VideoDecoders.Count == 0)
            return;
        var registry = new PlaybackProviderRegistry([new NativeFfmpegProvider(), new DirectShowProvider(() => plan)]);
        using var session = registry.Open(media, null, () => plan, PlaybackRoute.MadVR).Session;
        var ownFrame = false;
        var ended = false;
        session.Ended += () => ended = true;
        session.FrameArrived += frame =>
        {
            ownFrame = true;
            frame.ReturnBuffer();
        };
        session.Play();
        var advanced = false;
        for (var attempt = 0; attempt < 100 && !advanced; attempt++)
        {
            await Task.Delay(100);
            advanced = session.Position > TimeSpan.Zero;
        }
        var pipeline = session.GetPipeline();
        Assert.True(advanced, "madVR/DirectShow 时钟未推进：" + pipeline.Format());
        Assert.Equal("LAV + madVR", session.Name);
        Assert.Equal("LAV + madVR", pipeline.Engine);
        Assert.Equal("Unknown", pipeline.ZeroCopyLevel);
        Assert.True(pipeline.Width > 0 && pipeline.Height > 0, pipeline.Format());
        Assert.Contains(pipeline.Filters, name => name.Contains("madVR", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(pipeline.Usage, line => line.Contains("独立原生窗口", StringComparison.Ordinal));
        Assert.False(ownFrame);
        session.Seek(TimeSpan.FromSeconds(1));
        Assert.InRange(session.Position, TimeSpan.Zero, TimeSpan.FromSeconds(3));
        if (session.Duration > TimeSpan.Zero)
        {
            session.Seek(session.Duration - TimeSpan.FromMilliseconds(200));
            session.Play();
            for (var attempt = 0; attempt < 50 && !ended; attempt++)
            {
                await Task.Delay(100);
                _ = session.Position;
            }
            Assert.True(ended, $"madVR/DirectShow 到达 EOF 后未通知结束。position={session.Position} duration={session.Duration} state={((LavPlaybackEngine)((PlaybackSession)session).InnerEngine).ExternalTransportState} playing={session.IsPlaying}");
            _ = session.Position;
        }
        session.Stop();
    }
}
