using Xunit;

namespace Palera1nWin.Core.Tests;

public sealed class DowngradeQuickActionsActivationTests
{
    [Fact]
    public void DowngradeConstructorActivatesQuickActionsBeforeLoadedHandlers()
    {
        var root = FindRepositoryRoot();
        var constructorSource = File.ReadAllText(Path.Combine(
            root, "src", "Palera1nWin.App", "Views", "DowngradeView.xaml.cs"));
        var hooksSource = File.ReadAllText(Path.Combine(
            root, "src", "Palera1nWin.App", "Views", "DowngradeView.ExperienceHooks.cs"));
        var simpleSource = File.ReadAllText(Path.Combine(
            root, "src", "Palera1nWin.App", "Views", "DowngradeView.SimpleMode.cs"));

        var activateIndex = constructorSource.IndexOf(
            "WireDowngradeExperienceHooks();",
            StringComparison.Ordinal);
        var loadedIndex = constructorSource.IndexOf(
            "Loaded += DowngradeView_Loaded;",
            StringComparison.Ordinal);

        Assert.True(activateIndex >= 0, "DowngradeView must activate the enhanced DarkSword experience from its constructor.");
        Assert.True(loadedIndex > activateIndex, "Quick Actions must be activated before the normal Loaded handler can reveal legacy XAML.");
        Assert.Contains("InitializeUiHardening();", constructorSource, StringComparison.Ordinal);

        Assert.Contains("InitializeSimpleDowngradeUi();", hooksSource, StringComparison.Ordinal);
        Assert.Contains("ApplySimpleDowngradeLayout();", hooksSource, StringComparison.Ordinal);

        Assert.Contains("DARKSWORD QUICK ACTIONS", simpleSource, StringComparison.Ordinal);
        Assert.Contains("Start Downgrade", simpleSource, StringComparison.Ordinal);
        Assert.Contains("Test DFU → Pwned/Pongo", simpleSource, StringComparison.Ordinal);
        Assert.Contains("Boot Device", simpleSource, StringComparison.Ordinal);
        Assert.Contains("Import Boot Profile", simpleSource, StringComparison.Ordinal);
        Assert.Contains("element.Visibility = keep ? Visibility.Visible : Visibility.Collapsed;", simpleSource, StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            var marker = Path.Combine(
                current.FullName,
                "src",
                "Palera1nWin.App",
                "Views",
                "DowngradeView.xaml.cs");
            if (File.Exists(marker)) return current.FullName;
            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the Palera1nWin repository root from the test output directory.");
    }
}
