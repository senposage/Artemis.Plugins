using RGB.NET.Devices.OpenRGB;
using Xunit;

namespace OpenRGB.NET.Tests;

public class ProviderLifecycleTests
{
    [Fact]
    public void ChangedProtocolIdIsRemoveAndAdd()
    {
        (uint[] removed, uint[] added) = OpenRGBDeviceProvider.DiffControllerIds([10, 11], [10, 99]);

        Assert.Equal([11u], removed);
        Assert.Equal([99u], added);
    }

    [Fact]
    public void ReportedLocationOutlivesControllerInstanceId()
    {
        const string location = "IP: 192.168.1.103";

        string beforeRescan = TestOpenRGBDevice.GetControllerIdentity(location, 35);
        string afterRescan = TestOpenRGBDevice.GetControllerIdentity(location, 42);

        Assert.Equal("Location:IP: 192.168.1.103", beforeRescan);
        Assert.Equal(beforeRescan, afterRescan);
    }

    [Fact]
    public void MissingLocationFallsBackToControllerInstanceId()
    {
        Assert.Equal("Controller:42", TestOpenRGBDevice.GetControllerIdentity(string.Empty, 42));
    }

    [Fact]
    public void DisposedProviderCanBeRecreated()
    {
        OpenRGBDeviceProvider provider = OpenRGBDeviceProvider.Instance;
        provider.Dispose();

        OpenRGBDeviceProvider replacement = OpenRGBDeviceProvider.Instance;

        Assert.NotSame(provider, replacement);
        replacement.Dispose();
    }

    private abstract class TestOpenRGBDevice : AbstractOpenRGBDevice<OpenRGBDeviceInfo>
    {
        private TestOpenRGBDevice(OpenRGBDeviceInfo info, RGB.NET.Core.IUpdateQueue updateQueue)
            : base(info, updateQueue, "controller")
        {
        }
    }
}
