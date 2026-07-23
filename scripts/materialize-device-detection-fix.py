from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def replace(path: Path, old: str, new: str, count: int = 1, label: str = "replacement") -> None:
    text = path.read_text(encoding="utf-8-sig")
    actual = text.count(old)
    if actual != count:
        raise SystemExit(f"{path}: {label}: expected {count}, found {actual}")
    path.write_text(text.replace(old, new, count), encoding="utf-8", newline="\n")


# 1. Shared jailbreak/device-tab PID mapping and presence handling.
model = ROOT / "src/Palera1nWin.Core/Models/AppleUsbDevice.cs"
replace(
    model,
    '''    public bool IsPresent =>
        !string.IsNullOrWhiteSpace(DeviceId) &&
        !string.Equals(Status, "Unknown", StringComparison.OrdinalIgnoreCase) &&
        // Pongo often enumerates as Status=Error until libusbK/WinUSB binds — still real hardware.
        (ProductId == 0x4141 ||
         !string.Equals(Status, "Error", StringComparison.OrdinalIgnoreCase));''',
    '''    public bool IsPresent =>
        !string.IsNullOrWhiteSpace(DeviceId) &&
        (Mode is DeviceMode.Normal or DeviceMode.Recovery or DeviceMode.Dfu or
             DeviceMode.YoloDfu or DeviceMode.PwnedDfu or DeviceMode.Pongo ||
         (!string.Equals(Status, "Unknown", StringComparison.OrdinalIgnoreCase) &&
          !string.Equals(Status, "Error", StringComparison.OrdinalIgnoreCase)));''',
    label="known Apple modes remain present during transient driver status"
)
replace(
    model,
    '''        if (!string.IsNullOrWhiteSpace(status) &&
            !string.Equals(status, "OK", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(status, "Present", StringComparison.OrdinalIgnoreCase))
        {
            return DeviceMode.Busy;
        }

        return productId switch
        {
            0x12A8 or 0x12AB or 0x12A0 => DeviceMode.Normal,
            0x1280 or 0x1281 or 0x1282 or 0x1283 => DeviceMode.Recovery,
            0x1227 or 0x1222 => DeviceMode.Dfu,
            0x4141 => DeviceMode.Pongo,
            _ => DeviceMode.None,
        };''',
    '''        // A known Apple PID determines the mode even while Windows reports a
        // transient Error/Unknown driver state during USB re-enumeration.
        if (productId is 0x1227 or 0x1222) return DeviceMode.Dfu;
        if (productId is 0x1280 or 0x1281 or 0x1282 or 0x1283) return DeviceMode.Recovery;
        if (productId >= 0x12A0 && productId <= 0x12AF) return DeviceMode.Normal;

        if (!string.IsNullOrWhiteSpace(status) &&
            !string.Equals(status, "OK", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(status, "Present", StringComparison.OrdinalIgnoreCase))
        {
            return DeviceMode.Busy;
        }

        return DeviceMode.None;''',
    label="map all normal-mode Apple PIDs before status"
)

monitor = ROOT / "src/Palera1nWin.Core/Usb/AppleUsbMonitor.cs"
replace(
    monitor,
    '''        var devices = ScanPnPDevices().ToList();
        MergeUsbipdBusIds(devices);
        return devices
            .GroupBy(d => d.DeviceId, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .OrderByDescending(ScoreDevice)
            .ThenBy(d => d.DeviceId, StringComparer.OrdinalIgnoreCase)
            .ToList();''',
    '''        // Normal iOS exposes several MI_* composite interfaces for one physical
        // device. Collapse those interfaces before the exact-device count and before
        // associating the single usbipd bus ID.
        var devices = CollapsePhysicalInterfaces(ScanPnPDevices());
        MergeUsbipdBusIds(devices);
        return devices
            .OrderByDescending(ScoreDevice)
            .ThenBy(d => d.DeviceId, StringComparer.OrdinalIgnoreCase)
            .ToList();''',
    label="collapse composite interfaces"
)
replace(
    monitor,
    '''    private void PollSafe(bool waitForTurn)
''',
    '''    internal static List<AppleUsbDevice> CollapsePhysicalInterfaces(IEnumerable<AppleUsbDevice> source) =>
        source
            .GroupBy(device => (device.VendorId, device.ProductId, device.Mode))
            .Select(group => group
                .OrderByDescending(device => device.IsPresent)
                .ThenByDescending(device => !string.IsNullOrWhiteSpace(device.Service))
                .ThenBy(device => device.DeviceId, StringComparer.OrdinalIgnoreCase)
                .First())
            .ToList();

    private void PollSafe(bool waitForTurn)
''',
    label="insert composite interface helper"
)

