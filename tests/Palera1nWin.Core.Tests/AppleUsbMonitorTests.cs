using Palera1nWin.Core.Usb;

namespace Palera1nWin.Core.Tests;

public sealed class AppleUsbMonitorTests
{
    [Fact]
    public void ParseUsbipdList_DetectsNormalAppleBusId()
    {
        const string list = """
            Connected:
            BUSID  VID:PID    DEVICE                                                        STATE
            1-5    05ac:12ab  Apple Mobile Device USB Composite Device                      Shared
            """;

        var result = AppleUsbMonitor.ParseUsbipdList(list);

        Assert.True(result.TryGetValue("12AB", out var busId));
        Assert.Equal("1-5", busId);
    }

    [Fact]
    public void ParseUsbipdList_DetectsDfuAppleBusId()
    {
        const string list = """
            Connected:
            BUSID  VID:PID    DEVICE                                                        STATE
            1-5    05ac:1227  Apple Mobile Device (DFU Mode)                                Not shared
            """;

        var result = AppleUsbMonitor.ParseUsbipdList(list);

        Assert.True(result.TryGetValue("1227", out var busId));
        Assert.Equal("1-5", busId);
    }
}
