using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace DarkSwordRestore.Core;

public enum DowngradeUiMode
{
    Beginner,
    Expert
}

public sealed record CompatibilityAssessment(
    int Score,
    string Rating,
    bool IsSupported,
    IReadOnlyList<string> Reasons)
{
    public string Summary => $"{Rating} — {Score}/100";
}

public sealed class CompatibilityAssessmentService
{
    public CompatibilityAssessment Assess(
        DarkSwordDevice? device,
        IpswInspectionResult? ipsw,
        PreflightReport? preflight,
        bool toolchainReady,
        bool administrator)
    {
        var score = 0;
        var reasons = new List<string>();
        var supported = true;

        if (device is null)
        {
            reasons.Add("Connect a supported device so the exact ProductType can be verified.");
            supported = false;
        }
        else
        {
            score += 15;
            reasons.Add($"Exact device detected: {device.DisplayName} ({device.ProductType}, {device.Chip}).");
            if (device.UsesA9SepBlocks)
            {
                score += 25;
                reasons.Add("The active Windows SHC/PTE restore backend supports this chip.");
            }
            else
            {
                reasons.Add($"The {device.Chip} downloader and DFU guide work, but its separate Windows restore backend is not enabled.");
                supported = false;
            }
        }

        if (ipsw?.IsValid == true && device is not null && ipsw.MatchesProductType(device.ProductType) &&
            ipsw.ProductVersion?.StartsWith("15.", StringComparison.Ordinal) == true)
        {
            score += 25;
            reasons.Add($"The inspected IPSW exactly matches {device.ProductType} and targets {ipsw.ProductVersion}.");
        }
        else
        {
            reasons.Add("Select and inspect an exact-device iOS/iPadOS 15 IPSW.");
        }

        if (toolchainReady)
        {
            score += 15;
            reasons.Add("All packaged restore tools and resources are present.");
        }
        else
        {
            reasons.Add("The packaged restore toolchain is incomplete.");
            supported = false;
        }

        if (administrator)
        {
            score += 10;
            reasons.Add("Palera1nWin is running with administrator access.");
        }
        else
        {
            reasons.Add("Administrator access is required for the DFU USB driver.");
        }

        if (preflight is not null)
        {
            var passedRatio = preflight.Checks.Count == 0
                ? 0
                : preflight.Checks.Count(check => check.Passed) / (double)preflight.Checks.Count;
            score += (int)Math.Round(passedRatio * 10);
            reasons.Add(preflight.CanProceed
                ? "The latest preflight report passed every required check."
                : $"The latest preflight report has {preflight.Checks.Count(check => !check.Passed)} blocked check(s).");
        }
        else
        {
            reasons.Add("Run the complete preflight scan before starting.");
        }

        score = Math.Clamp(score, 0, 100);
        var rating = !supported
            ? "UNSUPPORTED"
            : score >= 90
                ? "READY"
                : score >= 70
                    ? "GOOD"
                    : score >= 45
                        ? "RISKY"
                        : "NOT READY";
        return new CompatibilityAssessment(score, rating, supported, reasons);
    }
}

public sealed record DowngradeStoragePlan(
    string Drive,
    long IpswBytes,
    long ExtractionBytes,
    long RestoreCacheBytes,
    long SessionBytes,
    long LogsBytes,
    long SafetyMarginBytes,
    long RequiredBytes,
    long AvailableBytes)
{
    public bool HasEnoughSpace => AvailableBytes >= RequiredBytes;
    public string Summary =>
        $"{FormatBytes(RequiredBytes)} required, {FormatBytes(AvailableBytes)} available on {Drive}";

    public string Details => string.Join(Environment.NewLine, new[]
    {
        $"IPSW: {FormatBytes(IpswBytes)}",
        $"Extraction/images: {FormatBytes(ExtractionBytes)}",
        $"Restore cache: {FormatBytes(RestoreCacheBytes)}",
        $"Session and boot assets: {FormatBytes(SessionBytes)}",
        $"Logs/diagnostics: {FormatBytes(LogsBytes)}",
        $"Safety margin: {FormatBytes(SafetyMarginBytes)}"
    });

    public static string FormatBytes(long value)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double size = Math.Max(0, value);
        var unit = 0;
        while (size >= 1024 && unit < units.Length - 1)
        {
            size /= 1024;
            unit++;
        }
        return $"{size:F1} {units[unit]}";
    }
}

