using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Security.Cryptography;
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
        var administrator = IsAdministrator();

        Add(
            "administrator",
            "Administrator access",
            administrator,
            administrator
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

        var activeDevice = DarkSwordDeviceCatalog.Find(expectedProductType);
        Add(
            "device-identity",
            "Exact device identity",
            activeDevice is not null,
            activeDevice is not null
                ? $"{activeDevice.DisplayName} ({activeDevice.ProductType}, {activeDevice.Chip}) detected."
                : string.IsNullOrWhiteSpace(expectedProductType)
                    ? "Connect and unlock the device once so its exact ProductType can be read."
                    : $"{expectedProductType} is outside the supported A9-A10X device catalog.");
        Add(
            "backend",
            "Windows restore backend",
            activeDevice?.UsesA9SepBlocks == true,
            activeDevice?.UsesA9SepBlocks == true
                ? $"The {activeDevice.Chip} SHC/PTE backend is enabled."
                : activeDevice is null
                    ? "The backend cannot be selected until ProductType is known."
                    : $"{activeDevice.Chip} detection and DFU guidance are available, but its restore backend is not enabled.");

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
                $"{FormatBytes(drive.AvailableFreeSpace)} free on {drive.Name}; {FormatBytes(required)} required.");
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
                ? "A network connection is available."
                : "Connect this PC to the internet before continuing.");

        Add(
            "dfu",
            "Apple DFU mode",
            snapshot.Mode == AppleDeviceMode.Dfu,
            snapshot.Mode == AppleDeviceMode.Dfu
                ? "Apple DFU is detected and the device screen should be completely black."
                : snapshot.Mode == AppleDeviceMode.Recovery
                    ? "Recovery Mode was detected. Use the guided DFU sequence until the screen stays black."
                    : $"Current mode is {snapshot.Mode}. Complete the guided DFU sequence before starting.");

        var repaired = false;
        var driverReady = snapshot.Mode == AppleDeviceMode.Dfu && DfuDriverService.IsLibusbK(snapshot.Service);
        if (snapshot.Mode == AppleDeviceMode.Dfu && !driverReady && repairDfuDriver && administrator)
        {
            try
            {
                var result = await _driver.EnsureDfuReadyAsync(_devices, log, cancellationToken).ConfigureAwait(false);
                snapshot = result.Snapshot;
                repaired = result.Changed;
                driverReady = DfuDriverService.IsLibusbK(snapshot.Service);
            }
            catch (Exception exception)
            {
                log?.Invoke($"DFU driver repair failed: {exception.Message}");
            }
        }
        Add(
            "driver",
            "Apple DFU USB driver",
            driverReady,
            driverReady
                ? $"Apple DFU is attached through {snapshot.Service ?? "libusbK"}."
                : snapshot.Mode != AppleDeviceMode.Dfu
                    ? "Driver state is checked after DFU is detected."
                    : $"Apple DFU is using '{snapshot.Service ?? "unknown"}'. The verified driver transaction did not complete.",
            repaired && driverReady);

        var battery = await TryReadBatteryAsync(snapshot, cancellationToken).ConfigureAwait(false);
        Add(
            "battery",
            "Battery and USB power",
            battery is null || battery >= 30,
            battery is null
                ? "Battery percentage is unavailable in DFU; keep the device connected directly to a powered USB port."
                : battery >= 30 ? $"Battery is {battery}%." : $"Battery is only {battery}%. Charge to at least 30%.");

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
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly ToolchainPaths _tools;
    private readonly ToolProcessRunner _runner;
    private readonly AppleDeviceMonitor _devices;
    private readonly RestoreSessionStore _sessions;
    private readonly DarkSwordOrchestrator _orchestrator;
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
        _orchestrator = new DarkSwordOrchestrator(tools, runner, new IpswInspector(), devices, sessions, driver);
    }

    public async Task<RecoveryCandidate?> FindLatestRecoverableAsync(CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(_sessions.RootDirectory)) return null;
        foreach (var directory in Directory.EnumerateDirectories(_sessions.RootDirectory)
                     .OrderByDescending(Directory.GetLastWriteTimeUtc))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var session = await _sessions.LoadAsync(directory, cancellationToken).ConfigureAwait(false);
            if (session is null || session.LastStage == RestoreStage.Completed) continue;
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
            throw new FileNotFoundException("The IPSW used by this recovery session is missing.", session.IpswPath);
        if (!session.Ipsw.MatchesProductType(expectedProductType))
            throw new DarkSwordException(
                RestoreStage.Preflight,
                $"Recovery session targets {string.Join(", ", session.Ipsw.SupportedProductTypes)}, not connected device {expectedProductType}.");

        var preShc = candidate.PreRestoreShc;
        var postShc = candidate.PostRestoreShc;
        var pte = candidate.PteBlock;
        var directory = session.SessionDirectory;

        try
        {
            if (pte is not null)
            {
                Report(RestoreStage.WaitingForDfu, 92, "Resume final tether boot", "Validated PTE found. Enter DFU to boot the restored system.");
                await _orchestrator.TetherBootAsync(pte, progress, log, cancellationToken).ConfigureAwait(false);
                return await CompleteAsync(session with { PteBlockPath = pte }, cancellationToken).ConfigureAwait(false);
            }

            if (postShc is null && preShc is not null)
            {
                Report(RestoreStage.WaitingForDfu, 28, "Resume firmware restore", "Validated pre-restore SHC found. Enter DFU to retry the restore.");
                await PreparePongoAsync(log, cancellationToken).ConfigureAwait(false);
                Report(RestoreStage.RestoringFirmware, 38, "Retrying firmware restore", "Reusing the validated SHC and firmware cache.", true);
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
                session = session with { LastStage = RestoreStage.RestoringFirmware, UpdatedAt = DateTimeOffset.UtcNow };
                await _sessions.SaveAsync(session, cancellationToken).ConfigureAwait(false);

                Report(RestoreStage.WaitingForDfu, 73, "Capture post-restore SHC", "Restore completed. Enter DFU again.");
                await PreparePongoAsync(log, cancellationToken).ConfigureAwait(false);
                postShc = await GenerateArtifactAsync(
                    session,
                    "shcblock",
                    new[] { "--get-shcblock", "--cache-path", Path.Combine(directory, "cache"), session.IpswPath },
                    log,
                    cancellationToken,
                    excludedPath: preShc).ConfigureAwait(false);
            }

            if (postShc is null)
                throw new DarkSwordException(RestoreStage.Preflight, "No validated SHC recovery artifact exists.");

            Report(RestoreStage.WaitingForDfu, 83, "Resume PTE generation", "Validated post-restore SHC found. Enter DFU.");
            await PreparePongoAsync(log, cancellationToken).ConfigureAwait(false);
            Report(RestoreStage.GeneratingPteBlock, 87, "Generating PTE block", "Creating the permanent tether-boot asset.");
            pte = await GenerateArtifactAsync(
                session,
                "pteblock",
                new[]
                {
                    "--get-pteblock", "--load-shcblock", postShc,
                    "--cache-path", Path.Combine(directory, "cache"), session.IpswPath
                },
                log,
                cancellationToken).ConfigureAwait(false);

            session = session with
            {
                ShcBlockPath = postShc,
                PteBlockPath = pte,
                LastStage = RestoreStage.GeneratingPteBlock,
                UpdatedAt = DateTimeOffset.UtcNow
            };
            await _sessions.SaveAsync(session, cancellationToken).ConfigureAwait(false);

            Report(RestoreStage.WaitingForDfu, 92, "Final DFU entry", "Enter DFU once more to boot the restored system.");
            await _orchestrator.TetherBootAsync(pte, progress, log, cancellationToken).ConfigureAwait(false);
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
        var artifacts = EnumerateValidatedArtifacts(session.SessionDirectory).ToArray();
        var shc = artifacts
            .Where(item => item.Metadata.ArtifactType.Contains("shc", StringComparison.OrdinalIgnoreCase))
            .OrderBy(item => item.Metadata.CreatedAt)
            .Select(item => item.Path)
            .ToArray();
        var pte = artifacts
            .Where(item => item.Metadata.ArtifactType.Contains("pte", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(item => item.Metadata.CreatedAt)
            .Select(item => item.Path)
            .FirstOrDefault();

        var pre = shc.FirstOrDefault();
        var post = shc.Length >= 2 ? shc[^1] : null;
        var canResume = pte is not null || post is not null || pre is not null;
        var description = pte is not null
            ? "Validated PTE is complete; retry final tether boot."
            : post is not null
                ? "Validated post-restore SHC exists; continue with PTE generation."
                : pre is not null
                    ? "Validated pre-restore SHC exists; retry restore without repeating the first capture."
                    : "No validated recovery artifact is available.";
        return new RecoveryCandidate(session, description, canResume, pre, post, pte);
    }

    private IEnumerable<(string Path, RestoreArtifactMetadata Metadata)> EnumerateValidatedArtifacts(string sessionDirectory)
    {
        if (!Directory.Exists(sessionDirectory)) yield break;
        foreach (var metadataPath in Directory.EnumerateFiles(sessionDirectory, "*.metadata.json", SearchOption.AllDirectories))
        {
            RestoreArtifactMetadata? metadata;
            try
            {
                metadata = JsonSerializer.Deserialize<RestoreArtifactMetadata>(File.ReadAllText(metadataPath), JsonOptions);
            }
            catch
            {
                continue;
            }
            if (metadata is null || !File.Exists(metadata.Path)) continue;
            var file = new FileInfo(metadata.Path);
            if (file.Length != metadata.Size || file.Length <= 0) continue;
            using var stream = File.OpenRead(metadata.Path);
            var actual = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
            if (!string.Equals(actual, metadata.Sha256, StringComparison.OrdinalIgnoreCase)) continue;
            yield return (metadata.Path, metadata);
        }
    }

    private Task PreparePongoAsync(Action<string>? log, CancellationToken cancellationToken) =>
        _orchestrator.ValidateDfuToPongoAsync(null, log, cancellationToken);

    private async Task<string> GenerateArtifactAsync(
        RestoreSession session,
        string artifactType,
        string[] arguments,
        Action<string>? log,
        CancellationToken cancellationToken,
        string? excludedPath = null)
    {
        var started = DateTimeOffset.UtcNow;
        var before = Directory.EnumerateFiles(session.SessionDirectory, "*", SearchOption.AllDirectories)
            .Select(Path.GetFullPath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (excludedPath is not null) before.Add(Path.GetFullPath(excludedPath));

        await _runner.RunAsync(_tools.IdeviceRestore, arguments, session.SessionDirectory, log, cancellationToken).ConfigureAwait(false);
        var generated = Directory.EnumerateFiles(session.SessionDirectory, "*", SearchOption.AllDirectories)
            .Select(Path.GetFullPath)
            .Where(path => Path.GetFileName(path).Contains(artifactType, StringComparison.OrdinalIgnoreCase))
            .Where(path => !before.Contains(path))
            .Where(path => File.GetLastWriteTimeUtc(path) >= started.UtcDateTime.AddSeconds(-2))
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault()
            ?? throw new DarkSwordException(
                artifactType.Contains("pte", StringComparison.OrdinalIgnoreCase) ? RestoreStage.GeneratingPteBlock : RestoreStage.GeneratingShcBlock,
                $"The tool completed without producing a new {artifactType} file.");

        var file = new FileInfo(generated);
        if (file.Length <= 0) throw new InvalidDataException($"Generated artifact is empty: {generated}");
        await using var stream = File.OpenRead(generated);
        var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false)).ToLowerInvariant();
        var metadata = new RestoreArtifactMetadata(
            session.SessionId,
            artifactType,
            generated,
            file.Length,
            hash,
            session.Ipsw.ProductVersion,
            session.Ipsw.BuildVersion,
            DateTimeOffset.UtcNow);
        await File.WriteAllTextAsync(
            generated + ".metadata.json",
            JsonSerializer.Serialize(metadata, JsonOptions),
            cancellationToken).ConfigureAwait(false);
        return generated;
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

    private async Task<RestoreSession> CompleteAsync(RestoreSession session, CancellationToken cancellationToken)
    {
        session = session with { LastStage = RestoreStage.Completed, UpdatedAt = DateTimeOffset.UtcNow };
        await _sessions.SaveAsync(session, cancellationToken).ConfigureAwait(false);
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
}