# 2. DarkSword exact monitor: ContainerId dedupe and decimal UniqueChipID normalization.
dark_monitor = ROOT / "DarkSwordRestore/src/DarkSwordRestore.Core/AppleDeviceMonitor.cs"
replace(
    dark_monitor,
    '''using System.Diagnostics;
using System.Text.Json;''',
    '''using System.Diagnostics;
using System.Globalization;
using System.Text.Json;''',
    label="add globalization"
)
replace(
    dark_monitor,
    '''    private static readonly Regex EcidPattern = new(
        @"(?im)^\\s*(?:ECID|UniqueChipID)\\s*:\\s*(?<value>(?:0x)?[0-9a-f]+)\\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);''',
    '''    private static readonly Regex EcidPattern = new(
        @"(?im)^\\s*(?:ECID|UniqueChipID)\\s*:\\s*(?<value>(?:0x)?[0-9a-f]+)\\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex NormalPidPattern = new(
        @"PID_12A[0-9A-F]",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);''',
    label="add normal PID pattern"
)
replace(
    dark_monitor,
    '''                "$h=(Get-PnpDeviceProperty -InstanceId $_.InstanceId -KeyName 'DEVPKEY_Device_HardwareIds' -ErrorAction SilentlyContinue).Data;" +
                "[pscustomobject]@{FriendlyName=$_.FriendlyName;InstanceId=$_.InstanceId;Service=$_.Service;HardwareIds=($h -join ';')}};" +''',
    '''                "$h=(Get-PnpDeviceProperty -InstanceId $_.InstanceId -KeyName 'DEVPKEY_Device_HardwareIds' -ErrorAction SilentlyContinue).Data;" +
                "$c=(Get-PnpDeviceProperty -InstanceId $_.InstanceId -KeyName 'DEVPKEY_Device_ContainerId' -ErrorAction SilentlyContinue).Data;" +
                "[pscustomobject]@{FriendlyName=$_.FriendlyName;InstanceId=$_.InstanceId;Service=$_.Service;HardwareIds=($h -join ';');ContainerId=($c -join ';')}};" +''',
    label="query PnP container ID"
)
replace(
    dark_monitor,
    '''            var devices = items.Select(Parse)
                .Where(snapshot => snapshot.Mode != AppleDeviceMode.Unknown)
                .OrderByDescending(snapshot => Priority(snapshot.Mode))
                .ThenBy(snapshot => snapshot.InstanceId, StringComparer.OrdinalIgnoreCase)
                .ToArray();''',
    '''            var devices = items
                .GroupBy(PhysicalPnpKey, StringComparer.OrdinalIgnoreCase)
                .Select(group => group
                    .OrderByDescending(item => Priority(Parse(item).Mode))
                    .First())
                .Select(Parse)
                .Where(snapshot => snapshot.Mode != AppleDeviceMode.Unknown)
                .OrderByDescending(snapshot => Priority(snapshot.Mode))
                .ThenBy(snapshot => snapshot.InstanceId, StringComparer.OrdinalIgnoreCase)
                .ToArray();''',
    label="dedupe physical PnP containers"
)
replace(
    dark_monitor,
    '''                Ecid = ecid.Success ? AppleDeviceSnapshot.NormalizeEcid(ecid.Value) : snapshot.Ecid,''',
    '''                Ecid = ecid.Success ? NormalizeIDeviceInfoEcid(ecid.Value) : snapshot.Ecid,''',
    label="normalize normal-mode decimal ECID"
)
replace(
    dark_monitor,
    '''            var text when text.Contains("PID_12A8") || text.Contains("PID_12AA") || text.Contains("PID_12AB") || text.Contains("PID_12A0") => AppleDeviceMode.Normal,''',
    '''            var text when NormalPidPattern.IsMatch(text) => AppleDeviceMode.Normal,''',
    label="recognize all normal PIDs"
)
replace(
    dark_monitor,
    '''    private static AppleDeviceSnapshot Parse(JsonElement item)
''',
    '''    private static string PhysicalPnpKey(JsonElement item)
    {
        var container = Get(item, "ContainerId");
        return !string.IsNullOrWhiteSpace(container)
            ? "container:" + container.Trim().ToUpperInvariant()
            : "instance:" + (Get(item, "InstanceId") ?? Guid.NewGuid().ToString("N"));
    }

    internal static string? NormalizeIDeviceInfoEcid(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var trimmed = value.Trim();
        if (trimmed.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return AppleDeviceSnapshot.NormalizeEcid(trimmed);
        if (trimmed.All(char.IsDigit) &&
            ulong.TryParse(trimmed, NumberStyles.None, CultureInfo.InvariantCulture, out var decimalValue))
            return decimalValue.ToString("X", CultureInfo.InvariantCulture);
        return AppleDeviceSnapshot.NormalizeEcid(trimmed);
    }

    private static AppleDeviceSnapshot Parse(JsonElement item)
''',
    label="insert container and ECID helpers"
)

