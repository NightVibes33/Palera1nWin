using System.Text;
using Xunit;

namespace Palera1nWin.Core.Tests;

public sealed class IntegratedDeviceWorkflowSourceTests
{
    private static string RepositoryRoot()
    {
        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            var directory = new DirectoryInfo(start);
            while (directory is not null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "src", "Palera1nWin.App", "Palera1nWin.App.csproj")))
                    return directory.FullName;
                directory = directory.Parent;
            }
        }
        throw new DirectoryNotFoundException("Could not locate repository root.");
    }

    private static string Read(params string[] parts) =>
        File.ReadAllText(Path.Combine(new[] { RepositoryRoot() }.Concat(parts).ToArray()), Encoding.UTF8);

    [Fact]
    public void DarkSwordMonitorDeduplicatesContainersAndNormalizesNormalModeEcid()
    {
        var source = Read("DarkSwordRestore", "src", "DarkSwordRestore.Core", "AppleDeviceMonitor.cs");
        Assert.Contains("DEVPKEY_Device_ContainerId", source, StringComparison.Ordinal);
        Assert.Contains("GroupBy(PhysicalPnpKey", source, StringComparison.Ordinal);
        Assert.Contains("NormalizeIDeviceInfoEcid", source, StringComparison.Ordinal);
        Assert.Contains("decimalValue.ToString(\"X\"", source, StringComparison.Ordinal);
        Assert.Contains("PID_12A[0-9A-F]", source, StringComparison.Ordinal);
    }

    [Fact]
    public void DowngradeUsesGuidedDfuAndSharedLogs()
    {
        var view = Read("src", "Palera1nWin.App", "Views", "DowngradeView.xaml.cs");
        var simple = Read("src", "Palera1nWin.App", "Views", "DowngradeView.SimpleMode.cs");
        var exact = Read("src", "Palera1nWin.App", "Views", "DowngradeView.ExactIdentity.cs");
        var guidance = Read("src", "Palera1nWin.App", "Views", "DowngradeView.DfuGuidance.cs");

        Assert.Contains("_shell?.AppendLog(\"downgrade\"", view, StringComparison.Ordinal);
        Assert.Contains("StartDowngradeDriverWatch", view, StringComparison.Ordinal);
        Assert.Contains("EnsureCleanDfuWithGuidanceAsync(\"Start Downgrade\"", simple, StringComparison.Ordinal);
        Assert.Contains("EnsureCleanDfuWithGuidanceAsync(\"Test DFU → Pwned/Pongo\"", simple, StringComparison.Ordinal);
        Assert.Contains("EnsureCleanDfuWithGuidanceAsync(\"Test DFU → Pwned/Pongo\"", exact, StringComparison.Ordinal);
        Assert.Contains("current.Mode == AppleDeviceMode.Dfu", guidance, StringComparison.Ordinal);
        Assert.Contains("The screen must remain completely black", guidance, StringComparison.Ordinal);
    }

    [Fact]
    public void JailbreakRestoresOriginalDriverWatchAroundOpenra1n()
    {
        var source = Read("src", "Palera1nWin.Core", "Orchestration", "JailbreakOrchestrator.cs");
        Assert.Contains("new LibusbKWatchdog(_monitor, _settings)", source, StringComparison.Ordinal);
        Assert.Contains("driverWatch.Start()", source, StringComparison.Ordinal);
        Assert.Contains("driverWatch.Stop()", source, StringComparison.Ordinal);
    }
}
