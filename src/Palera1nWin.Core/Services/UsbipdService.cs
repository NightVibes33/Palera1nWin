using System.Text.RegularExpressions;
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

    public UsbipdService(string? executablePath = null)
    {
        ExecutablePath = executablePath ?? ResolveExecutable();
    }

    public bool IsAvailable => !string.IsNullOrWhiteSpace(ExecutablePath) && File.Exists(ExecutablePath);

    public string ListDevices()
    {
        return Invoke("list");
    }

    public bool DetectsUsbDkConflict()
    {
        var output = ListDevices();
        return output.Contains("UsbDk", StringComparison.OrdinalIgnoreCase);
    }

    public string Bind(string busId, bool force = false)
    {
        return force
            ? Invoke("bind", "--busid", busId, "--force")
            : Invoke("bind", "--busid", busId);
    }

    public string AttachWsl(string busId, string distro = "Ubuntu")
    {
        return Invoke("attach", "--wsl", distro, "--busid", busId);
    }

    public string? FindAppleBusId()
    {
        return FindAppleDevice()?.BusId;
    }

    public UsbipdAppleDevice? FindAppleDevice()
    {
        return ParseAppleDevices(ListDevices()).FirstOrDefault();
    }

    public string Detach(string busId)
    {
        return Invoke("detach", "--busid", busId);
    }

    public string Unbind(string busId)
    {
        return Invoke("unbind", "--busid", busId);
    }

    public string DetachAll()
    {
        var output = ListDevices();
        var busIds = ParseBusIds(output);
        var combined = new List<string>();

        foreach (var busId in busIds)
        {
            combined.Add(Detach(busId));
        }

        return string.Join(Environment.NewLine, combined.Where(s => !string.IsNullOrWhiteSpace(s)));
    }

    /// <summary>
    /// Detach + unbind Apple USB so Windows owns the device again (libusbK/openra1n).
    /// Detach alone leaves the VBoxUSB stub bound — openra1n then crashes after YOLO.
    /// Unbind requires Administrator; elevates via UAC when needed.
    /// </summary>
    public AppleHostReleaseResult ReleaseAppleToHost()
    {
        var output = ListDevices();
        var apple = ParseAppleDevices(output).ToList();
        var messages = new List<string>();
        var busIds = apple.Select(d => d.BusId).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        if (busIds.Count == 0)
        {
            // Fallback: only bus IDs on lines that mention Apple VID.
            foreach (var line in output.Split('\n', '\r'))
            {
                if (!line.Contains("05ac:", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var parts = line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (parts.Length > 0 && parts[0].Contains('-', StringComparison.Ordinal))
                {
                    busIds.Add(parts[0]);
                }
            }

            busIds = busIds.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        if (busIds.Count == 0)
        {
            return new AppleHostReleaseResult
            {
                Succeeded = true,
                Message = "No Apple usbipd device to release.",
            };
        }

        var needsElevation = false;
        foreach (var busId in busIds)
        {
            var detachOut = Detach(busId);
            messages.Add($"detach {busId}: {Truncate(detachOut)}");
            if (IsAccessDenied(detachOut))
            {
                needsElevation = true;
            }

            var unbindOut = Unbind(busId);
            messages.Add($"unbind {busId}: {Truncate(unbindOut)}");
            if (IsAccessDenied(unbindOut))
            {
                needsElevation = true;
            }
            else
            {
                messages.Add($"released {busId}");
            }
        }

        if (needsElevation && !Elevation.IsAdmin() && ExecutablePath is not null)
        {
            messages.Add("usbipd detach/unbind needs Administrator — prompting UAC...");
            foreach (var busId in busIds)
            {
                Elevation.RunElevatedWait(ExecutablePath, new[] { "detach", "--busid", busId }, TimeSpan.FromSeconds(30));
                var ok = Elevation.RunElevatedWait(
                    ExecutablePath,
                    new[] { "unbind", "--busid", busId },
                    TimeSpan.FromSeconds(30));
                messages.Add(ok
                    ? $"elevated unbind {busId}: ok"
                    : $"elevated unbind {busId}: failed or UAC cancelled");
            }
        }
        else if (!Elevation.IsAdmin() && ExecutablePath is not null)
        {
            // Non-elevated path may return empty errors; if still Shared, force elevated unbind.
            var mid = ListDevices();
            var stillBound = ParseAppleDevices(mid)
                .Any(d => d.State is UsbipdAttachState.Shared or UsbipdAttachState.Attached or UsbipdAttachState.Error);
            if (stillBound)
            {
                messages.Add("Apple still Shared/Attached — prompting UAC for usbipd unbind...");
                foreach (var busId in busIds)
                {
                    Elevation.RunElevatedWait(ExecutablePath, new[] { "detach", "--busid", busId }, TimeSpan.FromSeconds(30));
                    var ok = Elevation.RunElevatedWait(
                        ExecutablePath,
                        new[] { "unbind", "--busid", busId },
                        TimeSpan.FromSeconds(30));
                    messages.Add(ok
                        ? $"elevated unbind {busId}: ok"
                        : $"elevated unbind {busId}: failed or UAC cancelled");
                }
            }
        }

        // Verify: only fail if usbipd still shows Shared/Attached/Error.
        // Also treat PnP VBoxUSB as still-bound even if list parsing is stale.
        var after = ListDevices();
        var appleAfter = ParseAppleDevices(after);
        var stillSharedOrAttached = appleAfter
            .Any(d => d.State is UsbipdAttachState.Shared or UsbipdAttachState.Attached or UsbipdAttachState.Error);

        if (stillSharedOrAttached)
        {
            return new AppleHostReleaseResult
            {
                Succeeded = false,
                Message =
                    "Apple USB is still Shared/Attached under usbipd (VBoxUSB stub). " +
                    "Relaunch Palera1nWin as Administrator, then retry." +
                    Environment.NewLine +
                    string.Join(Environment.NewLine, messages) +
                    Environment.NewLine +
                    string.Join(Environment.NewLine, appleAfter.Select(d => d.RawLine)),
                Detail = after,
            };
        }

        return new AppleHostReleaseResult
        {
            Succeeded = true,
            Message = string.Join(Environment.NewLine, messages.Append($"usbipd Apple state OK ({appleAfter.Count} device(s))")),
            Detail = after,
        };
    }

    private static bool IsAccessDenied(string output) =>
        output.Contains("Access denied", StringComparison.OrdinalIgnoreCase) ||
        output.Contains("administrator", StringComparison.OrdinalIgnoreCase);

    private static string Truncate(string text, int max = 160)
    {
        var t = text.Trim().Replace("\r", " ").Replace("\n", " ");
        return t.Length <= max ? t : t[..max] + "...";
    }

    public static void KillLeftoverUsbBridges()
    {
        try
        {
            using var searcher = new System.Management.ManagementObjectSearcher(
                "SELECT ProcessId, CommandLine FROM Win32_Process WHERE Name='powershell.exe' OR Name='pwsh.exe'");
            foreach (var obj in searcher.Get())
            {
                var cmd = obj["CommandLine"]?.ToString() ?? string.Empty;
                if (!cmd.Contains("usb-bridge.ps1", StringComparison.OrdinalIgnoreCase) &&
                    !cmd.Contains("pln-bridge", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var pid = Convert.ToInt32(obj["ProcessId"]);
                try
                {
                    System.Diagnostics.Process.GetProcessById(pid).Kill(entireProcessTree: true);
                }
                catch
                {
                    // Ignore.
                }
            }
        }
        catch
        {
            // Best effort.
        }
    }

    /// <summary>
    /// Force-bind + attach Apple USB into WSL and verify it appears in lsusb.
    /// This is what unblocks palera1n "Waiting for devices" on Windows.
    /// </summary>
    public async Task<WslAttachResult> EnsureAppleAttachedToWslAsync(
        string distro,
        WslService wsl,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default,
        TimeSpan? timeout = null)
    {
        if (!IsAvailable)
        {
            return new WslAttachResult
            {
                Succeeded = false,
                Message = "usbipd-win is not installed or not on PATH.",
            };
        }

        var usbDk = DetectsUsbDkConflict();
        if (usbDk)
        {
            progress?.Report(
                "UsbDk filter detected - usbipd requires bind --force. Uninstall UsbDk if attach keeps failing.");
        }

        await wsl.EnsureVhciModuleAsync(distro, cancellationToken).ConfigureAwait(false);

        var deadline = DateTimeOffset.UtcNow + (timeout ?? TimeSpan.FromMinutes(3));
        string? lastBusId = null;
        var attempt = 0;

        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            attempt++;

            var apple = FindAppleDevice();
            if (apple is null)
            {
                progress?.Report("No Apple USB device in usbipd list. Plug the phone in (Recovery/DFU/Normal).");
                await Task.Delay(1500, cancellationToken).ConfigureAwait(false);
                continue;
            }

            lastBusId = apple.BusId;
            progress?.Report($"Apple USB {apple.BusId} {apple.VidPid} state={apple.State}");

            if (apple.State == UsbipdAttachState.Attached)
            {
                if (await wsl.HasAppleUsbDeviceAsync(distro, cancellationToken).ConfigureAwait(false))
                {
                    return new WslAttachResult
                    {
                        Succeeded = true,
                        BusId = apple.BusId,
                        Message = $"Apple USB {apple.BusId} attached and visible in WSL.",
                        UsbDkDetected = usbDk,
                        SeenInWsl = true,
                    };
                }

                progress?.Report(
                    $"usbipd says Attached but WSL lsusb has no 05ac - detaching and retrying (attempt {attempt})...");
                Detach(apple.BusId);
                await Task.Delay(1000, cancellationToken).ConfigureAwait(false);
            }

            // Always force-bind when UsbDk is present; also force on retries after Shared-but-not-attached.
            progress?.Report($"usbipd bind --force --busid {apple.BusId}");
            var bindOut = Bind(apple.BusId, force: true);
            if (!string.IsNullOrWhiteSpace(bindOut))
            {
                progress?.Report(bindOut.Trim());
            }

            if (bindOut.Contains("Access denied", StringComparison.OrdinalIgnoreCase) ||
                bindOut.Contains("administrator", StringComparison.OrdinalIgnoreCase))
            {
                return new WslAttachResult
                {
                    Succeeded = false,
                    BusId = apple.BusId,
                    Message =
                        "usbipd bind requires Administrator. Relaunch Palera1nWin as admin, then retry.",
                    UsbDkDetected = usbDk,
                };
            }

            await Task.Delay(800, cancellationToken).ConfigureAwait(false);

            progress?.Report($"usbipd attach --wsl {distro} --busid {apple.BusId}");
            var attachOut = AttachWsl(apple.BusId, distro);
            if (!string.IsNullOrWhiteSpace(attachOut))
            {
                progress?.Report(attachOut.Trim());
            }

            if (attachOut.Contains("error state", StringComparison.OrdinalIgnoreCase) ||
                attachOut.Contains("Device busy", StringComparison.OrdinalIgnoreCase) ||
                attachOut.Contains("used by Windows", StringComparison.OrdinalIgnoreCase))
            {
                progress?.Report(
                    "Attach failed (error/busy). UNPLUG the Lightning cable, wait 2s, PLUG back in. " +
                    "If this repeats, uninstall UsbDk and reboot.");
                await Task.Delay(2500, cancellationToken).ConfigureAwait(false);
                continue;
            }

            // Wait for WSL visibility after attach / re-enumeration.
            var visibleDeadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(12);
            while (DateTimeOffset.UtcNow < visibleDeadline)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (await wsl.HasAppleUsbDeviceAsync(distro, cancellationToken).ConfigureAwait(false))
                {
                    return new WslAttachResult
                    {
                        Succeeded = true,
                        BusId = apple.BusId,
                        Message = $"Apple USB {apple.BusId} attached and visible in WSL.",
                        UsbDkDetected = usbDk,
                        SeenInWsl = true,
                    };
                }

                await Task.Delay(750, cancellationToken).ConfigureAwait(false);
            }

            progress?.Report(
                "Still not visible in WSL after attach. Unplug/replug now so the stub driver re-enumerates.");
            await Task.Delay(2000, cancellationToken).ConfigureAwait(false);
        }

        return new WslAttachResult
        {
            Succeeded = false,
            BusId = lastBusId,
            Message =
                "Timed out attaching Apple USB to WSL. Uninstall UsbDk if present, run as Administrator, " +
                "then unplug/replug and retry.",
            UsbDkDetected = usbDk,
            SeenInWsl = false,
        };
    }

    public static string? ResolveExecutable()
    {
        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var segment in pathEnv.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var candidate = Path.Combine(segment, "usbipd.exe");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        var fixedPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "usbipd-win", "usbipd.exe");
        if (File.Exists(fixedPath))
        {
            return fixedPath;
        }

        return null;
    }

    internal static IReadOnlyList<UsbipdAppleDevice> ParseAppleDevices(string listOutput)
    {
        var devices = new List<UsbipdAppleDevice>();
        foreach (var line in listOutput.Split('\n', '\r'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0)
            {
                continue;
            }

            if (!trimmed.Contains("05ac:", StringComparison.OrdinalIgnoreCase) &&
                !trimmed.Contains("05AC:", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var parts = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length < 2 || !parts[0].Contains('-', StringComparison.Ordinal))
            {
                continue;
            }

            var state = ParseUsbipdState(trimmed);

            devices.Add(new UsbipdAppleDevice
            {
                BusId = parts[0],
                VidPid = parts[1],
                State = state,
                RawLine = trimmed,
            });
        }

        return devices;
    }

    /// <summary>
    /// usbipd STATE column. Must check "Not shared" before "Shared" — otherwise
    /// "Not shared" is misclassified as Shared (substring match).
    /// </summary>
    internal static UsbipdAttachState ParseUsbipdState(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return UsbipdAttachState.NotFound;
        }

        if (line.Contains("Not shared", StringComparison.OrdinalIgnoreCase))
        {
            return UsbipdAttachState.NotShared;
        }

        if (line.Contains("Attached", StringComparison.OrdinalIgnoreCase))
        {
            return UsbipdAttachState.Attached;
        }

        // Match whole word Shared (STATE column), not substrings of other words.
        if (Regex.IsMatch(line, @"\bShared\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            return UsbipdAttachState.Shared;
        }

        if (line.Contains("error", StringComparison.OrdinalIgnoreCase))
        {
            return UsbipdAttachState.Error;
        }

        return UsbipdAttachState.NotShared;
    }

    internal static IReadOnlyList<string> ParseBusIds(string listOutput)
    {
        var ids = new List<string>();
        foreach (var line in listOutput.Split('\n', '\r'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0)
            {
                continue;
            }

            var parts = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length > 0 && parts[0].Contains('-', StringComparison.Ordinal))
            {
                ids.Add(parts[0]);
            }
        }

        return ids;
    }

    private string Invoke(params string[] args)
    {
        if (!IsAvailable || ExecutablePath is null)
        {
            return string.Empty;
        }

        try
        {
            var result = ProcessRunner.RunAsync(ExecutablePath, args).GetAwaiter().GetResult();
            return result.CombinedOutput;
        }
        catch
        {
            return string.Empty;
        }
    }
}