public static class DowngradeStoragePlanner
{
    private const long Gib = 1024L * 1024L * 1024L;
    private const long Mib = 1024L * 1024L;

    public static DowngradeStoragePlan Calculate(string? ipswPath, string fallbackPath)
    {
        var ipswBytes = !string.IsNullOrWhiteSpace(ipswPath) && File.Exists(ipswPath)
            ? new FileInfo(ipswPath).Length
            : 6 * Gib;
        var extraction = Math.Max(8 * Gib, (long)Math.Ceiling(ipswBytes * 1.45));
        var cache = Math.Max(7 * Gib, (long)Math.Ceiling(ipswBytes * 0.85));
        var sessions = 3 * Gib;
        var logs = 512 * Mib;
        var safety = 5 * Gib;
        var required = ipswBytes + extraction + cache + sessions + logs + safety;
        var path = !string.IsNullOrWhiteSpace(ipswPath) && File.Exists(ipswPath)
            ? ipswPath
            : fallbackPath;
        var root = Path.GetPathRoot(path) ?? "C:\\";
        var drive = new DriveInfo(root);
        return new DowngradeStoragePlan(
            drive.Name,
            ipswBytes,
            extraction,
            cache,
            sessions,
            logs,
            safety,
            required,
            drive.AvailableFreeSpace);
    }
}

public sealed record CableHealthSnapshot(
    string Rating,
    int Disconnects,
    int IdentityChanges,
    int TransferErrors,
    DateTimeOffset WindowStarted,
    string Recommendation)
{
    public bool IsHealthy => Rating is "EXCELLENT" or "STABLE";
    public string Summary =>
        $"{Rating} — disconnects {Disconnects}, USB identity changes {IdentityChanges}, transfer errors {TransferErrors}";
}

public sealed class CableStabilityTracker
{
    private readonly TimeSpan _window;
    private readonly Queue<DateTimeOffset> _disconnects = new();
    private readonly Queue<DateTimeOffset> _identityChanges = new();
    private readonly Queue<DateTimeOffset> _transferErrors = new();
    private AppleDeviceSnapshot _last = AppleDeviceSnapshot.Disconnected;
    private DateTimeOffset _windowStarted = DateTimeOffset.UtcNow;

    public CableStabilityTracker(TimeSpan? window = null) =>
        _window = window ?? TimeSpan.FromMinutes(5);

    public void Observe(AppleDeviceSnapshot snapshot)
    {
        var now = DateTimeOffset.UtcNow;
        Prune(now);
        if (_last.Mode != AppleDeviceMode.Disconnected && snapshot.Mode == AppleDeviceMode.Disconnected)
        {
            _disconnects.Enqueue(now);
        }
        if (_last.Mode != AppleDeviceMode.Disconnected && snapshot.Mode != AppleDeviceMode.Disconnected &&
            !string.IsNullOrWhiteSpace(_last.InstanceId) && !string.IsNullOrWhiteSpace(snapshot.InstanceId) &&
            !string.Equals(_last.InstanceId, snapshot.InstanceId, StringComparison.OrdinalIgnoreCase))
        {
            _identityChanges.Enqueue(now);
        }
        _last = snapshot;
    }

    public void RecordTransferError()
    {
        var now = DateTimeOffset.UtcNow;
        Prune(now);
        _transferErrors.Enqueue(now);
    }

