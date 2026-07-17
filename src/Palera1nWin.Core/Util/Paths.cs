using Palera1nWin.Core.Settings;

namespace Palera1nWin.Core.Util;

public static class Paths
{
    public static IEnumerable<string> GetToolchainCandidates()
    {
        // 1. A "toolchain" subfolder next to the app (users can drop the toolchain there).
        yield return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "toolchain"));

        // 2. Sibling directory (dev layout: repo cloned next to Palera1n-Windows).
        var sibling = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "Palera1n-Windows"));
        yield return sibling;

        // 3. Environment variable override.
        var env = Environment.GetEnvironmentVariable("PALERA1N_TOOLCHAIN");
        if (!string.IsNullOrWhiteSpace(env))
        {
            yield return env.Trim();
        }

        // 4. Program Files (system-wide install).
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        yield return Path.Combine(programFiles, "Palera1n-Windows");

        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        yield return Path.Combine(programFilesX86, "Palera1n-Windows");
    }

    public static string? ResolveToolchainRoot(string? configuredRoot)
    {
        if (!string.IsNullOrWhiteSpace(configuredRoot) && Directory.Exists(configuredRoot))
        {
            return Path.GetFullPath(configuredRoot);
        }

        foreach (var candidate in GetToolchainCandidates())
        {
            if (Directory.Exists(candidate))
            {
                return Path.GetFullPath(candidate);
            }
        }

        return null;
    }

    public static string GetOpenRa1nExecutable(string toolchainRoot) =>
        Path.Combine(toolchainRoot, "dist", "openra1n-win", "openra1n.exe");

    public static string GetPalera1nCmd(string toolchainRoot) =>
        Path.Combine(toolchainRoot, "palera1n.cmd");

    public static string GetPalera1nScript(string toolchainRoot) =>
        Path.Combine(toolchainRoot, "windows", "palera1n.ps1");

    public static string GetFakeCheckra1nScript(string toolchainRoot) =>
        Path.Combine(toolchainRoot, "build", "fake-checkra1n.sh");

    public static string GetStopAmdsScript(string toolchainRoot) =>
        Path.Combine(toolchainRoot, "build", "stop-amds.ps1");

    public static string GetLibusbKArchive(string toolchainRoot) =>
        ResolveNativeFile(toolchainRoot, "libusbK-bin.7z");

    public static string GetLibusbKInfDirectory(string toolchainRoot) =>
        ResolveNativeDirectory(toolchainRoot, "libusbK");

    public static string GetZadigExecutable(string toolchainRoot) =>
        ResolveNativeFile(toolchainRoot, "zadig.exe");

    /// <summary>
    /// Silent libwdi CLI for automated libusbK installs.
    /// Searches app directory first (bundled), then toolchain dist\native.
    /// </summary>
    public static string GetWdiSimpleExecutable(string toolchainRoot) =>
        ResolveNativeFile(toolchainRoot, "wdi-simple.exe");

    /// <summary>
    /// Signal file the GUI creates after the user clicks OK on the DFU Enter dialog.
    /// Watched by windows\palera1n.ps1 --gui-dfu-prompt.
    /// </summary>
    public static string GetDfuEnterSignalPath() =>
        Path.Combine(AppSettings.RuntimeDirectory, "dfu-enter.signal");

    public static bool ValidateToolchain(string toolchainRoot, out IReadOnlyList<string> missing)
    {
        var required = new[]
        {
            GetOpenRa1nExecutable(toolchainRoot),
            GetPalera1nCmd(toolchainRoot),
            GetFakeCheckra1nScript(toolchainRoot),
        };

        missing = required.Where(path => !File.Exists(path)).ToList();
        return missing.Count == 0;
    }

    /// <summary>
    /// Resolve a native binary: check the app's own directory first (for bundled releases),
    /// then fall back to toolchainRoot\dist\native\.
    /// </summary>
    private static string ResolveNativeFile(string toolchainRoot, string fileName)
    {
        // 1. App directory (bundled with the GUI release)
        var appDir = Path.Combine(AppContext.BaseDirectory, "native", fileName);
        if (File.Exists(appDir))
        {
            return appDir;
        }

        // 2. Toolchain dist\native
        return Path.Combine(toolchainRoot, "dist", "native", fileName);
    }

    private static string ResolveNativeDirectory(string toolchainRoot, string dirName)
    {
        var appDir = Path.Combine(AppContext.BaseDirectory, "native", dirName);
        if (Directory.Exists(appDir))
        {
            return appDir;
        }

        return Path.Combine(toolchainRoot, "dist", "native", dirName);
    }
}

