using Palera1nWin.Core.Util;

namespace Palera1nWin.Core.Services;

public sealed class WslProvisionService
{
    private readonly WslService _wsl;

    public WslProvisionService(WslService wsl) => _wsl = wsl;
    public WslProvisionService(string preferredDistro = "Ubuntu") => _wsl = new WslService(preferredDistro);

    public static string ConvertToWslPath(string windowsPath)
    {
        var full = Path.GetFullPath(windowsPath);
        if (full.Length >= 3 && full[1] == ':' && (full[2] == '\\' || full[2] == '/'))
        {
            var drive = char.ToLowerInvariant(full[0]);
            var rest = full[3..].Replace('\\', '/');
            return $"/mnt/{drive}/{rest}";
        }
        if (full.StartsWith("\\\\", StringComparison.Ordinal))
            throw new NotSupportedException("WSL provisioning does not accept a network/UNC path. Copy the app to a local drive first.");
        return full.Replace('\\', '/');
    }

    public async Task<bool> IsProvisionedAsync(string? distro = null, CancellationToken cancellationToken = default)
    {
        var result = await _wsl.RunRootCommandAsync(
            "test -x /opt/palera1n/pln-run.sh && test -x /opt/palera1n/palera1n && echo yes || echo no",
            distro,
            cancellationToken).ConfigureAwait(false);
        return result.Succeeded && result.StandardOutput.Split('\n', '\r').Any(line => line.Trim() == "yes");
    }

    public async Task<string?> GetInstalledVersionAsync(string? distro = null, CancellationToken cancellationToken = default)
    {
        var result = await _wsl.RunRootCommandAsync(
            "test -x /opt/palera1n/palera1n && timeout 15s /opt/palera1n/palera1n --version 2>&1 | head -n 1 || true",
            distro,
            cancellationToken).ConfigureAwait(false);
        return result.StandardOutput.Replace("\0", "", StringComparison.Ordinal)
            .Split('\n', '\r')
            .Select(value => value.Trim())
            .FirstOrDefault(value => value.StartsWith("palera1n", StringComparison.OrdinalIgnoreCase));
    }

    public async Task<ProcessResult> InstallPalera1nBinaryAsync(
        string windowsBinaryPath,
        string? distro = null,
        Action<string>? onOutput = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(windowsBinaryPath) || !File.Exists(windowsBinaryPath))
            throw new FileNotFoundException("palera1n binary not found.", windowsBinaryPath);
        if (new FileInfo(windowsBinaryPath).Length < 64 * 1024)
            throw new InvalidDataException("palera1n binary is unexpectedly small and will not be installed.");

        var resolvedDistro = string.IsNullOrWhiteSpace(distro)
            ? await _wsl.ResolveDistroAsync(cancellationToken).ConfigureAwait(false)
            : distro;
        if (string.IsNullOrWhiteSpace(resolvedDistro))
            throw new InvalidOperationException("No WSL distro detected. Install Ubuntu first, reboot, then retry.");

        var wslBin = EscapeShellSingleQuoted(ConvertToWslPath(windowsBinaryPath));
        onOutput?.Invoke($"Installing verified palera1n into {resolvedDistro}:/opt/palera1n/palera1n ...");
        var bash =
            "set -Eeuo pipefail; " +
            "install -d -m755 /opt/palera1n; " +
            $"install -m755 '{wslBin}' /opt/palera1n/palera1n.new; " +
            "if test -e /opt/palera1n/palera1n; then cp -a /opt/palera1n/palera1n /opt/palera1n/palera1n.previous; fi; " +
            "mv -f /opt/palera1n/palera1n.new /opt/palera1n/palera1n; " +
            "ln -sfn /opt/palera1n/palera1n /usr/local/bin/palera1n; " +
            "timeout 15s /opt/palera1n/palera1n --version 2>&1 | head -n 1";

