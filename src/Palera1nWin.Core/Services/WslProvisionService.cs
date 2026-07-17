using System.Text;
using Palera1nWin.Core.Util;

namespace Palera1nWin.Core.Services;

/// <summary>
/// Provisions the WSL side of the hybrid jailbreak: installs the runtime
/// packages (usbmuxd, usbutils, libusb, usbip), wires the usbip client,
/// auto-loads vhci-hcd, and installs the palera1n binary + pln-run.sh
/// wrapper into /opt/palera1n/. Idempotent — safe to run repeatedly.
///
/// This is the GUI equivalent of running `build/provision-wsl.sh` from the
/// CLI setup. Without it, palera1n.ps1 aborts with
/// "palera1n runtime not found in '<distro>'. Run .\setup.ps1 first."
/// </summary>
public sealed class WslProvisionService
{
    private readonly WslService _wsl;

    public WslProvisionService(WslService wsl)
    {
        _wsl = wsl;
    }

    public WslProvisionService(string preferredDistro = "Ubuntu")
    {
        _wsl = new WslService(preferredDistro);
    }

    /// <summary>
    /// Converts a Windows path (e.g. C:\Work\Palera1n-Windows) to the path
    /// WSL sees it at (e.g. /mnt/c/Work/Palera1n-Windows).
    /// </summary>
    public static string ConvertToWslPath(string windowsPath)
    {
        var full = Path.GetFullPath(windowsPath);
        // Drive letter form: X:\rest\of\path
        if (full.Length >= 2 && full[1] == ':')
        {
            var drive = char.ToLowerInvariant(full[0]);
            var rest = full.Substring(2).TrimStart('\\', '/').Replace('\\', '/');
            return $"/mnt/{drive}/{rest}";
        }

        // UNC or already-unix: best effort
        return full.Replace('\\', '/');
    }

