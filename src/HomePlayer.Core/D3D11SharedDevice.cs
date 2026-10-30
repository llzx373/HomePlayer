namespace HomePlayer.Core;

public sealed record D3D11SharedDevice(
    nint Device,
    nint DeviceContext,
    long AdapterLuid,
    long DeviceGeneration,
    object SyncRoot);
