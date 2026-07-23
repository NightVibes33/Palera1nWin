using Palera1nWin.Core.Models;

namespace Palera1nWin.Core.Tests;

public sealed class AppleUsbDevicePhysicalDetectionTests
{
    [Theory]
    [InlineData(0x12A0)]
    [InlineData(0x12A8)]
    [InlineData(0x12AA)]
    [InlineData(0x12AB)]
    public void KnownNormalModePidRemainsPresentDuringPnpError(int pid)
    {
        var device = AppleUsbDevice.FromPnpEntity(
            $"USB\\VID_05AC&PID_{pid:X4}\\PHYSICAL-IPAD",
            "Apple Mobile Device USB Composite Device",
            "Error",
            "usbaapl64");

        Assert.True(device.IsPresent);
        Assert.Equal(DeviceMode.Normal, device.Mode);
    }

    [Theory]
    [InlineData("Unknown")]
    [InlineData("Error")]
    public void PhysicalDfuRemainsDfuDuringDriverReenumeration(string status)
    {
        var device = AppleUsbDevice.FromPnpEntity(
            "USB\\VID_05AC&PID_1227\\CPID:8003",
            "Apple Mobile Device (DFU Mode)",
            status,
            string.Empty);

        Assert.True(device.IsPresent);
        Assert.Equal(DeviceMode.Dfu, device.Mode);
    }

    [Fact]
    public void UnknownApplePidWithErrorIsNotAcceptedAsPhysicalTarget()
    {
        var device = AppleUsbDevice.FromPnpEntity(
            "USB\\VID_05AC&PID_FFFF\\UNKNOWN",
            "Unknown Apple USB Device",
            "Error",
            string.Empty);

        Assert.False(device.IsPresent);
        Assert.Equal(DeviceMode.Busy, device.Mode);
    }

    [Fact]
    public void PongoRemainsPresentDuringDriverSwitch()
    {
        var device = AppleUsbDevice.FromPnpEntity(
            "USB\\VID_05AC&PID_4141\\PONGO",
            "PongoOS USB Device",
            "Error",
            "libusbK");

        Assert.True(device.IsPresent);
        Assert.Equal(DeviceMode.Pongo, device.Mode);
    }
}