# 3. Expose shared monitor/settings to the integrated downgrade page.
main_vm = ROOT / "src/Palera1nWin.App/ViewModels/MainViewModel.cs"
replace(
    main_vm,
    '''    public HardwareOperationCoordinator HardwareOperations => _hardwareOperations;
''',
    '''    public HardwareOperationCoordinator HardwareOperations => _hardwareOperations;
    public AppleUsbMonitor UsbMonitor => _monitor;
    public AppSettings Settings => _settings;
''',
    label="expose shared hardware services"
)

# 4. Guaranteed shared Logs-tab routing and temporary driver watchdog support.
downgrade = ROOT / "src/Palera1nWin.App/Views/DowngradeView.xaml.cs"
replace(
    downgrade,
    '''using Palera1nWin.App.ViewModels;
''',
    '''using Palera1nWin.App.ViewModels;
using Palera1nWin.Core.Drivers;
''',
    label="add watchdog namespace"
)
replace(
    downgrade,
    '''    private bool _disposed;
''',
    '''    private bool _disposed;
    private MainViewModel? _shell;
    private LibusbKWatchdog? _downgradeDriverWatch;
''',
    label="add shared shell and watchdog fields"
)
replace(
    downgrade,
    '''        Loaded += DowngradeView_Loaded;
    }

    private MainViewModel? Shell => DataContext as MainViewModel;''',
    '''        DataContextChanged += (_, args) => _shell = args.NewValue as MainViewModel;
        Loaded += DowngradeView_Loaded;
    }

    private MainViewModel? Shell => _shell ?? DataContext as MainViewModel;''',
    label="capture shell data context"
)
replace(
    downgrade,
    '''    private void AppendLog(string line)
    {
        var formatted = $"[{DateTimeOffset.Now:O}] {line}";
        lock (_logLock)
        {
            File.AppendAllText(_logPath, formatted + Environment.NewLine);
        }

        Dispatcher.BeginInvoke(() =>
        {
            LogBox.AppendText(formatted + Environment.NewLine);
            LogBox.ScrollToEnd();
            Shell?.AppendLog("darksword", line, line.Contains("error", StringComparison.OrdinalIgnoreCase));
        });
    }''',
    '''    private void AppendLog(string line)
    {
        var formatted = $"[{DateTimeOffset.Now:O}] {line}";
        lock (_logLock)
        {
            File.AppendAllText(_logPath, formatted + Environment.NewLine);
        }

        var isError = line.Contains("error", StringComparison.OrdinalIgnoreCase) ||
                      line.Contains("failed", StringComparison.OrdinalIgnoreCase) ||
                      line.Contains("exception", StringComparison.OrdinalIgnoreCase) ||
                      line.Contains("blocked", StringComparison.OrdinalIgnoreCase) ||
                      line.Contains("stop", StringComparison.OrdinalIgnoreCase);
        _shell?.AppendLog("downgrade", line, isError);

        Dispatcher.BeginInvoke(() =>
        {
            LogBox.AppendText(formatted + Environment.NewLine);
            LogBox.ScrollToEnd();
        });
    }''',
    label="route downgrade logs immediately to shared service"
)
replace(
    downgrade,
    '''    private void SetShellStatus(string text) => Shell?.SetStatusText(text);
''',
    '''    private void StartDowngradeDriverWatch()
    {
        StopDowngradeDriverWatch();
        if (Shell is null) return;
        _downgradeDriverWatch = new LibusbKWatchdog(Shell.UsbMonitor, Shell.Settings);
        _downgradeDriverWatch.LogReceived += DowngradeDriverWatch_LogReceived;
        _downgradeDriverWatch.Start();
    }

    private void StopDowngradeDriverWatch()
    {
        if (_downgradeDriverWatch is null) return;
        _downgradeDriverWatch.LogReceived -= DowngradeDriverWatch_LogReceived;
        _downgradeDriverWatch.Dispose();
        _downgradeDriverWatch = null;
    }

    private void DowngradeDriverWatch_LogReceived(object? sender, Palera1nWin.Core.Models.LogLine line) =>
        AppendLog($"[{line.Source}] {line.Message}");

    private void SetShellStatus(string text) => Shell?.SetStatusText(text);
''',
    label="add downgrade driver watchdog"
)
replace(
    downgrade,
    '''        _operationCts = null;
        _monitor.DeviceChanged -= Monitor_DeviceChanged;''',
    '''        _operationCts = null;
        StopDowngradeDriverWatch();
        _monitor.DeviceChanged -= Monitor_DeviceChanged;''',
    label="dispose downgrade watchdog"
)