    /// <summary>
    /// True when the WSL distro already has /opt/palera1n/pln-run.sh installed
    /// (i.e. provisioning has been completed at least once).
    /// </summary>
    public async Task<bool> IsProvisionedAsync(
        string? distro = null,
        CancellationToken cancellationToken = default)
    {
        var result = await _wsl.RunRootCommandAsync(
            "test -x /opt/palera1n/pln-run.sh && echo yes || echo no",
            distro,
            cancellationToken).ConfigureAwait(false);

        return result.StandardOutput.Contains("yes", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Returns the palera1n version string currently installed at
    /// /opt/palera1n/palera1n (e.g. "palera1n 2.2.1-..."), or null if not present.
    /// </summary>
    public async Task<string?> GetInstalledVersionAsync(
        string? distro = null,
        CancellationToken cancellationToken = default)
    {
        var result = await _wsl.RunRootCommandAsync(
            "test -x /opt/palera1n/palera1n && /opt/palera1n/palera1n --version 2>&1 | grep -m1 -Eo 'palera1n[ -][0-9][^ ]*' || true",
            distro,
            cancellationToken).ConfigureAwait(false);

        var line = result.StandardOutput
            .Replace("\0", "", StringComparison.Ordinal)
            .Split('\n', '\r')
            .Select(l => l.Trim())
            .FirstOrDefault(l => l.StartsWith("palera1n", StringComparison.OrdinalIgnoreCase));

        return string.IsNullOrWhiteSpace(line) ? null : line;
    }

    /// <summary>
    /// Installs a specific palera1n Linux binary into /opt/palera1n/palera1n
    /// (replacing whatever version was there). Used by the Versions tab to
    /// "activate" a downloaded release so the jailbreak actually runs it, instead
    /// of always using the bundled binary that provision-wsl.sh installs.
    /// </summary>
    public async Task<ProcessResult> InstallPalera1nBinaryAsync(
        string windowsBinaryPath,
        string? distro = null,
        Action<string>? onOutput = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(windowsBinaryPath) || !File.Exists(windowsBinaryPath))
        {
            throw new FileNotFoundException("palera1n binary not found.", windowsBinaryPath);
        }

        var resolvedDistro = distro;
        if (string.IsNullOrWhiteSpace(resolvedDistro))
        {
            resolvedDistro = await _wsl.ResolveDistroAsync(cancellationToken).ConfigureAwait(false);
        }

        if (string.IsNullOrWhiteSpace(resolvedDistro))
        {
            throw new InvalidOperationException(
                "No WSL distro detected. Install one first:  wsl --install -d Ubuntu  (admin), then re-run.");
        }

        var wslBin = ConvertToWslPath(windowsBinaryPath);
        var wslBinEsc = wslBin.Replace("'", "'\\''", StringComparison.Ordinal);
        onOutput?.Invoke($"Installing palera1n binary into {resolvedDistro}:/opt/palera1n/palera1n ...");

        // Copy the binary off the /mnt share (install applies 0755 to the copy),
        // symlink it onto PATH, then echo the resulting version for confirmation.
        var bash =
            "set -e; install -d /opt/palera1n; " +
            $"install -m755 '{wslBinEsc}' /opt/palera1n/palera1n; " +
            "ln -sfn /opt/palera1n/palera1n /usr/local/bin/palera1n; " +
            "/opt/palera1n/palera1n --version 2>&1 | grep -m1 -Eo 'palera1n[ -][0-9][^ ]*' || true";

        var args = new List<string>
        {
            "-d", resolvedDistro,
            "-u", "root",
            "--",
            "bash", "-lc",
            bash,
        };

        return await ProcessRunner.RunAsync(
            "wsl.exe",
            args,
            cancellationToken: cancellationToken,
            onStdoutLine: onOutput,
            onStderrLine: line => onOutput?.Invoke(line)).ConfigureAwait(false);
    }

    /// <summary>
    /// Runs provision-wsl.sh for the given toolchain root. Streams each output
    /// line to <paramref name="onOutput"/>. Returns the WSL exit code.
    /// When <paramref name="preferBinaryPath"/> points to a downloaded release
    /// binary, it is installed over the bundled one after provisioning so the
    /// user's selected version survives a re-provision.
    /// </summary>
    public async Task<ProcessResult> ProvisionAsync(
        string toolchainRoot,
        string? distro = null,
        Action<string>? onOutput = null,
        CancellationToken cancellationToken = default,
        string? preferBinaryPath = null)
    {
        if (string.IsNullOrWhiteSpace(toolchainRoot) || !Directory.Exists(toolchainRoot))
        {
            throw new InvalidOperationException(
                "Toolchain root is not configured or does not exist. Set it in Settings first.");
        }

        var provisionScript = Path.Combine(toolchainRoot, "build", "provision-wsl.sh");
        var linuxBinary = Path.Combine(toolchainRoot, "dist", "palera1n-linux-x86_64");

        if (!File.Exists(provisionScript))
        {
            throw new InvalidOperationException($"provision-wsl.sh not found in toolchain:\n  {provisionScript}");
        }

        if (!File.Exists(linuxBinary))
        {
            throw new InvalidOperationException(
                "palera1n-linux-x86_64 not found in toolchain. Use the Versions tab to download it first:\n" +
                $"  expected: {linuxBinary}");
        }

        var resolvedDistro = distro;
        if (string.IsNullOrWhiteSpace(resolvedDistro))
        {
            resolvedDistro = await _wsl.ResolveDistroAsync(cancellationToken).ConfigureAwait(false);
        }

        if (string.IsNullOrWhiteSpace(resolvedDistro))
        {
            throw new InvalidOperationException(
                "No WSL distro detected. Install one first:  wsl --install -d Ubuntu  (admin), then re-run.");
        }

        var wslToolchain = ConvertToWslPath(toolchainRoot);
        onOutput?.Invoke($"Provisioning WSL distro '{resolvedDistro}' from {wslToolchain} ...");

        // provision-wsl.sh expects the repo path as $1 and reads its binary from
        // $REPO/dist/palera1n-linux-x86_64. We `tr -d '\r'` to strip CRLF so bash
        // does not choke on Windows line endings, then exec it with the repo path.
        var bashCommand =
            $"set -e; " +
            $"tr -d '\\r' < '{wslToolchain}/build/provision-wsl.sh' > /tmp/pln-prov.sh; " +
            $"bash /tmp/pln-prov.sh '{wslToolchain}'";

        var args = new List<string>
        {
            "-d", resolvedDistro,
            "-u", "root",
            "--",
            "bash", "-lc",
            bashCommand,
        };

        var provisionResult = await ProcessRunner.RunAsync(
            "wsl.exe",
            args,
            cancellationToken: cancellationToken,
            onStdoutLine: onOutput,
            onStderrLine: line => onOutput?.Invoke(line)).ConfigureAwait(false);

        // provision-wsl.sh always installs the bundled binary. If the user has
        // downloaded a specific release, re-apply it so their choice sticks.
        if (provisionResult.Succeeded
            && !string.IsNullOrWhiteSpace(preferBinaryPath)
            && File.Exists(preferBinaryPath))
        {
            await InstallPalera1nBinaryAsync(preferBinaryPath, resolvedDistro, onOutput, cancellationToken)
                .ConfigureAwait(false);
        }

        return provisionResult;
    }
}
