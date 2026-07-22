using Palera1nWin.Core.Settings;

namespace Palera1nWin.Core.Util;

public static class Paths
{
    public static IEnumerable<string> GetToolchainCandidates()
    {
        yield return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "toolchain"));

        var env = Environment.GetEnvironmentVariable("PALERA1N_TOOLCHAIN");
        if (!string.IsNullOrWhiteSpace(env)) yield return env.Trim();

        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        yield return Path.Combine(programFiles, "Palera1n-Windows");

        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        yield return Path.Combine(programFilesX86, "Palera1n-Windows");
    }

    public static string? ResolveToolchainRoot(string? configuredRoot)
    {
        if (!string.IsNullOrWhiteSpace(configuredRoot) && Directory.Exists(configuredRoot))
            return Path.GetFullPath(configuredRoot);

        foreach (var candidate in GetToolchainCandidates())
        {
            if (Directory.Exists(candidate)) return Path.GetFullPath(candidate);
        }

        return null;
    }

    public static string GetOpenRa1nExecutable(string toolchainRoot)
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "toolchain", "openra1n.exe"),
            Path.Combine(toolchainRoot, "openra1n.exe"),
            Path.Combine(toolchainRoot, "bin", "openra1n.exe"),
            Path.Combine(toolchainRoot, "dist", "openra1n-win", "openra1n.exe"),
        };
        return candidates.FirstOrDefault(File.Exists) ?? candidates[0];
    }

    public static string GetOpenRa1nCoreExecutable(string toolchainRoot)
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "toolchain", "openra1n-core.exe"),
            Path.Combine(toolchainRoot, "openra1n-core.exe"),
            Path.Combine(toolchainRoot, "bin", "openra1n-core.exe"),
        };
        return candidates.FirstOrDefault(File.Exists) ?? candidates[0];
    }

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

    public static string GetWdiSimpleExecutable(string toolchainRoot) =>
        ResolveNativeFile(toolchainRoot, "wdi-simple.exe");

    public static string GetDfuEnterSignalPath() =>
        Path.Combine(AppSettings.RuntimeDirectory, "dfu-enter.signal");

    public static bool ValidateToolchain(string toolchainRoot, out IReadOnlyList<string> missing)
    {
        var required = new[]
        {
            GetOpenRa1nExecutable(toolchainRoot),
            GetOpenRa1nCoreExecutable(toolchainRoot),
            GetPalera1nCmd(toolchainRoot),
            GetFakeCheckra1nScript(toolchainRoot),
        };

        missing = required.Where(path => !File.Exists(path)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return missing.Count == 0;
    }

    private static string ResolveNativeFile(string toolchainRoot, string fileName)
    {
        var appToolchain = Path.Combine(AppContext.BaseDirectory, "toolchain", fileName);
        if (File.Exists(appToolchain)) return appToolchain;

        var appNative = Path.Combine(AppContext.BaseDirectory, "native", fileName);
        if (File.Exists(appNative)) return appNative;

        var rootFile = Path.Combine(toolchainRoot, fileName);
        if (File.Exists(rootFile)) return rootFile;

        return Path.Combine(toolchainRoot, "dist", "native", fileName);
    }

    private static string ResolveNativeDirectory(string toolchainRoot, string directoryName)
    {
        var appDirectory = Path.Combine(AppContext.BaseDirectory, "native", directoryName);
        if (Directory.Exists(appDirectory)) return appDirectory;

        return Path.Combine(toolchainRoot, "dist", "native", directoryName);
    }
}
