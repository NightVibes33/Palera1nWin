using Palera1nWin.Core.Drivers;
using Palera1nWin.Core.Interaction;
using Palera1nWin.Core.Models;
using Palera1nWin.Core.Services;
using Palera1nWin.Core.Settings;
using Palera1nWin.Core.Usb;
using Palera1nWin.Core.Util;

namespace Palera1nWin.Core.Orchestration;

public enum JailbreakStage
{
    Validating,
    StoppingAmds,
    DetachingUsbipd,
    EnsuringDfuDriver,
    RunningOpenRa1n,
    EnsuringPongoDriver,
    RunningPalera1n,
    Completed,
    Failed,
    Cancelled,
}

public sealed class JailbreakOrchestrator : IDisposable
{
    private readonly AppSettings _settings;
    private readonly AppleUsbMonitor _monitor;
    private readonly bool _ownsMonitor;
    private readonly DriverInstaller _driverInstaller;
    private readonly UsbipdService _usbipdService;
    private readonly OpenRa1nService _openRa1nService;
    private readonly Palera1nHostService _palera1nHostService;
    private readonly IUserPromptService? _userPrompts;
    private string? _selectedBusId;
    private string? _resolvedDistro;

    public JailbreakOrchestrator(
        AppSettings settings,
        IUserPromptService? userPrompts = null,
        AppleUsbMonitor? monitor = null)
    {
        _settings = settings;
        _userPrompts = userPrompts;
        _monitor = monitor ?? new AppleUsbMonitor();
        _ownsMonitor = monitor is null;
        _driverInstaller = new DriverInstaller(settings, _monitor);
        _usbipdService = new UsbipdService();
        _openRa1nService = new OpenRa1nService(_monitor);
        _palera1nHostService = new Palera1nHostService(userPrompts);
        _openRa1nService.LogReceived += ForwardLog;
        _palera1nHostService.LogReceived += ForwardLog;
    }

    public event EventHandler<LogLine>? LogReceived;
    public event EventHandler<ProgressEventArgs>? ProgressChanged;