    public CableHealthSnapshot GetSnapshot()
    {
        var now = DateTimeOffset.UtcNow;
        Prune(now);
        var weighted = _disconnects.Count * 3 + _identityChanges.Count * 2 + _transferErrors.Count * 2;
        var rating = weighted switch
        {
            0 => "EXCELLENT",
            <= 3 => "STABLE",
            <= 7 => "UNSTABLE",
            _ => "CRITICAL"
        };
        var recommendation = rating switch
        {
            "EXCELLENT" => "Connection is stable. Keep the same direct USB port and do not move the cable.",
            "STABLE" => "Connection is usable. Avoid hubs and keep the device still during destructive stages.",
            "UNSTABLE" => "Switch to a known-good data cable and a direct rear motherboard USB port before restoring.",
            _ => "Do not start or continue a restore until the cable and USB port are replaced."
        };
        return new CableHealthSnapshot(
            rating,
            _disconnects.Count,
            _identityChanges.Count,
            _transferErrors.Count,
            _windowStarted,
            recommendation);
    }

    private void Prune(DateTimeOffset now)
    {
        while (_disconnects.TryPeek(out var value) && now - value > _window) _disconnects.Dequeue();
        while (_identityChanges.TryPeek(out var value) && now - value > _window) _identityChanges.Dequeue();
        while (_transferErrors.TryPeek(out var value) && now - value > _window) _transferErrors.Dequeue();
        if (now - _windowStarted > _window) _windowStarted = now - _window;
    }
}

public sealed record DeviceDowngradeProfile(
    string Key,
    string ProductType,
    string DisplayName,
    string? Ecid,
    string? InstanceId,
    string? LastIpswPath,
    string? LastVersion,
    string? LastBuild,
    string? IpswSha256,
    string? LastPteBlockPath,
    string? LastSessionDirectory,
    DateTimeOffset UpdatedAt);

public sealed class DeviceProfileStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    public string RootDirectory { get; }

    public DeviceProfileStore(string? rootDirectory = null)
    {
        RootDirectory = rootDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DarkSword Restore",
            "devices");
        Directory.CreateDirectory(RootDirectory);
    }

    public async Task SaveAsync(DeviceDowngradeProfile profile, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(RootDirectory);
        var path = Path.Combine(RootDirectory, Sanitize(profile.Key) + ".json");
        var temporary = path + ".tmp";
        await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(profile, JsonOptions), cancellationToken)
            .ConfigureAwait(false);
        File.Move(temporary, path, overwrite: true);
    }

    public async Task<DeviceDowngradeProfile?> FindAsync(
        string? productType,
        string? ecid,
        string? instanceId,
        CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(RootDirectory)) return null;
        DeviceDowngradeProfile? best = null;
        foreach (var file in Directory.EnumerateFiles(RootDirectory, "*.json"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await using var stream = File.OpenRead(file);
                var profile = await JsonSerializer.DeserializeAsync<DeviceDowngradeProfile>(stream, JsonOptions, cancellationToken)
                    .ConfigureAwait(false);
                if (profile is null) continue;
                var match = !string.IsNullOrWhiteSpace(ecid) && string.Equals(profile.Ecid, ecid, StringComparison.OrdinalIgnoreCase) ||
                            !string.IsNullOrWhiteSpace(instanceId) && string.Equals(profile.InstanceId, instanceId, StringComparison.OrdinalIgnoreCase) ||
                            !string.IsNullOrWhiteSpace(productType) && string.Equals(profile.ProductType, productType, StringComparison.Ordinal);
                if (match && (best is null || profile.UpdatedAt > best.UpdatedAt)) best = profile;
            }
            catch
            {
                // A damaged profile must not block loading other known devices.
            }
        }
        return best;
    }

    public static string BuildKey(string? productType, string? ecid, string? instanceId) =>
        !string.IsNullOrWhiteSpace(ecid)
            ? $"ecid-{ecid}"
            : !string.IsNullOrWhiteSpace(instanceId)
                ? $"usb-{instanceId}"
                : $"product-{productType ?? "unknown"}";

    private static string Sanitize(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return new string(value.Select(character => invalid.Contains(character) || character is '\\' or '/' ? '_' : character).ToArray());
    }
}

