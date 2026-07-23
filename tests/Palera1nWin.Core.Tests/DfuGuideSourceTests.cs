using System.Text;

namespace Palera1nWin.Core.Tests;

public sealed class DfuGuideSourceTests
{
    private static string RepositoryRoot()
    {
        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            var directory = new DirectoryInfo(start);
            while (directory is not null)
            {
                var marker = Path.Combine(
                    directory.FullName,
                    "src",
                    "Palera1nWin.App",
                    "Palera1nWin.App.csproj");
                if (File.Exists(marker)) return directory.FullName;
                directory = directory.Parent;
            }
        }

        throw new DirectoryNotFoundException("Could not locate the repository root.");
    }

    private static string Read(params string[] path) =>
        File.ReadAllText(Path.Combine([RepositoryRoot(), .. path]), Encoding.UTF8);

    [Fact]
    public void CountdownUsesMonotonicElapsedTimeAndExactPhases()
    {
        var source = Read("src", "Palera1nWin.App", "Services", "DfuGuideSequence.cs");

        Assert.Contains("Stopwatch.StartNew()", source, StringComparison.Ordinal);
        Assert.Contains("TimeSpan.FromSeconds(3)", source, StringComparison.Ordinal);
        Assert.Contains("TimeSpan.FromSeconds(8)", source, StringComparison.Ordinal);
        Assert.Contains("TimeSpan.FromSeconds(10)", source, StringComparison.Ordinal);
        Assert.Contains("if (isDfuDetected()) return true", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Task.Delay(TimeSpan.FromSeconds(1)", source, StringComparison.Ordinal);
    }

    [Fact]
    public void OverlayContainsDetailedBezelAndBlackScreenGuidance()
    {
        var source = Read("src", "Palera1nWin.App", "Controls", "DfuGuideOverlay.cs");

        Assert.Contains("Enter DFU mode", source, StringComparison.Ordinal);
        Assert.Contains("DISPLAY MUST STAY BLACK", source, StringComparison.Ordinal);
        Assert.Contains("TOP / SIDE", source, StringComparison.Ordinal);
        Assert.Contains("VOLUME DOWN", source, StringComparison.Ordinal);
        Assert.Contains("HOME", source, StringComparison.Ordinal);
        Assert.Contains("Cancel guide", source, StringComparison.Ordinal);
        Assert.Contains("DoubleAnimation", source, StringComparison.Ordinal);
    }

    [Fact]
    public void JailbreakAndDowngradeBothUseTheDetailedGuide()
    {
        var source = Read("src", "Palera1nWin.App", "Views", "DetailedDfuGuideFeature.cs");

        Assert.Contains("typeof(JailbreakView)", source, StringComparison.Ordinal);
        Assert.Contains("typeof(DowngradeView)", source, StringComparison.Ordinal);
        Assert.Contains("DfuGuideSequence.RunAsync", source, StringComparison.Ordinal);
        Assert.Contains("_detailedJailbreakStartCommand.Execute(null)", source, StringComparison.Ordinal);
        Assert.Contains("SetDfuGuideSuccess()", source, StringComparison.Ordinal);
    }

    [Fact]
    public void PongoRuntimeIsSharedByJailbreakAndDarkSword()
    {
        var orchestrator = Read("src", "Palera1nWin.Core", "Orchestration", "JailbreakOrchestrator.cs");
        var nativeBuild = Read("DarkSwordRestore", "scripts", "run-native-build.sh");

        Assert.Contains("new OpenRa1nService(_monitor)", orchestrator, StringComparison.Ordinal);
        Assert.Contains("openra1n-core.exe", nativeBuild, StringComparison.Ordinal);
        Assert.Contains("PALERA1N_PONGO_SHA256", nativeBuild, StringComparison.Ordinal);
        Assert.Contains("5475c5f701423858b34e92176c966a9d4b12950f38acb8d1c347f14d5b272655", nativeBuild, StringComparison.Ordinal);
    }
}
