using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Security.Principal;
using System.Text.Json;

namespace DarkSwordRestore.Core;

public enum PreflightCheckState
{
    Passed,
    Failed
}

public sealed record PreflightCheckResult(
    string Key,
    string Title,
    PreflightCheckState State,
    string Detail,
    bool WasRepaired = false)
{
    public bool Passed => State == PreflightCheckState.Passed;
}

public sealed record PreflightReport(
    DateTimeOffset CompletedAt,
    IReadOnlyList<PreflightCheckResult> Checks,
    AppleDeviceSnapshot Device,
    IpswInspectionResult? Ipsw,
    string Fingerprint)
{
    public bool CanProceed => Checks.Count > 0 && Checks.All(check => check.Passed);
}

public sealed class DowngradePreflightService
{
    private const long Gib = 1024L * 1024L * 1024L;
    private readonly ToolchainPaths _tools;
    private readonly AppleDeviceMonitor _devices;
    private readonly IpswInspector _inspector;
    private readonly DfuDriverService _driver;

    public DowngradePreflightService(
        ToolchainPaths tools,
        AppleDeviceMonitor devices,
        IpswInspector inspector,
        DfuDriverService driver)
    {
        _tools = tools;
        _devices = devices;
        _inspector = inspector;
        _driver = driver;
    }