public sealed record FailureGuidance(
    string Title,
    string ProbableCause,
    IReadOnlyList<string> Actions,
    string DiagnosticCode)
{
    public string DisplayText => string.Join(Environment.NewLine, new[]
    {
        Title,
        $"Cause: {ProbableCause}",
        "Next actions:",
        string.Join(Environment.NewLine, Actions.Select((action, index) => $"{index + 1}. {action}")),
        $"Diagnostic: {DiagnosticCode}"
    });
}

public static class DowngradeFailureTranslator
{
    public static FailureGuidance Translate(string? message, RestoreStage? stage = null)
    {
        var text = message ?? string.Empty;
        if (Contains(text, "1223", "cancelled", "canceled", "user account control", "administrator approval"))
        {
            return Guide("Administrator approval was not completed", "Windows cancelled or blocked elevation for the DFU driver installer.",
                ["Close and reopen Palera1nWin, approve the startup UAC prompt, and keep it running as administrator.", "Open Windows Security Protection History and allow wdi-simple.exe if it was blocked.", "Reconnect in DFU and run preflight again."], "WIN-UAC-1223");
        }
        if (Contains(text, "libusb", "driver", "wdi-simple"))
        {
            return Guide("Apple DFU driver is not ready", "The DFU device is attached to the wrong Windows USB service or the driver switch did not persist.",
                ["Keep the device in DFU with a black screen.", "Run the automatic preflight repair as administrator.", "Reconnect to a direct USB port and verify the service reports WINUSB or libusbK."], "USB-DRIVER");
        }
        if (Contains(text, "PongoOS did not enumerate", "openra1n", "checkm8"))
        {
            return Guide("PongoOS did not appear", "The checkm8/Pongo handoff failed, usually because DFU timing, the USB driver, or cable stability changed.",
                ["Force-restart and re-enter DFU using the timed guide.", "Use a direct USB port with no hub or extension.", "Retry the current safe stage; do not delete the session folder."], "PONGO-ENUM");
        }
        if (Contains(text, "timed out waiting", "DFU was not detected", "Recovery Mode"))
        {
            return Guide("The required device mode was not detected", "The device entered Recovery Mode, disconnected, or missed the DFU button timing.",
                ["Confirm the screen is completely black.", "Use the exact model-specific timed DFU guide.", "Press Refresh Device after Windows finishes reconnecting it."], "DFU-TIMEOUT");
        }
        if (Contains(text, "SHA-1", "SHA-256", "integrity", "BuildManifest"))
        {
            return Guide("Firmware integrity check failed", "The IPSW is incomplete, modified, corrupt, or does not contain the expected manifest.",
                ["Delete the failed IPSW and its .partial file if hash verification failed.", "Download the exact-device IPSW again through the built-in catalog.", "Inspect it and confirm ProductType and hash before preflight."], "IPSW-INTEGRITY");
        }
        if (Contains(text, "ProductType", "does not match", "targets"))
        {
            return Guide("Firmware does not match the connected device", "The selected IPSW or recovery session belongs to a different exact hardware identifier.",
                ["Reconnect the intended device and refresh exact ProductType detection.", "Select firmware only from that device's built-in catalog.", "Never bypass the exact-target confirmation."], "TARGET-MISMATCH");
        }
        if (Contains(text, "space", "disk full", "not enough storage", "0x70"))
        {
            return Guide("The PC does not have enough free storage", "Firmware extraction or restore caching exhausted the destination drive.",
                ["Free the amount shown by the storage planner plus its safety margin.", "Move the IPSW to a drive with more free space.", "Keep the existing session folder so completed checkpoints can be reused."], "DISK-SPACE");
        }
        if (Contains(text, "SEP", "pteblock", "PTE block", "sep_racer"))
        {
            return Guide("SEP or tether-boot profile generation failed", "The post-restore SHC/PTE stage did not produce a valid device-specific boot asset.",
                ["Do not delete the post-restore SHC checkpoint.", "Re-enter DFU and use Retry Safe Stage.", "Verify sep_racer.bin and kpf.bin are present in the packaged resources."], "SEP-PTE");
        }
        if (Contains(text, "disconnected", "device was removed", "bad file descriptor", "I/O", "transfer"))
        {
            return Guide("USB transfer was interrupted", "The cable, port, hub, power state, or Windows USB stack interrupted the active transfer.",
                ["Replace the cable with a known-good data cable.", "Use a direct motherboard USB port and disable hubs/extensions.", "Resume only from the newest safe checkpoint shown by the app."], "USB-TRANSFER");
        }
        if (Contains(text, "exited with code", "restore failed", "turdus"))
        {
            return Guide("The firmware restore tool stopped", "The native restore backend returned a failure before completing the current stage.",
                ["Open Expert Mode and copy the last native log lines.", "Check cable health, disk space, exact IPSW match, and DFU driver state.", "Use Retry Safe Stage instead of restarting completed SHC work."], "RESTORE-EXIT");
        }

        return stage switch
        {
            RestoreStage.InstallingDfuDriver => Translate("libusb driver"),
            RestoreStage.EnteringPwnedDfu or RestoreStage.BootingPongo => Translate("PongoOS did not enumerate"),
            RestoreStage.GeneratingPteBlock or RestoreStage.LoadingSepExploit => Translate("PTE block SEP"),
            RestoreStage.RestoringFirmware => Translate("restore failed"),
            _ => Guide("Downgrade stage needs attention", string.IsNullOrWhiteSpace(text) ? "The operation stopped without a recognized error signature." : text,
                ["Keep the device connected and do not delete the session folder.", "Review the live device mode and cable-health panels.", "Use Recovery & Targeted Retry when a safe checkpoint is available."], "UNCLASSIFIED")
        };
    }

