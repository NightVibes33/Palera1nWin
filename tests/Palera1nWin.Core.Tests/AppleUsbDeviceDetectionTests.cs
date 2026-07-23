using Palera1nWin.Core.Models;
using Palera1nWin.Core.Usb;
using Xunit;

namespace Palera1nWin.Core.Tests;

public sealed class AppleUsbDeviceDetectionTests
{
    [Theory]
    [InlineData(0x12A0)]
    [InlineData(0x12A1)]
    [InlineData(0x12A8)]
    [InlineData(0x12AA)]
    [InlineData(0x12AB)]
    [InlineData(0x12AF)]
    public void NormalModePidRemainsDetectedDuringTransientDriverError(int pid)
    {
        var device = AppleUsbDevice.FromPnpEntity(
            $"USB\\VID_05AC&PID_{pid:X4}&MI_00\\TEST",
            "Apple Mobile Device USB Composite Device",
            "Error",
            string.Empty);

        Assert.Equal(DeviceMode.Normal, device.Mode);
        Assert.True(device.IsPresent);
    }

    [Theory]
    [InlineData(0x1227, DeviceMode.Dfu)]
    [InlineData(0x1281, DeviceMode.Recovery)]
    [InlineData(0x4141, DeviceMode.Pongo)]
    public void KnownBootModeWinsOverTemporaryWindowsStatus(int pid, DeviceMode expected)
    {
        var device = AppleUsbDevice.FromPnpEntity(
            $"USB\\VID_05AC&PID_{pid:X4}\\TEST",
            "Apple Device",
            "Error",
            string.Empty);

        Assert.Equal(expected, device.Mode);
        Assert.True(device.IsPresent);
    }

    [Fact]
    public void CompositeNormalInterfacesCollapseToOnePhysicalCandidate()
    {
        var devices = new[]
        {
            AppleUsbDevice.FromPnpEntity("USB\\VID_05AC&PID_12A8&MI_00\\A", "Apple USB", "OK", "usbaapl64"),
            AppleUsbDevice.FromPnpEntity("USB\\VID_05AC&PID_12A8&MI_01\\B", "Apple MTP", "Error", "WpdUsb"),
            AppleUsbDevice.FromPnpEntity("USB\\VID_05AC&PID_12A8&MI_02\\C", "Apple Composite", "OK", "usbccgp"),
        };

        var collapsed = AppleUsbMonitor.CollapsePhysicalInterfaces(devices);

        Assert.Single(collapsed);
        Assert.Equal(DeviceMode.Normal, collapsed[0].Mode);
        Assert.True(collapsed[0].IsPresent);
    }

    [Fact]
    public void DifferentAppleModesAreNotCollapsedTogether()
    {
        var devices = new[]
        {
            AppleUsbDevice.FromPnpEntity("USB\\VID_05AC&PID_12A8\\NORMAL", "Apple", "OK", "usbaapl64"),
            AppleUsbDevice.FromPnpEntity("USB\\VID_05AC&PID_1227\\DFU", "Apple DFU", "OK", "libusbK"),
        };

        Assert.Equal(2, AppleUsbMonitor.CollapsePhysicalInterfaces(devices).Count);
    }
}