# 5. Automatic DFU guidance before every visible test and restore stage.
exact = ROOT / "src/Palera1nWin.App/Views/DowngradeView.ExactIdentity.cs"
replace(
    exact,
    '''        SetBusy(true, "Test exact DFU → PongoOS", "No firmware is erased. The DFU ProductType and ECID are saved only after the complete Pongo bridge test passes.");
        try
        {
            var identityTask = _monitor.WaitForModeAsync(''',
    '''        SetBusy(true, "Test exact DFU → PongoOS", "No firmware is erased. The DFU ProductType and ECID are saved only after the complete Pongo bridge test passes.");
        StartDowngradeDriverWatch();
        try
        {
            await EnsureCleanDfuWithGuidanceAsync("Test DFU → Pwned/Pongo", _operationCts.Token);
            var identityTask = _monitor.WaitForModeAsync(''',
    label="guide exact hardware test into DFU"
)
replace(
    exact,
    '''        finally
        {
            await lease.DisposeAsync();
            _operationCts?.Dispose();''',
    '''        finally
        {
            StopDowngradeDriverWatch();
            await lease.DisposeAsync();
            _operationCts?.Dispose();''',
    count=1,
    label="stop exact hardware watchdog"
)

simple = ROOT / "src/Palera1nWin.App/Views/DowngradeView.SimpleMode.cs"
replace(
    simple,
    '''            SetBusy(true, "Start Downgrade", "Enter clean DFU on the validated device. The app will continue automatically.");
            try
            {
                var session = await _orchestrator.RunFullDowngradeAsync(''',
    '''            SetBusy(true, "Start Downgrade", "Enter clean DFU on the validated device. The app will continue automatically.");
            StartDowngradeDriverWatch();
            try
            {
                await EnsureCleanDfuWithGuidanceAsync("Start Downgrade", _operationCts.Token);
                var session = await _orchestrator.RunFullDowngradeAsync(''',
    label="guide destructive restore into DFU"
)
replace(
    simple,
    '''            finally
            {
                await lease.DisposeAsync();
                _operationCts?.Dispose();''',
    '''            finally
            {
                StopDowngradeDriverWatch();
                await lease.DisposeAsync();
                _operationCts?.Dispose();''',
    count=1,
    label="stop full downgrade watchdog"
)
replace(
    simple,
    '''        SetBusy(true, "Test DFU → Pwned/Pongo", "Enter clean DFU. This test does not erase firmware.");
        try
        {
            var identity = await _orchestrator.ValidateDfuToPongoAsync(''',
    '''        SetBusy(true, "Test DFU → Pwned/Pongo", "Enter clean DFU. This test does not erase firmware.");
        StartDowngradeDriverWatch();
        try
        {
            await EnsureCleanDfuWithGuidanceAsync("Test DFU → Pwned/Pongo", _operationCts.Token);
            var identity = await _orchestrator.ValidateDfuToPongoAsync(''',
    label="guide automatic hardware validation into DFU"
)
# There are now two matching finally blocks; patch the second one by anchoring its completion text.
replace(
    simple,
    '''        finally
        {
            await lease.DisposeAsync();
            _operationCts?.Dispose();
            _operationCts = null;
            SetBusy(false, "Hardware test complete", "Re-enter clean DFU when Start Downgrade asks for it.");''',
    '''        finally
        {
            StopDowngradeDriverWatch();
            await lease.DisposeAsync();
            _operationCts?.Dispose();
            _operationCts = null;
            SetBusy(false, "Hardware test complete", "Re-enter clean DFU when Start Downgrade asks for it.");''',
    label="stop automatic validation watchdog"
)

