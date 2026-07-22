using Palera1nWin.Core.Util;

namespace Palera1nWin.Core.Services;

public sealed class WslService
{
    private readonly string _preferredDistro;

    public WslService(string preferredDistro = "Ubuntu") =>
        _preferredDistro = string.IsNullOrWhiteSpace(preferredDistro) ? "Ubuntu" : preferredDistro.Trim();

    public async Task<string?> ResolveDistroAsync(CancellationToken cancellationToken = default)
    {
        var result = await ProcessRunner.RunAsync(
            "wsl.exe",
            new[] { "--list", "--quiet" },
            cancellationToken: cancellationToken,
            timeout: TimeSpan.FromSeconds(20)).ConfigureAwait(false);
        if (!result.Succeeded) return null;

        var distros = result.StandardOutput.Replace("\0", "", StringComparison.Ordinal)
            .Split('\n', '\r')
            .Select(line => line.Trim().TrimStart('*').Trim())
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (distros.Count == 0) return null;

        return distros.FirstOrDefault(d => string.Equals(d, _preferredDistro, StringComparison.OrdinalIgnoreCase))
               ?? distros.FirstOrDefault(d => d.StartsWith(_preferredDistro + "-", StringComparison.OrdinalIgnoreCase))
               ?? distros[0];
    }

    public async Task EnsureVhciModuleAsync(string? distro = null, CancellationToken cancellationToken = default)
    {
        distro ??= await ResolveDistroAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(distro)) throw new InvalidOperationException("No WSL distro available.");
        await RunRootCommandAsync("modprobe vhci-hcd 2>/dev/null || true", distro, cancellationToken).ConfigureAwait(false);
    }

    public Task<ProcessResult> RunCommandAsync(
        string command,
        string? distro = null,
        CancellationToken cancellationToken = default,
        TimeSpan? timeout = null) =>
        RunDistroCommandAsync(distro, command, asRoot: false, cancellationToken, timeout);

    public Task<ProcessResult> RunRootCommandAsync(
        string command,
        string? distro = null,
        CancellationToken cancellationToken = default,
        TimeSpan? timeout = null) =>
        RunDistroCommandAsync(distro, command, asRoot: true, cancellationToken, timeout);

    public async Task<bool> HasAppleUsbDeviceAsync(string? distro = null, CancellationToken cancellationToken = default)
    {
        var result = await RunCommandAsync(
            "timeout 5s lsusb 2>/dev/null | grep -i '05ac:' || true",
            distro,
            cancellationToken,
            TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        return result.StandardOutput.Contains("05ac:", StringComparison.OrdinalIgnoreCase);
    }

    private async Task<ProcessResult> RunDistroCommandAsync(
        string? distro,
        string command,
        bool asRoot,
        CancellationToken cancellationToken,
        TimeSpan? timeout)
    {
        distro ??= await ResolveDistroAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(distro)) throw new InvalidOperationException("No WSL distro available.");

        var args = new List<string> { "-d", distro };
        if (asRoot) args.AddRange(["-u", "root"]);
        args.AddRange(["--", "bash", "-lc", command]);
        return await ProcessRunner.RunAsync(
            "wsl.exe",
            args,
            cancellationToken: cancellationToken,
            timeout: timeout ?? TimeSpan.FromMinutes(10)).ConfigureAwait(false);
    }
}