    public async Task<PreflightReport> RunAsync(
        string ipswPath,
        string? expectedProductType,
        bool repairDfuDriver,
        Action<string>? log,
        CancellationToken cancellationToken)
    {
        var checks = new List<PreflightCheckResult>();
        var snapshot = await _devices.ProbeAsync(cancellationToken).ConfigureAwait(false);
        IpswInspectionResult? inspection = null;

        Add(
            "administrator",
            "Administrator access",
            IsAdministrator(),
            IsAdministrator()
                ? "Palera1nWin is running elevated."
                : "Restart Palera1nWin and approve the Windows User Account Control prompt.");

        var missing = _tools.MissingFiles().ToList();
        var resources = Path.Combine(_tools.Root, "resources");
        foreach (var resource in new[] { "sep_racer.bin", "kpf.bin" })
        {
            var path = Path.Combine(resources, resource);
            if (!File.Exists(path)) missing.Add(path);
        }
        Add(
            "toolchain",
            "Restore toolchain",
            missing.Count == 0,
            missing.Count == 0
                ? "All managed and native restore components are present."
                : "Missing: " + string.Join(", ", missing.Select(Path.GetFileName)));

        Add(
            "device-identity",
            "Exact device identity",
            !string.IsNullOrWhiteSpace(expectedProductType) && DarkSwordDeviceCatalog.IsSupported(expectedProductType),
            string.IsNullOrWhiteSpace(expectedProductType)
                ? "Connect and unlock the device once so its exact ProductType can be read."
                : DarkSwordDeviceCatalog.Find(expectedProductType) is { } device
                    ? $"{device.DisplayName} ({device.ProductType}, {device.Chip}) detected."
                    : $"{expectedProductType} is outside the supported A9-A10X device catalog.");

        var activeDevice = DarkSwordDeviceCatalog.Find(expectedProductType);
        Add(
            "backend",
            "Windows restore backend",
            activeDevice?.UsesA9SepBlocks == true,
            activeDevice?.UsesA9SepBlocks == true
                ? $"The {activeDevice.Chip} SHC/PTE backend is enabled."
                : activeDevice is null
                    ? "The device backend cannot be selected until ProductType is known."
                    : $"{activeDevice.Chip} detection and DFU guidance are available, but its separate restore backend is not enabled.");

        if (!File.Exists(ipswPath))
        {
            Add("ipsw", "Firmware integrity", false, "Select or download an IPSW before preflight.");
        }
        else
        {
            try
            {
                inspection = await _inspector.InspectAsync(ipswPath, cancellationToken).ConfigureAwait(false);
                var exactMatch = inspection.IsValid &&
                                 inspection.ProductVersion?.StartsWith("15.", StringComparison.Ordinal) == true &&
                                 inspection.MatchesProductType(expectedProductType);
                Add(
                    "ipsw",
                    "Firmware integrity",
                    exactMatch,
                    exactMatch
                        ? $"iOS/iPadOS {inspection.ProductVersion} ({inspection.BuildVersion}) matches {expectedProductType}; SHA-256 {inspection.Sha256}."
                        : string.Join(" ", inspection.Errors.Concat(new[]
                        {
                            $"The IPSW must be iOS/iPadOS 15 and contain exact ProductType {expectedProductType ?? "unknown"}."
                        })));
            }
            catch (Exception exception)
            {
                Add("ipsw", "Firmware integrity", false, exception.Message);
            }
        }

        try
        {
            var driveRoot = Path.GetPathRoot(ipswPath) ?? Path.GetPathRoot(AppContext.BaseDirectory) ?? "C:\\";
            var drive = new DriveInfo(driveRoot);
            var ipswSize = File.Exists(ipswPath) ? new FileInfo(ipswPath).Length : 0;
            var required = Math.Max(20 * Gib, (long)(ipswSize * 2.5) + 5 * Gib);
            Add(
                "disk",
                "Free disk space",
                drive.AvailableFreeSpace >= required,
                $"{FormatBytes(drive.AvailableFreeSpace)} free on {drive.Name}; {FormatBytes(required)} required for firmware caches, restore images, and recovery assets.");
        }
        catch (Exception exception)
        {
            Add("disk", "Free disk space", false, exception.Message);
        }

        Add(
            "network",
            "Internet connection",
            NetworkInterface.GetIsNetworkAvailable(),
            NetworkInterface.GetIsNetworkAvailable()
                ? "A network connection is available for firmware metadata and any required downloads."
                : "Connect this PC to the internet before continuing.");

        Add(
            "dfu",
            "Apple DFU mode",
            snapshot.Mode == AppleDeviceMode.Dfu,
            snapshot.Mode == AppleDeviceMode.Dfu
                ? "Apple DFU mode is detected and the device screen should be completely black."
                : snapshot.Mode == AppleDeviceMode.Recovery
                    ? "Recovery Mode was detected. Use the guided DFU sequence until the screen stays black."
                    : $"Current mode is {snapshot.Mode}. Complete the guided DFU sequence before starting.");

        var driverReady = snapshot.Mode == AppleDeviceMode.Dfu && DfuDriverService.IsReadyUsbBackend(snapshot.Service);
        var repaired = false;
        if (snapshot.Mode == AppleDeviceMode.Dfu && !driverReady && repairDfuDriver && IsAdministrator())
        {
            log?.Invoke($"DFU device is using service '{snapshot.Service ?? "unknown"}'. Repairing WinUSB/libusb backend automatically.");
            await _driver.InstallLibusbKAsync(cancellationToken).ConfigureAwait(false);
            repaired = true;
            snapshot = await WaitForDfuDriverAsync(cancellationToken).ConfigureAwait(false);
            driverReady = snapshot.Mode == AppleDeviceMode.Dfu && DfuDriverService.IsReadyUsbBackend(snapshot.Service);
        }
        Add(
            "driver",
            "Apple DFU USB driver",
            driverReady,
            driverReady
                ? $"Apple DFU is attached through {snapshot.Service ?? "WinUSB/libusbK"}."
                : snapshot.Mode != AppleDeviceMode.Dfu
                    ? "Driver state is checked after Apple DFU mode is detected."
                    : $"Apple DFU is using '{snapshot.Service ?? "an unknown service"}'. Automatic WinUSB/libusbK repair did not verify successfully.",
            repaired && driverReady);

        var battery = await TryReadBatteryAsync(snapshot, cancellationToken).ConfigureAwait(false);
        Add(
            "battery",
            "Battery and USB power",
            battery is null || battery >= 30,
            battery is null
                ? "Battery percentage is unavailable in DFU; keep the device connected directly to a powered USB port."
                : battery >= 30
                    ? $"Battery is {battery}%."
                    : $"Battery is only {battery}%. Charge to at least 30% before restoring.");

        var fingerprint = BuildFingerprint(expectedProductType, ipswPath, snapshot, inspection);
        return new PreflightReport(DateTimeOffset.UtcNow, checks, snapshot, inspection, fingerprint);

        void Add(string key, string title, bool passed, string detail, bool wasRepaired = false) =>
            checks.Add(new PreflightCheckResult(
                key,
                title,
                passed ? PreflightCheckState.Passed : PreflightCheckState.Failed,
                detail,
                wasRepaired));
    }

