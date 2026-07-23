using System.Text;

namespace Palera1nWin.Core.Tests;

public sealed class OnboardingThemeSourceTests
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

        throw new DirectoryNotFoundException("Could not locate the repository root from the current or test output directory.");
    }

    [Fact]
    public void ThemeDefinesProgrammaticSurfaceAliases()
    {
        var theme = File.ReadAllText(
            Path.Combine(RepositoryRoot(), "src", "Palera1nWin.App", "Themes", "Theme.xaml"),
            Encoding.UTF8);

        Assert.Contains("x:Key=\"Brush.Card\"", theme, StringComparison.Ordinal);
        Assert.Contains("x:Key=\"Brush.SurfaceSecondary\"", theme, StringComparison.Ordinal);
        Assert.Contains("x:Key=\"Brush.SurfaceTertiary\"", theme, StringComparison.Ordinal);
        Assert.Contains("x:Key=\"Brush.Border\"", theme, StringComparison.Ordinal);
        Assert.Contains("x:Key=\"Brush.TextDisabled\"", theme, StringComparison.Ordinal);
    }

    [Fact]
    public void OnboardingMatchesFourActionDowngradeFlow()
    {
        var guide = File.ReadAllText(
            Path.Combine(RepositoryRoot(), "src", "Palera1nWin.App", "Views", "OnboardingWindow.cs"),
            Encoding.UTF8);

        Assert.Contains("Start Downgrade", guide, StringComparison.Ordinal);
        Assert.Contains("Test DFU → Pwned/Pongo", guide, StringComparison.Ordinal);
        Assert.Contains("Boot Device", guide, StringComparison.Ordinal);
        Assert.Contains("Import Boot Profile", guide, StringComparison.Ordinal);
        Assert.DoesNotContain("type the exact ProductType", guide, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Start Full Downgrade", guide, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void OnboardingContentVersionWasAdvanced()
    {
        var store = File.ReadAllText(
            Path.Combine(RepositoryRoot(), "src", "Palera1nWin.App", "Services", "OnboardingStateStore.cs"),
            Encoding.UTF8);

        Assert.Contains("CurrentContentVersion = 2", store, StringComparison.Ordinal);
        Assert.Contains("PreparedContentVersion", store, StringComparison.Ordinal);
    }
}