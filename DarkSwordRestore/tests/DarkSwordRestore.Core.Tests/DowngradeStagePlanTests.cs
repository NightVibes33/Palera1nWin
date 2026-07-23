using DarkSwordRestore.Core;
using Xunit;

namespace DarkSwordRestore.Core.Tests;

public sealed class DowngradeStagePlanTests
{
    [Theory]
    [InlineData("PWND: yolo")]
    [InlineData("PWND: [yolo]")]
    [InlineData("CPID: 0x8003\r\nPWND: yolo\r\nMODE: DFU")]
    [InlineData("CPID: 0x8003\nPWND: [yolo]\nMODE: DFU")]
    public void RecognizesTurdusCompatiblePwnedDfuMarker(string output)
    {
        Assert.True(DowngradeStagePlan.IsPwnedDfuQueryOutput(output));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("YOLO:checkra1n")]
    [InlineData("PWND: checkra1n")]
    [InlineData("MODE: PongoOS")]
    public void RejectsUnverifiedOrLegacyMarkers(string? output)
    {
        Assert.False(DowngradeStagePlan.IsPwnedDfuQueryOutput(output));
    }

    [Fact]
    public void UsesExactNativePwnedDfuArgumentAndMarker()
    {
        Assert.Equal("--pwned-dfu-only", DowngradeStagePlan.PwnedDfuOnlyArgument);
        Assert.Equal("PWND:[yolo]", DowngradeStagePlan.RequiredPwnedDfuMarker);
    }
}