    public static string BuildFingerprint(
        string? productType,
        string ipswPath,
        AppleDeviceSnapshot snapshot,
        IpswInspectionResult? inspection = null)
    {
        var file = File.Exists(ipswPath) ? new FileInfo(ipswPath) : null;
        return string.Join('|', new[]
        {
            productType ?? string.Empty,
            ipswPath,
            file?.Length.ToString() ?? "0",
            file?.LastWriteTimeUtc.Ticks.ToString() ?? "0",
            snapshot.Mode.ToString(),
            snapshot.Service ?? string.Empty,
            inspection?.Sha256 ?? string.Empty
        });
    }

    private async Task<AppleDeviceSnapshot> WaitForDfuDriverAsync(CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(25);
        AppleDeviceSnapshot last = AppleDeviceSnapshot.Disconnected;
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            last = await _devices.ProbeAsync(cancellationToken).ConfigureAwait(false);
            if (last.Mode == AppleDeviceMode.Dfu && DfuDriverService.IsReadyUsbBackend(last.Service)) return last;
            await Task.Delay(750, cancellationToken).ConfigureAwait(false);
        }
        return last;
    }

    private async Task<int?> TryReadBatteryAsync(AppleDeviceSnapshot snapshot, CancellationToken cancellationToken)
    {
        if (snapshot.Mode != AppleDeviceMode.Normal) return null;
        var ideviceInfo = Path.Combine(_tools.Root, "ideviceinfo.exe");
        if (!File.Exists(ideviceInfo)) return null;
        var output = await RunCaptureAsync(
            ideviceInfo,
            new[] { "-q", "com.apple.mobile.battery", "-k", "BatteryCurrentCapacity" },
            cancellationToken).ConfigureAwait(false);
        return int.TryParse(output.Trim(), out var capacity) ? capacity : null;
    }

    private static async Task<string> RunCaptureAsync(
        string fileName,
        IEnumerable<string> arguments,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            WorkingDirectory = Path.GetDirectoryName(fileName) ?? AppContext.BaseDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
        using var process = Process.Start(startInfo);
        if (process is null) return string.Empty;
        var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        return (await stdout.ConfigureAwait(false)) + Environment.NewLine + (await stderr.ConfigureAwait(false));
    }

    private static bool IsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    private static string FormatBytes(long value)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double size = value;
        var unit = 0;
        while (size >= 1024 && unit < units.Length - 1)
        {
            size /= 1024;
            unit++;
        }
        return $"{size:F1} {units[unit]}";
    }
}

public sealed record RecoveryCandidate(
    RestoreSession Session,
    string Description,
    bool CanResume,
    string? PreRestoreShc,
    string? PostRestoreShc,
    string? PteBlock);

public sealed class DowngradeRecoveryService
{
    private const string PreShcFlag = "checkpoint-pre-shc.ok";
    private const string RestoreFlag = "checkpoint-restore.ok";
    private const string PostShcFlag = "checkpoint-post-shc.ok";
    private const string PteFlag = "checkpoint-pte.ok";
    private const string CompleteFlag = "checkpoint-complete.ok";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly ToolchainPaths _tools;
    private readonly ToolProcessRunner _runner;
    private readonly AppleDeviceMonitor _devices;
    private readonly RestoreSessionStore _sessions;
    private readonly DfuDriverService _driver;
    private string? _lastProgressSignature;

    public DowngradeRecoveryService(
        ToolchainPaths tools,
        ToolProcessRunner runner,
        AppleDeviceMonitor devices,
        RestoreSessionStore sessions,
        DfuDriverService driver)
    {
        _tools = tools;
        _runner = runner;
        _devices = devices;
        _sessions = sessions;
        _driver = driver;
    }

