using System.Text.RegularExpressions;
using Palera1nWin.Core.Settings;
using Palera1nWin.Core.Util;

namespace Palera1nWin.Core.Services;

public enum UsbipdAttachState
{
    NotFound,
    NotShared,
    Shared,
    Attached,
    Error,
}

public sealed class UsbipdAppleDevice
{
    public required string BusId { get; init; }
    public required string VidPid { get; init; }
    public required UsbipdAttachState State { get; init; }
    public required string RawLine { get; init; }
}

public sealed class WslAttachResult
{
    public bool Succeeded { get; init; }
    public string? BusId { get; init; }
    public string Message { get; init; } = string.Empty;
    public bool UsbDkDetected { get; init; }
    public bool SeenInWsl { get; init; }
}

public sealed class AppleHostReleaseResult
{
    public bool Succeeded { get; init; }
    public string Message { get; init; } = string.Empty;
    public string? Detail { get; init; }
}

public sealed class UsbipdService
{
    public string? ExecutablePath { get; }

    public UsbipdService(string? executablePath = null) =>
        ExecutablePath = executablePath ?? ResolveExecutable();

    public bool IsAvailable => !string.IsNullOrWhiteSpace(ExecutablePath) && File.Exists(ExecutablePath);
    public string ListDevices() => Invoke("list");
    public bool DetectsUsbDkConflict() => ListDevices().Contains("UsbDk", StringComparison.OrdinalIgnoreCase);
    public string Bind(string busId, bool force = false) =>
        force ? Invoke("bind", "--busid", busId, "--force") : Invoke("bind", "--busid", busId);
    public string AttachWsl(string busId, string distro = "Ubuntu") =>
        Invoke("attach", "--wsl", distro, "--busid", busId);
    public string Detach(string busId) => Invoke("detach", "--busid", busId);
    public string Unbind(string busId) => Invoke("unbind", "--busid", busId);

    public string? FindAppleBusId() => FindAppleDevice()?.BusId;

