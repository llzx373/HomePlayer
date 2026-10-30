using HomePlayer.Core;

namespace HomePlayer.Core.Tests;

public class D3D11SharedDeviceTests
{
    [Fact]
    public void Update_invalidates_previous_generation_and_replaces_device_handles()
    {
        var device = new D3D11SharedDevice(1, 2, 3, 4, new object());

        device.Update(10, 20, 5);

        Assert.Equal(10, device.Device);
        Assert.Equal(20, device.DeviceContext);
        Assert.Equal(3, device.AdapterLuid);
        Assert.Equal(5, device.DeviceGeneration);
    }
}