    private static bool Contains(string text, params string[] values) =>
        values.Any(value => text.Contains(value, StringComparison.OrdinalIgnoreCase));

    private static FailureGuidance Guide(string title, string cause, string[] actions, string code) =>
        new(title, cause, actions, code);
}

public sealed record ModeRecoveryAdvice(string Title, string Action, bool KeepConnected);

public static class ModeRecoveryAdvisor
{
    public static ModeRecoveryAdvice GetAdvice(AppleDeviceSnapshot snapshot, RestoreStage? stage)
    {
        return snapshot.Mode switch
        {
            AppleDeviceMode.Disconnected => new("Reconnect the device", "Use a direct data cable and the same USB port. Wait for exact ProductType detection before continuing.", false),
            AppleDeviceMode.Normal => new("Normal mode detected", "Unlock and trust the computer, confirm your backup, then select firmware and run preflight.", true),
            AppleDeviceMode.Recovery => new("Recovery Mode is not DFU", "Force-restart, then use the timed DFU guide until the screen remains completely black.", true),
            AppleDeviceMode.Dfu => new("DFU mode ready", "Run preflight so the app can verify or repair the WINUSB/libusb driver, then continue the required stage.", true),
            AppleDeviceMode.Pongo => new("PongoOS is active", "Do not disconnect. The app is preparing or transferring restore components.", true),
            AppleDeviceMode.Restore => new("Firmware restore is active", "Do not touch the cable, close the app, let Windows sleep, or power off the PC.", true),
            AppleDeviceMode.PwnedDfu => new("Pwned DFU detected", "Keep the device connected while the app transitions into the restore environment.", true),
            _ => new("Unknown Apple USB mode", $"Current stage: {stage?.ToString() ?? "idle"}. Refresh detection or reconnect through a direct USB port.", true)
        };
    }
}

public sealed record RestoreHealthSnapshot(
    string State,
    string Summary,
    TimeSpan Elapsed,
    TimeSpan SinceProgress,
    IReadOnlyList<string> ActiveTools);

public sealed class RestoreHealthTracker
{
    private DateTimeOffset _startedAt;
    private DateTimeOffset _lastProgressAt;
    private DateTimeOffset _lastLogAt;
    private RestoreStage _stage = RestoreStage.Idle;
    private double _percent;
    private AppleDeviceSnapshot _device = AppleDeviceSnapshot.Disconnected;
    private bool _active;

