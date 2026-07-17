using Palera1nWin.Core.Util;

namespace Palera1nWin.Core.Services;

public sealed class WslService
{
    private readonly string _preferredDistro;

    public WslService(string preferredDistro = "Ubuntu")
    {
        _preferredDistro = preferredDistro;
    }

    public async Task<string?> ResolveDistroAsync(CancellationToken cancellationToken = default)
    {
        var result = await ProcessRunner.RunAsync(
            "wsl.exe",
            new[] { "--list", "--quiet" },
            cancellationToken: cancellationToken).ConfigureAwait(false);

        var distros = result.StandardOutput
            .Split('\n', '\r')
            .Select(line => line.Trim().Trim('\0'))
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .ToList();

        if (distros.Count == 0)
        {
            return null;
        }

        var match = distros.FirstOrDefault(d =>
            string.Equals(d, _preferredDistro, StringComparison.OrdinalIgnoreCase));

        return match ?? distros[0];
    }

    public async Task EnsureVhciModuleAsync(string? distro = null, CancellationToken cancellationToken = default)
    {
        distro ??= await ResolveDistroAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(distro))
        {
            throw new InvalidOperationException("No WSL distro available.");
        }

        await RunRootCommandAsync(
            distro,
            "modprobe vhci-hcd 2>/dev/null || true",
            cancellationToken).ConfigureAwait(false);
    }

    public Task<ProcessResult> RunCommandAsync(
        string command,
        string? distro = null,
        CancellationToken cancellationToken = default)
    {
        return RunDistroCommandAsync(distro, command, asRoot: false, cancellationToken);
    }

    public Task<ProcessResult> RunRootCommandAsync(
        string command,
        string? distro = null,
        CancellationToken cancellationToken = default)
    {
        return RunDistroCommandAsync(distro, command, asRoot: true, cancellationToken);
    }

    /// <summary>
    /// True when WSL lsusb reports any Apple Inc. device (VID 05ac).
    /// </summary>
    public async Task<bool> HasAppleUsbDeviceAsync(
        string? distro = null,
        CancellationToken cancellationToken = default)
    {
        var result = await RunCommandAsync(
            "lsusb 2>/dev/null | grep -i '05ac:' || true",
            distro,
            cancellationToken).ConfigureAwait(false);

        return result.StandardOutput.Contains("05ac:", StringComparison.OrdinalIgnoreCase);
    }

    private async Task<ProcessResult> RunDistroCommandAsync(
        string? distro,
        string command,
        bool asRoot,
        CancellationToken cancellationToken)
    {
        distro ??= await ResolveDistroAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(distro))
        {
            throw new InvalidOperationException("No WSL distro available.");
        }

        var args = new List<string> { "-d", distro };
        if (asRoot)
        {
            args.Add("-u");
            args.Add("root");
        }

        args.Add("--");
        args.Add("bash");
        args.Add("-lc");
        args.Add(command);

        return await ProcessRunner.RunAsync("wsl.exe", args, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
    }
}
