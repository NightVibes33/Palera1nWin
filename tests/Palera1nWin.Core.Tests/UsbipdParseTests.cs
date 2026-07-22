using Palera1nWin.Core.Services;

namespace Palera1nWin.Core.Tests;

public sealed class UsbipdParseTests
{
    [Fact]
    public void ParseAppleDevices_DetectsAttachedRecovery()
    {
        const string list =
            """
            Connected:
            BUSID  VID:PID    DEVICE                                                        STATE
            1-3    05ac:1281  Apple Recovery (iBoot) USB Composite Device, Apple Mobile...  Attached
            1-5    046d:c092  G102 LIGHTSYNC, USB Input Device                              Not shared

            Persisted:
            GUID                                  DEVICE
            """;

        var devices = UsbipdService.ParseAppleDevices(list);
        Assert.Single(devices);
        Assert.Equal("1-3", devices[0].BusId);
        Assert.Equal("05ac:1281", devices[0].VidPid);
        Assert.Equal(UsbipdAttachState.Attached, devices[0].State);
    }

    [Fact]
    public void ParseAppleDevices_DetectsSharedNotAttached()
    {
        const string list =
            "1-3    05ac:1227  Apple Mobile Device (DFU Mode)                                 Shared";

        var devices = UsbipdService.ParseAppleDevices(list);
        Assert.Single(devices);
        Assert.Equal(UsbipdAttachState.Shared, devices[0].State);
    }

    [Fact]
    public void ParseAppleDevices_NotShared_IsNotMisclassifiedAsShared()
    {
        const string list =
            """
            Connected:
            BUSID  VID:PID    DEVICE                                                        STATE
            1-3    05ac:1227  Apple Mobile Device (DFU Mode)                                Not shared
            """;

        var devices = UsbipdService.ParseAppleDevices(list);
        Assert.Single(devices);
        Assert.Equal(UsbipdAttachState.NotShared, devices[0].State);
    }

    [Fact]
    public void ParseUsbipdState_PrefersNotSharedOverSharedSubstring()
    {
        Assert.Equal(UsbipdAttachState.NotShared, UsbipdService.ParseUsbipdState("1-3 05ac:1227 DFU Not shared"));
        Assert.Equal(UsbipdAttachState.Shared, UsbipdService.ParseUsbipdState("1-3 05ac:1227 DFU Shared"));
        Assert.Equal(UsbipdAttachState.Attached, UsbipdService.ParseUsbipdState("1-3 05ac:1227 DFU Attached"));
    }
}
