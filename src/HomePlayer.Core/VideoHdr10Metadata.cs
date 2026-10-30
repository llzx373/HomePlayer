namespace HomePlayer.Core;

public sealed record VideoHdr10Metadata
{
    public ushort RedPrimaryX { get; init; } = 13200;
    public ushort RedPrimaryY { get; init; } = 34500;
    public ushort GreenPrimaryX { get; init; } = 7500;
    public ushort GreenPrimaryY { get; init; } = 30000;
    public ushort BluePrimaryX { get; init; } = 30000;
    public ushort BluePrimaryY { get; init; } = 7500;
    public ushort WhitePointX { get; init; } = 15635;
    public ushort WhitePointY { get; init; } = 16450;
    public uint MaxMasteringLuminance { get; init; } = 1000;
    public uint MinMasteringLuminance { get; init; } = 50;
    public uint MaxContentLightLevel { get; init; } = 1000;
    public uint MaxFrameAverageLightLevel { get; init; } = 400;

    public static VideoHdr10Metadata Bt2020Fallback { get; } = new();
}
