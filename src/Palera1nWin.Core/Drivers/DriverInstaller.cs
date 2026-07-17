using System.Runtime.InteropServices;
using Microsoft.Win32;
using Palera1nWin.Core.Models;
using Palera1nWin.Core.Settings;
using Palera1nWin.Core.Usb;
using Palera1nWin.Core.Util;

namespace Palera1nWin.Core.Drivers;

public enum DriverInstallResult
{
    AlreadyOk = 0,
    Installed = 1,
    NeedsManualZadig = 2,
    Failed = 3,
}

public sealed class DriverRestoreResult
{
    public bool Succeeded { get; init; }

    public int DevicesProcessed { get; init; }

    public int DevicesFailed { get; init; }

    public string Message { get; init; } = string.Empty;
}

/// <summary>
/// Installs libusbK for Apple DFU / Pongo (and Recovery if needed).
/// Primary path: libwdi wdi-simple (same engine as Zadig) — verified on Windows 11.
/// Unsigned SetupAPI INF installs fail with 0xE000022F on modern Windows; do not rely on them.
/// Manual Zadig is last-resort only after user confirmation in the UI.
/// </summary>
public sealed class DriverInstaller
{
    private static readonly ushort[] SupportedProductIds =
    {
        0x1227, 0x1280, 0x1281, 0x1282, 0x1283, 0x4141,
    };

    private readonly AppSettings _settings;
    private readonly AppleUsbMonitor _monitor;

    public DriverInstaller(AppSettings settings, AppleUsbMonitor? monitor = null)
    {
        _settings = settings;
        _monitor = monitor ?? new AppleUsbMonitor();
    }

    public async Task<DriverInstallResult> EnsureLibusbKAsync(
        ushort productId,
        IProgress<ProgressEventArgs>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!SupportedProductIds.Contains(productId))
        {
            progress?.Report(new ProgressEventArgs("driver", $"PID 0x{productId:X4} is not supported."));
            return DriverInstallResult.Failed;
        }

        var device = FindPresentDevice(productId);
        if (device is null)
        {
            progress?.Report(new ProgressEventArgs("driver", $"No present Apple USB device with PID 0x{productId:X4}."));
            return DriverInstallResult.Failed;
        }

        var service = DetectService(device);
        progress?.Report(new ProgressEventArgs("driver", $"Current driver service: {service ?? "(unknown)"}"));

        if (IsLibusbKService(service))
        {
            progress?.Report(new ProgressEventArgs("driver", "libusbK already active."));
            return DriverInstallResult.AlreadyOk;
        }

        if (IsUsbipdStubService(service))
        {
            progress?.Report(new ProgressEventArgs(
                "driver",
                $"DFU is on usbipd stub ({service}) — openra1n will crash. Replacing with libusbK..."));
        }

        if (!_settings.AutoInstallDrivers)
        {
            progress?.Report(new ProgressEventArgs("driver", "Auto-install disabled - open Zadig manually."));
            return DriverInstallResult.NeedsManualZadig;
        }

        var toolchain = Paths.ResolveToolchainRoot(_settings.ToolchainRoot);
        if (toolchain is null)
        {
            progress?.Report(new ProgressEventArgs("driver", "Toolchain root is not configured."));
            return DriverInstallResult.Failed;
        }

        // Primary automation: libwdi wdi-simple (live-tested: DFU 1227 WINUSB -> libusbK).
        var wdiSimple = Paths.GetWdiSimpleExecutable(toolchain);
        if (!File.Exists(wdiSimple))
        {
            progress?.Report(new ProgressEventArgs(
                "driver",
                "wdi-simple.exe missing from dist\\native. Run build\\build-wdi-simple.ps1 to build it."));
            return DriverInstallResult.NeedsManualZadig;
        }

        progress?.Report(new ProgressEventArgs(
            "driver",
            $"Installing libusbK via wdi-simple (automated Zadig) for PID 0x{productId:X4}..."));

