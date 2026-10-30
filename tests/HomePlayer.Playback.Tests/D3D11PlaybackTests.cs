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
        }
        finally
        {
            frame.ReturnBuffer();
            player.Pause();
        }
    }

    private static long ToInt64(Vortice.Luid value) => (uint)value.LowPart | ((long)value.HighPart << 32);
}
