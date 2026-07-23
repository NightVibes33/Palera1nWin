using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DarkSwordRestore.Core;

public sealed class RestoreSessionStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public RestoreSessionStore(string? rootDirectory = null)
    {
        RootDirectory = Path.GetFullPath(rootDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DarkSword Restore", "sessions"));
        Directory.CreateDirectory(RootDirectory);
    }

    public string RootDirectory { get; }

    public RestoreSession Create(string ipswPath, IpswInspectionResult ipsw)
    {
        var id = $"{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}"[..28];
        var directory = Path.Combine(RootDirectory, id);
        Directory.CreateDirectory(directory);
        Directory.CreateDirectory(Path.Combine(directory, "block"));
        Directory.CreateDirectory(Path.Combine(directory, "image4"));
        Directory.CreateDirectory(Path.Combine(directory, "cache"));
        var now = DateTimeOffset.UtcNow;
        return new RestoreSession(id, directory, ipswPath, ipsw, null, null, RestoreStage.Preflight, now, now);
    }

    public async Task SaveAsync(RestoreSession session, CancellationToken cancellationToken)
    {
        var directory = RequireInsideRoot(session.SessionDirectory);
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "session.json");
        var temporary = path + ".tmp";
        await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(session, JsonOptions), cancellationToken).ConfigureAwait(false);
        File.Move(temporary, path, overwrite: true);
    }

    public async Task<RestoreSession?> LoadAsync(string sessionDirectory, CancellationToken cancellationToken)
    {
        var directory = RequireInsideRoot(sessionDirectory);
        var path = Path.Combine(directory, "session.json");
        if (!File.Exists(path)) return null;
        await using var stream = File.OpenRead(path);
        var session = await JsonSerializer.DeserializeAsync<RestoreSession>(stream, JsonOptions, cancellationToken).ConfigureAwait(false);
        if (session is null) return null;
        if (!string.Equals(Path.GetFullPath(session.SessionDirectory), directory, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The session file points outside its containing session directory.");
        return session;
    }

    private string RequireInsideRoot(string path)
    {
        var full = Path.GetFullPath(path);
        var relative = Path.GetRelativePath(RootDirectory, full);
        if (relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new InvalidDataException("Session path is outside the DarkSword session store.");
        return full;
    }
}

public sealed record UsbDriverEnsureResult(AppleDeviceSnapshot Snapshot, bool Changed, string RequiredBinding);

public sealed class DfuDriverService
{
    private readonly ToolProcessRunner _runner;
    private readonly ToolchainPaths _tools;

    public DfuDriverService(ToolProcessRunner runner, ToolchainPaths tools)
    {
        _runner = runner;
        _tools = tools;
    }

    public Task<ToolResult> InstallLibusbKAsync(CancellationToken cancellationToken) =>
        InstallForPidAsync(0x1227, "Apple Mobile Device (DFU Mode)", cancellationToken);

    public Task<ToolResult> InstallPongoLibusbKAsync(CancellationToken cancellationToken) =>
        InstallForPidAsync(0x4141, "Apple Mobile Device (PongoOS Mode)", cancellationToken);

    public Task<UsbDriverEnsureResult> EnsureDfuReadyAsync(
        AppleDeviceMonitor devices, Action<string>? log, CancellationToken cancellationToken) =>
        EnsureModeAsync(devices, AppleDeviceMode.Dfu, 0x1227, "Apple Mobile Device (DFU Mode)",
            IsLibusbK, "libusbK", log, cancellationToken);

    public Task<UsbDriverEnsureResult> EnsurePongoReadyAsync(
        AppleDeviceMonitor devices, Action<string>? log, CancellationToken cancellationToken) =>
        EnsureModeAsync(devices, AppleDeviceMode.Pongo, 0x4141, "Apple Mobile Device (PongoOS Mode)",
            service => IsLibusbK(service) || IsWinUsb(service), "libusbK or WinUSB", log, cancellationToken);

    private async Task<UsbDriverEnsureResult> EnsureModeAsync(
        AppleDeviceMonitor devices,
        AppleDeviceMode requiredMode,
        ushort pid,
        string deviceName,
        Func<string?, bool> bindingAccepted,
        string requiredBinding,
        Action<string>? log,
        CancellationToken cancellationToken)
    {
        var current = await devices.ProbeAsync(cancellationToken).ConfigureAwait(false);
        if (current.Mode != requiredMode)
            throw new DarkSwordException(
                requiredMode == AppleDeviceMode.Pongo ? RestoreStage.BootingPongo : RestoreStage.InstallingDfuDriver,
                $"Expected {requiredMode}, but Windows reports {current.Mode}.");

        if (bindingAccepted(current.Service))
        {
            SafeLog(log, $"USB 05AC:{pid:X4} already uses {current.Service ?? requiredBinding}; no driver mutation.");
            return new UsbDriverEnsureResult(current, false, requiredBinding);
        }

        SafeLog(log, $"Installing libusbK once for exact Apple PID 05AC:{pid:X4}.");
        await InstallForPidAsync(pid, deviceName, cancellationToken).ConfigureAwait(false);
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(35);
        var last = AppleDeviceSnapshot.Disconnected;
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            last = await devices.ProbeAsync(cancellationToken).ConfigureAwait(false);
            if (last.Mode == requiredMode && bindingAccepted(last.Service))
                return new UsbDriverEnsureResult(last, true, requiredBinding);
            await Task.Delay(600, cancellationToken).ConfigureAwait(false);
        }
        throw new DarkSwordException(
            requiredMode == AppleDeviceMode.Pongo ? RestoreStage.BootingPongo : RestoreStage.InstallingDfuDriver,
            $"05AC:{pid:X4} did not re-enumerate with {requiredBinding}. Last mode={last.Mode}, service={last.Service ?? "unknown"}.");
    }

    private Task<ToolResult> InstallForPidAsync(ushort pid, string deviceName, CancellationToken cancellationToken) =>
        _runner.RunElevatedAsync(
            _tools.WdiSimple,
            ["--vid", "0x05AC", "--pid", $"0x{pid:X4}", "--type", "2", "--name", deviceName],
            _tools.Root,
            cancellationToken,
            TimeSpan.FromMinutes(2));

    public static bool IsLibusbK(string? service) =>
        !string.IsNullOrWhiteSpace(service) && service.Contains("libusb", StringComparison.OrdinalIgnoreCase);
    public static bool IsWinUsb(string? service) =>
        !string.IsNullOrWhiteSpace(service) && service.Contains("winusb", StringComparison.OrdinalIgnoreCase);

    private static void SafeLog(Action<string>? log, string message)
    {
        try { log?.Invoke(message); } catch { }
    }
}

public sealed record RestoreArtifactMetadata(
    string SessionId,
    string ArtifactType,
    string Path,
    long Size,
    string Sha256,
    string? ProductVersion,
    string? BuildVersion,
    DateTimeOffset CreatedAt,
    string? ProductType = null,
    string? Ecid = null,
    string? RelativePath = null);

public sealed class DarkSwordOrchestrator
{
    private static readonly Regex PercentPattern = new(@"(?<!\d)(?<value>\d{1,3}(?:\.\d+)?)\s*%", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private readonly ToolchainPaths _tools;
    private readonly ToolProcessRunner _runner;
    private readonly IpswInspector _inspector;
    private readonly AppleDeviceMonitor _devices;
    private readonly RestoreSessionStore _sessions;
    private readonly DfuDriverService _driver;

    public DarkSwordOrchestrator(
        ToolchainPaths tools,
        ToolProcessRunner runner,
        IpswInspector inspector,
        AppleDeviceMonitor devices,
        RestoreSessionStore sessions,
        DfuDriverService driver)
    {
        _tools = tools;
        _runner = runner;
        _inspector = inspector;
        _devices = devices;
        _sessions = sessions;
        _driver = driver;
    }

    public async Task<RestoreSession> RunFullDowngradeAsync(
        string ipswPath,
        bool destructiveOperationConfirmed,
        IProgress<RestoreProgress>? progress,
        Action<string>? log,
        CancellationToken cancellationToken,
        string? expectedHardwareGateEcid = null)
    {
        if (!destructiveOperationConfirmed)
            throw new DarkSwordException(RestoreStage.Preflight, "The erase and tethered-boot warning must be confirmed.");

        Report(RestoreStage.Preflight, 1, "Inspecting firmware", "Verifying exact ProductType, iOS/iPadOS 15, ZIP structure, and SHA-256.");
        var inspection = await _inspector.InspectAsync(ipswPath, cancellationToken).ConfigureAwait(false);
        if (!inspection.IsValid || !inspection.SupportsIpad5 || inspection.ProductVersion?.StartsWith("15.", StringComparison.Ordinal) != true)
            throw new DarkSwordException(RestoreStage.Preflight, string.Join(Environment.NewLine, inspection.Errors));
        var missing = _tools.MissingFiles();
        if (missing.Count > 0)
            throw new DarkSwordException(RestoreStage.Preflight,
                "The release toolchain is incomplete:" + Environment.NewLine + string.Join(Environment.NewLine, missing));

        var session = _sessions.Create(ipswPath, inspection);
        await _sessions.SaveAsync(session, cancellationToken).ConfigureAwait(false);
        try
        {
            Report(RestoreStage.WaitingForDfu, 5, "Enter DFU mode", "Connect exactly one target device and enter clean DFU.");
            var firstDfu = await PrepareDfuAsync(null, log, cancellationToken).ConfigureAwait(false);
            RequireExactIdentity(firstDfu, inspection);
            if (!string.IsNullOrWhiteSpace(expectedHardwareGateEcid) &&
                !string.Equals(firstDfu.NormalizedEcid, AppleDeviceSnapshot.NormalizeEcid(expectedHardwareGateEcid), StringComparison.OrdinalIgnoreCase))
                throw new DarkSwordException(RestoreStage.Preflight,
                    "The connected ECID does not match the device that passed the DFU → PongoOS hardware gate.");

            session = session with
            {
                BoundProductType = firstDfu.ProductType,
                BoundEcid = firstDfu.NormalizedEcid,
                LastStage = RestoreStage.WaitingForDfu,
                UpdatedAt = DateTimeOffset.UtcNow,
            };
            await _sessions.SaveAsync(session, cancellationToken).ConfigureAwait(false);

            await BootPongoAsync(log, cancellationToken).ConfigureAwait(false);
            Report(RestoreStage.GeneratingShcBlock, 20, "Capturing pre-restore SHC block", "Creating the initial restore-only SHC checkpoint.");
            var preShc = await RunBlockOperationAsync(
                session, "pre-shcblock", "shcblock",
                ["--get-shcblock", "--cache-path", Path.Combine(session.SessionDirectory, "cache"), ipswPath],
                log, cancellationToken).ConfigureAwait(false);
            session = session with { ShcBlockPath = preShc, LastStage = RestoreStage.GeneratingShcBlock, UpdatedAt = DateTimeOffset.UtcNow };
            await _sessions.SaveAsync(session, cancellationToken).ConfigureAwait(false);

            Report(RestoreStage.WaitingForDfu, 28, "Re-enter DFU mode", "The SHC capture rebooted the device. Re-enter DFU on the same ECID.");
            await PrepareDfuAsync(session, log, cancellationToken).ConfigureAwait(false);
            await BootPongoAsync(log, cancellationToken).ConfigureAwait(false);

            Report(RestoreStage.RestoringFirmware, 38, "Restoring firmware", "Erasing and restoring the exact iOS/iPadOS 15 IPSW.", true);
            await RunRestoreAsync(
                session,
                ["-o", "--plain-progress", "--no-input", "--cache-path", Path.Combine(session.SessionDirectory, "cache"), "--load-shcblock", preShc, ipswPath],
                log, progress, cancellationToken).ConfigureAwait(false);
            session = session with { LastStage = RestoreStage.RestoringFirmware, UpdatedAt = DateTimeOffset.UtcNow };
            await _sessions.SaveAsync(session, cancellationToken).ConfigureAwait(false);

            Report(RestoreStage.WaitingForDfu, 73, "Create permanent boot profile", "After restore, enter DFU on the same ECID.");
            await PrepareDfuAsync(session, log, cancellationToken).ConfigureAwait(false);
            await BootPongoAsync(log, cancellationToken).ConfigureAwait(false);

            Report(RestoreStage.GeneratingShcBlock, 78, "Capturing post-restore SHC block", "Creating the post-restore SHC checkpoint.");
            var postShc = await RunBlockOperationAsync(
                session, "post-shcblock", "shcblock",
                ["--get-shcblock", "--cache-path", Path.Combine(session.SessionDirectory, "cache"), ipswPath],
                log, cancellationToken, preShc).ConfigureAwait(false);

            Report(RestoreStage.WaitingForDfu, 83, "Enter DFU again", "Re-enter DFU on the same ECID for PTE generation.");
            await PrepareDfuAsync(session, log, cancellationToken).ConfigureAwait(false);
            await BootPongoAsync(log, cancellationToken).ConfigureAwait(false);

            Report(RestoreStage.GeneratingPteBlock, 87, "Generating PTE block", "Creating the ECID-bound SEP pairing block for cold boot.");
            var pte = await RunBlockOperationAsync(
                session, "pteblock", "pteblock",
                ["--get-pteblock", "--load-shcblock", postShc, "--cache-path", Path.Combine(session.SessionDirectory, "cache"), ipswPath],
                log, cancellationToken).ConfigureAwait(false);
            session = session with
            {
                ShcBlockPath = postShc,
                PteBlockPath = pte,
                LastStage = RestoreStage.GeneratingPteBlock,
                UpdatedAt = DateTimeOffset.UtcNow,
            };
            await _sessions.SaveAsync(session, cancellationToken).ConfigureAwait(false);

            Report(RestoreStage.WaitingForDfu, 92, "Final DFU entry", "Enter DFU on the same ECID for the first tether boot.");
            await PrepareDfuAsync(session, log, cancellationToken).ConfigureAwait(false);
            await TetherBootCoreAsync(pte, progress, log, cancellationToken).ConfigureAwait(false);

            session = session with { LastStage = RestoreStage.Completed, UpdatedAt = DateTimeOffset.UtcNow };
            await _sessions.SaveAsync(session, cancellationToken).ConfigureAwait(false);
            Report(RestoreStage.Completed, 100, "Downgrade complete", "The exact target received the complete tether boot sequence.");
            return session;
        }
        catch (OperationCanceledException)
        {
            session = session with { LastStage = RestoreStage.Cancelled, UpdatedAt = DateTimeOffset.UtcNow };
            await _sessions.SaveAsync(session, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        catch (Exception exception)
        {
            session = session with { LastStage = RestoreStage.Failed, UpdatedAt = DateTimeOffset.UtcNow };
            await _sessions.SaveAsync(session, CancellationToken.None).ConfigureAwait(false);
            if (exception is DarkSwordException) throw;
            throw new DarkSwordException(RestoreStage.Failed, exception.Message, exception);
        }

        void Report(RestoreStage stage, double percent, string title, string detail, bool destructive = false) =>
            progress?.Report(new RestoreProgress(stage, percent, title, detail, destructive));
    }

    public async Task<AppleDeviceSnapshot> ValidateDfuToPongoAsync(
        IProgress<RestoreProgress>? progress,
        Action<string>? log,
        CancellationToken cancellationToken)
    {
        progress?.Report(new RestoreProgress(RestoreStage.WaitingForDfu, 5, "Hardware validation", "Enter clean DFU with exactly one Apple device connected."));
        var identity = await PrepareDfuAsync(null, log, cancellationToken).ConfigureAwait(false);
        if (!identity.HasExactIdentity)
            throw new DarkSwordException(RestoreStage.Preflight, "ProductType and ECID must both be readable before the hardware gate can pass.");
        progress?.Report(new RestoreProgress(RestoreStage.BootingPongo, 35, "Testing checkm8 and PongoOS", "Running the non-destructive ECID-bound hardware gate."));
        await BootPongoAsync(log, cancellationToken).ConfigureAwait(false);
        progress?.Report(new RestoreProgress(RestoreStage.Completed, 100, "Hardware gate passed", $"PongoOS bridge verified for {identity.ProductType} ECID {identity.NormalizedEcid}."));
        return identity;
    }

    public async Task TetherBootAsync(
        string pteBlockPath,
        IProgress<RestoreProgress>? progress,
        Action<string>? log,
        CancellationToken cancellationToken,
        string? expectedProductType = null,
        string? expectedEcid = null)
    {
        if (!File.Exists(pteBlockPath)) throw new FileNotFoundException("PTE block not found.", pteBlockPath);
        progress?.Report(new RestoreProgress(RestoreStage.WaitingForDfu, 5, "Enter DFU mode", "Connect exactly one downgraded device and enter DFU."));
        var dfu = await PrepareDfuAsync(null, log, cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(expectedProductType) || !string.IsNullOrWhiteSpace(expectedEcid))
        {
            if (!dfu.MatchesIdentity(expectedProductType, expectedEcid))
                throw new DarkSwordException(RestoreStage.Preflight, "The connected DFU ProductType/ECID does not match the requested cold-boot profile.");
        }
        await TetherBootCoreAsync(pteBlockPath, progress, log, cancellationToken).ConfigureAwait(false);
        progress?.Report(new RestoreProgress(RestoreStage.Completed, 100, "Boot complete", "The validated tether boot sequence was sent."));
    }

    private async Task<AppleDeviceSnapshot> PrepareDfuAsync(
        RestoreSession? session,
        Action<string>? log,
        CancellationToken cancellationToken)
    {
        var observed = await _devices.WaitForModeAsync([AppleDeviceMode.Dfu], TimeSpan.FromMinutes(5), cancellationToken).ConfigureAwait(false);
        var exact = await _devices.ProbeAsync(cancellationToken).ConfigureAwait(false);
        if (exact.Mode != AppleDeviceMode.Dfu) exact = observed;
        if (!exact.HasExactIdentity)
            throw new DarkSwordException(RestoreStage.WaitingForDfu, "DFU was detected, but ProductType/ECID could not be read. Reconnect and retry.");
        if (session is not null && !session.MatchesBoundIdentity(exact))
            throw new DarkSwordException(RestoreStage.Preflight,
                $"Wrong physical device. Session is bound to {session.BoundProductType} ECID {session.BoundEcid}, but DFU reports {exact.ProductType} ECID {exact.NormalizedEcid}.");

        var result = await _driver.EnsureDfuReadyAsync(_devices, log, cancellationToken).ConfigureAwait(false);
        if (session is not null && !session.MatchesBoundIdentity(result.Snapshot))
            throw new DarkSwordException(RestoreStage.Preflight, "Device identity changed during the DFU driver transaction.");
        return result.Snapshot;
    }

    private static void RequireExactIdentity(AppleDeviceSnapshot dfu, IpswInspectionResult inspection)
    {
        if (!dfu.HasExactIdentity)
            throw new DarkSwordException(RestoreStage.Preflight, "Exact ProductType and ECID are required.");
        if (!inspection.MatchesProductType(dfu.ProductType))
            throw new DarkSwordException(RestoreStage.Preflight,
                $"The IPSW does not contain connected ProductType {dfu.ProductType}.");
    }

    private async Task TetherBootCoreAsync(
        string pteBlockPath,
        IProgress<RestoreProgress>? progress,
        Action<string>? log,
        CancellationToken cancellationToken)
    {
        progress?.Report(new RestoreProgress(RestoreStage.BootingPongo, 20, "Booting PongoOS", "Running checkm8 and verifying the bridge."));
        await BootPongoAsync(log, cancellationToken).ConfigureAwait(false);
        var resourceRoot = Path.Combine(_tools.Root, "resources");
        var sepRacer = Path.Combine(resourceRoot, "sep_racer.bin");
        var kpf = Path.Combine(resourceRoot, "kpf.bin");
        if (!File.Exists(sepRacer) || !File.Exists(kpf))
            throw new DarkSwordException(RestoreStage.LoadingSepExploit, "The release is missing sep_racer.bin or kpf.bin.");

        progress?.Report(new RestoreProgress(RestoreStage.LoadingSepExploit, 45, "Running SEP exploit", "Loading the exact PTE, sep_racer, and KPF assets."));
        await _runner.RunAsync(
            _tools.PongoBridge,
            ["boot", "--pteblock", pteBlockPath, "--sep-racer", sepRacer, "--kpf", kpf],
            _tools.Root, log, cancellationToken, timeout: TimeSpan.FromMinutes(5)).ConfigureAwait(false);
        progress?.Report(new RestoreProgress(RestoreStage.BootingXnu, 90, "Booting XNU", "Pongo accepted the final boot command sequence."));
    }

    private async Task BootPongoAsync(Action<string>? log, CancellationToken cancellationToken)
    {
        var current = await _devices.ProbeAsync(cancellationToken).ConfigureAwait(false);
        if (current.Mode == AppleDeviceMode.Pongo)
        {
            await EnsurePongoAccessibleAsync(log, cancellationToken).ConfigureAwait(false);
            return;
        }
        if (current.Mode != AppleDeviceMode.Dfu)
            throw new DarkSwordException(RestoreStage.BootingPongo, $"openra1n requires clean DFU; current mode is {current.Mode}.");

        await using var openSession = _runner.StartSession(
            _tools.OpenRa1n, [], _tools.Root, log, cancellationToken, TimeSpan.FromMinutes(3));
        try
        {
            var pongoTask = _devices.WaitForModeAsync([AppleDeviceMode.Pongo], TimeSpan.FromMinutes(2), cancellationToken);
            var winner = await Task.WhenAny(pongoTask, openSession.Completion).ConfigureAwait(false);
            if (winner == openSession.Completion)
            {
                var result = await openSession.Completion.ConfigureAwait(false);
                var after = await _devices.ProbeAsync(cancellationToken).ConfigureAwait(false);
                if (after.Mode != AppleDeviceMode.Pongo)
                    throw new DarkSwordException(RestoreStage.BootingPongo,
                        $"openra1n exited before PongoOS. Exit={result.ExitCode}; mode={after.Mode}; service={after.Service ?? "unknown"}.\n{result.StandardError}");
            }
            else
            {
                await pongoTask.ConfigureAwait(false);
            }
            openSession.Kill();
            await EnsurePongoAccessibleAsync(log, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            openSession.Kill();
        }
    }

    private async Task EnsurePongoAccessibleAsync(Action<string>? log, CancellationToken cancellationToken)
    {
        await _driver.EnsurePongoReadyAsync(_devices, log, cancellationToken).ConfigureAwait(false);
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(30);
        ToolResult? last = null;
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            last = await _runner.RunAsync(
                _tools.PongoBridge, ["probe"], _tools.Root, log, cancellationToken,
                requireZeroExitCode: false, timeout: TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            if (last.Success) return;
            await Task.Delay(600, cancellationToken).ConfigureAwait(false);
        }
        throw new DarkSwordException(RestoreStage.BootingPongo,
            $"Pongo enumerated but the bridge could not open it. Exit={last?.ExitCode}; stderr={last?.StandardError ?? "none"}.");
    }

    private async Task<string> RunBlockOperationAsync(
        RestoreSession session,
        string artifactRole,
        string fileToken,
        string[] arguments,
        Action<string>? log,
        CancellationToken cancellationToken,
        string? excludedPath = null)
    {
        var started = DateTimeOffset.UtcNow;
        var before = Directory.EnumerateFiles(session.SessionDirectory, "*", SearchOption.AllDirectories)
            .Select(Path.GetFullPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (excludedPath is not null) before.Add(Path.GetFullPath(excludedPath));

        var result = await _runner.RunAsync(
            _tools.IdeviceRestore, arguments, session.SessionDirectory, log, cancellationToken,
            timeout: TimeSpan.FromMinutes(25)).ConfigureAwait(false);
        var candidates = Directory.EnumerateFiles(session.SessionDirectory, "*", SearchOption.AllDirectories)
            .Select(Path.GetFullPath)
            .Where(path => IsInside(session.SessionDirectory, path))
            .Where(path => !before.Contains(path))
            .Where(path => Path.GetFileName(path).Contains(fileToken, StringComparison.OrdinalIgnoreCase))
            .Where(path => !path.EndsWith(".metadata.json", StringComparison.OrdinalIgnoreCase))
            .Where(path => File.GetLastWriteTimeUtc(path) >= started.UtcDateTime.AddSeconds(-2))
            .ToArray();
        if (candidates.Length != 1)
            throw new DarkSwordException(
                fileToken.Contains("pte", StringComparison.OrdinalIgnoreCase) ? RestoreStage.GeneratingPteBlock : RestoreStage.GeneratingShcBlock,
                $"Expected exactly one new {fileToken} artifact, found {candidates.Length}. Native output:\n{result.CombinedOutput}");

        await ValidateAndRecordArtifactAsync(session, candidates[0], artifactRole, cancellationToken).ConfigureAwait(false);
        return candidates[0];
    }

    private static async Task ValidateAndRecordArtifactAsync(
        RestoreSession session,
        string path,
        string artifactType,
        CancellationToken cancellationToken)
    {
        var full = Path.GetFullPath(path);
        if (!IsInside(session.SessionDirectory, full)) throw new InvalidDataException("Generated artifact escaped the session directory.");
        var file = new FileInfo(full);
        if (!file.Exists || file.Length <= 0) throw new InvalidDataException($"Generated {artifactType} is missing or empty.");
        await using var stream = File.OpenRead(full);
        var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false)).ToLowerInvariant();
        var metadata = new RestoreArtifactMetadata(
            session.SessionId, artifactType, full, file.Length, hash,
            session.Ipsw.ProductVersion, session.Ipsw.BuildVersion, DateTimeOffset.UtcNow,
            session.BoundProductType, session.BoundEcid,
            Path.GetRelativePath(session.SessionDirectory, full).Replace('\\', '/'));
        var metadataPath = full + ".metadata.json";
        var temporary = metadataPath + ".tmp";
        await File.WriteAllTextAsync(temporary,
            JsonSerializer.Serialize(metadata, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }),
            cancellationToken).ConfigureAwait(false);
        File.Move(temporary, metadataPath, overwrite: true);
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
                try { log?.Invoke(line); } catch { }
                if (TryParsePlainProgress(line, out var percentage, out var detail))
                    progress?.Report(new RestoreProgress(RestoreStage.RestoringFirmware, 38 + percentage * 0.34, "Restoring firmware", detail, true));
            },
            cancellationToken,
            timeout: TimeSpan.FromHours(2)).ConfigureAwait(false);
    }

    private static bool TryParsePlainProgress(string line, out double percent, out string detail)
    {
        percent = 0;
        detail = line;
        var matches = PercentPattern.Matches(line);
        if (matches.Count == 0) return false;
        if (!double.TryParse(matches[^1].Groups["value"].Value,
                System.Globalization.NumberStyles.AllowDecimalPoint,
                System.Globalization.CultureInfo.InvariantCulture,
                out percent)) return false;
        return percent is >= 0 and <= 100;
    }

    private static bool IsInside(string root, string path)
    {
        var relative = Path.GetRelativePath(Path.GetFullPath(root), Path.GetFullPath(path));
        return relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }
}