    public async Task<RecoveryCandidate?> FindLatestRecoverableAsync(CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(_sessions.RootDirectory)) return null;
        foreach (var directory in Directory.EnumerateDirectories(_sessions.RootDirectory)
                     .OrderByDescending(Directory.GetLastWriteTimeUtc))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var session = await _sessions.LoadAsync(directory, cancellationToken).ConfigureAwait(false);
            if (session is null || File.Exists(Path.Combine(directory, CompleteFlag))) continue;
            var candidate = BuildCandidate(session);
            if (candidate.CanResume) return candidate;
        }
        return null;
    }

    public async Task MarkLatestCheckpointAsync(RestoreProgress progress, CancellationToken cancellationToken = default)
    {
        var signature = $"{progress.Stage}:{Math.Floor(progress.Percent)}:{progress.Title}";
        if (signature == _lastProgressSignature) return;
        _lastProgressSignature = signature;

        var directory = Directory.Exists(_sessions.RootDirectory)
            ? Directory.EnumerateDirectories(_sessions.RootDirectory).OrderByDescending(Directory.GetCreationTimeUtc).FirstOrDefault()
            : null;
        if (directory is null || !File.Exists(Path.Combine(directory, "session.json"))) return;

        if (progress.Stage == RestoreStage.WaitingForDfu && progress.Percent >= 28) Mark(directory, PreShcFlag);
        if (progress.Stage == RestoreStage.WaitingForDfu && progress.Percent >= 73) Mark(directory, RestoreFlag);
        if (progress.Stage == RestoreStage.WaitingForDfu && progress.Percent >= 83) Mark(directory, PostShcFlag);
        if (progress.Stage == RestoreStage.WaitingForDfu && progress.Percent >= 92) Mark(directory, PteFlag);
        if (progress.Stage == RestoreStage.Completed) Mark(directory, CompleteFlag);

        var statePath = Path.Combine(directory, "recovery-progress.json");
        var temporary = statePath + ".tmp";
        await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(progress, JsonOptions), cancellationToken).ConfigureAwait(false);
        File.Move(temporary, statePath, overwrite: true);
    }

    public async Task<RestoreSession> ResumeAsync(
        RecoveryCandidate candidate,
        string expectedProductType,
        IProgress<RestoreProgress>? progress,
        Action<string>? log,
        CancellationToken cancellationToken)
    {
        var session = candidate.Session;
        if (!File.Exists(session.IpswPath))
        {
            throw new FileNotFoundException("The IPSW used by this recovery session is missing.", session.IpswPath);
        }
        if (!session.Ipsw.MatchesProductType(expectedProductType))
        {
            throw new DarkSwordException(
                RestoreStage.Preflight,
                $"Recovery session targets {string.Join(", ", session.Ipsw.SupportedProductTypes)}, not connected device {expectedProductType}.");
        }

        var preShc = candidate.PreRestoreShc;
        var postShc = candidate.PostRestoreShc;
        var pte = candidate.PteBlock;
        var directory = session.SessionDirectory;

        try
        {
            if (pte is not null)
            {
                Report(RestoreStage.WaitingForDfu, 92, "Resume final tether boot", "Existing PTE block found. Enter DFU to boot the restored system.");
                await PrepareDfuAsync(cancellationToken).ConfigureAwait(false);
                await TetherBootCoreAsync(pte, progress, log, cancellationToken).ConfigureAwait(false);
                return await CompleteAsync(session with { PteBlockPath = pte }, cancellationToken).ConfigureAwait(false);
            }

            if (postShc is null && File.Exists(Path.Combine(directory, RestoreFlag)))
            {
                Report(RestoreStage.WaitingForDfu, 73, "Resume boot-profile creation", "The firmware restore completed. Enter DFU to capture the post-restore SHC block.");
                await PrepareDfuAsync(cancellationToken).ConfigureAwait(false);
                await PwnDfuAsync(log, cancellationToken).ConfigureAwait(false);
                postShc = await RunBlockOperationAsync(
                    session,
                    "shcblock",
                    new[] { "--get-shcblock", "--cache-path", Path.Combine(directory, "cache"), session.IpswPath },
                    log,
                    cancellationToken,
                    excludedPath: preShc).ConfigureAwait(false);
                Mark(directory, PostShcFlag);
            }

            if (postShc is null && preShc is not null)
            {
                Report(RestoreStage.WaitingForDfu, 28, "Resume firmware restore", "The pre-restore SHC block is safe. Enter DFU to retry the firmware restore.");
                await PrepareDfuAsync(cancellationToken).ConfigureAwait(false);
                await PwnDfuAsync(log, cancellationToken).ConfigureAwait(false);
                Report(RestoreStage.RestoringFirmware, 38, "Retrying firmware restore", "Reusing the completed SHC capture and firmware cache.", true);
                await RunRestoreAsync(
                    session,
                    new[]
                    {
                        "-o", "--plain-progress", "--no-input",
                        "--cache-path", Path.Combine(directory, "cache"),
                        "--load-shcblock", preShc,
                        session.IpswPath
                    },
                    log,
                    progress,
                    cancellationToken).ConfigureAwait(false);
                Mark(directory, RestoreFlag);

                Report(RestoreStage.WaitingForDfu, 73, "Create permanent boot profile", "Restore retry completed. Enter DFU to capture the post-restore SHC block.");
                await PrepareDfuAsync(cancellationToken).ConfigureAwait(false);
                await PwnDfuAsync(log, cancellationToken).ConfigureAwait(false);
                postShc = await RunBlockOperationAsync(
                    session,
                    "shcblock",
                    new[] { "--get-shcblock", "--cache-path", Path.Combine(directory, "cache"), session.IpswPath },
                    log,
                    cancellationToken,
                    excludedPath: preShc).ConfigureAwait(false);
                Mark(directory, PostShcFlag);
            }

            if (postShc is null)
            {
                throw new DarkSwordException(
                    RestoreStage.Preflight,
                    "No safe SHC recovery checkpoint exists. Start a new downgrade; the existing session remains preserved for diagnostics.");
            }

            Report(RestoreStage.WaitingForDfu, 83, "Resume PTE generation", "Post-restore SHC block found. Enter DFU to generate the permanent tether-boot asset.");
            await PrepareDfuAsync(cancellationToken).ConfigureAwait(false);
            await PwnDfuAsync(log, cancellationToken).ConfigureAwait(false);
            Report(RestoreStage.GeneratingPteBlock, 87, "Generating PTE block", "Reusing the post-restore SHC block.");
            pte = await RunBlockOperationAsync(
                session,
                "pteblock",
                new[]
                {
                    "--get-pteblock", "--load-shcblock", postShc,
                    "--cache-path", Path.Combine(directory, "cache"), session.IpswPath
                },
                log,
                cancellationToken).ConfigureAwait(false);
            Mark(directory, PteFlag);

            session = session with
            {
                ShcBlockPath = postShc,
                PteBlockPath = pte,
                LastStage = RestoreStage.GeneratingPteBlock,
                UpdatedAt = DateTimeOffset.UtcNow
            };
            await _sessions.SaveAsync(session, cancellationToken).ConfigureAwait(false);

            Report(RestoreStage.WaitingForDfu, 92, "Final DFU entry", "Enter DFU once more to boot the restored system.");
            await PrepareDfuAsync(cancellationToken).ConfigureAwait(false);
            await TetherBootCoreAsync(pte, progress, log, cancellationToken).ConfigureAwait(false);
            return await CompleteAsync(session, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            session = session with { LastStage = RestoreStage.Cancelled, UpdatedAt = DateTimeOffset.UtcNow };
            await _sessions.SaveAsync(session, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        catch (Exception exception) when (exception is not DarkSwordException)
        {
            session = session with { LastStage = RestoreStage.Failed, UpdatedAt = DateTimeOffset.UtcNow };
            await _sessions.SaveAsync(session, CancellationToken.None).ConfigureAwait(false);
            throw new DarkSwordException(session.LastStage, exception.Message, exception);
        }

        void Report(RestoreStage stage, double percent, string title, string detail, bool destructive = false) =>
            progress?.Report(new RestoreProgress(stage, percent, title, detail, destructive));
    }

    private RecoveryCandidate BuildCandidate(RestoreSession session)
    {
        var blockDirectory = Path.Combine(session.SessionDirectory, "block");
        var shc = Directory.Exists(blockDirectory)
            ? Directory.EnumerateFiles(blockDirectory, "*", SearchOption.AllDirectories)
                .Where(path => Path.GetFileName(path).Contains("shcblock", StringComparison.OrdinalIgnoreCase))
                .OrderBy(File.GetLastWriteTimeUtc)
                .ToArray()
            : [];
        var pte = Directory.Exists(blockDirectory)
            ? Directory.EnumerateFiles(blockDirectory, "*", SearchOption.AllDirectories)
                .Where(path => Path.GetFileName(path).Contains("pteblock", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault()
            : null;
        pte ??= File.Exists(session.PteBlockPath) ? session.PteBlockPath : null;

        var pre = shc.FirstOrDefault();
        var post = shc.Length >= 2 ? shc[^1] : null;
        var canResume = pte is not null || post is not null || pre is not null;
        var description = pte is not null
            ? "PTE block is complete; retry the final tether boot."
            : post is not null
                ? "Post-restore SHC exists; continue with PTE generation."
                : pre is not null
                    ? "Pre-restore SHC exists; retry the restore without repeating the first capture."
                    : "No safe recovery artifact is available.";
        return new RecoveryCandidate(session, description, canResume, pre, post, pte);
    }

    private async Task PrepareDfuAsync(CancellationToken cancellationToken)
    {
        await _devices.WaitForModeAsync(new[] { AppleDeviceMode.Dfu }, TimeSpan.FromMinutes(5), cancellationToken).ConfigureAwait(false);
        await _driver.InstallLibusbKAsync(cancellationToken).ConfigureAwait(false);
        await Task.Delay(1200, cancellationToken).ConfigureAwait(false);
        await _devices.WaitForModeAsync(new[] { AppleDeviceMode.Dfu }, TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
    }

    private async Task PwnDfuAsync(Action<string>? log, CancellationToken cancellationToken)
    {
        const int maxAttempts = 3;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            log?.Invoke($"Starting gaster pwn attempt {attempt}/{maxAttempts}. Keep the device in black-screen DFU.");
            try
            {
                await _runner.RunWithTimeoutAsync(
                    _tools.Gaster,
                    new[] { "pwn" },
                    _tools.Root,
                    log,
                    TimeSpan.FromSeconds(55),
                    cancellationToken).ConfigureAwait(false);

                await Task.Delay(1500, cancellationToken).ConfigureAwait(false);
                await _devices.WaitForModeAsync(new[] { AppleDeviceMode.Dfu }, TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
                await _driver.InstallLibusbKAsync(cancellationToken).ConfigureAwait(false);
                log?.Invoke("gaster pwn completed and DFU is ready for turdus_merula.");
                return;
            }
            catch (TimeoutException exception) when (attempt < maxAttempts)
            {
                log?.Invoke($"{exception.Message} Retrying after USB reset.");
                await ResetDfuAsync(log, cancellationToken).ConfigureAwait(false);
            }
            catch (InvalidOperationException exception) when (attempt < maxAttempts)
            {
                log?.Invoke($"gaster pwn failed: {exception.Message} Retrying after USB reset.");
                await ResetDfuAsync(log, cancellationToken).ConfigureAwait(false);
            }
        }

        throw new DarkSwordException(
            RestoreStage.EnteringPwnedDfu,
            "gaster pwn did not complete after three attempts. Force-reboot the iPad, re-enter black-screen DFU on a direct USB port, then retry.");
    }

    private async Task ResetDfuAsync(Action<string>? log, CancellationToken cancellationToken)
    {
        try
        {
            await _runner.RunWithTimeoutAsync(
                _tools.Gaster,
                new[] { "reset" },
                _tools.Root,
                log,
                TimeSpan.FromSeconds(12),
                cancellationToken,
                requireZeroExitCode: false).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is TimeoutException or InvalidOperationException)
        {
            log?.Invoke($"gaster reset did not complete cleanly: {exception.Message}");
        }

        await Task.Delay(2000, cancellationToken).ConfigureAwait(false);
        await _devices.WaitForModeAsync(new[] { AppleDeviceMode.Dfu }, TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
        await _driver.InstallLibusbKAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task BootPongoAsync(Action<string>? log, CancellationToken cancellationToken)
    {
        var openTask = _runner.RunAsync(
            _tools.OpenRa1n,
            Array.Empty<string>(),
            _tools.Root,
            log,
            cancellationToken,
            requireZeroExitCode: false);
        try
        {
            await _devices.WaitForModeAsync(new[] { AppleDeviceMode.Pongo }, TimeSpan.FromMinutes(2), cancellationToken).ConfigureAwait(false);
            await _driver.InstallPongoLibusbKAsync(cancellationToken).ConfigureAwait(false);
            await Task.Delay(2000, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            var result = await openTask.ConfigureAwait(false);
            throw new DarkSwordException(
                RestoreStage.BootingPongo,
                $"PongoOS did not enumerate. openra1n exit code: {result.ExitCode}.{Environment.NewLine}{result.StandardError}");
        }

        var openResult = await openTask.ConfigureAwait(false);
        if (openResult.ExitCode != 0)
        {
            log?.Invoke(
                $"openra1n exited with code {openResult.ExitCode} after PongoOS was detected; " +
                "continuing because the PongoOS libusbK driver was installed successfully.");
        }
    }

    private async Task<string> RunBlockOperationAsync(
        RestoreSession session,
        string fileToken,
        string[] arguments,
        Action<string>? log,
        CancellationToken cancellationToken,
        string? excludedPath = null)
    {
        var blockDirectory = Path.Combine(session.SessionDirectory, "block");
        Directory.CreateDirectory(blockDirectory);
        var before = Directory.EnumerateFiles(blockDirectory, "*", SearchOption.AllDirectories)
            .Select(Path.GetFullPath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (excludedPath is not null) before.Add(Path.GetFullPath(excludedPath));

        await _runner.RunAsync(
            _tools.IdeviceRestore,
            arguments,
            session.SessionDirectory,
            log,
            cancellationToken).ConfigureAwait(false);

        var generated = Directory.EnumerateFiles(session.SessionDirectory, "*", SearchOption.AllDirectories)
            .Where(path => Path.GetFileName(path).Contains(fileToken, StringComparison.OrdinalIgnoreCase))
            .Where(path => !before.Contains(Path.GetFullPath(path)))
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();
        return generated ?? throw new DarkSwordException(
            fileToken.Contains("pte", StringComparison.OrdinalIgnoreCase) ? RestoreStage.GeneratingPteBlock : RestoreStage.GeneratingShcBlock,
            $"The tool completed without producing a new {fileToken} file.");
    }

    private async Task RunRestoreAsync(
        RestoreSession session,
        string[] arguments,
        Action<string>? log,
        IProgress<RestoreProgress>? progress,
        CancellationToken cancellationToken)
    {
        await _runner.RunAsync(
            _tools.IdeviceRestore,
            arguments,
            session.SessionDirectory,
            line =>
            {
                log?.Invoke(line);
                if (TryParsePlainProgress(line, out var percentage))
                {
                    progress?.Report(new RestoreProgress(
                        RestoreStage.RestoringFirmware,
                        38 + percentage * 0.34,
                        "Restoring firmware",
                        line,
                        true));
                }
            },
            cancellationToken).ConfigureAwait(false);
    }

    private async Task TetherBootCoreAsync(
        string pteBlockPath,
        IProgress<RestoreProgress>? progress,
        Action<string>? log,
        CancellationToken cancellationToken)
    {
        progress?.Report(new RestoreProgress(RestoreStage.BootingPongo, 20, "Booting PongoOS", "Running checkm8 and uploading PongoOS."));
        await BootPongoAsync(log, cancellationToken).ConfigureAwait(false);

        var resourceRoot = Path.Combine(_tools.Root, "resources");
        var sepRacer = Path.Combine(resourceRoot, "sep_racer.bin");
        var kpf = Path.Combine(resourceRoot, "kpf.bin");
        if (!File.Exists(sepRacer) || !File.Exists(kpf))
        {
            throw new DarkSwordException(RestoreStage.LoadingSepExploit, "The release is missing sep_racer.bin or kpf.bin.");
        }

        progress?.Report(new RestoreProgress(RestoreStage.LoadingSepExploit, 45, "Running SEP exploit", "Loading sep_racer and the saved PTE block."));
        await _runner.RunAsync(
            _tools.PongoBridge,
            new[] { "boot", "--pteblock", pteBlockPath, "--sep-racer", sepRacer, "--kpf", kpf },
            _tools.Root,
            log,
            cancellationToken).ConfigureAwait(false);
        progress?.Report(new RestoreProgress(RestoreStage.BootingXnu, 90, "Booting XNU", "Applying tethered kernel patches and issuing bootux."));
    }

    private async Task<RestoreSession> CompleteAsync(RestoreSession session, CancellationToken cancellationToken)
    {
        session = session with { LastStage = RestoreStage.Completed, UpdatedAt = DateTimeOffset.UtcNow };
        await _sessions.SaveAsync(session, cancellationToken).ConfigureAwait(false);
        Mark(session.SessionDirectory, CompleteFlag);
        return session;
    }

    private static bool TryParsePlainProgress(string line, out double percent)
    {
        percent = 0;
        var values = line.Split(new[] { ' ', ':', '%', '[', ']' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(token => double.TryParse(token, out var value) ? value : -1)
            .Where(value => value is >= 0 and <= 100)
            .ToArray();
        if (values.Length == 0) return false;
        percent = values[^1];
        return true;
    }

    private static void Mark(string directory, string flag)
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, flag), DateTimeOffset.UtcNow.ToString("O"));
    }
}