    public void Start(AppleDeviceSnapshot device)
    {
        _active = true;
        _startedAt = DateTimeOffset.UtcNow;
        _lastProgressAt = _startedAt;
        _lastLogAt = _startedAt;
        _stage = RestoreStage.Preflight;
        _percent = 0;
        _device = device;
    }

    public void Stop() => _active = false;

    public void ObserveProgress(RestoreStage stage, double percent)
    {
        if (!_active) return;
        if (stage != _stage || Math.Abs(percent - _percent) >= 0.1)
        {
            _lastProgressAt = DateTimeOffset.UtcNow;
            _stage = stage;
            _percent = percent;
        }
    }

    public void PulseLog()
    {
        if (_active) _lastLogAt = DateTimeOffset.UtcNow;
    }

    public void ObserveDevice(AppleDeviceSnapshot device) => _device = device;

    public RestoreHealthSnapshot GetSnapshot()
    {
        if (!_active)
        {
            return new RestoreHealthSnapshot("IDLE", "No destructive downgrade stage is active.", TimeSpan.Zero, TimeSpan.Zero, []);
        }

        var now = DateTimeOffset.UtcNow;
        var elapsed = now - _startedAt;
        var sinceProgress = now - (_lastProgressAt > _lastLogAt ? _lastProgressAt : _lastLogAt);
        var tools = GetActiveTools();
        var waitingForUser = _stage == RestoreStage.WaitingForDfu;
        var destructive = _stage is RestoreStage.RestoringFirmware or RestoreStage.GeneratingPteBlock or RestoreStage.LoadingSepExploit or RestoreStage.BootingXnu;
        string state;
        string summary;

        if (destructive && _device.Mode == AppleDeviceMode.Disconnected)
        {
            state = "CRITICAL";
            summary = "The device disconnected during a destructive or boot-profile stage. Keep the session folder and follow targeted recovery guidance.";
        }
        else if (!waitingForUser && sinceProgress > TimeSpan.FromMinutes(6))
        {
            state = "CRITICAL";
            summary = $"No progress or tool output for {sinceProgress:mm\\:ss}. The stage may be frozen; do not unplug until recovery guidance is shown.";
        }
        else if (!waitingForUser && sinceProgress > TimeSpan.FromMinutes(2))
        {
            state = "WARNING";
            summary = $"No visible progress for {sinceProgress:mm\\:ss}. Monitoring native tools and USB state.";
        }
        else
        {
            state = "HEALTHY";
            summary = waitingForUser
                ? "Waiting for the required DFU entry; the lack of progress is expected."
                : $"Stage {_stage} is active at {_percent:F1}%. Last activity {sinceProgress:mm\\:ss} ago.";
        }
        return new RestoreHealthSnapshot(state, summary, elapsed, sinceProgress, tools);
    }

    private static IReadOnlyList<string> GetActiveTools()
    {
        var names = new[] { "openra1n", "turdus_merula", "darksword-pongo", "wdi-simple", "irecovery", "ideviceinfo" };
        var active = new List<string>();
        foreach (var name in names)
        {
            try
            {
                if (Process.GetProcessesByName(name).Length > 0) active.Add(name);
            }
            catch
            {
                // Process enumeration is advisory only.
            }
        }
        return active;
    }
}

public sealed class PowerProtectionLease : IDisposable
{
    [Flags]
    private enum ExecutionState : uint
    {
        SystemRequired = 0x00000001,
        DisplayRequired = 0x00000002,
        AwayModeRequired = 0x00000040,
        Continuous = 0x80000000
    }

    private bool _disposed;

    public PowerProtectionLease()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            _ = SetThreadExecutionState(
                ExecutionState.Continuous |
                ExecutionState.SystemRequired |
                ExecutionState.DisplayRequired |
                ExecutionState.AwayModeRequired);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            _ = SetThreadExecutionState(ExecutionState.Continuous);
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern ExecutionState SetThreadExecutionState(ExecutionState executionState);
}

