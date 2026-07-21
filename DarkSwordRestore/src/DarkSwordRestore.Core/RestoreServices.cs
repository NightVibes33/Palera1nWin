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
        _runner.RunElevatedAsync(
            _tools.WdiSimple,
            new[]
            {
                "--vid", "0x05AC",
                "--pid", "0x1227",
                "--type", "2",
                "--name", "Apple Mobile Device (DFU Mode)"
            },
            _tools.Root,
            cancellationToken);
}

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
            Report(RestoreStage.WaitingForDfu, 5, "Enter DFU mode", "Connect the iPad directly with USB-A to Lightning and enter DFU mode.");
            await WaitForDfuAsync(cancellationToken).ConfigureAwait(false);

            Report(RestoreStage.InstallingDfuDriver, 8, "Preparing Windows USB", "Installing libusbK only for Apple DFU mode.");
            await _driver.InstallLibusbKAsync(cancellationToken).ConfigureAwait(false);
            await Task.Delay(1500, cancellationToken).ConfigureAwait(false);

            Report(RestoreStage.EnteringPwnedDfu, 12, "Running checkm8", "Booting the turdus-compatible PongoOS environment.");
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
            await WaitForDfuAsync(cancellationToken).ConfigureAwait(false);
            await _driver.InstallLibusbKAsync(cancellationToken).ConfigureAwait(false);

            Report(RestoreStage.EnteringPwnedDfu, 31, "Preparing restore environment", "Running checkm8 and PongoOS for the firmware restore.");
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
            await WaitForDfuAsync(cancellationToken).ConfigureAwait(false);
            await _driver.InstallLibusbKAsync(cancellationToken).ConfigureAwait(false);
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
            await WaitForDfuAsync(cancellationToken).ConfigureAwait(false);
            await _driver.InstallLibusbKAsync(cancellationToken).ConfigureAwait(false);
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
            await WaitForDfuAsync(cancellationToken).ConfigureAwait(false);
            await _driver.InstallLibusbKAsync(cancellationToken).ConfigureAwait(false);
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

    public async Task TetherBootAsync(
        string pteBlockPath,
        IProgress<RestoreProgress>? progress,
        Action<string>? log,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(pteBlockPath)) throw new FileNotFoundException("PTE block not found.", pteBlockPath);
        progress?.Report(new RestoreProgress(RestoreStage.WaitingForDfu, 5, "Enter DFU mode", "Connect the downgraded iPad and enter DFU mode."));
        await WaitForDfuAsync(cancellationToken).ConfigureAwait(false);
        await _driver.InstallLibusbKAsync(cancellationToken).ConfigureAwait(false);
        await TetherBootCoreAsync(pteBlockPath, progress, log, cancellationToken).ConfigureAwait(false);
        progress?.Report(new RestoreProgress(RestoreStage.Completed, 100, "Boot complete", "The iPad should continue into iOS."));
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

        progress?.Report(new RestoreProgress(RestoreStage.LoadingSepExploit, 45, "Running SEP exploit", "Loading sep_racer and the device PTE block."));
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
        }
        catch
        {
            var result = await openTask.ConfigureAwait(false);
            throw new DarkSwordException(
                RestoreStage.BootingPongo,
                $"PongoOS did not enumerate. openra1n exit code: {result.ExitCode}.{Environment.NewLine}{result.StandardError}");
        }

        await openTask.ConfigureAwait(false);
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
        var blockDirectory = Path.Combine(session.SessionDirectory, "block");
        Directory.CreateDirectory(blockDirectory);
        var before = Directory.EnumerateFiles(blockDirectory).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (excludedPath is not null) before.Add(Path.GetFullPath(excludedPath));

        await _runner.RunAsync(
            _tools.IdeviceRestore,
            arguments,
            session.SessionDirectory,
            log,
            cancellationToken).ConfigureAwait(false);

        var generated = Directory.EnumerateFiles(blockDirectory, "*", SearchOption.AllDirectories)
            .Where(path => Path.GetFileName(path).Contains(fileToken, StringComparison.OrdinalIgnoreCase))
            .Where(path => !before.Contains(Path.GetFullPath(path)))
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();

        if (generated is null)
        {
            generated = Directory.EnumerateFiles(session.SessionDirectory, "*", SearchOption.AllDirectories)
                .Where(path => Path.GetFileName(path).Contains(fileToken, StringComparison.OrdinalIgnoreCase))
                .Where(path => excludedPath is null || !Path.GetFullPath(path).Equals(Path.GetFullPath(excludedPath), StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();
        }

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
