using System.Security.Cryptography;
using System.Text.Json;

namespace DarkSwordRestore.Core;

public sealed class RestoreSessionStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public string RootDirectory { get; }

    public RestoreSessionStore(string? rootDirectory = null)
    {
        RootDirectory = rootDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DarkSword Restore", "sessions");
        Directory.CreateDirectory(RootDirectory);
    }

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
        Directory.CreateDirectory(session.SessionDirectory);
        var path = Path.Combine(session.SessionDirectory, "session.json");
        var temporary = path + ".tmp";
        await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(session, JsonOptions), cancellationToken).ConfigureAwait(false);
        File.Move(temporary, path, overwrite: true);
    }

    public async Task<RestoreSession?> LoadAsync(string sessionDirectory, CancellationToken cancellationToken)
    {
        var path = Path.Combine(sessionDirectory, "session.json");
        if (!File.Exists(path)) return null;
        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<RestoreSession>(stream, JsonOptions, cancellationToken).ConfigureAwait(false);
    }
}

public sealed record UsbDriverEnsureResult(
    AppleDeviceSnapshot Snapshot,
    bool Changed,
    string RequiredBinding);

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
        AppleDeviceMonitor devices,
        Action<string>? log,
        CancellationToken cancellationToken) =>
        EnsureModeAsync(
            devices,
            AppleDeviceMode.Dfu,
            0x1227,
            "Apple Mobile Device (DFU Mode)",
            service => IsLibusbK(service),
            "libusbK",
            log,
            cancellationToken);

    public Task<UsbDriverEnsureResult> EnsurePongoReadyAsync(
        AppleDeviceMonitor devices,
        Action<string>? log,
        CancellationToken cancellationToken) =>
        EnsureModeAsync(
            devices,
            AppleDeviceMode.Pongo,
            0x4141,
            "Apple Mobile Device (PongoOS Mode)",
            service => IsLibusbK(service) || IsWinUsb(service),
            "libusbK or WinUSB",
            log,
            cancellationToken);

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
        {
            throw new DarkSwordException(
                requiredMode == AppleDeviceMode.Pongo ? RestoreStage.BootingPongo : RestoreStage.InstallingDfuDriver,
                $"Expected {requiredMode}, but Windows currently reports {current.Mode}.");
        }

        if (bindingAccepted(current.Service))
        {
            log?.Invoke($"USB driver already acceptable for 05AC:{pid:X4}: {current.Service ?? "unknown"}. No reinstall needed.");
            return new UsbDriverEnsureResult(current, false, requiredBinding);
        }

        log?.Invoke($"USB 05AC:{pid:X4} is using '{current.Service ?? "unknown"}'. Installing libusbK once.");
        await InstallForPidAsync(pid, deviceName, cancellationToken).ConfigureAwait(false);

        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(35);
        AppleDeviceSnapshot last = AppleDeviceSnapshot.Disconnected;
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            last = await devices.ProbeAsync(cancellationToken).ConfigureAwait(false);
            if (last.Mode == requiredMode && bindingAccepted(last.Service))
            {
                log?.Invoke($"USB 05AC:{pid:X4} re-enumerated with {last.Service ?? requiredBinding}.");
                return new UsbDriverEnsureResult(last, true, requiredBinding);
            }

            await Task.Delay(750, cancellationToken).ConfigureAwait(false);
        }

        throw new DarkSwordException(
            requiredMode == AppleDeviceMode.Pongo ? RestoreStage.BootingPongo : RestoreStage.InstallingDfuDriver,
            $"USB 05AC:{pid:X4} did not re-enumerate with {requiredBinding}. Last mode={last.Mode}, service={last.Service ?? "unknown"}.");
    }

    private Task<ToolResult> InstallForPidAsync(ushort pid, string deviceName, CancellationToken cancellationToken) =>
        _runner.RunElevatedAsync(
            _tools.WdiSimple,
            new[]
            {
                "--vid", "0x05AC",
                "--pid", $"0x{pid:X4}",
                "--type", "2",
                "--name", deviceName
            },
            _tools.Root,
            cancellationToken);

    public static bool IsLibusbK(string? service) =>
        !string.IsNullOrWhiteSpace(service) &&
        service.Contains("libusb", StringComparison.OrdinalIgnoreCase);

    public static bool IsWinUsb(string? service) =>
        !string.IsNullOrWhiteSpace(service) &&
        service.Contains("winusb", StringComparison.OrdinalIgnoreCase);
}

