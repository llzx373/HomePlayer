using HomePlayer.Core;

namespace HomePlayer.Core.Tests;

public class FrameLeaseTests
{
    [Fact]
    public void Existing_video_frame_is_a_bgra_cpu_lease()
    {
        using var frame = new VideoFrame
        {
            Width = 1,
            Height = 1,
            Stride = 4,
            Data = new byte[4]
        };

        CpuFrameLease lease = frame;
        Assert.Equal(VideoFrameStorage.Cpu, lease.Storage);
        Assert.Equal(VideoPixelFormat.Bgra32, lease.PixelFormat);
        Assert.Equal(ZeroCopyLevel.Z1, lease.ZeroCopyLevel);
        Assert.Same(frame.Data, lease.Data);
    }

    [Fact]
    public void Cpu_lease_releases_once_across_threads()
    {
        var released = 0;
        var lease = new CpuFrameLease
        {
            Width = 2,
            Height = 2,
            PixelFormat = VideoPixelFormat.Bgra32,
            Data = new byte[16],
            Planes = [new VideoFramePlane(0, 8)],
            ReleaseBuffer = () => Interlocked.Increment(ref released)
        };

        Parallel.For(0, 32, _ => lease.ReturnBuffer());
        lease.Dispose();

        Assert.Equal(1, released);
        Assert.True(lease.IsReleased);
        Assert.Equal(VideoFrameStorage.Cpu, lease.Storage);
    }

    [Fact]
    public void Gpu_lease_preserves_resource_color_and_geometry()
    {
        var released = 0;
        var sessionId = Guid.NewGuid();
        var resource = new D3D11FrameResource(1234, 2, 5678, 9012, 3456, 42);
        var color = new VideoColorMetadata(
            VideoColorMatrix.Bt2020,
            VideoColorRange.Limited,
            VideoColorPrimaries.Bt2020,
            VideoTransferFunction.Pq,
            VideoChromaLocation.TopLeft);
        var geometry = new VideoFrameGeometry(4, 3, 2, 4, 6, 8, 90, true, true, true);
        var lease = new D3D11FrameLease
        {
            Width = 1920,
            Height = 1080,
            PixelFormat = VideoPixelFormat.P010,
            Color = color,
            Geometry = geometry,
            SessionId = sessionId,
            Epoch = 7,
            TimelineId = "timeline",
            DeviceGeneration = 3,
            ZeroCopyLevel = ZeroCopyLevel.Z3,
            Resource = resource,
            ReleaseBuffer = () => released++
        };

        Assert.Equal(VideoFrameStorage.D3D11Texture, lease.Storage);
        Assert.Equal(resource, lease.Resource);
        Assert.Equal(color, lease.Color);
        Assert.Equal(geometry, lease.Geometry);
        Assert.True(lease.IsCurrent(sessionId, 7, "timeline", 3));
        Assert.False(lease.IsCurrent(sessionId, 7, "timeline", 4));
        Assert.False(lease.IsCurrent(sessionId, 8, "timeline", 3));
        Assert.False(lease.IsCurrent(sessionId, 7, "other", 3));
        Assert.False(lease.IsCurrent(Guid.NewGuid(), 7, "timeline", 3));

        lease.Dispose();
        lease.ReturnBuffer();

        Assert.Equal(1, released);
    }

    [Fact]
    public void Invalid_device_generation_is_rejected_before_rendering()
    {
        var lease = new D3D11FrameLease
        {
            Width = 1,
            Height = 1,
            SessionId = Guid.Empty,
            TimelineId = "",
            DeviceGeneration = 10,
            Resource = new D3D11FrameResource(1, 0, 2)
        };

        Assert.False(lease.IsCurrent(Guid.Empty, 0, "", 11));
    }
}
