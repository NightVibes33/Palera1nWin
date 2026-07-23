using System.Diagnostics;
using System.Text;

namespace Palera1nWin.Core.Tests;

public sealed class Palera1nLauncherSourceTests
{
    private static string RepositoryRoot()
    {
        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            var directory = new DirectoryInfo(start);
            while (directory is not null)
            {
                var marker = Path.Combine(directory.FullName, "src", "Palera1nWin.App", "Palera1nWin.App.csproj");
                if (File.Exists(marker)) return directory.FullName;
                directory = directory.Parent;
            }
        }

        throw new DirectoryNotFoundException("Could not locate the repository root.");
    }

    [Fact]
    public void LauncherUsesMatchedPongoAndExplicitContinuationShim()
    {
        var root = RepositoryRoot();
        var launcher = File.ReadAllText(
            Path.Combine(root, "DarkSwordRestore", "runtime", "jailbreak", "windows", "palera1n.ps1"),
            Encoding.UTF8);
        var wrapper = File.ReadAllText(
            Path.Combine(root, "DarkSwordRestore", "native", "openra1n-wrapper", "openra1n_wrapper.c"),
            Encoding.UTF8);

        Assert.DoesNotContain("$Value.Replace(\"'\", \"'\\\"'\\\"'\")", launcher, StringComparison.Ordinal);
        Assert.Contains("--override-checkra1n", launcher, StringComparison.Ordinal);
        Assert.Contains("/opt/palera1n/checkra1n", launcher, StringComparison.Ordinal);
        Assert.Contains("05ac:4141", launcher, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("-p -S --yes", wrapper, StringComparison.Ordinal);
        Assert.Contains("windows\\\\palera1n.ps1", wrapper, StringComparison.Ordinal);
        Assert.Contains("official palera1n matched checkra1n/PongoOS loader", wrapper, StringComparison.Ordinal);
        Assert.Contains("openra1n-core.exe remains packaged only for diagnostics", wrapper, StringComparison.Ordinal);
    }

    [Fact]
    public void WindowsPowerShellCanParseLauncher()
    {
        if (!OperatingSystem.IsWindows()) return;

        var script = Path.Combine(
            RepositoryRoot(),
            "DarkSwordRestore",
            "runtime",
            "jailbreak",
            "windows",
            "palera1n.ps1");
        var escaped = script.Replace("'", "''", StringComparison.Ordinal);
        var parserCommand =
            "$tokens=$null;$errors=$null;" +
            $"[System.Management.Automation.Language.Parser]::ParseFile('{escaped}',[ref]$tokens,[ref]$errors)|Out-Null;" +
            "if($errors.Count -gt 0){$errors|ForEach-Object{[Console]::Error.WriteLine($_.Message)};exit 1};exit 0";

        var start = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.ArgumentList.Add("-NoLogo");
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-NonInteractive");
        start.ArgumentList.Add("-Command");
        start.ArgumentList.Add(parserCommand);

        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start Windows PowerShell parser test.");
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();

        Assert.True(process.ExitCode == 0, $"PowerShell parser rejected palera1n.ps1.\nSTDOUT:\n{stdout}\nSTDERR:\n{stderr}");
    }
}