        var result = await ProcessRunner.RunAsync(
            "wsl.exe",
            new[] { "-d", resolvedDistro, "-u", "root", "--", "bash", "-lc", bash },
            cancellationToken: cancellationToken,
            onStdoutLine: onOutput,
            onStderrLine: line => onOutput?.Invoke(line),
            timeout: TimeSpan.FromMinutes(3)).ConfigureAwait(false);
        if (!result.Succeeded) await TryRollbackInstalledBinaryAsync(resolvedDistro, CancellationToken.None).ConfigureAwait(false);
        return result;
    }

    public async Task<ProcessResult> ProvisionAsync(
        string toolchainRoot,
        string? distro = null,
        Action<string>? onOutput = null,
        CancellationToken cancellationToken = default,
        string? preferBinaryPath = null)
    {
        if (string.IsNullOrWhiteSpace(toolchainRoot) || !Directory.Exists(toolchainRoot))
            throw new InvalidOperationException("Toolchain root is not configured or does not exist.");

        var provisionScript = Path.Combine(toolchainRoot, "build", "provision-wsl.sh");
        var linuxBinary = Path.Combine(toolchainRoot, "dist", "palera1n-linux-x86_64");
        if (!File.Exists(provisionScript)) throw new FileNotFoundException("Packaged provision-wsl.sh is missing.", provisionScript);
        if (!File.Exists(linuxBinary)) throw new FileNotFoundException("Packaged palera1n Linux runtime is missing.", linuxBinary);

        var resolvedDistro = string.IsNullOrWhiteSpace(distro)
            ? await _wsl.ResolveDistroAsync(cancellationToken).ConfigureAwait(false)
            : distro;
        if (string.IsNullOrWhiteSpace(resolvedDistro))
            throw new InvalidOperationException("No WSL distro detected. Install Ubuntu first, reboot, then retry.");

        var wslToolchain = EscapeShellSingleQuoted(ConvertToWslPath(toolchainRoot));
        onOutput?.Invoke($"Provisioning WSL distro '{resolvedDistro}' from {toolchainRoot} ...");
        var bashCommand =
            "set -Eeuo pipefail; " +
            "tmp=$(mktemp /tmp/palera1nwin-provision.XXXXXX); " +
            "trap 'rm -f -- \"$tmp\"' EXIT; " +
            $"tr -d '\\r' < '{wslToolchain}/build/provision-wsl.sh' > \"$tmp\"; " +
            "chmod 700 \"$tmp\"; " +
            $"bash \"$tmp\" '{wslToolchain}'";

        var provisionResult = await ProcessRunner.RunAsync(
            "wsl.exe",
            new[] { "-d", resolvedDistro, "-u", "root", "--", "bash", "-lc", bashCommand },
            cancellationToken: cancellationToken,
            onStdoutLine: onOutput,
            onStderrLine: line => onOutput?.Invoke(line),
            timeout: TimeSpan.FromMinutes(20)).ConfigureAwait(false);

        if (provisionResult.Succeeded && !string.IsNullOrWhiteSpace(preferBinaryPath) && File.Exists(preferBinaryPath))
        {
            var install = await InstallPalera1nBinaryAsync(preferBinaryPath, resolvedDistro, onOutput, cancellationToken).ConfigureAwait(false);
            if (!install.Succeeded) return install;
        }
        return provisionResult;
    }

    private async Task TryRollbackInstalledBinaryAsync(string distro, CancellationToken cancellationToken)
    {
        try
        {
            await _wsl.RunRootCommandAsync(
                "if test -x /opt/palera1n/palera1n.previous; then mv -f /opt/palera1n/palera1n.previous /opt/palera1n/palera1n; fi",
                distro,
                cancellationToken).ConfigureAwait(false);
        }
        catch { }
    }

    private static string EscapeShellSingleQuoted(string value) => value.Replace("'", "'\\''", StringComparison.Ordinal);
}