public sealed record RestoreArtifactMetadata(
    string SessionId,
    string ArtifactType,
    string Path,
    long Size,
    string Sha256,
    string? ProductVersion,
    string? BuildVersion,
    DateTimeOffset CreatedAt);

public sealed class DarkSwordOrchestrator
{
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
        CancellationToken cancellationToken)
    {
        if (!destructiveOperationConfirmed)
        {
            throw new DarkSwordException(RestoreStage.Preflight, "The erase and tethered-boot warning must be confirmed.");
        }

        Report(RestoreStage.Preflight, 1, "Inspecting firmware", "Verifying the selected Apple IPSW.");
        var inspection = await _inspector.InspectAsync(ipswPath, cancellationToken).ConfigureAwait(false);
        if (!inspection.IsValid || !inspection.SupportsIpad5)
        {
            throw new DarkSwordException(RestoreStage.Preflight, string.Join(Environment.NewLine, inspection.Errors));
        }

        var missing = _tools.MissingFiles();
        if (missing.Count > 0)
        {
            throw new DarkSwordException(
                RestoreStage.Preflight,
                "The release toolchain is incomplete:" + Environment.NewLine + string.Join(Environment.NewLine, missing));
        }

        var session = _sessions.Create(ipswPath, inspection);
        await _sessions.SaveAsync(session, cancellationToken).ConfigureAwait(false);

        try
        {
            Report(RestoreStage.WaitingForDfu, 5, "Enter DFU mode", "Connect the iPad directly and enter DFU mode.");
            await PrepareDfuAsync(log, cancellationToken).ConfigureAwait(false);

            Report(RestoreStage.EnteringPwnedDfu, 12, "Running checkm8", "Booting and verifying the turdus-compatible PongoOS environment.");
            await BootPongoAsync(log, cancellationToken).ConfigureAwait(false);

            Report(RestoreStage.GeneratingShcBlock, 20, "Capturing pre-restore SHC block", "Creating the block used only for the initial restore.");
            var preShc = await RunBlockOperationAsync(
                session,
                "shcblock",
                new[] { "--get-shcblock", "--cache-path", Path.Combine(session.SessionDirectory, "cache"), ipswPath },
                log,
                cancellationToken).ConfigureAwait(false);

            session = session with
            {
                ShcBlockPath = preShc,
                LastStage = RestoreStage.GeneratingShcBlock,
                UpdatedAt = DateTimeOffset.UtcNow
            };
            await _sessions.SaveAsync(session, cancellationToken).ConfigureAwait(false);

            Report(RestoreStage.WaitingForDfu, 28, "Re-enter DFU mode", "The iPad rebooted after SHC capture. Enter DFU again.");
            await PrepareDfuAsync(log, cancellationToken).ConfigureAwait(false);

            Report(RestoreStage.EnteringPwnedDfu, 31, "Preparing restore environment", "Running checkm8 and verifying PongoOS for the firmware restore.");
            await BootPongoAsync(log, cancellationToken).ConfigureAwait(false);

            Report(RestoreStage.RestoringFirmware, 38, "Restoring firmware", "This erases the iPad and installs the selected unsigned-by-Apple stock IPSW.", true);
            await RunRestoreAsync(
                session,
                new[]
                {
                    "-o", "--plain-progress", "--no-input",
                    "--cache-path", Path.Combine(session.SessionDirectory, "cache"),
                    "--load-shcblock", preShc,
                    ipswPath
                },
                log,
                progress,
                cancellationToken).ConfigureAwait(false);

            session = session with { LastStage = RestoreStage.RestoringFirmware, UpdatedAt = DateTimeOffset.UtcNow };
            await _sessions.SaveAsync(session, cancellationToken).ConfigureAwait(false);

            Report(RestoreStage.WaitingForDfu, 73, "Create permanent boot profile", "After the restore completes, enter DFU mode again.");
            await PrepareDfuAsync(log, cancellationToken).ConfigureAwait(false);
            await BootPongoAsync(log, cancellationToken).ConfigureAwait(false);

            Report(RestoreStage.GeneratingShcBlock, 78, "Capturing post-restore SHC block", "Generating the block tied to the restored installation.");
            var postShc = await RunBlockOperationAsync(
                session,
                "shcblock",
                new[] { "--get-shcblock", "--cache-path", Path.Combine(session.SessionDirectory, "cache"), ipswPath },
                log,
                cancellationToken,
                excludedPath: preShc).ConfigureAwait(false);

            Report(RestoreStage.WaitingForDfu, 83, "Enter DFU mode again", "The post-restore SHC capture rebooted the iPad.");
            await PrepareDfuAsync(log, cancellationToken).ConfigureAwait(false);
            await BootPongoAsync(log, cancellationToken).ConfigureAwait(false);

            Report(RestoreStage.GeneratingPteBlock, 87, "Generating PTE block", "Creating the device-specific SEP pairing block used for every tether boot.");
            var pte = await RunBlockOperationAsync(
                session,
                "pteblock",
                new[]
                {
                    "--get-pteblock", "--load-shcblock", postShc,
                    "--cache-path", Path.Combine(session.SessionDirectory, "cache"), ipswPath
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

            Report(RestoreStage.WaitingForDfu, 92, "Final DFU entry", "Enter DFU one last time to boot the restored system.");
            await PrepareDfuAsync(log, cancellationToken).ConfigureAwait(false);
            await TetherBootCoreAsync(pte, progress, log, cancellationToken).ConfigureAwait(false);

            session = session with { LastStage = RestoreStage.Completed, UpdatedAt = DateTimeOffset.UtcNow };
            await _sessions.SaveAsync(session, cancellationToken).ConfigureAwait(false);
            Report(RestoreStage.Completed, 100, "Downgrade complete", "The iPad should now boot the restored firmware.");
            return session;
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

    public async Task ValidateDfuToPongoAsync(
        IProgress<RestoreProgress>? progress,
        Action<string>? log,
        CancellationToken cancellationToken)
    {
        progress?.Report(new RestoreProgress(RestoreStage.WaitingForDfu, 5, "Hardware validation", "Enter clean DFU mode. No firmware will be erased."));
        await PrepareDfuAsync(log, cancellationToken).ConfigureAwait(false);
        progress?.Report(new RestoreProgress(RestoreStage.BootingPongo, 35, "Testing checkm8 and PongoOS", "Running the isolated non-destructive hardware gate."));
        await BootPongoAsync(log, cancellationToken).ConfigureAwait(false);
        progress?.Report(new RestoreProgress(RestoreStage.Completed, 100, "Hardware gate passed", "PongoOS is accessible through the packaged bridge. No restore was performed."));
    }

    public async Task TetherBootAsync(
        string pteBlockPath,
        IProgress<RestoreProgress>? progress,
        Action<string>? log,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(pteBlockPath)) throw new FileNotFoundException("PTE block not found.", pteBlockPath);
        progress?.Report(new RestoreProgress(RestoreStage.WaitingForDfu, 5, "Enter DFU mode", "Connect the downgraded iPad and enter DFU mode."));
        await PrepareDfuAsync(log, cancellationToken).ConfigureAwait(false);
        await TetherBootCoreAsync(pteBlockPath, progress, log, cancellationToken).ConfigureAwait(false);
        progress?.Report(new RestoreProgress(RestoreStage.Completed, 100, "Boot complete", "The iPad should continue into iOS."));
    }

    private async Task PrepareDfuAsync(Action<string>? log, CancellationToken cancellationToken)
    {
        await WaitForDfuAsync(cancellationToken).ConfigureAwait(false);
        await _driver.EnsureDfuReadyAsync(_devices, log, cancellationToken).ConfigureAwait(false);
    }

    private async Task TetherBootCoreAsync(
        string pteBlockPath,
        IProgress<RestoreProgress>? progress,
        Action<string>? log,
        CancellationToken cancellationToken)
    {
        progress?.Report(new RestoreProgress(RestoreStage.BootingPongo, 20, "Booting PongoOS", "Running checkm8 and verifying the PongoOS USB bridge."));
        await BootPongoAsync(log, cancellationToken).ConfigureAwait(false);

        var resourceRoot = Path.Combine(_tools.Root, "resources");
        var sepRacer = Path.Combine(resourceRoot, "sep_racer.bin");
        var kpf = Path.Combine(resourceRoot, "kpf.bin");
        if (!File.Exists(sepRacer) || !File.Exists(kpf))
        {
            throw new DarkSwordException(RestoreStage.LoadingSepExploit, "The release is missing sep_racer.bin or kpf.bin.");
        }

        progress?.Report(new RestoreProgress(RestoreStage.LoadingSepExploit, 45, "Running SEP exploit", "Loading sep_racer and the exact saved PTE block."));
        await _runner.RunAsync(
            _tools.PongoBridge,
            new[] { "boot", "--pteblock", pteBlockPath, "--sep-racer", sepRacer, "--kpf", kpf },
            _tools.Root,
            log,
            cancellationToken).ConfigureAwait(false);
        progress?.Report(new RestoreProgress(RestoreStage.BootingXnu, 90, "Booting XNU", "Applying tethered kernel patches and issuing bootux."));
    }

    private async Task BootPongoAsync(Action<string>? log, CancellationToken cancellationToken)
    {
        var current = await _devices.ProbeAsync(cancellationToken).ConfigureAwait(false);
        if (current.Mode == AppleDeviceMode.Pongo)
        {
            log?.Invoke("PongoOS already enumerated; skipping openra1n.");
            await EnsurePongoAccessibleAsync(log, cancellationToken).ConfigureAwait(false);
            return;
        }

        await using var openSession = _runner.StartSession(
            _tools.OpenRa1n,
            Array.Empty<string>(),
            _tools.Root,
            log,
            cancellationToken);

        var pongoTask = _devices.WaitForModeAsync(
            new[] { AppleDeviceMode.Pongo },
            TimeSpan.FromMinutes(2),
            cancellationToken);
        var winner = await Task.WhenAny(pongoTask, openSession.Completion).ConfigureAwait(false);

        if (winner == openSession.Completion)
        {
            var earlyResult = await openSession.Completion.ConfigureAwait(false);
            var afterExit = await _devices.ProbeAsync(cancellationToken).ConfigureAwait(false);
            if (afterExit.Mode != AppleDeviceMode.Pongo)
            {
                throw new DarkSwordException(
                    RestoreStage.BootingPongo,
                    $"openra1n exited before PongoOS was accessible. Exit code={earlyResult.ExitCode}.{Environment.NewLine}" +
                    $"Last device mode={afterExit.Mode}, service={afterExit.Service ?? "unknown"}.{Environment.NewLine}" +
                    earlyResult.StandardError);
            }
        }
        else
        {
            await pongoTask.ConfigureAwait(false);
        }

        await EnsurePongoAccessibleAsync(log, cancellationToken).ConfigureAwait(false);
        openSession.Kill();
    }

    private async Task EnsurePongoAccessibleAsync(Action<string>? log, CancellationToken cancellationToken)
    {
        await _driver.EnsurePongoReadyAsync(_devices, log, cancellationToken).ConfigureAwait(false);
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(30);
        ToolResult? last = null;
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                last = await _runner.RunAsync(
                    _tools.PongoBridge,
                    new[] { "probe" },
                    _tools.Root,
                    log,
                    cancellationToken,
                    requireZeroExitCode: false).ConfigureAwait(false);
                if (last.Success)
                {
                    log?.Invoke("Pongo bridge accessible (05AC:4141).");
                    return;
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                log?.Invoke($"Pongo probe retry: {exception.Message}");
            }

            await Task.Delay(750, cancellationToken).ConfigureAwait(false);
        }

        throw new DarkSwordException(
            RestoreStage.BootingPongo,
            $"PongoOS enumerated but the bridge could not open it. Last exit code={last?.ExitCode.ToString() ?? "not started"}; " +
            $"stderr={last?.StandardError ?? "none"}.");
    }

    private Task<AppleDeviceSnapshot> WaitForDfuAsync(CancellationToken cancellationToken) =>
        _devices.WaitForModeAsync(new[] { AppleDeviceMode.Dfu }, TimeSpan.FromMinutes(5), cancellationToken);

    private async Task<string> RunBlockOperationAsync(
        RestoreSession session,
        string fileToken,
        string[] arguments,
        Action<string>? log,
        CancellationToken cancellationToken,
        string? excludedPath = null)
    {
        var operationStarted = DateTimeOffset.UtcNow;
        var blockDirectory = Path.Combine(session.SessionDirectory, "block");
        Directory.CreateDirectory(blockDirectory);
        var before = Directory.EnumerateFiles(blockDirectory).Select(Path.GetFullPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (excludedPath is not null) before.Add(Path.GetFullPath(excludedPath));

        await _runner.RunAsync(
            _tools.IdeviceRestore,
            arguments,
            session.SessionDirectory,
            log,
            cancellationToken).ConfigureAwait(false);

        var generated = Directory.EnumerateFiles(blockDirectory, "*", SearchOption.AllDirectories)
            .Concat(Directory.EnumerateFiles(session.SessionDirectory, "*", SearchOption.AllDirectories))
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(path => Path.GetFileName(path).Contains(fileToken, StringComparison.OrdinalIgnoreCase))
            .Where(path => !before.Contains(path))
            .Where(path => File.GetLastWriteTimeUtc(path) >= operationStarted.UtcDateTime.AddSeconds(-2))
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();

        if (generated is null)
        {
            throw new DarkSwordException(
                fileToken.Contains("pte", StringComparison.OrdinalIgnoreCase) ? RestoreStage.GeneratingPteBlock : RestoreStage.GeneratingShcBlock,
                $"The tool completed without producing a new {fileToken} file.");
        }

        await ValidateAndRecordArtifactAsync(session, generated, fileToken, cancellationToken).ConfigureAwait(false);
        return generated;
    }

    private static async Task ValidateAndRecordArtifactAsync(
        RestoreSession session,
        string path,
        string artifactType,
        CancellationToken cancellationToken)
    {
        var file = new FileInfo(path);
        if (!file.Exists || file.Length <= 0)
        {
            throw new DarkSwordException(
                artifactType.Contains("pte", StringComparison.OrdinalIgnoreCase) ? RestoreStage.GeneratingPteBlock : RestoreStage.GeneratingShcBlock,
                $"Generated {artifactType} is missing or empty: {path}");
        }

        await using var stream = File.OpenRead(path);
        var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false)).ToLowerInvariant();
        var metadata = new RestoreArtifactMetadata(
            session.SessionId,
            artifactType,
            Path.GetFullPath(path),
            file.Length,
            hash,
            session.Ipsw.ProductVersion,
            session.Ipsw.BuildVersion,
            DateTimeOffset.UtcNow);
        var metadataPath = path + ".metadata.json";
        await File.WriteAllTextAsync(
            metadataPath,
            JsonSerializer.Serialize(metadata, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }),
            cancellationToken).ConfigureAwait(false);
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
                if (TryParsePlainProgress(line, out var percentage, out var detail))
                {
                    progress?.Report(new RestoreProgress(RestoreStage.RestoringFirmware, 38 + percentage * 0.34, "Restoring firmware", detail, true));
                }
            },
            cancellationToken).ConfigureAwait(false);
    }

    private static bool TryParsePlainProgress(string line, out double percent, out string detail)
    {
        percent = 0;
        detail = line;
        var numbers = line.Split(new[] { ' ', ':', '%', '[', ']' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(token => double.TryParse(token, out var value) ? value : -1)
            .Where(value => value is >= 0 and <= 100)
            .ToArray();
        if (numbers.Length == 0) return false;
        percent = numbers[^1];
        return true;
    }
}