        // wdi-simple often leaves WINUSB on the first try (Windows races the driver
        // assignment). Retry up to 3 times — same approach as the global watchdog.
        const int maxRetries = 3;
        for (var retry = 0; retry < maxRetries; retry++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (retry > 0)
            {
                progress?.Report(new ProgressEventArgs(
                    "driver",
                    $"wdi-simple retry {retry + 1}/{maxRetries} for PID 0x{productId:X4}..."));
            }

            var wdiOk = await TryWdiSimpleAsync(wdiSimple, productId, progress, cancellationToken)
                .ConfigureAwait(false);

            // Brief settle after install.
            await Task.Delay(1000, cancellationToken).ConfigureAwait(false);

            var afterService = ResolveServiceName(FindPresentDevice(productId)?.DeviceId ?? string.Empty);
            if (IsLibusbKService(afterService))
            {
                progress?.Report(new ProgressEventArgs(
                    "driver",
                    $"libusbK installed via wdi-simple (retry {retry + 1})."));
                return DriverInstallResult.Installed;
            }

            progress?.Report(new ProgressEventArgs(
                "driver",
                $"wdi-simple finished but service={afterService ?? "none"} (need libusbK)."));

            if (retry < maxRetries - 1)
            {
                await Task.Delay(500, cancellationToken).ConfigureAwait(false);
            }
        }