    public async Task<JailbreakStage> RunAsync(CancellationToken cancellationToken = default)
    {
        AmdsServiceLease? amds = null;
        try
        {
            Report(JailbreakStage.Validating, "Validating packaged runtime, WSL and exact Apple USB target...", 0);
            _settings.Clamp();

            var toolchain = Paths.ResolveToolchainRoot(_settings.ToolchainRoot);
            if (toolchain is null)
            {
                Fail("Toolchain root is missing or invalid.");
                return JailbreakStage.Failed;
            }
            if (!Paths.ValidateToolchain(toolchain, out var missing))
            {
                Fail($"Missing packaged jailbreak files: {string.Join(", ", missing.Select(Path.GetFileName))}");
                return JailbreakStage.Failed;
            }

            var initial = RequireSingleAppleDevice();
            if (initial.Mode == DeviceMode.PwnedDfu)
            {
                Fail("Stale generic gaster PWND was detected. Force-reboot and enter clean DFU.");
                return JailbreakStage.Failed;
            }

            var wsl = new WslService(_settings.WslDistro);
            _resolvedDistro = await wsl.ResolveDistroAsync(cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(_resolvedDistro))
            {
                Fail("No WSL distro is installed. Provision WSL from Setup before starting Jailbreak.");
                return JailbreakStage.Failed;
            }

            var usbipdDevices = _usbipdService.IsAvailable
                ? UsbipdService.ParseAppleDevices(_usbipdService.ListDevices())
                : [];
            if (usbipdDevices.Count > 1)
            {
                Fail($"{usbipdDevices.Count} Apple devices are visible to usbipd. Disconnect all but the target device.");
                return JailbreakStage.Failed;
            }
            _selectedBusId = usbipdDevices.SingleOrDefault()?.BusId ?? initial.BusId;

            Report(JailbreakStage.StoppingAmds, "Temporarily stopping Apple Mobile Device Service for this transaction...", 8);
            amds = await AmdsServiceLease.AcquireAsync(
                line => Emit("amds", line),
                cancellationToken).ConfigureAwait(false);

            if (_usbipdService.DetectsUsbDkConflict())
            {
                Emit(
                    "orchestrator",
                    "UsbDk is installed and can conflict with usbipd. Uninstall it and reboot if the exact-device handoff fails.",
                    true);
            }

            if (!IsPongoPresent())
            {
                Report(JailbreakStage.EnsuringDfuDriver, "Waiting for the selected device in clean Apple DFU...", 18);
                if (!await WaitForDfuAsync(toolchain, wsl, cancellationToken).ConfigureAwait(false))
                    return JailbreakStage.Failed;

                Report(JailbreakStage.DetachingUsbipd, "Returning the selected DFU bus to Windows...", 24);
                if (!ReleaseSelectedAppleToWindows()) return JailbreakStage.Failed;
                await Task.Delay(1200, cancellationToken).ConfigureAwait(false);

                Report(JailbreakStage.EnsuringDfuDriver, "Verifying the exact DFU host driver...", 30);
                if (!await EnsureModeDriverAsync(
                        DeviceMode.Dfu,
                        0x1227,
                        allowWinUsb: false,
                        cancellationToken).ConfigureAwait(false))
                {
                    return JailbreakStage.Failed;
                }

                var dfu = RequireSingleDeviceForPid(0x1227);
                var service = DriverInstaller.ResolveServiceName(dfu.DeviceId) ?? DriverInstaller.DetectService(dfu);
                if (!DriverInstaller.IsLibusbKService(service) || DriverInstaller.IsUsbipdStubService(service))
                {
                    Fail($"Refusing openra1n: selected DFU service is '{service ?? "missing"}', not Windows libusbK.");
                    return JailbreakStage.Failed;
                }

                using var driverWatch = new LibusbKWatchdog(_monitor, _settings);
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
            }

            Report(JailbreakStage.RunningPalera1n, $"Running palera1n in {_resolvedDistro} through the selected USB bus...", 75);
            var options = new JailbreakOptions
            {
                Rootless = _settings.IsRootless,
                SafeMode = _settings.SafeMode,
                VerboseBoot = _settings.VerboseBoot,
                DebugLogging = _settings.DebugLogging,
                WslDistro = _resolvedDistro,
                ForceBusId = _selectedBusId,
            };
            var exitCode = await _palera1nHostService.RunPalera1nAsync(
                    toolchain,
                    options,
                    cancellationToken)
                .ConfigureAwait(false);
            if (exitCode != 0)
            {
                Fail($"palera1n exited with code {exitCode}.");
                return JailbreakStage.Failed;
            }

            Report(JailbreakStage.Completed, "Jailbreak flow completed and process ownership was cleaned up.", 100);
            return JailbreakStage.Completed;
        }
        catch (OperationCanceledException)
        {
            Report(JailbreakStage.Cancelled, "Jailbreak cancelled; cleaning up services and USB ownership.", 100);
            return JailbreakStage.Cancelled;
        }
        catch (Exception exception)
        {
            Fail(exception.Message);
            Emit("orchestrator", exception.ToString(), true);
            return JailbreakStage.Failed;
        }
        finally
        {
            UsbipdService.KillLeftoverUsbBridges();
            if (amds is not null) await amds.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task<bool> EnsureModeDriverAsync(
        DeviceMode requiredMode,
        ushort productId,
        bool allowWinUsb,
        CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow.AddMinutes(3);
        var offeredManualZadig = false;
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AppleUsbDevice device;
            try
            {
                device = RequireSingleDeviceForPid(productId);
            }
            catch (InvalidOperationException exception)
            {
                if (exception.Message.Contains("not present", StringComparison.OrdinalIgnoreCase))
                {
                    await Task.Delay(600, cancellationToken).ConfigureAwait(false);
                    continue;
                }
                throw;
            }

            if (device.Mode == DeviceMode.PwnedDfu)
            {
                Fail("Generic PWND without the expected YOLO/Pongo handoff is not accepted.");
                return false;
            }

            var modeAccepted = device.Mode == requiredMode ||
                               (requiredMode == DeviceMode.Dfu && device.Mode == DeviceMode.YoloDfu);
            if (!modeAccepted)
            {
                await Task.Delay(600, cancellationToken).ConfigureAwait(false);
                continue;
            }

            var service = DriverInstaller.ResolveServiceName(device.DeviceId) ?? DriverInstaller.DetectService(device);
            if (DriverInstaller.IsUsbipdStubService(service))
            {
                if (!ReleaseSelectedAppleToWindows()) return false;
                await Task.Delay(1200, cancellationToken).ConfigureAwait(false);
                continue;
            }
            if (DriverInstaller.IsLibusbKService(service) ||
                (allowWinUsb && DriverInstaller.IsWinUsbService(service)))
            {
                return true;
            }

            var result = await _driverInstaller.EnsureLibusbKAsync(
                    productId,
                    new Progress<ProgressEventArgs>(value => ProgressChanged?.Invoke(this, value)),
                    cancellationToken)
                .ConfigureAwait(false);

            if ((result is DriverInstallResult.AlreadyOk or DriverInstallResult.Installed) &&
                await WaitForAcceptedDriverAsync(
                        productId,
                        allowWinUsb,
                        TimeSpan.FromSeconds(35),
                        cancellationToken)
                    .ConfigureAwait(false))
            {
                return true;
            }

            if (result == DriverInstallResult.NeedsManualZadig &&
                !offeredManualZadig &&
                _userPrompts is not null)
            {
                offeredManualZadig = true;
                var toolchain = Paths.ResolveToolchainRoot(_settings.ToolchainRoot);
                var confirmed = await _userPrompts.ConfirmAsync(
                        new UserPromptRequest
                        {
                            Title = "Exact Apple USB driver repair required",
                            Message =
                                $"Automated libusbK verification failed for Apple 05AC:{productId:X4}. " +
                                "Open Zadig only for this one connected DFU/Pongo device.",
                            ConfirmText = "Open Zadig",
                            CancelText = "Cancel",
                        },
                        cancellationToken)
                    .ConfigureAwait(false);
                if (!confirmed || toolchain is null) return false;

                _driverInstaller.LaunchZadig(toolchain);
                if (await WaitForAcceptedDriverAsync(
                        productId,
                        allowWinUsb,
                        TimeSpan.FromMinutes(6),
                        cancellationToken).ConfigureAwait(false))
                {
                    return true;
                }
                Fail("The exact USB binding did not verify after Zadig.");
                return false;
            }

            if (result == DriverInstallResult.Failed) return false;
            await Task.Delay(600, cancellationToken).ConfigureAwait(false);
        }

        Fail($"Timed out verifying Apple 05AC:{productId:X4} driver state.");
        return false;
    }

    private async Task<bool> WaitForAcceptedDriverAsync(
        ushort productId,
        bool allowWinUsb,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var device = RequireSingleDeviceForPid(productId);
                var service = DriverInstaller.ResolveServiceName(device.DeviceId) ?? DriverInstaller.DetectService(device);
                if (DriverInstaller.IsLibusbKService(service) ||
                    (allowWinUsb && DriverInstaller.IsWinUsbService(service)))
                {
                    return true;
                }
            }
            catch (InvalidOperationException)
            {
                // Re-enumeration can temporarily remove the target.
            }
            await Task.Delay(600, cancellationToken).ConfigureAwait(false);
        }
        return false;
    }

    private async Task<bool> WaitForDfuAsync(
        string toolchainRoot,
        WslService wsl,
        CancellationToken cancellationToken)
    {
        if (HasSingleDfuDevice() || IsPongoPresent()) return true;
        if (!_usbipdService.IsAvailable)
        {
            Fail("usbipd-win is required for the guided DFU helper, or enter DFU manually before starting.");
            return false;
        }

        const int maximumAttempts = 4;
        for (var attempt = 1; attempt <= maximumAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (HasSingleDfuDevice() || IsPongoPresent()) return true;

            var attach = await _usbipdService.EnsureAppleAttachedToWslAsync(
                    _resolvedDistro!,
                    wsl,
                    new Progress<string>(message => Emit("usbipd", message)),
                    cancellationToken,
                    TimeSpan.FromSeconds(45),
                    _selectedBusId)
                .ConfigureAwait(false);
            if (!attach.Succeeded)
            {
                Emit("usbipd", attach.Message, true);
                if (attempt == maximumAttempts) return false;
                await Task.Delay(1200, cancellationToken).ConfigureAwait(false);
                continue;
            }
            _selectedBusId = attach.BusId;

            var dfuMissed = false;
            var promptSeen = false;
            void ObserveDfuLog(object? _, LogLine line)
            {
                if (line.Message.Contains(
                        "Whoops, device did not enter DFU mode",
                        StringComparison.OrdinalIgnoreCase))
                {
                    dfuMissed = true;
                }
                if (line.Message.Contains(
                        "Press Enter when ready for DFU mode",
                        StringComparison.OrdinalIgnoreCase))
                {
                    promptSeen = true;
                }
            }

            _palera1nHostService.LogReceived += ObserveDfuLog;
            using var helperCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            try
            {
                var started = DateTimeOffset.UtcNow;
                DateTimeOffset? missedAt = null;
                var helperTask = _palera1nHostService.RunDfuHelperAsync(
                    toolchainRoot,
                    _resolvedDistro,
                    helperCts.Token);

                while (!helperTask.IsCompleted)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (HasSingleDfuDevice() || IsPongoPresent())
                    {
                        helperCts.Cancel();
                        break;
                    }

                    if (dfuMissed)
                    {
                        missedAt ??= DateTimeOffset.UtcNow;
                        if (DateTimeOffset.UtcNow - missedAt > TimeSpan.FromSeconds(8))
                        {
                            helperCts.Cancel();
                            break;
                        }
                    }
                    else if (!promptSeen && DateTimeOffset.UtcNow - started > TimeSpan.FromSeconds(30))
                    {
                        helperCts.Cancel();
                        break;
                    }

                    await Task.WhenAny(
                            helperTask,
                            Task.Delay(600, cancellationToken))
                        .ConfigureAwait(false);
                }

                try
                {
                    var exitCode = await helperTask.ConfigureAwait(false);
                    if (exitCode != 0 && !dfuMissed && !HasSingleDfuDevice())
                    {
                        Fail($"DFU helper exited with code {exitCode}.");
                        return false;
                    }
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    // Intentional stop after DFU success or a bounded missed prompt.
                }
            }
            finally
            {
                _palera1nHostService.LogReceived -= ObserveDfuLog;
            }

            if (HasSingleDfuDevice() || IsPongoPresent()) return true;
            await Task.Delay(1200, cancellationToken).ConfigureAwait(false);
        }

        Fail("The selected device never entered clean DFU mode.");
        return false;
    }

    private bool ReleaseSelectedAppleToWindows()
    {
        if (!_usbipdService.IsAvailable) return true;
        var result = _usbipdService.ReleaseAppleToHost(_selectedBusId);
        Emit("usbipd", result.Message, !result.Succeeded);
        if (!result.Succeeded) Fail(result.Message);
        return result.Succeeded;
    }

    private AppleUsbDevice RequireSingleAppleDevice()
    {
        _monitor.PollNow();
        var devices = _monitor.ScanDevices().Where(device => device.IsPresent).ToArray();
        if (devices.Length != 1)
        {
            throw new InvalidOperationException(
                $"Exactly one Apple USB device must be connected; detected {devices.Length}.");
        }
        return devices[0];
    }

    private AppleUsbDevice RequireSingleDeviceForPid(ushort productId)
    {
        _monitor.PollNow();
        var all = _monitor.ScanDevices().Where(device => device.IsPresent).ToArray();
        if (all.Length > 1)
        {
            throw new InvalidOperationException(
                $"Multiple Apple USB devices are connected ({all.Length}). Disconnect all but the target.");
        }

        var matches = all
            .Where(device => device.ProductId == productId && device.Mode != DeviceMode.Busy)
            .ToArray();
        if (matches.Length == 0)
            throw new InvalidOperationException($"Apple USB PID {productId:X4} is not present.");
        if (matches.Length != 1)
            throw new InvalidOperationException($"Apple USB PID {productId:X4} is ambiguous.");
        return matches[0];
    }

    private bool HasSingleDfuDevice()
    {
        try
        {
            var device = RequireSingleDeviceForPid(0x1227);
            return device.Mode is DeviceMode.Dfu or DeviceMode.YoloDfu;
        }
        catch
        {
            return false;
        }
    }

    private bool IsPongoPresent()
    {
        try
        {
            return RequireSingleDeviceForPid(0x4141).Mode == DeviceMode.Pongo;
        }
        catch
        {
            if (!_usbipdService.IsAvailable) return false;
            return _monitor.IsPongoVisibleInUsbipd() &&
                   UsbipdService.ParseAppleDevices(_usbipdService.ListDevices()).Count <= 1;
        }
    }

    private void Report(JailbreakStage stage, string message, int percent)
    {
        ProgressChanged?.Invoke(this, new ProgressEventArgs(stage.ToString(), message, percent));
        Emit("orchestrator", message);
    }

    private void Fail(string message)
    {
        Emit("orchestrator", message, true);
        ProgressChanged?.Invoke(
            this,
            new ProgressEventArgs(JailbreakStage.Failed.ToString(), message, 100));
    }

    private void Emit(string source, string message, bool isError = false) =>
        LogReceived?.Invoke(
            this,
            new LogLine { Source = source, Message = message, IsError = isError });

    private void ForwardLog(object? _, LogLine line) => LogReceived?.Invoke(this, line);

    public void Dispose()
    {
        _openRa1nService.LogReceived -= ForwardLog;
        _palera1nHostService.LogReceived -= ForwardLog;
        _openRa1nService.Dispose();
        if (_ownsMonitor) _monitor.Dispose();
    }
}