public sealed class SessionExportService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    public string ExportDirectory { get; }

    public SessionExportService(string? exportDirectory = null)
    {
        ExportDirectory = exportDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DarkSword Restore",
            "exports");
        Directory.CreateDirectory(ExportDirectory);
    }

    public async Task<string> ExportAsync(
        RestoreSession session,
        string? appLogPath,
        DeviceDowngradeProfile? profile,
        CableHealthSnapshot cableHealth,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(ExportDirectory);
        var safeProduct = session.Ipsw.SupportedProductTypes.FirstOrDefault() ?? "AppleDevice";
        var destination = Path.Combine(
            ExportDirectory,
            $"DarkSword-{safeProduct}-{session.SessionId}-{DateTimeOffset.Now:yyyyMMdd-HHmmss}.zip");
        var temporary = destination + ".tmp";
        if (File.Exists(temporary)) File.Delete(temporary);

        using (var archive = ZipFile.Open(temporary, ZipArchiveMode.Create))
        {
            foreach (var file in EnumeratePortableSessionFiles(session.SessionDirectory))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var relative = Path.GetRelativePath(session.SessionDirectory, file).Replace('\\', '/');
                archive.CreateEntryFromFile(file, $"session/{relative}", CompressionLevel.Optimal);
            }

            if (!string.IsNullOrWhiteSpace(appLogPath) && File.Exists(appLogPath))
            {
                archive.CreateEntryFromFile(appLogPath, $"logs/{Path.GetFileName(appLogPath)}", CompressionLevel.Optimal);
            }

            var manifest = new
            {
                exportedAt = DateTimeOffset.UtcNow,
                session.SessionId,
                session.LastStage,
                session.CreatedAt,
                session.UpdatedAt,
                productTypes = session.Ipsw.SupportedProductTypes,
                session.Ipsw.ProductVersion,
                session.Ipsw.BuildVersion,
                session.Ipsw.Sha256,
                session.Ipsw.FileSize,
                session.ShcBlockPath,
                session.PteBlockPath,
                profile,
                cableHealth
            };
            var entry = archive.CreateEntry("manifest.json", CompressionLevel.Optimal);
            await using var output = entry.Open();
            await JsonSerializer.SerializeAsync(output, manifest, JsonOptions, cancellationToken).ConfigureAwait(false);
        }

        File.Move(temporary, destination, overwrite: true);
        return destination;
    }

    private static IEnumerable<string> EnumeratePortableSessionFiles(string directory)
    {
        if (!Directory.Exists(directory)) yield break;
        foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(directory, file);
            if (relative.StartsWith("cache" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) continue;
            if (file.EndsWith(".ipsw", StringComparison.OrdinalIgnoreCase)) continue;
            if (new FileInfo(file).Length > 512L * 1024L * 1024L) continue;
            yield return file;
        }
    }
}

public sealed class ExperiencePreferencesStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly string _path;

    public ExperiencePreferencesStore(string? path = null)
    {
        _path = path ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DarkSword Restore",
            "experience.json");
    }

    public async Task<DowngradeUiMode> LoadAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            if (!File.Exists(_path)) return DowngradeUiMode.Beginner;
            await using var stream = File.OpenRead(_path);
            var preferences = await JsonSerializer.DeserializeAsync<ExperiencePreferences>(stream, JsonOptions, cancellationToken)
                .ConfigureAwait(false);
            return preferences?.Mode ?? DowngradeUiMode.Beginner;
        }
        catch
        {
            return DowngradeUiMode.Beginner;
        }
    }

    public async Task SaveAsync(DowngradeUiMode mode, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temporary = _path + ".tmp";
        await File.WriteAllTextAsync(
            temporary,
            JsonSerializer.Serialize(new ExperiencePreferences(mode), JsonOptions),
            cancellationToken).ConfigureAwait(false);
        File.Move(temporary, _path, overwrite: true);
    }

    private sealed record ExperiencePreferences(DowngradeUiMode Mode);
}
