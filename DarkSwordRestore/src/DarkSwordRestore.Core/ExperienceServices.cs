using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DarkSwordRestore.Core;

public enum PreflightCheckState { Passed, Failed }

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
        AppleDeviceSnapshot snapshot;
        try
        {
            snapshot = await _devices.ProbeAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            snapshot = AppleDeviceSnapshot.Disconnected;
            Add("device-count", "One physical Apple target", false, exception.Message);
        }

        var administrator = IsAdministrator();
        Add("administrator", "Administrator access", administrator,
            administrator ? "Palera1nWin is running elevated." : "Restart Palera1nWin as administrator.");

        var missing = _tools.MissingFiles().ToList();
        foreach (var resource in new[] { "sep_racer.bin", "kpf.bin" })
        {
            var path = Path.Combine(_tools.Root, "resources", resource);
            if (!File.Exists(path)) missing.Add(path);
        }
        Add("toolchain", "Restore toolchain", missing.Count == 0,
            missing.Count == 0 ? "Every packaged restore component is present." :
            "Missing: " + string.Join(", ", missing.Select(Path.GetFileName)));

        var device = DarkSwordDeviceCatalog.Find(expectedProductType);
        var exactIdentity = snapshot.HasExactIdentity &&
                            string.Equals(snapshot.ProductType, expectedProductType, StringComparison.Ordinal);
        Add("device-identity", "Exact ProductType and ECID", device is not null && exactIdentity,
            device is null ? "Connect exactly one supported device." :
            exactIdentity ? $"{snapshot.ProductType} ECID {snapshot.NormalizedEcid} is bound to this preflight." :
            "The connected exact identity does not match the detected ProductType.");
        Add("backend", "Windows restore backend", device?.UsesA9SepBlocks == true,
            device?.UsesA9SepBlocks == true ? $"The {device.Chip} SHC/PTE backend is enabled." :
            "This chip's Windows restore backend is disabled.");

        IpswInspectionResult? inspection = null;
        if (!File.Exists(ipswPath))
        {
            Add("ipsw", "Firmware integrity", false, "Select an IPSW before preflight.");
        }
        else
        {
            try
            {
                inspection = await _inspector.InspectAsync(ipswPath, cancellationToken).ConfigureAwait(false);
                var match = inspection.IsValid && inspection.MatchesProductType(expectedProductType);
                Add("ipsw", "Firmware integrity", match,
                    match ? $"{inspection.ProductVersion} ({inspection.BuildVersion}) matches {expectedProductType}; SHA-256 {inspection.Sha256}." :
                    string.Join(" ", inspection.Errors));
            }
            catch (Exception exception)
            {
                Add("ipsw", "Firmware integrity", false, exception.Message);
            }
        }

        try
        {
            var root = Path.GetPathRoot(ipswPath) ?? Path.GetPathRoot(AppContext.BaseDirectory) ?? "C:\\";
            var drive = new DriveInfo(root);
            var size = File.Exists(ipswPath) ? new FileInfo(ipswPath).Length : 0;
            var required = Math.Max(20 * Gib, (long)(size * 2.5) + 5 * Gib);
            Add("disk", "Free disk space", drive.AvailableFreeSpace >= required,
                $"{FormatBytes(drive.AvailableFreeSpace)} free; {FormatBytes(required)} required.");
        }
        catch (Exception exception)
        {
            Add("disk", "Free disk space", false, exception.Message);
        }

        var online = NetworkInterface.GetIsNetworkAvailable();
        Add("network", "Internet connection", online, online ? "Network is available." : "Connect to the internet.");
        Add("dfu", "Apple DFU mode", snapshot.Mode == AppleDeviceMode.Dfu,
            snapshot.Mode == AppleDeviceMode.Dfu ? "Clean DFU detected." : $"Current mode is {snapshot.Mode}.");

        var repaired = false;
        var driverReady = snapshot.Mode == AppleDeviceMode.Dfu && DfuDriverService.IsLibusbK(snapshot.Service);
        if (snapshot.Mode == AppleDeviceMode.Dfu && !driverReady && repairDfuDriver && administrator)
        {
            try
            {
                var result = await _driver.EnsureDfuReadyAsync(_devices, SafeLog(log), cancellationToken).ConfigureAwait(false);
                snapshot = result.Snapshot;
                repaired = result.Changed;
                driverReady = DfuDriverService.IsLibusbK(snapshot.Service);
            }
            catch (Exception exception)
            {
                TryLog(log, $"DFU driver repair failed: {exception.Message}");
            }
        }
        Add("driver", "Apple DFU USB driver", driverReady,
            driverReady ? $"DFU uses {snapshot.Service ?? "libusbK"}." : "DFU is not using verified libusbK.",
            repaired && driverReady);

        var battery = await TryReadBatteryAsync(snapshot, cancellationToken).ConfigureAwait(false);
        Add("battery", "Battery and USB power", battery is null || battery >= 30,
            battery is null ? "Battery is unavailable in DFU; use a powered direct USB port." : $"Battery is {battery}%.");

        return new PreflightReport(
            DateTimeOffset.UtcNow,
            checks,
            snapshot,
            inspection,
            BuildFingerprint(expectedProductType, ipswPath, snapshot, inspection));

        void Add(string key, string title, bool passed, string detail, bool wasRepaired = false) =>
            checks.Add(new PreflightCheckResult(key, title,
                passed ? PreflightCheckState.Passed : PreflightCheckState.Failed,
                detail, wasRepaired));
    }

    public static string BuildFingerprint(
        string? productType,
        string ipswPath,
        AppleDeviceSnapshot snapshot,
        IpswInspectionResult? inspection = null)
    {
        var file = File.Exists(ipswPath) ? new FileInfo(ipswPath) : null;
        return string.Join('|',
            productType ?? string.Empty,
            snapshot.NormalizedEcid ?? string.Empty,
            ipswPath,
            file?.Length.ToString() ?? "0",
            file?.LastWriteTimeUtc.Ticks.ToString() ?? "0",
            snapshot.Mode,
            snapshot.Service ?? string.Empty,
            inspection?.Sha256 ?? string.Empty);
    }

    private async Task<int?> TryReadBatteryAsync(AppleDeviceSnapshot snapshot, CancellationToken cancellationToken)
    {
        if (snapshot.Mode != AppleDeviceMode.Normal) return null;
        var tool = Path.Combine(_tools.Root, "ideviceinfo.exe");
        if (!File.Exists(tool)) return null;
        var output = await RunCaptureAsync(tool,
            ["-q", "com.apple.mobile.battery", "-k", "BatteryCurrentCapacity"], cancellationToken)
            .ConfigureAwait(false);
        return int.TryParse(output.Trim(), out var capacity) ? capacity : null;
    }

    private static async Task<string> RunCaptureAsync(
        string fileName,
        IEnumerable<string> arguments,
        CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo
        {
            FileName = fileName,
            WorkingDirectory = Path.GetDirectoryName(fileName) ?? AppContext.BaseDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start);
        if (process is null) return string.Empty;
        var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        try { await process.WaitForExitAsync(linked.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
            return string.Empty;
        }
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
        while (size >= 1024 && unit < units.Length - 1) { size /= 1024; unit++; }
        return $"{size:F1} {units[unit]}";
    }

    private static Action<string>? SafeLog(Action<string>? log) => log is null ? null : value => TryLog(log, value);
    private static void TryLog(Action<string>? log, string value) { try { log?.Invoke(value); } catch { } }
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
    private static readonly Regex PercentPattern = new(@"(?<!\d)(?<value>\d{1,3}(?:\.\d+)?)\s*%",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
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
            try
            {
                var session = await _sessions.LoadAsync(directory, cancellationToken).ConfigureAwait(false);
                if (session is null || !session.HasBoundIdentity || session.LastStage == RestoreStage.Completed) continue;
                var candidate = BuildCandidate(session);
                if (candidate.CanResume) return candidate;
            }
            catch { }
        }
        return null;
    }

    public async Task MarkLatestCheckpointAsync(RestoreProgress progress, CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(_sessions.RootDirectory)) return;
        var active = new List<RestoreSession>();
        foreach (var directory in Directory.EnumerateDirectories(_sessions.RootDirectory))
        {
            try
            {
                var session = await _sessions.LoadAsync(directory, cancellationToken).ConfigureAwait(false);
                if (session is not null && session.HasBoundIdentity &&
                    session.LastStage is not RestoreStage.Completed and not RestoreStage.Cancelled)
                    active.Add(session);
            }
            catch { }
        }
        if (active.Count == 1)
            await MarkCheckpointAsync(active[0], progress, cancellationToken).ConfigureAwait(false);
    }

    private async Task MarkCheckpointAsync(
        RestoreSession session,
        RestoreProgress progress,
        CancellationToken cancellationToken)
    {
        var signature = $"{session.SessionId}:{progress.Stage}:{Math.Floor(progress.Percent)}:{progress.Title}";
        if (signature == _lastProgressSignature) return;
        _lastProgressSignature = signature;
        var path = Path.Combine(session.SessionDirectory, "recovery-progress.json");
        var temporary = path + ".tmp";
        var payload = new
        {
            schema = 2,
            session.SessionId,
            session.BoundProductType,
            session.BoundEcid,
            progress,
            writtenAt = DateTimeOffset.UtcNow,
        };
        await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(payload, JsonOptions), cancellationToken)
            .ConfigureAwait(false);
        File.Move(temporary, path, overwrite: true);
    }

    public async Task<RestoreSession> ResumeAsync(
        RecoveryCandidate candidate,
        string expectedProductType,
        IProgress<RestoreProgress>? progress,
        Action<string>? log,
        CancellationToken cancellationToken)
    {
        var session = candidate.Session;
        if (!session.HasBoundIdentity)
            throw new DarkSwordException(RestoreStage.Preflight, "Legacy ProductType-only sessions cannot be resumed safely.");
        if (!File.Exists(session.IpswPath))
            throw new FileNotFoundException("The session IPSW is missing.", session.IpswPath);
        if (!string.Equals(session.BoundProductType, expectedProductType, StringComparison.Ordinal) ||
            !session.Ipsw.MatchesProductType(expectedProductType))
            throw new DarkSwordException(RestoreStage.Preflight,
                $"Recovery is bound to {session.BoundProductType}, not {expectedProductType}.");

        var pre = candidate.PreRestoreShc;
        var post = candidate.PostRestoreShc;
        var pte = candidate.PteBlock;
        try
        {
            if (pte is not null)
            {
                progress?.Report(new RestoreProgress(RestoreStage.WaitingForDfu, 92,
                    "Resume final tether boot", "Enter DFU on the same ECID."));
                await _orchestrator.TetherBootAsync(pte, progress, SafeLog(log), cancellationToken,
                    session.BoundProductType, session.BoundEcid).ConfigureAwait(false);
                return await CompleteAsync(session with { PteBlockPath = pte }, cancellationToken).ConfigureAwait(false);
            }

            if (post is null && pre is not null)
            {
                await PreparePongoAsync(session, log, cancellationToken).ConfigureAwait(false);
                await RunRestoreAsync(session,
                    ["-o", "--plain-progress", "--no-input",
                     "--cache-path", Path.Combine(session.SessionDirectory, "cache"),
                     "--load-shcblock", pre, session.IpswPath],
                    log, progress, cancellationToken).ConfigureAwait(false);
                session = session with { LastStage = RestoreStage.RestoringFirmware, UpdatedAt = DateTimeOffset.UtcNow };
                await _sessions.SaveAsync(session, cancellationToken).ConfigureAwait(false);

                await PreparePongoAsync(session, log, cancellationToken).ConfigureAwait(false);
                post = await GenerateArtifactAsync(session, "post-shcblock", "shcblock",
                    ["--get-shcblock", "--cache-path", Path.Combine(session.SessionDirectory, "cache"), session.IpswPath],
                    log, cancellationToken, pre).ConfigureAwait(false);
            }

            if (post is null)
                throw new DarkSwordException(RestoreStage.Preflight, "No validated post-restore SHC exists.");

            await PreparePongoAsync(session, log, cancellationToken).ConfigureAwait(false);
            pte = await GenerateArtifactAsync(session, "pteblock", "pteblock",
                ["--get-pteblock", "--load-shcblock", post,
                 "--cache-path", Path.Combine(session.SessionDirectory, "cache"), session.IpswPath],
                log, cancellationToken).ConfigureAwait(false);
            session = session with
            {
                ShcBlockPath = post,
                PteBlockPath = pte,
                LastStage = RestoreStage.GeneratingPteBlock,
                UpdatedAt = DateTimeOffset.UtcNow,
            };
            await _sessions.SaveAsync(session, cancellationToken).ConfigureAwait(false);
            await _orchestrator.TetherBootAsync(pte, progress, SafeLog(log), cancellationToken,
                session.BoundProductType, session.BoundEcid).ConfigureAwait(false);
            return await CompleteAsync(session, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await SaveTerminalStateAsync(session, RestoreStage.Cancelled).ConfigureAwait(false);
            throw;
        }
        catch (Exception exception)
        {
            await SaveTerminalStateAsync(session, RestoreStage.Failed).ConfigureAwait(false);
            if (exception is DarkSwordException) throw;
            throw new DarkSwordException(RestoreStage.Failed, exception.Message, exception);
        }
    }

    private RecoveryCandidate BuildCandidate(RestoreSession session)
    {
        var artifacts = EnumerateValidatedArtifacts(session).ToArray();
        string? Find(string role) => artifacts
            .Where(item => string.Equals(item.Metadata.ArtifactType, role, StringComparison.Ordinal))
            .OrderByDescending(item => item.Metadata.CreatedAt)
            .Select(item => item.Path)
            .FirstOrDefault();
        var pre = Find("pre-shcblock");
        var post = Find("post-shcblock");
        var pte = Find("pteblock");
        var canResume = pte is not null || post is not null || pre is not null;
        var description = pte is not null ? "Validated ECID-bound PTE is ready." :
            post is not null ? "Validated post-restore SHC is ready for PTE generation." :
            pre is not null ? "Validated pre-restore SHC is ready for restore retry." :
            "No exact-session recovery artifact is available.";
        return new RecoveryCandidate(session, description, canResume, pre, post, pte);
    }

    private IEnumerable<(string Path, RestoreArtifactMetadata Metadata)> EnumerateValidatedArtifacts(RestoreSession session)
    {
        var root = Path.GetFullPath(session.SessionDirectory);
        if (!Directory.Exists(root)) yield break;
        foreach (var metadataPath in Directory.EnumerateFiles(root, "*.metadata.json", SearchOption.AllDirectories))
        {
            RestoreArtifactMetadata? metadata;
            try { metadata = JsonSerializer.Deserialize<RestoreArtifactMetadata>(File.ReadAllText(metadataPath), JsonOptions); }
            catch { continue; }
            if (metadata is null ||
                !string.Equals(metadata.SessionId, session.SessionId, StringComparison.Ordinal) ||
                !string.Equals(metadata.ProductVersion, session.Ipsw.ProductVersion, StringComparison.Ordinal) ||
                !string.Equals(metadata.BuildVersion, session.Ipsw.BuildVersion, StringComparison.Ordinal) ||
                !string.Equals(metadata.ProductType, session.BoundProductType, StringComparison.Ordinal) ||
                !string.Equals(AppleDeviceSnapshot.NormalizeEcid(metadata.Ecid),
                    AppleDeviceSnapshot.NormalizeEcid(session.BoundEcid), StringComparison.OrdinalIgnoreCase))
                continue;

            string path;
            try
            {
                path = !string.IsNullOrWhiteSpace(metadata.RelativePath)
                    ? Path.GetFullPath(Path.Combine(root, metadata.RelativePath.Replace('/', Path.DirectorySeparatorChar)))
                    : Path.GetFullPath(metadata.Path);
            }
            catch { continue; }
            if (!IsInside(root, path) || !File.Exists(path)) continue;
            var file = new FileInfo(path);
            if (file.Length <= 0 || file.Length != metadata.Size) continue;
            using var stream = File.OpenRead(path);
            var hash = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
            if (string.Equals(hash, metadata.Sha256, StringComparison.OrdinalIgnoreCase)) yield return (path, metadata);
        }
    }

    private async Task PreparePongoAsync(RestoreSession session, Action<string>? log, CancellationToken cancellationToken)
    {
        var snapshot = await _devices.WaitForModeAsync([AppleDeviceMode.Dfu], TimeSpan.FromMinutes(5), cancellationToken)
            .ConfigureAwait(false);
        var exact = await _devices.ProbeAsync(cancellationToken).ConfigureAwait(false);
        if (exact.Mode != AppleDeviceMode.Dfu) exact = snapshot;
        if (!session.MatchesBoundIdentity(exact))
            throw new DarkSwordException(RestoreStage.Preflight,
                $"Recovery is bound to ECID {session.BoundEcid}; connected ECID is {exact.NormalizedEcid}.");
        _ = await _orchestrator.ValidateDfuToPongoAsync(null, SafeLog(log), cancellationToken).ConfigureAwait(false);
    }

    private async Task<string> GenerateArtifactAsync(
        RestoreSession session,
        string role,
        string token,
        string[] arguments,
        Action<string>? log,
        CancellationToken cancellationToken,
        string? excludedPath = null)
    {
        var started = DateTimeOffset.UtcNow;
        var before = Directory.EnumerateFiles(session.SessionDirectory, "*", SearchOption.AllDirectories)
            .Select(Path.GetFullPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (excludedPath is not null) before.Add(Path.GetFullPath(excludedPath));
        await _runner.RunAsync(_tools.IdeviceRestore, arguments, session.SessionDirectory, SafeLog(log),
            cancellationToken, timeout: TimeSpan.FromMinutes(25)).ConfigureAwait(false);
        var files = Directory.EnumerateFiles(session.SessionDirectory, "*", SearchOption.AllDirectories)
            .Select(Path.GetFullPath)
            .Where(path => IsInside(session.SessionDirectory, path) && !before.Contains(path))
            .Where(path => !path.EndsWith(".metadata.json", StringComparison.OrdinalIgnoreCase))
            .Where(path => Path.GetFileName(path).Contains(token, StringComparison.OrdinalIgnoreCase))
            .Where(path => File.GetLastWriteTimeUtc(path) >= started.UtcDateTime.AddSeconds(-2))
            .ToArray();
        if (files.Length != 1) throw new InvalidDataException($"Expected one new {token} artifact; found {files.Length}.");

        var info = new FileInfo(files[0]);
        await using var stream = File.OpenRead(files[0]);
        var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false))
            .ToLowerInvariant();
        var metadata = new RestoreArtifactMetadata(session.SessionId, role, files[0], info.Length, hash,
            session.Ipsw.ProductVersion, session.Ipsw.BuildVersion, DateTimeOffset.UtcNow,
            session.BoundProductType, session.BoundEcid,
            Path.GetRelativePath(session.SessionDirectory, files[0]).Replace('\\', '/'));
        var metadataPath = files[0] + ".metadata.json";
        var temporary = metadataPath + ".tmp";
        await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(metadata, JsonOptions), cancellationToken)
            .ConfigureAwait(false);
        File.Move(temporary, metadataPath, overwrite: true);
        return files[0];
    }

    private async Task RunRestoreAsync(
        RestoreSession session,
        string[] arguments,
        Action<string>? log,
        IProgress<RestoreProgress>? progress,
        CancellationToken cancellationToken)
    {
        await _runner.RunAsync(_tools.IdeviceRestore, arguments, session.SessionDirectory, line =>
        {
            TryLog(log, line);
            if (TryParsePercent(line, out var value))
                progress?.Report(new RestoreProgress(RestoreStage.RestoringFirmware, 38 + value * 0.34,
                    "Restoring firmware", line, true));
        }, cancellationToken, timeout: TimeSpan.FromHours(2)).ConfigureAwait(false);
    }

    private async Task<RestoreSession> CompleteAsync(RestoreSession session, CancellationToken cancellationToken)
    {
        session = session with { LastStage = RestoreStage.Completed, UpdatedAt = DateTimeOffset.UtcNow };
        await _sessions.SaveAsync(session, cancellationToken).ConfigureAwait(false);
        return session;
    }

    private async Task SaveTerminalStateAsync(RestoreSession session, RestoreStage stage)
    {
        session = session with { LastStage = stage, UpdatedAt = DateTimeOffset.UtcNow };
        await _sessions.SaveAsync(session, CancellationToken.None).ConfigureAwait(false);
    }

    private static bool TryParsePercent(string line, out double value)
    {
        value = 0;
        var matches = PercentPattern.Matches(line);
        return matches.Count > 0 && double.TryParse(matches[^1].Groups["value"].Value,
            System.Globalization.NumberStyles.AllowDecimalPoint,
            System.Globalization.CultureInfo.InvariantCulture, out value) && value is >= 0 and <= 100;
    }

    private static bool IsInside(string root, string path)
    {
        var relative = Path.GetRelativePath(Path.GetFullPath(root), Path.GetFullPath(path));
        return relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }

    private static Action<string>? SafeLog(Action<string>? log) => log is null ? null : value => TryLog(log, value);
    private static void TryLog(Action<string>? log, string value) { try { log?.Invoke(value); } catch { } }
}
