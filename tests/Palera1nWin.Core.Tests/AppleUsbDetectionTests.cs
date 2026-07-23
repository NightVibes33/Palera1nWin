using Palera1nWin.Core.Models;
using Palera1nWin.Core.Usb;

namespace Palera1nWin.Core.Tests;

public sealed class AppleUsbDetectionTests
{
    [Theory]
    [InlineData(0x12A0)]
    [InlineData(0x12A8)]
    [InlineData(0x12AA)]
    [InlineData(0x12AB)]
    public void KnownNormalPidWinsOverTransientPnpError(int pid)
    {
        var device = AppleUsbDevice.FromPnpEntity(
            $"USB\\VID_05AC&PID_{pid:X4}\\SERIAL",
            "Apple Mobile Device USB Composite Device",
            "Error",
            "usbaapl64");

        Assert.True(device.IsPresent);
        Assert.Equal(DeviceMode.Normal, device.Mode);
    }

    [Fact]
    public void CompositeParentAndInterfacesCollapseToOnePhysicalDevice()
    {
        var rows = new[]
        {
            AppleUsbDevice.FromPnpEntity(
                "USB\\VID_05AC&PID_12A8\\SERIAL",
                "Apple Mobile Device USB Composite Device",
                "OK",
                "usbccgp"),
            AppleUsbDevice.FromPnpEntity(
                "USB\\VID_05AC&PID_12A8&MI_00\\7&ABC&0&0000",
                "Apple Mobile Device USB Driver",
                "OK",
                "usbaapl64"),
            AppleUsbDevice.FromPnpEntity(
                "USB\\VID_05AC&PID_12A8&MI_01\\7&ABC&0&0001",
                "Apple Mobile Device USB Driver",
                "OK",
                "usbaapl64"),
        };

        var collapsed = AppleUsbMonitor.CollapseCompositeInterfaces(rows);

        var device = Assert.Single(collapsed);
        Assert.DoesNotContain("&MI_", device.DeviceId, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(DeviceMode.Normal, device.Mode);
    }

    [Fact]
    public void TwoPhysicalParentsAreNotCollapsed()
    {
        var rows = new[]
        {
            AppleUsbDevice.FromPnpEntity(
                "USB\\VID_05AC&PID_12A8\\SERIAL-ONE",
                "Apple Mobile Device USB Composite Device",
                "OK",
                "usbccgp"),
            AppleUsbDevice.FromPnpEntity(
                "USB\\VID_05AC&PID_12A8\\SERIAL-TWO",
                "Apple Mobile Device USB Composite Device",
                "OK",
                "usbccgp"),
        };

        var collapsed = AppleUsbMonitor.CollapseCompositeInterfaces(rows);

        Assert.Equal(2, collapsed.Count);
    }
}
