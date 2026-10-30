namespace HomePlayer.Core;

public sealed class D3D11SharedDevice
{
    private long _deviceGeneration;

    public D3D11SharedDevice(nint device, nint deviceContext, long adapterLuid, long deviceGeneration, object syncRoot)
    {
        Device = device;
        DeviceContext = deviceContext;
        AdapterLuid = adapterLuid;
        _deviceGeneration = deviceGeneration;
        SyncRoot = syncRoot;
    }

    public nint Device { get; private set; }
    public nint DeviceContext { get; private set; }
    public long AdapterLuid { get; }
    public long DeviceGeneration => Interlocked.Read(ref _deviceGeneration);
    public object SyncRoot { get; }

    public void Update(nint device, nint deviceContext, long deviceGeneration)
    {
        Device = device;
        DeviceContext = deviceContext;
        Interlocked.Exchange(ref _deviceGeneration, deviceGeneration);
    }
}
