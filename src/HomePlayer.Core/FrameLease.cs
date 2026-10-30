namespace HomePlayer.Core;

public enum VideoFrameStorage
{
    Cpu,
    D3D11Texture
}

public enum VideoPixelFormat
{
    Unknown,
    Bgra32,
    Nv12,
    P010
}

public enum VideoColorMatrix
{
    Unknown,
    Bt601,
    Bt709,
    Bt2020
}

public enum VideoColorRange
{
    Unknown,
    Limited,
    Full
}

public enum VideoColorPrimaries
{
    Unknown,
    Bt709,
    Bt2020,
    DciP3
}

public enum VideoTransferFunction
{
    Unknown,
    Sdr,
    Pq,
    Hlg
}

public enum VideoChromaLocation
{
    Unknown,
    Left,
    Center,
    TopLeft
}

public readonly record struct VideoFramePlane(int Offset, int Stride);

public readonly record struct VideoColorMetadata(
    VideoColorMatrix Matrix = VideoColorMatrix.Unknown,
    VideoColorRange Range = VideoColorRange.Unknown,
    VideoColorPrimaries Primaries = VideoColorPrimaries.Unknown,
    VideoTransferFunction Transfer = VideoTransferFunction.Unknown,
    VideoChromaLocation ChromaLocation = VideoChromaLocation.Unknown)
{
    public VideoHdr10Metadata? Hdr10 { get; init; }
}

public readonly record struct VideoFrameGeometry(
    int SampleAspectRatioNumerator = 1,
    int SampleAspectRatioDenominator = 1,
    int CropLeft = 0,
    int CropTop = 0,
    int CropRight = 0,
    int CropBottom = 0,
    int RotationDegrees = 0,
    bool Mirrored = false,
    bool Interlaced = false,
    bool TopFieldFirst = false);

public readonly record struct D3D11FrameResource(
    nint Texture,
    int Subresource,
    long AdapterLuid,
    nint SharedHandle = 0,
    nint Fence = 0,
    ulong FenceValue = 0,
    nint SourceDevice = 0);

public abstract class FrameLease : IDisposable
{
    private int _released;

    public required int Width { get; init; }
    public required int Height { get; init; }
    public VideoPixelFormat PixelFormat { get; init; } = VideoPixelFormat.Unknown;
    public VideoColorMetadata Color { get; init; }
    public VideoFrameGeometry Geometry { get; init; } = new();
    public long PresentationTimestamp { get; init; }
    public long Duration { get; init; }
    public MediaTimeBase TimeBase { get; init; } = MediaTimeBase.TimeSpanTicks;
    public Guid SessionId { get; set; }
    public long Epoch { get; set; }
    public string TimelineId { get; set; } = "";
    public long FrameSequence { get; init; }
    public long DecodedTimestamp { get; init; }
    public long OpenedTimestamp { get; init; }
    public long SeekStartedTimestamp { get; init; }
    public long? SeekCommandId { get; init; }
    public long DeviceGeneration { get; init; }
    public ZeroCopyLevel ZeroCopyLevel { get; init; } = ZeroCopyLevel.Unknown;
    public Action? ReleaseBuffer { get; init; }
    public abstract VideoFrameStorage Storage { get; }
    public bool IsReleased => Volatile.Read(ref _released) != 0;

    public bool IsCurrent(Guid sessionId, long epoch, string timelineId, long deviceGeneration) =>
        SessionId == sessionId
        && Epoch == epoch
        && string.Equals(TimelineId, timelineId, StringComparison.Ordinal)
        && DeviceGeneration == deviceGeneration;

    public void ReturnBuffer() => Dispose();

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _released, 1) == 0)
            ReleaseBuffer?.Invoke();
    }
}

public class CpuFrameLease : FrameLease
{
    public required byte[] Data { get; init; }
    public IReadOnlyList<VideoFramePlane> Planes { get; init; } = [];
    public override VideoFrameStorage Storage => VideoFrameStorage.Cpu;
}

public sealed class D3D11FrameLease : FrameLease
{
    public required D3D11FrameResource Resource { get; init; }
    public override VideoFrameStorage Storage => VideoFrameStorage.D3D11Texture;
}
