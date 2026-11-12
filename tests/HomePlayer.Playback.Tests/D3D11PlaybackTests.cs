using HomePlayer.Core;
using HomePlayer.Ffmpeg;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace HomePlayer.Playback.Tests;

public sealed class D3D11PlaybackTests
{
    [Fact]
    public async Task Hardware_decode_delivers_same_device_z3_texture()
    {
        var media = Environment.GetEnvironmentVariable("HOMEPLAYER_TEST_D3D11_MEDIA");
        if (string.IsNullOrWhiteSpace(media))
            return;

        var result = D3D11.D3D11CreateDevice(
            null,
            DriverType.Hardware,
            DeviceCreationFlags.BgraSupport,
            new[] { FeatureLevel.Level_11_0 },
            out ID3D11Device device,
            out ID3D11DeviceContext context);
        Assert.True(result.Success, $"无法创建 D3D11 硬件设备：0x{result.Code:X8}");
        using var _ = device;
        using var __ = context;
        using var dxgi = device.QueryInterface<IDXGIDevice>();
        using var adapter = dxgi.GetAdapter();
        var luid = ToInt64(adapter.Description.Luid);
        var shared = new D3D11SharedDevice(device.NativePointer, context.NativePointer, luid, 0, new object());
        using var player = new FfmpegPlayer { D3D11Device = shared, EnableD3D11Va = true };
        var signal = new TaskCompletionSource<FrameLease>(TaskCreationOptions.RunContinuationsAsynchronously);
        player.FrameArrived += frame =>
        {
            if (!signal.TrySetResult(frame))
                frame.ReturnBuffer();
        };
        player.Open(media);
        player.Play();
        var frame = await signal.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            var gpu = Assert.IsType<D3D11FrameLease>(frame);
            Assert.Equal(ZeroCopyLevel.Z3, gpu.ZeroCopyLevel);
            Assert.Equal(luid, gpu.Resource.AdapterLuid);
            Assert.NotEqual(0, gpu.Resource.Texture);
            Assert.Equal("Z3", player.GetPipeline().ZeroCopyLevel);
            Assert.Equal("D3D11VA（同设备纹理）", player.GetPipeline().Hardware);
            using var texture = new ID3D11Texture2D(gpu.Resource.Texture);
            texture.AddRef();
            var description = texture.Description;
            Assert.True(description.Width >= (uint)gpu.Width);
            Assert.True(description.Height >= (uint)gpu.Height);
            Assert.Equal(Format.NV12, description.Format);
            Assert.True((description.BindFlags & BindFlags.ShaderResource) != 0, $"BindFlags={description.BindFlags}");
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

            using var renderer = new LibplaceboRenderer(device, context);
            var preset = BuiltInRenderPresets.Profiles.Single(profile => profile.Id == BuiltInRenderPresets.LiveActionId);
            var targetWidth = gpu.Width * 2;
            var targetHeight = gpu.Height * 2;
            using var target = device.CreateTexture2D(new Texture2DDescription
            {
                Width = (uint)targetWidth,
                Height = (uint)targetHeight,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.B8G8R8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Default,
                BindFlags = BindFlags.RenderTarget
            });
            var geometry = VideoAspect.Quad(targetWidth, targetHeight, gpu.Width, gpu.Height, AspectMode.Stretch, false);
            var rendered = renderer.Render(texture, texture, gpu.Resource.Subresource, gpu.Resource.Subresource,
                gpu.PixelFormat, false, gpu.Color, target, gpu.Width, gpu.Height, targetWidth, targetHeight,
                geometry, 1, 1, false, LibplaceboQuality.HighQuality);
            Assert.True(rendered, renderer.Status.Warning);
            var captured = D3D11FrameCapture.CopyBgra(device, context, target, false);
            AssertRedIfRequested(captured, "without shaders");

            renderer.UpdateProfile(new ResolvedRenderProfile(preset.Id, null, VideoRenderQuality.HighQuality,
                preset.Shaders.Select(shader => new ResolvedRenderShader(shader.File, shader.Parameters)).ToArray(), "e2e"),
                ReadBuiltInShader);
            rendered = renderer.Render(texture, texture, gpu.Resource.Subresource, gpu.Resource.Subresource,
                gpu.PixelFormat, false, gpu.Color, target, gpu.Width, gpu.Height, targetWidth, targetHeight,
                geometry, 1, 1, false, LibplaceboQuality.HighQuality);

            Assert.True(rendered, renderer.Status.Warning);
            Assert.Equal(preset.Id, renderer.Status.ProfileId);
            Assert.Equal(preset.Shaders.Count, renderer.Status.Shaders.Count);
            captured = D3D11FrameCapture.CopyBgra(device, context, target, false);
            Assert.True(captured.Data.Chunk(4).Any(pixel => pixel[0] > 0 || pixel[1] > 0 || pixel[2] > 0),
                "The decoded frame became black after shared-plane rendering");
            AssertRedIfRequested(captured, "with the live-action preset");
        }
        finally
        {
            frame.ReturnBuffer();
            player.Pause();
        }
    }

    private static void AssertRedIfRequested(D3D11CapturedFrame captured, string stage)
    {
        if (Environment.GetEnvironmentVariable("HOMEPLAYER_TEST_EXPECT_RED") != "1")
            return;
        var invalid = captured.Data.Chunk(4).FirstOrDefault(pixel =>
            pixel[2] <= 180 || pixel[1] >= 80 || pixel[0] >= 80);
        Assert.True(invalid == null, invalid == null ? "" :
            $"Expected red {stage}, got B={invalid[0]} G={invalid[1]} R={invalid[2]}");
    }

    private static string ReadBuiltInShader(string file)
    {
        var relative = BuiltInRenderPresets.ShaderPath(file).Replace('/', Path.DirectorySeparatorChar);
        return File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "render", relative));
    }

    private static long ToInt64(Vortice.Luid value) => (uint)value.LowPart | ((long)value.HighPart << 32);
}
