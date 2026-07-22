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
        try
        {
            Report(JailbreakStage.Validating, "Validating shared toolchain and USB state...", 0);
            _settings.Clamp();

            var toolchain = Paths.ResolveToolchainRoot(_settings.ToolchainRoot);
            if (toolchain is null)
            {
                Fail("Toolchain root is missing or invalid.");
                return JailbreakStage.Failed;
            }
            if (!Paths.ValidateToolchain(toolchain, out var missing))
            {
                Fail($"Missing toolchain files: {string.Join(", ", missing)}");
                return JailbreakStage.Failed;
            }

            Report(JailbreakStage.StoppingAmds, "Stopping Apple Mobile Device Service for the active jailbreak transaction...", 10);
            await StopAmdsIfPresentAsync(toolchain, cancellationToken).ConfigureAwait(false);

            if (_usbipdService.DetectsUsbDkConflict())
            {
                Emit(
                    "orchestrator",
                    "UsbDk is installed and can conflict with usbipd. The operation will use explicit ownership handoffs; uninstall UsbDk if WSL attach continues to fail.",
                    isError: true);
            }

            _monitor.PollNow();
            if (_monitor.CurrentDevice.Mode == DeviceMode.PwnedDfu)
            {
                Fail("A stale gaster PWND state was detected. This backend requires clean DFU or YOLO-compatible DFU. Force-reboot, re-enter clean DFU, then retry.");
                return JailbreakStage.Failed;
            }

            if (!IsPongoPresent())
            {
                Report(JailbreakStage.EnsuringDfuDriver, "Waiting for clean Apple DFU (05AC:1227)...", 18);
                if (!await WaitForDfuAsync(toolchain, cancellationToken).ConfigureAwait(false))
                {
                    return JailbreakStage.Failed;
                }

                Report(JailbreakStage.DetachingUsbipd, "Transferring DFU ownership from WSL to Windows...", 22);
                if (!ReleaseAppleToWindows())
                {
                    return JailbreakStage.Failed;
                }
                await Task.Delay(2000, cancellationToken).ConfigureAwait(false);

                Report(JailbreakStage.EnsuringDfuDriver, "Verifying the DFU host driver transaction...", 28);
                if (!await EnsureModeDriverAsync(DeviceMode.Dfu, 0x1227, allowWinUsb: false, cancellationToken)
                        .ConfigureAwait(false))
                {
                    return JailbreakStage.Failed;
                }

                var dfu = FindDevice(0x1227);
                var dfuService = dfu is null ? null : DriverInstaller.DetectService(dfu);
                if (dfu is null || DriverInstaller.IsUsbipdStubService(dfuService) ||
                    !DriverInstaller.IsLibusbKService(dfuService))
                {
                    Fail($"Refusing openra1n: DFU host service is '{dfuService ?? "missing"}'. It must be libusbK and owned by Windows.");
                    return JailbreakStage.Failed;
                }
                Emit("orchestrator", $"DFU transaction ready: mode={dfu.Mode}, service={dfuService}, owner=Windows.");

                Report(JailbreakStage.RunningOpenRa1n, "Running openra1n until PongoOS enumerates...", 40);
                var pongoReached = await _openRa1nService.RunUntilPongoAsync(toolchain, cancellationToken)
                    .ConfigureAwait(false);
                if (!pongoReached)
                {
                    Fail("PongoOS USB 05AC:4141 was not detected after openra1n. No background driver-reinstall loop was started; inspect the exact final mode and driver in Device/Logs.");
                    return JailbreakStage.Failed;
                }
            }
            else
            {
                Report(JailbreakStage.RunningOpenRa1n, "PongoOS is already present; skipping openra1n.", 45);
                if (!ReleaseAppleToWindows())
                {
                    return JailbreakStage.Failed;
                }
            }

            Report(JailbreakStage.EnsuringPongoDriver, "Verifying the PongoOS host driver transaction...", 60);
            if (!await EnsureModeDriverAsync(DeviceMode.Pongo, 0x4141, allowWinUsb: true, cancellationToken)
                    .ConfigureAwait(false))
            {
                return JailbreakStage.Failed;
            }

            var pongo = FindDevice(0x4141);
            var pongoService = pongo is null ? null : DriverInstaller.DetectService(pongo);
            Emit(
                "orchestrator",
                $"PongoOS transaction ready: service={pongoService ?? "unknown"}, owner=Windows. The palera1n launcher will perform the explicit Windows-to-WSL payload handoff.");

            Report(JailbreakStage.RunningPalera1n, "Running palera1n payloads through the controlled WSL handoff...", 75);
            var options = JailbreakOptions.FromSettings(_settings);
            var exitCode = await _palera1nHostService.RunPalera1nAsync(toolchain, options, cancellationToken)
                .ConfigureAwait(false);
            if (exitCode != 0)
            {
                Fail($"palera1n exited with code {exitCode}.");
                return JailbreakStage.Failed;
            }

            Report(JailbreakStage.Completed, "Jailbreak flow completed.", 100);
            return JailbreakStage.Completed;
        }
        catch (OperationCanceledException)
        {
            Report(JailbreakStage.Cancelled, "Jailbreak cancelled.", 100);
            return JailbreakStage.Cancelled;
        }
        catch (Exception exception)
        {
            Fail(exception.Message);
            return JailbreakStage.Failed;
        }
        finally
        {
            UsbipdService.KillLeftoverUsbBridges();
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
            var device = FindDevice(productId);
            if (device is null)
            {
                await Task.Delay(750, cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (device.Mode == DeviceMode.PwnedDfu)
            {
                Fail("Gaster PWND without a YOLO-compatible handoff cannot boot this Pongo path. Force-reboot into clean DFU.");
                return false;
            }

            var modeAccepted = device.Mode == requiredMode ||
                               (requiredMode == DeviceMode.Dfu && device.Mode == DeviceMode.YoloDfu);
            if (!modeAccepted)
            {
                await Task.Delay(750, cancellationToken).ConfigureAwait(false);
                continue;
            }

            var service = DriverInstaller.ResolveServiceName(device.DeviceId) ?? DriverInstaller.DetectService(device);
            if (DriverInstaller.IsUsbipdStubService(service))
            {
                Emit(
                    "orchestrator",
                    $"Apple PID 0x{productId:X4} is still owned by usbipd ({service}); releasing it before driver verification.",
                    isError: true);
                if (!ReleaseAppleToWindows()) return false;
                await Task.Delay(2000, cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (DriverInstaller.IsLibusbKService(service) ||
                (allowWinUsb && DriverInstaller.IsWinUsbService(service)))
            {
                Emit("driver", $"PID 0x{productId:X4} already has acceptable service '{service}'. No reinstall needed.");
                return true;
            }

            var result = await _driverInstaller.EnsureLibusbKAsync(
                productId,
                new Progress<ProgressEventArgs>(progress => ProgressChanged?.Invoke(this, progress)),
                cancellationToken).ConfigureAwait(false);

            if (result is DriverInstallResult.AlreadyOk or DriverInstallResult.Installed)
            {
                if (await WaitForAcceptedDriverAsync(productId, allowWinUsb, TimeSpan.FromSeconds(35), cancellationToken)
                        .ConfigureAwait(false))
                {
                    return true;
                }
            }

            if (result == DriverInstallResult.NeedsManualZadig && !offeredManualZadig && _userPrompts is not null)
            {
                offeredManualZadig = true;
                var toolchain = Paths.ResolveToolchainRoot(_settings.ToolchainRoot);
                var openZadig = await _userPrompts.ConfirmAsync(
                    new UserPromptRequest
                    {
                        Title = "Apple USB driver transaction needs manual repair",
                        Message =
                            $"Automated libusbK installation did not verify for Apple VID 05AC PID {productId:X4}.\n\n" +
                            "Open Zadig only for this exact Apple DFU/Pongo device, select libusbK, and replace the driver. Never select a mouse, keyboard, storage device, or normal-mode Apple device.",
                        ConfirmText = "Open Zadig",
                        CancelText = "Cancel",
                    },
                    cancellationToken).ConfigureAwait(false);

                if (!openZadig || toolchain is null)
                {
                    Fail("The required Apple USB binding was not installed.");
                    return false;
                }

                _driverInstaller.LaunchZadig(toolchain);
                if (await WaitForAcceptedDriverAsync(productId, allowWinUsb, TimeSpan.FromMinutes(6), cancellationToken)
                        .ConfigureAwait(false))
                {
                    return true;
                }
                Fail("The required Apple USB binding still did not verify after the Zadig repair window.");
                return false;
            }

            if (result == DriverInstallResult.Failed)
            {
                Fail($"Driver transaction failed for Apple PID 0x{productId:X4}.");
                return false;
            }

            await Task.Delay(750, cancellationToken).ConfigureAwait(false);
        }

        Fail($"Timed out verifying the driver transaction for Apple PID 0x{productId:X4}.");
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
            var device = FindDevice(productId);
            if (device is not null)
            {
                var service = DriverInstaller.ResolveServiceName(device.DeviceId) ?? DriverInstaller.DetectService(device);
                if (DriverInstaller.IsLibusbKService(service) ||
                    (allowWinUsb && DriverInstaller.IsWinUsbService(service)))
                {
                    return true;
                }
            }
            await Task.Delay(750, cancellationToken).ConfigureAwait(false);
        }
        return false;
    }

    private async Task<bool> WaitForDfuAsync(string toolchainRoot, CancellationToken cancellationToken)
    {
        _monitor.PollNow();
        var current = _monitor.CurrentDevice;
        if (current.ProductId == 0x1227 && current.Mode is DeviceMode.Dfu or DeviceMode.YoloDfu)
        {
            return true;
        }
        if (current.Mode == DeviceMode.PwnedDfu)
        {
            Fail("Stale gaster PWND was detected. Force-reboot into clean DFU before continuing.");
            return false;
        }
        if (current.Mode == DeviceMode.Pongo)
        {
            return true;
        }

        if (!_usbipdService.IsAvailable)
        {
            Fail("usbipd-win is required for the guided normal/recovery-to-DFU helper. Install usbipd-win or enter DFU manually before pressing Start Jailbreak.");
            return false;
        }

        const int maxAttempts = 4;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (HostHasDfuDevice() || IsPongoPresent()) return true;

            var wsl = new WslService(_settings.WslDistro);
            var attachProgress = new Progress<string>(message => Emit("usbipd", message));
            Report(
                JailbreakStage.EnsuringDfuDriver,
                attempt == 1
                    ? "Temporarily attaching Apple USB to WSL for the guided DFU helper..."
                    : $"Guided DFU retry {attempt}/{maxAttempts}: attaching Apple USB to WSL...",
                18);

            var attach = await _usbipdService.EnsureAppleAttachedToWslAsync(
                _settings.WslDistro,
                wsl,
                attachProgress,
                cancellationToken,
                timeout: TimeSpan.FromSeconds(45)).ConfigureAwait(false);
            if (!attach.Succeeded)
            {
                Emit("usbipd", attach.Message, isError: true);
                UsbipdService.KillLeftoverUsbBridges();
                if (attempt == maxAttempts)
                {
                    Fail(attach.Message);
                    return false;
                }
                await Task.Delay(1500, cancellationToken).ConfigureAwait(false);
                continue;
            }

            var dfuMissed = false;
            var promptSeen = false;
            void ObserveDfuLog(object? sender, LogLine line)
            {
                if (line.Message.Contains("Whoops, device did not enter DFU mode", StringComparison.OrdinalIgnoreCase))
                    dfuMissed = true;
                if (line.Message.Contains("Press Enter when ready for DFU mode", StringComparison.OrdinalIgnoreCase))
                    promptSeen = true;
            }

            _palera1nHostService.LogReceived += ObserveDfuLog;
            using var helperCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var helperEndedNaturally = false;
            var helperExit = 0;
            try
            {
                var started = DateTimeOffset.UtcNow;
                DateTimeOffset? missedAt = null;
                var helperTask = _palera1nHostService.RunDfuHelperAsync(
                    toolchainRoot,
                    _settings.WslDistro,
                    helperCts.Token);

                while (!helperTask.IsCompleted)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (HostHasDfuDevice() || IsPongoPresent())
                    {
                        helperCts.Cancel();
                        break;
                    }

                    if (dfuMissed)
                    {
                        missedAt ??= DateTimeOffset.UtcNow;
                        if (DateTimeOffset.UtcNow - missedAt > TimeSpan.FromSeconds(8))
                        {
                            Emit("orchestrator", "The DFU timing attempt was missed; stopping the helper instead of allowing an infinite reconnect loop.", isError: true);
                            helperCts.Cancel();
                            break;
                        }
                    }
                    else if (!promptSeen && DateTimeOffset.UtcNow - started > TimeSpan.FromSeconds(30))
                    {
                        Emit("orchestrator", "The DFU helper did not reach its prompt within 30 seconds; stopping the stale WSL/device-event wait.", isError: true);
                        helperCts.Cancel();
                        break;
                    }

                    await Task.WhenAny(helperTask, Task.Delay(750, cancellationToken)).ConfigureAwait(false);
                }

                try
                {
                    helperExit = await helperTask.ConfigureAwait(false);
                    helperEndedNaturally = !HostHasDfuDevice() && !IsPongoPresent();
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    // Intentional bounded stop after DFU success or a known stale helper state.
                }
            }
            finally
            {
                _palera1nHostService.LogReceived -= ObserveDfuLog;
                UsbipdService.KillLeftoverUsbBridges();
            }

            if (HostHasDfuDevice() || IsPongoPresent()) return true;
            if (helperEndedNaturally && helperExit != 0 && !dfuMissed)
            {
                Fail($"The DFU helper failed with exit code {helperExit}; this was not a button-timing miss.");
                return false;
            }

            if (attempt < maxAttempts)
            {
                Emit("orchestrator", $"DFU was not reached on attempt {attempt}/{maxAttempts}; retrying with a fresh ownership transaction.");
                await Task.Delay(2000, cancellationToken).ConfigureAwait(false);
            }
        }

        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromMinutes(2);
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (HostHasDfuDevice() || IsPongoPresent()) return true;
            await Task.Delay(750, cancellationToken).ConfigureAwait(false);
        }

        Fail("DFU never appeared on Windows. The app will not continue to openra1n or driver installation without a real 05AC:1227 device.");
        return false;
    }

    private bool ReleaseAppleToWindows()
    {
        UsbipdService.KillLeftoverUsbBridges();
        if (!_usbipdService.IsAvailable) return true;

        var result = _usbipdService.ReleaseAppleToHost();
        if (!string.IsNullOrWhiteSpace(result.Message))
        {
            Emit("usbipd", result.Message.Trim(), isError: !result.Succeeded);
        }
        if (!result.Succeeded)
        {
            Fail(result.Message);
            return false;
        }
        return true;
    }

    private AppleUsbDevice? FindDevice(ushort productId)
    {
        _monitor.PollNow();
        return _monitor.ScanDevices()
            .FirstOrDefault(device =>
                device.IsPresent &&
                device.ProductId == productId &&
                device.Mode is not DeviceMode.Busy);
    }

    private bool IsPongoPresent()
    {
        if (FindDevice(0x4141) is not null) return true;
        return _monitor.IsPongoVisibleInUsbipd();
    }

    private bool HostHasDfuDevice()
    {
        var device = FindDevice(0x1227);
        return device?.Mode is DeviceMode.Dfu or DeviceMode.YoloDfu;
    }

    private static async Task StopAmdsIfPresentAsync(string toolchainRoot, CancellationToken cancellationToken)
    {
        var script = Paths.GetStopAmdsScript(toolchainRoot);
        if (!File.Exists(script)) return;

        await ProcessRunner.RunAsync(
            "powershell.exe",
            new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", script },
            workingDirectory: toolchainRoot,
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private void Report(JailbreakStage stage, string message, int percent)
    {
        ProgressChanged?.Invoke(this, new ProgressEventArgs(stage.ToString(), message, percent));
        Emit("orchestrator", message);
    }

    private void Fail(string message)
    {
        Emit("orchestrator", message, isError: true);
        ProgressChanged?.Invoke(this, new ProgressEventArgs(JailbreakStage.Failed.ToString(), message, 100));
    }

    private void Emit(string source, string message, bool isError = false) =>
        LogReceived?.Invoke(this, new LogLine { Source = source, Message = message, IsError = isError });

    private void ForwardLog(object? sender, LogLine line) => LogReceived?.Invoke(this, line);

    public void Dispose()
    {
        _openRa1nService.LogReceived -= ForwardLog;
        _palera1nHostService.LogReceived -= ForwardLog;
        _openRa1nService.Dispose();
        if (_ownsMonitor) _monitor.Dispose();
    }
}
