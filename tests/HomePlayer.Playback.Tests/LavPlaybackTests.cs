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

        using var engine = new LavPlaybackEngine(plan) { UseD3D11Sink = true };
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
            using var device = new ID3D11Device(engine.DecoderDevice);
            device.AddRef();
            using var texture = new ID3D11Texture2D(gpu.Resource.Texture);
            texture.AddRef();
            var description = texture.Description;
            Assert.Equal(Format.NV12, description.Format);
            Assert.True((description.BindFlags & BindFlags.ShaderResource) != 0);
            Assert.InRange(gpu.Resource.Subresource, 0, (int)description.ArraySize - 1);
            using var view = description.ArraySize > 1
                ? device.CreateShaderResourceView(texture, new ShaderResourceViewDescription
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
                : device.CreateShaderResourceView(texture, new ShaderResourceViewDescription
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
}