# 6. Restore the original libusbK watchdog around jailbreak openra1n/Pongo.
jailbreak = ROOT / "src/Palera1nWin.Core/Orchestration/JailbreakOrchestrator.cs"
replace(
    jailbreak,
    '''                Report(JailbreakStage.RunningOpenRa1n, "Running openra1n until PongoOS is observed and the child exits...", 42);
                if (!await _openRa1nService.RunUntilPongoAsync(toolchain, cancellationToken).ConfigureAwait(false))
                {
                    Fail("PongoOS USB 05AC:4141 was not detected after openra1n.");
                    return JailbreakStage.Failed;
                }
            }
            else
            {
                Report(JailbreakStage.RunningOpenRa1n, "PongoOS is already present; skipping openra1n.", 48);
                if (!ReleaseSelectedAppleToWindows()) return JailbreakStage.Failed;
            }

            Report(JailbreakStage.EnsuringPongoDriver, "Verifying the single PongoOS device and host driver...", 60);
            if (!await EnsureModeDriverAsync(
                    DeviceMode.Pongo,
                    0x4141,
                    allowWinUsb: true,
                    cancellationToken).ConfigureAwait(false))
            {
                return JailbreakStage.Failed;
            }
            _ = RequireSingleDeviceForPid(0x4141);''',
    '''                using var driverWatch = new LibusbKWatchdog(_monitor, _settings);
                driverWatch.LogReceived += ForwardLog;
                driverWatch.Start();
                try
                {
                    Report(JailbreakStage.RunningOpenRa1n, "Running openra1n until PongoOS is observed and the child exits...", 42);
                    if (!await _openRa1nService.RunUntilPongoAsync(toolchain, cancellationToken).ConfigureAwait(false))
                    {
                        Fail("PongoOS USB 05AC:4141 was not detected after openra1n.");
                        return JailbreakStage.Failed;
                    }

                    Report(JailbreakStage.EnsuringPongoDriver, "Verifying the single PongoOS device and host driver...", 60);
                    if (!await EnsureModeDriverAsync(
                            DeviceMode.Pongo,
                            0x4141,
                            allowWinUsb: true,
                            cancellationToken).ConfigureAwait(false))
                    {
                        return JailbreakStage.Failed;
                    }
                    _ = RequireSingleDeviceForPid(0x4141);
                }
                finally
                {
                    driverWatch.LogReceived -= ForwardLog;
                    driverWatch.Stop();
                }
            }
            else
            {
                Report(JailbreakStage.RunningOpenRa1n, "PongoOS is already present; skipping openra1n.", 48);
                if (!ReleaseSelectedAppleToWindows()) return JailbreakStage.Failed;

                using var pongoWatch = new LibusbKWatchdog(_monitor, _settings);
                pongoWatch.LogReceived += ForwardLog;
                pongoWatch.Start();
                try
                {
                    Report(JailbreakStage.EnsuringPongoDriver, "Verifying the single PongoOS device and host driver...", 60);
                    if (!await EnsureModeDriverAsync(
                            DeviceMode.Pongo,
                            0x4141,
                            allowWinUsb: true,
                            cancellationToken).ConfigureAwait(false))
                    {
                        return JailbreakStage.Failed;
                    }
                    _ = RequireSingleDeviceForPid(0x4141);
                }
                finally
                {
                    pongoWatch.LogReceived -= ForwardLog;
                    pongoWatch.Stop();
                }
            }''',
    label="restore jailbreak driver watchdog"
)

print("Materialized normal-mode detection, DFU guidance, watchdog, ECID, and shared logging fixes.")