    /// <summary>
    /// Returns a device only when selection is unambiguous. With no bus id, multiple
    /// connected Apple devices intentionally return null instead of selecting the first.
    /// </summary>
    public UsbipdAppleDevice? FindAppleDevice(string? busId = null, string? vidPid = null)
    {
        var devices = ParseAppleDevices(ListDevices());
        if (!string.IsNullOrWhiteSpace(busId))
            devices = devices.Where(device => string.Equals(device.BusId, busId, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (!string.IsNullOrWhiteSpace(vidPid))
            devices = devices.Where(device => string.Equals(device.VidPid, vidPid, StringComparison.OrdinalIgnoreCase)).ToArray();
        return devices.Count == 1 ? devices[0] : null;
    }

    public string DetachAll()
    {
        var devices = ParseAppleDevices(ListDevices());
        if (devices.Count > 1)
            return "Refusing DetachAll: multiple Apple devices are connected. Select an exact bus id.";
        return devices.Count == 1 ? Detach(devices[0].BusId) : string.Empty;
    }

    public AppleHostReleaseResult ReleaseAppleToHost(string? targetBusId = null)
    {
        if (!IsAvailable)
            return new AppleHostReleaseResult { Succeeded = true, Message = "usbipd-win is not installed; Windows already owns the device." };

        var beforeText = ListDevices();
        var all = ParseAppleDevices(beforeText);
        var targets = string.IsNullOrWhiteSpace(targetBusId)
            ? all
            : all.Where(device => string.Equals(device.BusId, targetBusId, StringComparison.OrdinalIgnoreCase)).ToArray();

        if (targets.Count == 0)
            return new AppleHostReleaseResult { Succeeded = true, Message = "The selected Apple USB device is not bound through usbipd.", Detail = beforeText };
        if (string.IsNullOrWhiteSpace(targetBusId) && targets.Count != 1)
            return new AppleHostReleaseResult
            {
                Succeeded = false,
                Message = $"Refusing to detach/unbind {targets.Count} Apple devices. Disconnect all but the target device.",
                Detail = beforeText,
            };

        var target = targets.Single();
        var messages = new List<string>();
        var detach = Detach(target.BusId);
        messages.Add($"detach {target.BusId}: {Truncate(detach)}");
        var unbind = Unbind(target.BusId);
        messages.Add($"unbind {target.BusId}: {Truncate(unbind)}");

        if ((IsAccessDenied(detach) || IsAccessDenied(unbind)) && !Elevation.IsAdmin() && ExecutablePath is not null)
        {
            messages.Add("usbipd requires Administrator; requesting elevation for the selected bus only.");
            _ = Elevation.RunElevatedWait(ExecutablePath, new[] { "detach", "--busid", target.BusId }, TimeSpan.FromSeconds(30));
            var elevated = Elevation.RunElevatedWait(ExecutablePath, new[] { "unbind", "--busid", target.BusId }, TimeSpan.FromSeconds(30));
            messages.Add(elevated ? "elevated unbind: ok" : "elevated unbind: failed or UAC cancelled");
        }

        var afterText = ListDevices();
        var after = ParseAppleDevices(afterText)
            .FirstOrDefault(device => string.Equals(device.BusId, target.BusId, StringComparison.OrdinalIgnoreCase));
        var stillBound = after?.State is UsbipdAttachState.Shared or UsbipdAttachState.Attached or UsbipdAttachState.Error;
        return new AppleHostReleaseResult
        {
            Succeeded = !stillBound,
            Message = stillBound
                ? $"Apple USB {target.BusId} is still owned by usbipd. Run Palera1nWin as Administrator.\n{string.Join(Environment.NewLine, messages)}"
                : $"Apple USB {target.BusId} released to Windows.\n{string.Join(Environment.NewLine, messages)}",
            Detail = afterText,
        };
    }

    /// <summary>
    /// Legacy bridge cleanup is restricted to a PID file created by this application.
    /// It never scans and kills unrelated PowerShell processes by command-line text.
    /// </summary>
    public static void KillLeftoverUsbBridges()
    {
        var pidPath = Path.Combine(AppSettings.RuntimeDirectory, "usb-bridge.pid");
        try
        {
            if (!File.Exists(pidPath)) return;
            var text = File.ReadAllText(pidPath).Trim();
            if (int.TryParse(text, out var pid))
            {
                try
                {
                    using var process = System.Diagnostics.Process.GetProcessById(pid);
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit(3000);
                }
                catch { }
            }
            File.Delete(pidPath);
        }
        catch { }
    }

    public async Task<WslAttachResult> EnsureAppleAttachedToWslAsync(
        string distro,
        WslService wsl,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default,
        TimeSpan? timeout = null,
        string? targetBusId = null)
    {
        if (!IsAvailable)
            return new WslAttachResult { Succeeded = false, Message = "usbipd-win is not installed or not on PATH." };

        var usbDk = DetectsUsbDkConflict();
        if (usbDk) progress?.Report("UsbDk filter detected. Uninstall it and reboot if attach fails.");
        await wsl.EnsureVhciModuleAsync(distro, cancellationToken).ConfigureAwait(false);

        var deadline = DateTimeOffset.UtcNow + (timeout ?? TimeSpan.FromMinutes(3));
        string? lastBusId = targetBusId;
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var devices = ParseAppleDevices(ListDevices());
            UsbipdAppleDevice? apple;
            if (!string.IsNullOrWhiteSpace(targetBusId))
            {
                apple = devices.FirstOrDefault(device => string.Equals(device.BusId, targetBusId, StringComparison.OrdinalIgnoreCase));
            }
            else if (devices.Count == 1)
            {
                apple = devices[0];
                targetBusId = apple.BusId;
            }
            else if (devices.Count > 1)
            {
                return new WslAttachResult
                {
                    Succeeded = false,
                    Message = $"Refusing usbipd attach: {devices.Count} Apple devices are connected. Disconnect all but the target.",
                    UsbDkDetected = usbDk,
                };
            }
            else
            {
                apple = null;
            }

            if (apple is null)
            {
                progress?.Report("Waiting for the selected Apple USB device to appear in usbipd...");
                await Task.Delay(1000, cancellationToken).ConfigureAwait(false);
                continue;
            }

            lastBusId = apple.BusId;
            progress?.Report($"Apple USB {apple.BusId} {apple.VidPid} state={apple.State}");
            if (apple.State == UsbipdAttachState.Attached &&
                await wsl.HasAppleUsbDeviceAsync(distro, cancellationToken).ConfigureAwait(false))
            {
                return Success(apple.BusId, usbDk);
            }

            if (apple.State == UsbipdAttachState.Attached)
            {
                Detach(apple.BusId);
                await Task.Delay(750, cancellationToken).ConfigureAwait(false);
            }

            var bindOutput = Bind(apple.BusId, force: true);
            progress?.Report(Truncate(bindOutput));
            if (IsAccessDenied(bindOutput))
                return new WslAttachResult
                {
                    Succeeded = false,
                    BusId = apple.BusId,
                    Message = "usbipd bind requires Administrator.",
                    UsbDkDetected = usbDk,
                };

            await Task.Delay(500, cancellationToken).ConfigureAwait(false);
            var attachOutput = AttachWsl(apple.BusId, distro);
            progress?.Report(Truncate(attachOutput));
            if (attachOutput.Contains("error state", StringComparison.OrdinalIgnoreCase) ||
                attachOutput.Contains("Device busy", StringComparison.OrdinalIgnoreCase) ||
                attachOutput.Contains("used by Windows", StringComparison.OrdinalIgnoreCase))
            {
                await Task.Delay(1500, cancellationToken).ConfigureAwait(false);
                continue;
            }

            var visibleDeadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(12);
            while (DateTimeOffset.UtcNow < visibleDeadline)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (await wsl.HasAppleUsbDeviceAsync(distro, cancellationToken).ConfigureAwait(false))
                    return Success(apple.BusId, usbDk);
                await Task.Delay(500, cancellationToken).ConfigureAwait(false);
            }
        }

        return new WslAttachResult
        {
            Succeeded = false,
            BusId = lastBusId,
            Message = "Timed out attaching the selected Apple USB device to WSL.",
            UsbDkDetected = usbDk,
        };

        static WslAttachResult Success(string busId, bool conflict) => new()
        {
            Succeeded = true,
            BusId = busId,
            Message = $"Apple USB {busId} attached and visible in WSL.",
            UsbDkDetected = conflict,
            SeenInWsl = true,
        };
    }

