namespace DarkSwordRestore.Core;

public sealed class RestoreOrchestrator
{
    private readonly ToolchainLocator _tools;
    private readonly ProcessRunner _runner;
    private readonly SessionLogger _log;
    private readonly AppleUsbMonitor _monitor;
    private readonly DfuDriverService _driver;
    private readonly IpswInspector _inspector;

    public RestoreOrchestrator(
        ToolchainLocator tools,
        ProcessRunner runner,
        SessionLogger log,
        AppleUsbMonitor monitor,
        DfuDriverService driver,
        IpswInspector inspector)
    {
        _tools = tools;
        _runner = runner;
        _log = log;
        _monitor = monitor;
        _driver = driver;
        _inspector = inspector;
    }

    public async Task<RestoreSession> RunFullDowngradeAsync(
        string ipswPath,
        bool destructiveOperationConfirmed,
        IProgress<RestoreProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        progress?.Report(new RestoreProgress(RestoreStage.Preflight, 1, "Checking files and device", "Validating the toolchain and IPSW"));
        if (!destructiveOperationConfirmed)
            throw new DarkSwordException(RestoreStage.Preflight, "The erase-and-tether warnings must be accepted before starting.");
        if (!_driver.IsAdministrator())
            throw new DarkSwordException(RestoreStage.Preflight, "Run DarkSword Restore as Administrator.");

        var missingTools = _tools.MissingRequiredTools();
        if (missingTools.Count > 0)
            throw new DarkSwordException(RestoreStage.Preflight, $"Toolchain is incomplete: {string.Join(", ", missingTools)}");

        var ipsw = await _inspector.InspectAsync(ipswPath, cancellationToken).ConfigureAwait(false);
        if (!ipsw.IsValid)
            throw new DarkSwordException(RestoreStage.Preflight, string.Join(Environment.NewLine, ipsw.Errors));

        EnsureDiskSpace(ipsw);
        var sessionDirectory = CreateSessionDirectory();
        var session = new RestoreSession(
            Path.GetFileName(sessionDirectory), sessionDirectory, ipsw.Path, ipsw,
            null, null, RestoreStage.Preflight, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        await SessionStore.SaveAsync(session, cancellationToken).ConfigureAwait(false);

        try
        {
            progress?.Report(new RestoreProgress(RestoreStage.GeneratingShcBlock, 5, "Pre-restore SEP preparation", "Put the iPad in DFU mode to generate the temporary SHC block"));
            await PreparePwnedDfuAsync(sessionDirectory, progress, cancellationToken).ConfigureAwait(false);
            var beforePreShc = SnapshotFiles(sessionDirectory, "*shcblock*.bin");
            await RunTurdusAsync(sessionDirectory,
                new[] { "-P", "-y", "--get-shcblock", ipsw.Path },
                RestoreStage.GeneratingShcBlock,
                TimeSpan.FromMinutes(20),
                cancellationToken).ConfigureAwait(false);
            var preRestoreShc = DiscoverNewFile(sessionDirectory, "*shcblock*.bin", beforePreShc)
                ?? throw new DarkSwordException(RestoreStage.GeneratingShcBlock, "The pre-restore SHC block was not created.");
            session = await CheckpointAsync(session with { ShcBlockPath = preRestoreShc }, RestoreStage.GeneratingShcBlock, cancellationToken).ConfigureAwait(false);

            progress?.Report(new RestoreProgress(RestoreStage.RestoringFirmware, 25, "Ready to erase and restore", "Re-enter DFU mode. The next operation erases the iPad.", true));
            await PreparePwnedDfuAsync(sessionDirectory, progress, cancellationToken).ConfigureAwait(false);
            await RunTurdusAsync(sessionDirectory,
                new[] { "-P", "-y", "-o", "--load-shcblock", preRestoreShc, ipsw.Path },
                RestoreStage.RestoringFirmware,
                TimeSpan.FromHours(2),
                cancellationToken).ConfigureAwait(false);
            session = await CheckpointAsync(session, RestoreStage.RestoringFirmware, cancellationToken).ConfigureAwait(false);

            progress?.Report(new RestoreProgress(RestoreStage.GeneratingShcBlock, 63, "Creating permanent boot profile", "After the restore, place the iPad in DFU mode again for the post-restore SHC block"));
            await PreparePwnedDfuAsync(sessionDirectory, progress, cancellationToken).ConfigureAwait(false);
            var beforePostShc = SnapshotFiles(sessionDirectory, "*shcblock*.bin");
            await RunTurdusAsync(sessionDirectory,
                new[] { "-P", "-y", "--get-shcblock", ipsw.Path },
                RestoreStage.GeneratingShcBlock,
                TimeSpan.FromMinutes(20),
                cancellationToken).ConfigureAwait(false);
            var postRestoreShc = DiscoverNewFile(sessionDirectory, "*shcblock*.bin", beforePostShc)
                ?? FindNewestFile(sessionDirectory, "*shcblock*.bin", exclude: preRestoreShc)
                ?? throw new DarkSwordException(RestoreStage.GeneratingShcBlock, "The post-restore SHC block was not created.");

            progress?.Report(new RestoreProgress(RestoreStage.GeneratingPteBlock, 75, "Generating the PTE block", "Re-enter DFU mode for the device-specific boot ticket"));
            await PreparePwnedDfuAsync(sessionDirectory, progress, cancellationToken).ConfigureAwait(false);
            var beforePte = SnapshotFiles(sessionDirectory, "*pteblock*.bin");
            await RunTurdusAsync(sessionDirectory,
                new[] { "-P", "-y", "--get-pteblock", "--load-shcblock", postRestoreShc, ipsw.Path },
                RestoreStage.GeneratingPteBlock,
                TimeSpan.FromMinutes(30),
                cancellationToken).ConfigureAwait(false);
            var pteBlock = DiscoverNewFile(sessionDirectory, "*pteblock*.bin", beforePte)
                ?? FindNewestFile(sessionDirectory, "*pteblock*.bin")
                ?? throw new DarkSwordException(RestoreStage.GeneratingPteBlock, "The PTE block was not created.");
            session = await CheckpointAsync(session with { ShcBlockPath = postRestoreShc, PteBlockPath = pteBlock }, RestoreStage.GeneratingPteBlock, cancellationToken).ConfigureAwait(false);

            progress?.Report(new RestoreProgress(RestoreStage.BootingPongo, 86, "First tether boot", "Enter DFU mode one final time to boot iOS"));
            await TetherBootAsync(pteBlock, progress, cancellationToken).ConfigureAwait(false);
            session = await CheckpointAsync(session, RestoreStage.Completed, cancellationToken).ConfigureAwait(false);
            progress?.Report(new RestoreProgress(RestoreStage.Completed, 100, "Downgrade complete", "Keep the PTE block. Every cold boot requires DarkSword Restore."));
            return session;
        }
        catch (OperationCanceledException)
        {
            await CheckpointAsync(session, RestoreStage.Cancelled, CancellationToken.None).ConfigureAwait(false);
            progress?.Report(new RestoreProgress(RestoreStage.Cancelled, 0, "Operation cancelled", "No additional commands will be sent."));
            throw;
        }
        catch (Exception ex)
        {
            _log.Error(ex.ToString());
            await CheckpointAsync(session, RestoreStage.Failed, CancellationToken.None).ConfigureAwait(false);
            progress?.Report(new RestoreProgress(RestoreStage.Failed, 0, "Restore stopped", ex.Message));
            throw;
        }
    }

    public async Task TetherBootAsync(
        string pteBlockPath,
        IProgress<RestoreProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(pteBlockPath))
            throw new FileNotFoundException("The selected PTE block does not exist.", pteBlockPath);

        progress?.Report(new RestoreProgress(RestoreStage.WaitingForDfu, 4, "Waiting for DFU mode", "Connect the iPad directly and complete the DFU button sequence"));
        await WaitForDfuAsync(cancellationToken).ConfigureAwait(false);
        progress?.Report(new RestoreProgress(RestoreStage.InstallingDfuDriver, 10, "Preparing Windows USB", "Assigning libusbK to Apple DFU mode"));
        await _driver.InstallLibusbKForDfuAsync(cancellationToken).ConfigureAwait(false);

        progress?.Report(new RestoreProgress(RestoreStage.EnteringPwnedDfu, 18, "Running checkm8", "Uploading PongoOS with openra1n"));
        var openRa1n = await _runner.RunAsync(
            _tools.OpenRa1n,
            Array.Empty<string>(),
            Path.GetDirectoryName(_tools.OpenRa1n),
            timeout: TimeSpan.FromMinutes(5),
            cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!openRa1n.Success)
            throw new DarkSwordException(RestoreStage.EnteringPwnedDfu, $"openra1n failed with exit code {openRa1n.ExitCode}.");

        if (!await _monitor.WaitForModeAsync(AppleDeviceMode.Pongo, TimeSpan.FromSeconds(45), cancellationToken).ConfigureAwait(false))
            throw new DarkSwordException(RestoreStage.BootingPongo, "PongoOS did not enumerate as USB 05AC:4141.");

        using var pongo = new PongoTransport(_tools, _log);
        pongo.Open();
        await pongo.TetherBootAsync(
            Path.Combine(_tools.ResourcesDirectory, "sep_racer.bin"),
            Path.Combine(_tools.ResourcesDirectory, "kpf.bin"),
            pteBlockPath,
            progress,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task PreparePwnedDfuAsync(
        string workingDirectory,
        IProgress<RestoreProgress>? progress,
        CancellationToken cancellationToken)
    {
        progress?.Report(new RestoreProgress(RestoreStage.WaitingForDfu, 0, "Waiting for DFU mode", "Hold Power + Home for 8 seconds, release Power, and keep holding Home"));
        await WaitForDfuAsync(cancellationToken).ConfigureAwait(false);
        progress?.Report(new RestoreProgress(RestoreStage.InstallingDfuDriver, 0, "Preparing DFU driver", "This change applies only to Apple DFU mode"));
        await _driver.InstallLibusbKForDfuAsync(cancellationToken).ConfigureAwait(false);
        progress?.Report(new RestoreProgress(RestoreStage.EnteringPwnedDfu, 0, "Entering pwned DFU", "Running the A9 checkm8 stage"));

        var result = await _runner.RunAsync(
            _tools.Gaster,
            new[] { "pwn" },
            workingDirectory,
            timeout: TimeSpan.FromMinutes(4),
            cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!result.Success)
            throw new DarkSwordException(RestoreStage.EnteringPwnedDfu, $"gaster pwn failed with exit code {result.ExitCode}.");
    }

    private async Task WaitForDfuAsync(CancellationToken cancellationToken)
    {
        if (_monitor.Current.Mode == AppleDeviceMode.Dfu) return;
        if (!await _monitor.WaitForModeAsync(AppleDeviceMode.Dfu, TimeSpan.FromMinutes(5), cancellationToken).ConfigureAwait(false))
            throw new DarkSwordException(RestoreStage.WaitingForDfu, "DFU mode was not detected within five minutes.");
    }

    private async Task RunTurdusAsync(
        string workingDirectory,
        IEnumerable<string> arguments,
        RestoreStage stage,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var result = await _runner.RunAsync(
            _tools.TurdusRestore,
            arguments,
            workingDirectory,
            new Dictionary<string, string> { ["DARKSWORD_SESSION"] = workingDirectory },
            timeout,
            cancellationToken).ConfigureAwait(false);
        if (!result.Success)
            throw new DarkSwordException(stage, $"turdus_merula failed with exit code {result.ExitCode}. {result.StandardError.Trim()}");
    }

    private static HashSet<string> SnapshotFiles(string root, string pattern) =>
        Directory.Exists(root)
            ? Directory.GetFiles(root, pattern, SearchOption.AllDirectories).Select(Path.GetFullPath).ToHashSet(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    private static string? DiscoverNewFile(string root, string pattern, HashSet<string> before) =>
        Directory.GetFiles(root, pattern, SearchOption.AllDirectories)
            .Select(Path.GetFullPath)
            .Where(path => !before.Contains(path))
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();

    private static string? FindNewestFile(string root, string pattern, string? exclude = null) =>
        Directory.GetFiles(root, pattern, SearchOption.AllDirectories)
            .Where(path => !string.Equals(Path.GetFullPath(path), Path.GetFullPath(exclude ?? string.Empty), StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();

    private static void EnsureDiskSpace(IpswInspectionResult ipsw)
    {
        var root = Path.GetPathRoot(AppContext.BaseDirectory) ?? "C:\\";
        var drive = new DriveInfo(root);
        var required = Math.Max(ipsw.FileSize * 4, 20L * 1024L * 1024L * 1024L);
        if (drive.AvailableFreeSpace < required)
            throw new DarkSwordException(RestoreStage.Preflight, $"At least {required / 1024d / 1024d / 1024d:F1} GB free is required on {drive.Name}.");
    }

    private static string CreateSessionDirectory()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "sessions");
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, $"{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}"[..24]);
        Directory.CreateDirectory(path);
        Directory.CreateDirectory(Path.Combine(path, "block"));
        Directory.CreateDirectory(Path.Combine(path, "image4"));
        return path;
    }

    private static async Task<RestoreSession> CheckpointAsync(RestoreSession session, RestoreStage stage, CancellationToken cancellationToken)
    {
        var updated = session with { LastStage = stage, UpdatedAt = DateTimeOffset.UtcNow };
        await SessionStore.SaveAsync(updated, cancellationToken).ConfigureAwait(false);
        return updated;
    }
}