        progress?.Report(new ProgressEventArgs(
            "driver",
            $"wdi-simple did not leave libusbK active for PID 0x{productId:X4} after {maxRetries} attempts. Manual Zadig may be required."));
        return DriverInstallResult.NeedsManualZadig;
    }

    public void LaunchZadig(string toolchainRoot)
    {
        var zadig = Paths.GetZadigExecutable(toolchainRoot);
        if (File.Exists(zadig))
        {
            TryLaunchZadig(zadig);
        }
    }

    /// <summary>
    /// Fix Windows Drivers: removes libusbK / WinUSB / usbipd-stub from all present Apple USB
    /// devices and triggers a hardware re-scan so Windows re-installs the stock Apple driver
    /// (usbappl for Normal/Recovery, or the default USB composite for DFU/Pongo).
    /// This is the "restore to normal" cleanup after a jailbreak session.
    /// </summary>
    public async Task<DriverRestoreResult> RestoreAppleDriversAsync(
        IProgress<ProgressEventArgs>? progress = null,
        CancellationToken cancellationToken = default)
    {
        progress?.Report(new ProgressEventArgs("driver", "Scanning for Apple USB devices with non-Apple drivers..."));

        var devices = _monitor.ScanDevices()
            .Where(d => d.IsPresent && d.VendorId == 0x05AC)
            .ToList();

        if (devices.Count == 0)
        {
            progress?.Report(new ProgressEventArgs("driver", "No Apple USB devices present. Nothing to restore."));
            return new DriverRestoreResult
            {
                Succeeded = true,
                DevicesProcessed = 0,
                Message = "No Apple USB devices present.",
            };
        }

        var toReset = new List<AppleUsbDevice>();
        foreach (var device in devices)
        {
            var service = DetectService(device);
            if (IsNonAppleDriver(service))
            {
                toReset.Add(device);
                progress?.Report(new ProgressEventArgs(
                    "driver",
                    $"Found {device.Name} (PID 0x{device.ProductId:X4}) on driver '{service}' — will reset."));
            }
            else
            {
                progress?.Report(new ProgressEventArgs(
                    "driver",
                    $"{device.Name} (PID 0x{device.ProductId:X4}) already on '{service ?? "default"}' — skipping."));
            }
        }

        if (toReset.Count == 0)
        {
            progress?.Report(new ProgressEventArgs("driver", "All Apple devices already use default drivers. Nothing to fix."));
            return new DriverRestoreResult
            {
                Succeeded = true,
                DevicesProcessed = 0,
                Message = "All Apple devices already use default drivers.",
            };
        }

        var processed = 0;
        var failed = 0;

        foreach (var device in toReset)
        {
            cancellationToken.ThrowIfCancellationRequested();

            progress?.Report(new ProgressEventArgs(
                "driver",
                $"Removing non-Apple driver from {device.Name} (instance: {Truncate(device.DeviceId, 80)})..."));

            var removed = await RemoveDeviceNodeAsync(device.DeviceId, progress, cancellationToken)
                .ConfigureAwait(false);

            if (removed)
            {
                processed++;
                progress?.Report(new ProgressEventArgs(
                    "driver",
                    $"Device node removed for PID 0x{device.ProductId:X4}. Windows will re-detect it."));
            }
            else
            {
                failed++;
                progress?.Report(new ProgressEventArgs(
                    "driver",
                    $"Could not remove device node for PID 0x{device.ProductId:X4} (it may have already re-enumerated).",
                    isError: true));
            }
        }

        // Trigger a global hardware scan so Windows re-installs the default driver for all removed nodes.
        progress?.Report(new ProgressEventArgs("driver", "Triggering hardware re-scan (pnputil /scan-devices)..."));
        await ScanForHardwareChangesAsync(cancellationToken).ConfigureAwait(false);

        // Give Windows a moment to settle the re-enumeration.
        await Task.Delay(2500, cancellationToken).ConfigureAwait(false);

        // Verify: re-scan and check how many Apple devices are now on a default (non-jailbreak) driver.
        _monitor.PollNow();
        var postDevices = _monitor.ScanDevices().Where(d => d.IsPresent && d.VendorId == 0x05AC).ToList();
        var stillNonApple = postDevices.Count(d => IsNonAppleDriver(DetectService(d)));

        var ok = failed == 0 && stillNonApple == 0;
        var message = ok
            ? $"Restored {processed} Apple device(s) to default Windows drivers."
            : $"Processed {processed}, {failed} failed, {stillNonApple} still on non-Apple drivers. Reconnect the device or reboot if needed.";

        progress?.Report(new ProgressEventArgs("driver", message, isError: !ok));
        return new DriverRestoreResult
        {
            Succeeded = ok,
            DevicesProcessed = processed,
            DevicesFailed = failed,
            Message = message,
        };
    }

    /// <summary>
    /// Locate the UsbDk uninstaller from the Windows registry (Uninstall key).
    /// Returns (uninstallString, isQuiet) or null if UsbDk is not installed.
    /// </summary>
    public static string? FindUsbDkUninstaller()
    {
        const string displayName = "UsbDk";

        foreach (var hive in new[]
        {
                 @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
                 @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall",
             })
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(hive);
                if (key is null)
                {
                    continue;
                }

                foreach (var subName in key.GetSubKeyNames())
                {
                    using var subKey = key.OpenSubKey(subName);
                    if (subKey is null)
                    {
                        continue;
                    }

                    var name = subKey.GetValue("DisplayName") as string;
                    if (string.IsNullOrWhiteSpace(name) ||
                        !name.Contains(displayName, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var uninstall = subKey.GetValue("UninstallString") as string;
                    if (!string.IsNullOrWhiteSpace(uninstall))
                    {
                        return uninstall.Trim();
                    }

                    var quiet = subKey.GetValue("QuietUninstallString") as string;
                    if (!string.IsNullOrWhiteSpace(quiet))
                    {
                        return quiet.Trim();
                    }
                }
            }
            catch
            {
                // Registry access may fail in restricted environments.
            }
        }

        return null;
    }

    /// <summary>
    /// Uninstall UsbDk by running its registered uninstaller. Prefers the quiet variant.
    /// Requires elevation (UAC) if the app is not already admin.
    /// </summary>
    public async Task<bool> UninstallUsbDkAsync(
        IProgress<ProgressEventArgs>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var uninstaller = FindUsbDkUninstaller();
        if (string.IsNullOrWhiteSpace(uninstaller))
        {
            progress?.Report(new ProgressEventArgs("driver", "UsbDk is not installed (no uninstaller found in registry)."));
            return true;
        }

        progress?.Report(new ProgressEventArgs("driver", $"Found UsbDk uninstaller: {uninstaller}"));

        // Parse the uninstall string into (exe, args). It usually looks like:
        //   "C:\Program Files\UsbDk\uninstall.exe" /S   OR   MsiExec /X{GUID} /qn
        var (exe, args) = ParseUninstallString(uninstaller);

        // Append silent flag if the uninstaller is an exe (NSIS/Inno use /S or /SILENT).
        var finalArgs = args;
        if (!string.IsNullOrWhiteSpace(exe) &&
            exe.Contains("uninstall", StringComparison.OrdinalIgnoreCase) &&
            !finalArgs.Contains("/S", StringComparison.Ordinal) &&
            !finalArgs.Contains("/SILENT", StringComparison.OrdinalIgnoreCase))
        {
            finalArgs = string.IsNullOrWhiteSpace(finalArgs) ? "/S" : $"{finalArgs} /S";
        }

        progress?.Report(new ProgressEventArgs("driver", $"Running UsbDk uninstaller (elevated): {exe} {finalArgs}"));

        var elevated = Elevation.RunElevatedWait(
            exe,
            string.IsNullOrWhiteSpace(finalArgs) ? Array.Empty<string>() : finalArgs.Split(' ', StringSplitOptions.RemoveEmptyEntries),
            TimeSpan.FromMinutes(3));

        if (!elevated)
        {
            // The uninstaller may have succeeded even if RunElevatedWait returned false (e.g. UAC cancelled,
            // or the process returned non-zero). Check whether UsbDk is still present.
            await Task.Delay(1500, cancellationToken).ConfigureAwait(false);
            var stillThere = FindUsbDkUninstaller() is not null;
            if (!stillThere)
            {
                progress?.Report(new ProgressEventArgs("driver", "UsbDk uninstalled successfully."));
                return true;
            }

            progress?.Report(new ProgressEventArgs(
                "driver",
                "UsbDk uninstall failed or was cancelled. Try uninstalling it from Settings → Apps manually.",
                isError: true));
            return false;
        }

        progress?.Report(new ProgressEventArgs("driver", "UsbDk uninstalled successfully. A reboot is recommended."));
        return true;
    }

    private static (string exe, string args) ParseUninstallString(string uninstallString)
    {
        var trimmed = uninstallString.Trim();

        // Case: "C:\Path\To\uninstall.exe" /S
        if (trimmed.StartsWith('"'))
        {
            var close = trimmed.IndexOf('"', 1);
            if (close > 0)
            {
                var exe = trimmed[1..close];
                var args = trimmed[(close + 1)..].Trim();
                return (exe, args);
            }
        }

        // Case: C:\Path\To\uninstall.exe /S  (no quotes)
        var firstSpace = trimmed.IndexOf(' ');
        if (firstSpace > 0)
        {
            var exe = trimmed[..firstSpace];
            var args = trimmed[(firstSpace + 1)..].Trim();
            return (exe, args);
        }

        // Case: MsiExec /X{GUID}
        if (trimmed.StartsWith("MsiExec", StringComparison.OrdinalIgnoreCase))
        {
            var parts = trimmed.Split(' ', 2);
            var exe = parts[0];
            var args = parts.Length > 1 ? parts[1] : string.Empty;
            if (!args.Contains("/qn", StringComparison.Ordinal))
            {
                args = string.IsNullOrWhiteSpace(args) ? "/qn" : $"{args} /qn";
            }

            return (exe, args);
        }

        return (trimmed, string.Empty);
    }

    private static async Task<bool> RemoveDeviceNodeAsync(
        string deviceInstanceId,
        IProgress<ProgressEventArgs>? progress,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await ProcessRunner.RunAsync(
                "pnputil.exe",
                new[] { "/remove-device", deviceInstanceId },
                cancellationToken: cancellationToken,
                timeout: TimeSpan.FromMinutes(1)).ConfigureAwait(false);

            progress?.Report(new ProgressEventArgs(
                "driver",
                $"pnputil /remove-device exit={result.ExitCode}: {Truncate(result.CombinedOutput, 200)}"));

            return result.ExitCode == 0 ||
                   result.CombinedOutput.Contains("Successfully", StringComparison.OrdinalIgnoreCase) ||
                   result.CombinedOutput.Contains("removed", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            progress?.Report(new ProgressEventArgs("driver", $"pnputil /remove-device failed: {ex.Message}", isError: true));
            return false;
        }
    }

    private static async Task ScanForHardwareChangesAsync(CancellationToken cancellationToken)
    {
        try
        {
            await ProcessRunner.RunAsync(
                "pnputil.exe",
                new[] { "/scan-devices" },
                cancellationToken: cancellationToken,
                timeout: TimeSpan.FromMinutes(1)).ConfigureAwait(false);
        }
        catch
        {
            // Best effort — the re-scan is not critical; Windows will re-enumerate on its own.
        }
    }

    public static string? DetectService(AppleUsbDevice device)
    {
        var resolved = ResolveServiceName(device.DeviceId);
        if (!string.IsNullOrWhiteSpace(resolved))
        {
            return resolved;
        }

        if (!string.IsNullOrWhiteSpace(device.Service))
        {
            return device.Service;
        }

        return QueryServiceFromPnP(device.DeviceId);
    }

    /// <summary>
    /// Read the device's driver service from the PnP Enum registry.
    /// Win32_PnPEntity.Service is often empty on Win10/11 ("unknown" in the UI).
    /// </summary>
    public static string? ResolveServiceName(string deviceId)
    {
        if (string.IsNullOrWhiteSpace(deviceId))
        {
            return null;
        }

        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\" + deviceId);
            var service = key?.GetValue("Service") as string;
            if (!string.IsNullOrWhiteSpace(service))
            {
                return service;
            }
        }
        catch
        {
            // Access / missing key.
        }

        return QueryServiceFromPnP(deviceId);
    }

    /// <summary>
    /// One-shot silent libusbK install (for watchdog re-applies). Public so the watchdog can call it.
    /// </summary>
    public static Task<bool> RunWdiSimpleOnceAsync(
        string wdiSimplePath,
        ushort productId,
        CancellationToken cancellationToken = default)
        => TryWdiSimpleAsync(wdiSimplePath, productId, progress: null, cancellationToken);

    public static bool IsLibusbKService(string? service)
    {
        if (string.IsNullOrWhiteSpace(service))
        {
            return false;
        }

        return service.Contains("libusbK", StringComparison.OrdinalIgnoreCase) ||
               service.Contains("libusb0", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsWinUsbService(string? service)
    {
        return !string.IsNullOrWhiteSpace(service) &&
               service.Contains("WinUSB", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The default Apple driver service for Normal/Recovery modes (Apple Mobile Device USB Driver).
    /// </summary>
    public static bool IsAppleDefaultService(string? service)
    {
        if (string.IsNullOrWhiteSpace(service))
        {
            return false;
        }

        return service.Contains("usbappl", StringComparison.OrdinalIgnoreCase) ||
               service.Contains("AppleMobileDevice", StringComparison.OrdinalIgnoreCase) ||
               service.Contains("AMDSUSB", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Any driver that the jailbreak flow may have installed on an Apple device that is NOT
    /// the stock Apple driver. Used by Fix Windows Drivers to decide which devices to reset.
    /// </summary>
    public static bool IsNonAppleDriver(string? service)
    {
        if (string.IsNullOrWhiteSpace(service))
        {
            return false;
        }

        return IsLibusbKService(service) ||
               IsWinUsbService(service) ||
               IsUsbipdStubService(service) ||
               service.Contains("libusb", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// usbipd stub / filter drivers — openra1n cannot use these (host needs libusbK).
    /// </summary>
    public static bool IsUsbipdStubService(string? service)
    {
        if (string.IsNullOrWhiteSpace(service))
        {
            return false;
        }

        return service.Contains("VBoxUSB", StringComparison.OrdinalIgnoreCase) ||
               service.Contains("USBIP", StringComparison.OrdinalIgnoreCase) ||
               service.Contains("UsbDk", StringComparison.OrdinalIgnoreCase);
    }

    private AppleUsbDevice? FindPresentDevice(ushort productId)
    {
        return _monitor.ScanDevices()
            .FirstOrDefault(d => d.ProductId == productId && d.IsPresent);
    }

    private static string? QueryServiceFromPnP(string deviceId)
    {
        try
        {
            var escaped = deviceId.Replace("\\", "\\\\", StringComparison.Ordinal);
            var query = $"SELECT Service FROM Win32_PnPEntity WHERE DeviceID = '{escaped}'";
            using var searcher = new System.Management.ManagementObjectSearcher(query);
            foreach (var obj in searcher.Get())
            {
                return obj["Service"]?.ToString();
            }
        }
        catch
        {
            // Ignore WMI failures.
        }

        return null;
    }

    private static async Task<string?> EnsureLibusbKPackageAsync(
        string toolchainRoot,
        IProgress<ProgressEventArgs>? progress,
        CancellationToken cancellationToken)
    {
        var packageRoot = Paths.GetLibusbKInfDirectory(toolchainRoot);
        var sysPath = Directory.Exists(packageRoot)
            ? Directory.GetFiles(packageRoot, "libusbK.sys", SearchOption.AllDirectories)
                .FirstOrDefault(p => p.Contains("amd64", StringComparison.OrdinalIgnoreCase)
                                     || p.Contains("x64", StringComparison.OrdinalIgnoreCase))
            : null;

        if (sysPath is not null)
        {
            return Path.GetDirectoryName(sysPath);
        }

        var archivePath = Paths.GetLibusbKArchive(toolchainRoot);
        if (!File.Exists(archivePath))
        {
            progress?.Report(new ProgressEventArgs("driver", $"libusbK archive missing: {archivePath}"));
            return null;
        }

        Directory.CreateDirectory(packageRoot);
        progress?.Report(new ProgressEventArgs("driver", "Extracting libusbK package (via WSL 7z)..."));

        // Prefer WSL 7z (already used in this toolchain); fall back to host 7-Zip.
        var wslResult = await ProcessRunner.RunAsync(
            "wsl.exe",
            new[]
            {
                "-d", "Ubuntu", "--", "bash", "-lc",
                $"mkdir -p '{ToWslPath(packageRoot)}' && 7z x -y -o'{ToWslPath(packageRoot)}' '{ToWslPath(archivePath)}'",
            },
            cancellationToken: cancellationToken).ConfigureAwait(false);

        if (!wslResult.Succeeded)
        {
            var sevenZip = FindSevenZipExecutable();
            if (sevenZip is null)
            {
                progress?.Report(new ProgressEventArgs("driver", $"Extract failed: {wslResult.CombinedOutput}"));
                return null;
            }

            var extractResult = await ProcessRunner.RunAsync(
                sevenZip,
                new[] { "x", archivePath, $"-o{packageRoot}", "-y" },
                cancellationToken: cancellationToken).ConfigureAwait(false);

            if (!extractResult.Succeeded)
            {
                progress?.Report(new ProgressEventArgs("driver", $"7z extract failed: {extractResult.CombinedOutput}"));
                return null;
            }
        }

        sysPath = Directory.GetFiles(packageRoot, "libusbK.sys", SearchOption.AllDirectories)
            .FirstOrDefault(p => p.Contains("amd64", StringComparison.OrdinalIgnoreCase));
        return sysPath is null ? null : Path.GetDirectoryName(sysPath);
    }

    private static async Task<string> WriteDeviceInfAsync(
        string sysDirectory,
        ushort productId,
        CancellationToken cancellationToken)
    {
        var infPath = Path.Combine(sysDirectory, $"palera1nwin_libusbk_{productId:x4}.inf");
        var deviceName = productId switch
        {
            0x1227 => "Apple Mobile Device (DFU Mode)",
            0x4141 => "PongoOS USB Device",
            _ => $"Apple USB Device (PID_{productId:X4})",
        };

        // Minimal libusbK INF targeting one hardware ID. Unsigned; SetupAPI FORCE is required.
        var classGuid = "{ECFB0CEE-F82C-4943-83EA-1B9DFFC8C895}";
        var pid = productId.ToString("X4");
        var inf =
            "; Auto-generated by Palera1nWin - do not edit" + Environment.NewLine +
            "[Version]" + Environment.NewLine +
            "Signature = \"$Windows NT$\"" + Environment.NewLine +
            "Class = \"libusbK USB Devices\"" + Environment.NewLine +
            "ClassGuid = " + classGuid + Environment.NewLine +
            "Provider = %ProviderName%" + Environment.NewLine +
            "DriverVer = 07/17/2026,3.1.0.0" + Environment.NewLine +
            "CatalogFile =" + Environment.NewLine +
            Environment.NewLine +
            "[DestinationDirs]" + Environment.NewLine +
            "DefaultDestDir = 12" + Environment.NewLine +
            "libusbK.Files = 12" + Environment.NewLine +
            Environment.NewLine +
            "[Manufacturer]" + Environment.NewLine +
            "%ManufacturerName% = DeviceList,NTamd64" + Environment.NewLine +
            Environment.NewLine +
            "[DeviceList.NTamd64]" + Environment.NewLine +
            "%DeviceName% = libusbK_Install, USB\\VID_05AC&PID_" + pid + Environment.NewLine +
            Environment.NewLine +
            "[libusbK_Install.NT]" + Environment.NewLine +
            "CopyFiles = libusbK.Files" + Environment.NewLine +
            Environment.NewLine +
            "[libusbK_Install.NT.Services]" + Environment.NewLine +
            "AddService = libusbK, 0x00000002, libusbK_Service" + Environment.NewLine +
            Environment.NewLine +
            "[libusbK.Files]" + Environment.NewLine +
            "libusbK.sys" + Environment.NewLine +
            Environment.NewLine +
            "[libusbK_Service]" + Environment.NewLine +
            "DisplayName = %ServiceName%" + Environment.NewLine +
            "ServiceType = 1" + Environment.NewLine +
            "StartType = 3" + Environment.NewLine +
            "ErrorControl = 1" + Environment.NewLine +
            "ServiceBinary = %12%\\libusbK.sys" + Environment.NewLine +
            Environment.NewLine +
            "[SourceDisksNames]" + Environment.NewLine +
            "1 = %DiskName%,,," + Environment.NewLine +
            Environment.NewLine +
            "[SourceDisksFiles]" + Environment.NewLine +
            "libusbK.sys = 1" + Environment.NewLine +
            Environment.NewLine +
            "[Strings]" + Environment.NewLine +
            "ProviderName = \"libusbK\"" + Environment.NewLine +
            "ManufacturerName = \"libusbK\"" + Environment.NewLine +
            "DeviceName = \"" + deviceName + "\"" + Environment.NewLine +
            "ServiceName = \"libusbK\"" + Environment.NewLine +
            "DiskName = \"libusbK Driver Disk\"" + Environment.NewLine;

        await File.WriteAllTextAsync(infPath, inf, cancellationToken).ConfigureAwait(false);
        return infPath;
    }

    private static bool TryUpdateDriver(string hardwareId, string infPath, out string error)
    {
        error = string.Empty;
        try
        {
            var ok = UpdateDriverForPlugAndPlayDevices(
                IntPtr.Zero,
                hardwareId,
                infPath,
                INSTALLFLAG_FORCE | INSTALLFLAG_NONINTERACTIVE,
                out var reboot);

            if (!ok)
            {
                var code = Marshal.GetLastWin32Error();
                error = $"Win32={code}";
                return false;
            }

            if (reboot)
            {
                error = "reboot-required";
            }

            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private const uint INSTALLFLAG_FORCE = 0x00000001;
    private const uint INSTALLFLAG_NONINTERACTIVE = 0x00000004;

    [DllImport("newdev.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool UpdateDriverForPlugAndPlayDevices(
        IntPtr hwndParent,
        string hardwareId,
        string fullInfPath,
        uint installFlags,
        out bool bRebootRequired);

    private static string? FindSevenZipExecutable()
    {
        foreach (var candidate in new[]
                 {
                     @"C:\Program Files\7-Zip\7z.exe",
                     @"C:\Program Files (x86)\7-Zip\7z.exe",
                 })
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static string ToWslPath(string windowsPath)
    {
        var full = Path.GetFullPath(windowsPath);
        if (full.Length >= 3 && full[1] == ':')
        {
            var drive = char.ToLowerInvariant(full[0]);
            var rest = full[2..].Replace('\\', '/');
            return $"/mnt/{drive}{rest}";
        }

        return windowsPath.Replace('\\', '/');
    }

    private static async Task<bool> TryPnPUtilInstallAsync(
        string infPath,
        IProgress<ProgressEventArgs>? progress,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await ProcessRunner.RunAsync(
                "pnputil.exe",
                new[] { "/add-driver", infPath, "/install" },
                cancellationToken: cancellationToken,
                timeout: TimeSpan.FromMinutes(2)).ConfigureAwait(false);

            progress?.Report(new ProgressEventArgs(
                "driver",
                $"pnputil exit={result.ExitCode}: {Truncate(result.CombinedOutput, 200)}"));
            return result.ExitCode == 0;
        }
        catch (Exception ex)
        {
            progress?.Report(new ProgressEventArgs("driver", $"pnputil failed: {ex.Message}"));
            return false;
        }
    }

    private static async Task<bool> TryWdiSimpleAsync(
        string wdiSimplePath,
        ushort productId,
        IProgress<ProgressEventArgs>? progress,
        CancellationToken cancellationToken)
    {
        try
        {
            // -t 2 = libusbK, -s = silent, -o = wait for pending installs
            var name = productId switch
            {
                0x1227 => "Apple Mobile Device (DFU Mode)",
                0x4141 => "PongoOS USB Device",
                0x1281 => "Apple Mobile Device (Recovery Mode)",
                _ => $"Apple USB Device PID_{productId:X4}",
            };

            var destDir = Path.Combine(AppSettings.RuntimeDirectory, $"wdi-{productId:x4}");
            Directory.CreateDirectory(destDir);

            var args = new List<string>
            {
                "-n", name,
                "-v", "0x05AC",
                "-p", $"0x{productId:X4}",
                "-t", "2",
                "-d", destDir,
                "-s",
                "-o", "120000",
            };

            // Recovery composites often need interface 0.
            if (productId is >= 0x1280 and <= 0x1283)
            {
                args.Add("-i");
                args.Add("0");
            }

            var result = await ProcessRunner.RunAsync(
                wdiSimplePath,
                args,
                workingDirectory: Path.GetDirectoryName(wdiSimplePath),
                cancellationToken: cancellationToken,
                timeout: TimeSpan.FromMinutes(3)).ConfigureAwait(false);

            progress?.Report(new ProgressEventArgs(
                "driver",
                $"wdi-simple exit={result.ExitCode}: {Truncate(result.CombinedOutput, 240)}"));

            // libwdi returns 0 on WDI_SUCCESS. Also accept success if logs show install completed
            // (some Windows builds return non-zero while still swapping the driver).
            if (result.ExitCode == 0)
            {
                return true;
            }

            var combined = result.CombinedOutput;
            return combined.Contains("driver update completed", StringComparison.OrdinalIgnoreCase) ||
                   combined.Contains("Successfully signed file", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            progress?.Report(new ProgressEventArgs("driver", $"wdi-simple failed: {ex.Message}"));
            return false;
        }
    }

    private static string Truncate(string value, int max)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var trimmed = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return trimmed.Length <= max ? trimmed : trimmed[..max] + "...";
    }

    private static void TryLaunchZadig(string zadigPath)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = zadigPath,
                UseShellExecute = true,
            });
        }
        catch
        {
            // Best effort.
        }
    }

    private async Task<bool> WaitForStableLibusbKAsync(
        ushort productId,
        TimeSpan timeout,
        IProgress<ProgressEventArgs>? progress,
        CancellationToken cancellationToken)
    {
        const int requiredStableReads = 4;
        var stableCount = 0;
        var deadline = DateTimeOffset.UtcNow + timeout;

        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Delay(TimeSpan.FromMilliseconds(750), cancellationToken).ConfigureAwait(false);

            var device = FindPresentDevice(productId);
            if (device is null)
            {
                stableCount = 0;
                continue;
            }

            var service = DetectService(device);
            if (IsLibusbKService(service))
            {
                stableCount++;
                progress?.Report(new ProgressEventArgs(
                    "driver",
                    $"libusbK confirmed ({stableCount}/{requiredStableReads}).",
                    Math.Min(99, stableCount * 25)));
                if (stableCount >= requiredStableReads)
                {
                    return true;
                }
            }
            else
            {
                stableCount = 0;
                progress?.Report(new ProgressEventArgs("driver", $"Waiting for libusbK (service={service ?? "?"})..."));
            }
        }

        return false;
    }
}