    public static string? ResolveExecutable()
    {
        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var segment in pathEnv.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var candidate = Path.Combine(segment, "usbipd.exe");
            if (File.Exists(candidate)) return candidate;
        }
        var fixedPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "usbipd-win", "usbipd.exe");
        return File.Exists(fixedPath) ? fixedPath : null;
    }

    internal static IReadOnlyList<UsbipdAppleDevice> ParseAppleDevices(string listOutput)
    {
        var devices = new List<UsbipdAppleDevice>();
        foreach (var line in listOutput.Split('\n', '\r'))
        {
            var trimmed = line.Trim();
            if (!trimmed.Contains("05ac:", StringComparison.OrdinalIgnoreCase)) continue;
            var parts = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length < 2 || !Regex.IsMatch(parts[0], @"^\d+-\d+(?:\.\d+)*$")) continue;
            devices.Add(new UsbipdAppleDevice
            {
                BusId = parts[0],
                VidPid = parts[1],
                State = ParseUsbipdState(trimmed),
                RawLine = trimmed,
            });
        }
        return devices;
    }

    internal static UsbipdAttachState ParseUsbipdState(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return UsbipdAttachState.NotFound;
        if (line.Contains("Not shared", StringComparison.OrdinalIgnoreCase)) return UsbipdAttachState.NotShared;
        if (line.Contains("Attached", StringComparison.OrdinalIgnoreCase)) return UsbipdAttachState.Attached;
        if (Regex.IsMatch(line, @"\bShared\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)) return UsbipdAttachState.Shared;
        if (line.Contains("error", StringComparison.OrdinalIgnoreCase)) return UsbipdAttachState.Error;
        return UsbipdAttachState.NotShared;
    }

    internal static IReadOnlyList<string> ParseBusIds(string listOutput) =>
        ParseAppleDevices(listOutput).Select(device => device.BusId).ToArray();

    private static bool IsAccessDenied(string output) =>
        output.Contains("Access denied", StringComparison.OrdinalIgnoreCase) ||
        output.Contains("administrator", StringComparison.OrdinalIgnoreCase);

    private static string Truncate(string? text, int max = 240)
    {
        var value = (text ?? string.Empty).Trim().Replace("\r", " ").Replace("\n", " ");
        return value.Length <= max ? value : value[..max] + "...";
    }

    private string Invoke(params string[] args)
    {
        if (!IsAvailable || ExecutablePath is null) return string.Empty;
        var result = ProcessRunner.Run(ExecutablePath, args, timeout: TimeSpan.FromSeconds(30));
        return result.CombinedOutput;
    }
}
