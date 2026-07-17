using Palera1nWin.Core.Models;

namespace Palera1nWin.Core.Tests;

public sealed class DeviceModeMappingTests
{
    [Theory]
    [InlineData("USB\\VID_05AC&PID_12A8\\SERIAL", 0x12A8, "OK", DeviceMode.Normal)]
    [InlineData("USB\\VID_05AC&PID_12AB\\SERIAL", 0x12AB, "OK", DeviceMode.Normal)]
    [InlineData("USB\\VID_05AC&PID_1280\\SERIAL", 0x1280, "OK", DeviceMode.Recovery)]
    [InlineData("USB\\VID_05AC&PID_1281\\SERIAL", 0x1281, "OK", DeviceMode.Recovery)]
    [InlineData("USB\\VID_05AC&PID_1282\\SERIAL", 0x1282, "OK", DeviceMode.Recovery)]
    [InlineData("USB\\VID_05AC&PID_1283\\SERIAL", 0x1283, "OK", DeviceMode.Recovery)]
    [InlineData("USB\\VID_05AC&PID_1227\\SERIAL", 0x1227, "OK", DeviceMode.Dfu)]
    [InlineData("USB\\VID_05AC&PID_4141\\SERIAL", 0x4141, "OK", DeviceMode.Pongo)]
    [InlineData("USB\\VID_05AC&PID_1227&YOLO:\\SERIAL", 0x1227, "OK", DeviceMode.YoloDfu)]
    [InlineData("USB\\VID_05AC&PID_1227&PWND:CHECKM8\\SERIAL", 0x1227, "OK", DeviceMode.PwnedDfu)]
    [InlineData("USB\\VID_05AC&PID_1227\\SERIAL", 0x1227, "Error", DeviceMode.Busy)]
    [InlineData("USB\\VID_05AC&PID_4141\\SERIAL", 0x4141, "Error", DeviceMode.Pongo)]
    public void MapMode_MatchesExpected(string deviceId, ushort pid, string status, DeviceMode expected)
    {
        var mode = AppleUsbDevice.MapMode(deviceId, pid, status);
        Assert.Equal(expected, mode);
    }

    [Fact]
    public void Pongo_WithErrorStatus_IsStillPresent()
    {
        var device = AppleUsbDevice.FromPnpEntity(
            "USB\\VID_05AC&PID_4141\\PONGO",
            "PongoOS USB Device",
            "Error",
            null);

        Assert.Equal(DeviceMode.Pongo, device.Mode);
        Assert.True(device.IsPresent);
    }

    [Fact]
    public void FromPnpEntity_ParsesVidPidAndMode()
    {
        var device = AppleUsbDevice.FromPnpEntity(
            "USB\\VID_05AC&PID_4141\\ABC",
            "Apple Mobile Device (PongoOS)",
            "OK",
            "libusbK");

        Assert.Equal(0x05AC, device.VendorId);
        Assert.Equal(0x4141, device.ProductId);
        Assert.Equal(DeviceMode.Pongo, device.Mode);
        Assert.True(device.IsPresent);
    }
}
