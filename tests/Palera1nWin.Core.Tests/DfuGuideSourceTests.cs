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
    public void CountdownUsesMonotonicElapsedTimeAndNativePalera1nPhases()
    {
        var source = Read("src", "Palera1nWin.App", "Services", "DfuGuideSequence.cs");

        Assert.Contains("Stopwatch.StartNew()", source, StringComparison.Ordinal);
        Assert.Contains("TimeSpan.FromSeconds(3)", source, StringComparison.Ordinal);
        Assert.Contains("TimeSpan.FromSeconds(4)", source, StringComparison.Ordinal);
        Assert.Contains("TimeSpan.FromSeconds(10)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("TimeSpan.FromSeconds(8)", source, StringComparison.Ordinal);
        Assert.Contains("holdSequenceStarting", source, StringComparison.Ordinal);
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
    public void JailbreakVisualStartsFromTheNativePromptInsteadOfHijackingStart()
    {
        var feature = Read("src", "Palera1nWin.App", "Views", "DetailedDfuGuideFeature.cs");
        var prompts = Read("src", "Palera1nWin.App", "Services", "WpfUserPromptService.cs");

        Assert.Contains("typeof(JailbreakView)", feature, StringComparison.Ordinal);
        Assert.Contains("typeof(DowngradeView)", feature, StringComparison.Ordinal);
        Assert.Contains("JailbreakDfuVisualCoordinator", feature, StringComparison.Ordinal);
        Assert.Contains("BeginFromNativePromptAsync", prompts, StringComparison.Ordinal);
        Assert.Contains("Ready for DFU mode?", prompts, StringComparison.Ordinal);
        Assert.DoesNotContain("_detailedJailbreakStartCommand", feature, StringComparison.Ordinal);
        Assert.Contains("SetDfuGuideSuccess()", feature, StringComparison.Ordinal);
    }

    [Fact]
    public void WindowsOwnsDfuUntilTheSharedPongoStageCompletes()
    {
        var orchestrator = Read("src", "Palera1nWin.Core", "Orchestration", "JailbreakOrchestrator.cs");
        var nativeBuild = Read("DarkSwordRestore", "scripts", "run-native-build.sh");
        var wrapper = Read("DarkSwordRestore", "native", "openra1n-wrapper", "openra1n_wrapper.c");

        Assert.Contains("new OpenRa1nService(_monitor)", orchestrator, StringComparison.Ordinal);
        Assert.Contains("openra1n-core.exe", nativeBuild, StringComparison.Ordinal);
        Assert.Contains("patch-openra1n.py", nativeBuild, StringComparison.Ordinal);
        Assert.DoesNotContain("PALERA1N_PONGO_SHA256", nativeBuild, StringComparison.Ordinal);
        Assert.Contains("Windows-native openra1n", wrapper, StringComparison.Ordinal);
        Assert.DoesNotContain("windows\\\\palera1n.ps1", wrapper, StringComparison.Ordinal);
    }
}
