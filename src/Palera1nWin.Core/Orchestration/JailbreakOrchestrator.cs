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
    private readonly DriverInstaller _driverInstaller;
    private readonly UsbipdService _usbipdService;
    private readonly OpenRa1nService _openRa1nService;
    private readonly Palera1nHostService _palera1nHostService;
    private readonly IUserPromptService? _userPrompts;

    public JailbreakOrchestrator(AppSettings settings, IUserPromptService? userPrompts = null)
    {
        _settings = settings;
        _userPrompts = userPrompts;
        _monitor = new AppleUsbMonitor();
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
            Report(JailbreakStage.Validating, "Validating toolchain and settings...", 0);

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

            Report(JailbreakStage.StoppingAmds, "Stopping Apple Mobile Device Service...", 10);
            await StopAmdsIfPresentAsync(toolchain, cancellationToken).ConfigureAwait(false);

            if (_usbipdService.DetectsUsbDkConflict())
            {
                ForwardLog(this, new LogLine
                {
                    Source = "orchestrator",
                    Message =
                        "UsbDk filter is installed — NOT required for jailbreak and conflicts with usbipd. " +
                        "Original success used libusbK only. Prefer uninstalling UsbDk; binds use --force meanwhile.",
                    IsError = false,
                });
            }

            // Do NOT detach usbipd before DFU helper - palera1n -D needs the phone inside WSL.
            Report(JailbreakStage.EnsuringDfuDriver, "Waiting for Apple DFU (05AC:1227)...", 20);
            var alreadyPongo = IsPongoPresent();
            if (!alreadyPongo)
            {
                var inDfu = await WaitForDfuAsync(toolchain, cancellationToken).ConfigureAwait(false);
                if (!inDfu)
                {
                    Fail("Device never entered DFU. Unplug/replug, then retry. Use Device tab to confirm mode.");
                    return JailbreakStage.Failed;
                }

                // Host must own DFU for openra1n. Detach alone leaves VBoxUSB stub bound —
                // openra1n then ACCESS_VIOLATIONs after YOLO. Unbind + kill leftover bridges.
                Report(JailbreakStage.DetachingUsbipd, "Handing DFU back to Windows for openra1n...", 22);
                UsbipdService.KillLeftoverUsbBridges();
                if (_usbipdService.IsAvailable)
                {
                    var release = _usbipdService.ReleaseAppleToHost();
                    if (!string.IsNullOrWhiteSpace(release.Message))
                    {
                        ForwardLog(this, new LogLine
                        {
                            Source = "usbipd",
                            Message = release.Message.Trim(),
                            IsError = !release.Succeeded,
                        });
                    }

                    if (!release.Succeeded)
                    {
                        Fail(release.Message);
                        return JailbreakStage.Failed;
                    }
                }

                // Re-enumeration after unbind; stub must leave before host USB/openra1n.
                await Task.Delay(2500, cancellationToken).ConfigureAwait(false);

                // Step 1: install/verify the host USB backend first (no watchdog running; avoids two wdi-simple
                // instances clashing, which causes the orchestrator to give up while the
                // watchdog succeeds 1s later).
                Report(JailbreakStage.EnsuringDfuDriver, "Ensuring DFU WinUSB/libusb driver...", 25);
                var dfuReady = await EnsureModeDriverAsync(
                    DeviceMode.Dfu,
                    0x1227,
                    cancellationToken).ConfigureAwait(false);

                if (!dfuReady)
                {
                    Fail(
                        "DFU WinUSB/libusb driver is not ready (must not be VBoxUSB/usbipd stub). " +
                        "In Zadig: Apple DFU -> libusbK -> Replace Driver, wait for SUCCESS.");
                    return JailbreakStage.Failed;
                }

                // Final host-driver gate before openra1n (reject VBoxUSB even if prior check raced).
                _monitor.PollNow();
                var dfuDevice = _monitor.ScanDevices()
                    .FirstOrDefault(d => d.IsPresent && d.ProductId == 0x1227 && d.Mode is not DeviceMode.Busy);
                var dfuService = dfuDevice is null ? null : DriverInstaller.DetectService(dfuDevice);
                if (dfuDevice is null)
                {
                    Fail("DFU disappeared after driver setup. Re-enter DFU and retry.");
                    return JailbreakStage.Failed;
                }

                if (DriverInstaller.IsUsbipdStubService(dfuService) ||
                    !DriverInstaller.IsHostUsbBackendService(dfuService))
                {
                    Fail(
                        $"Refusing openra1n: DFU service is '{dfuService ?? "unknown"}' " +
                        "(need WINUSB/libusbK, not VBoxUSB/usbipd).");
                    return JailbreakStage.Failed;
                }

                ForwardLog(this, new LogLine
                {
                    Source = "orchestrator",
                    Message = $"DFU host driver OK: {dfuService} (mode={dfuDevice.Mode})",
                });

                // Step 2: NOW start the watchdog. The initial backend check is already done,
                // so the watchdog only fires if openra1n leaves DFU/Pongo on a bad service.
                using var driverWatch = new LibusbKWatchdog(_monitor, _settings);
                driverWatch.LogReceived += ForwardLog;
                driverWatch.Start();
                try
                {
                    Report(JailbreakStage.RunningOpenRa1n, "Running openra1n until PongoOS...", 40);
                    var pongoReached = await _openRa1nService.RunUntilPongoAsync(toolchain, cancellationToken)
                        .ConfigureAwait(false);

                    if (!pongoReached)
                    {
                        Fail(
                            "PongoOS USB (05AC:4141) not seen in time after openra1n. " +
                            "If Device tab already shows PongoOS, just Start Jailbreak again — it will skip openra1n.");
                        return JailbreakStage.Failed;
                    }

                    Report(JailbreakStage.EnsuringPongoDriver, "Ensuring PongoOS driver...", 60);
                    var pongoReady = await EnsureModeDriverAsync(
                        DeviceMode.Pongo,
                        0x4141,
                        cancellationToken,
                        allowWinUsb: true).ConfigureAwait(false);

                    if (!pongoReady)
                    {
                        Fail("PongoOS driver is not ready.");
                        return JailbreakStage.Failed;
                    }
                }
                finally
                {
                    driverWatch.LogReceived -= ForwardLog;
                    driverWatch.Stop();
                }
            }
            else
            {
                Report(JailbreakStage.RunningOpenRa1n, "PongoOS already present - skipping openra1n.", 40);
                UsbipdService.KillLeftoverUsbBridges();
                if (_usbipdService.IsAvailable)
                {
                    _usbipdService.ReleaseAppleToHost();
                }

                Report(JailbreakStage.EnsuringPongoDriver, "Ensuring PongoOS driver...", 60);
                using var pongoWatch = new LibusbKWatchdog(_monitor, _settings);
                pongoWatch.LogReceived += ForwardLog;
                pongoWatch.Start();
                try
                {
                    var pongoReady = await EnsureModeDriverAsync(
                        DeviceMode.Pongo,
                        0x4141,
                        cancellationToken,
                        allowWinUsb: true).ConfigureAwait(false);

                    if (!pongoReady)
                    {
                        Fail("PongoOS driver is not ready.");
                        return JailbreakStage.Failed;
                    }
                }
                finally
                {
                    pongoWatch.LogReceived -= ForwardLog;
                    pongoWatch.Stop();
                }
            }

            Report(JailbreakStage.RunningPalera1n, "Running palera1n payloads...", 75);
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
        catch (Exception ex)
        {
            Fail(ex.Message);
            return JailbreakStage.Failed;
        }
    }

    private async Task<bool> EnsureModeDriverAsync(
        DeviceMode requiredMode,
        ushort productId,
        CancellationToken cancellationToken,
        bool allowWinUsb = false)
    {
        var deadline = DateTimeOffset.UtcNow.AddMinutes(3);
        var offeredManualZadig = false;

        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _monitor.PollNow();

            var device = _monitor.ScanDevices()
                .FirstOrDefault(d =>
                    d.ProductId == productId &&
                    d.IsPresent &&
                    d.Mode is not DeviceMode.Busy);

            if (device is null)
            {
                await Task.Delay(750, cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (device.Mode == DeviceMode.PwnedDfu)
            {
                Fail("Gaster PWND without YOLO cannot boot Pongo. Force-reboot into clean DFU.");
                return false;
            }

            if (device.Mode != requiredMode && requiredMode == DeviceMode.Dfu &&
                device.Mode == DeviceMode.YoloDfu)
            {
                // YOLO DFU is acceptable for openra1n (skips exploit, boots Pongo).
            }
            else if (device.Mode != requiredMode && requiredMode == DeviceMode.Dfu)
            {
                await Task.Delay(750, cancellationToken).ConfigureAwait(false);
                continue;
            }

            var service = DriverInstaller.DetectService(device);
            if (DriverInstaller.IsUsbipdStubService(service))
            {
                ForwardLog(this, new LogLine
                {
                    Source = "orchestrator",
                    Message =
                        $"Apple PID 0x{productId:X4} still on usbipd stub ({service}). " +
                        "Releasing usbipd bind and reinstalling libusbK...",
                    IsError = true,
                });

                UsbipdService.KillLeftoverUsbBridges();
                if (_usbipdService.IsAvailable)
                {
                    _usbipdService.ReleaseAppleToHost();
                }

                await Task.Delay(2000, cancellationToken).ConfigureAwait(false);
                // Fall through to EnsureLibusbKAsync — do not treat stub as ready.
            }
            else if (DriverInstaller.IsHostUsbBackendService(service) ||
                     (allowWinUsb && DriverInstaller.IsWinUsbService(service)))
            {
                return true;
            }

            var result = await _driverInstaller.EnsureLibusbKAsync(
                productId,
                new Progress<ProgressEventArgs>(p => ProgressChanged?.Invoke(this, p)),
                cancellationToken).ConfigureAwait(false);

            if (result == DriverInstallResult.AlreadyOk || result == DriverInstallResult.Installed)
            {
                return true;
            }

            if (result == DriverInstallResult.NeedsManualZadig && !offeredManualZadig && _userPrompts is not null)
            {
                offeredManualZadig = true;
                var toolchain = Paths.ResolveToolchainRoot(_settings.ToolchainRoot);
                var openZadig = await _userPrompts.ConfirmAsync(
                    new UserPromptRequest
                    {
                        Title = "DFU USB driver needed",
                        Message =
                            $"Automated WinUSB/libusb driver install did not stick for Apple USB PID 0x{productId:X4}.\n\n" +
                            "Click OK to open Zadig. In Zadig you MUST select the Apple DFU/Pongo device " +
                            $"(VID 05AC PID {productId:X4}) — not your mouse/keyboard — then choose libusbK → Replace Driver.\n\n" +
                            "Click Cancel to abort.",
                        ConfirmText = "Open Zadig",
                        CancelText = "Cancel",
                    },
                    cancellationToken).ConfigureAwait(false);

                if (!openZadig || toolchain is null)
                {
                    Fail("A WinUSB/libusb driver is required and was not installed.");
                    return false;
                }

                ForwardLog(this, new LogLine
                {
                    Source = "orchestrator",
                    Message =
                        $"Opening Zadig — select Apple device PID {productId:X4} → libusbK → Replace Driver.",
                });
                _driverInstaller.LaunchZadig(toolchain);

                if (await WaitForHostUsbBackendAsync(productId, TimeSpan.FromMinutes(6), cancellationToken)
                        .ConfigureAwait(false))
                {
                    return true;
                }

                Fail("WINUSB/libusbK still not active after Zadig wait.");
                return false;
            }

            if (result == DriverInstallResult.Failed)
            {
                Fail($"Driver install failed for PID 0x{productId:X4}.");
                return false;
            }

            await Task.Delay(750, cancellationToken).ConfigureAwait(false);
        }

        return false;
    }

    private async Task<bool> WaitForHostUsbBackendAsync(
        ushort productId,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _monitor.PollNow();
            var device = _monitor.ScanDevices()
                .FirstOrDefault(d => d.ProductId == productId && d.IsPresent);
            if (device is not null && DriverInstaller.IsHostUsbBackendService(DriverInstaller.DetectService(device)))
            {
                return true;
            }

            await Task.Delay(750, cancellationToken).ConfigureAwait(false);
        }

        return false;
    }

    private async Task<bool> WaitForDfuAsync(string toolchainRoot, CancellationToken cancellationToken)
    {
        _monitor.PollNow();
        var current = _monitor.CurrentDevice;
        if (current is not null &&
            current.ProductId == 0x1227 &&
            current.Mode is DeviceMode.Dfu or DeviceMode.YoloDfu)
        {
            return true;
        }

        if (current is not null && current.Mode == DeviceMode.Pongo)
        {
            ForwardLog(this, new LogLine
            {
                Source = "orchestrator",
                Message = "PongoOS already present - skipping DFU/openra1n.",
            });
            return true;
        }

        // If Normal/Recovery, attach into WSL first, then kick palera1n DFU helper (-D).
        if (current is null ||
            current.Mode is DeviceMode.Normal or DeviceMode.Recovery or DeviceMode.None or DeviceMode.Busy)
        {
            if (!_usbipdService.IsAvailable)
            {
                Fail("usbipd-win is required for DFU helper. Install dorssel.usbipd-win and relaunch as admin.");
                return false;
            }

            // palera1n -D over usbipd is flaky: it often prints "Whoops, device did not
            // enter DFU mode" and then loops forever on "Waiting for device to reconnect",
            // and it can also hang at "Entering recovery mode" when the re-enumerated
            // device isn't re-attached to WSL. Both are exactly the hangs you hit and had
            // to fix with a manual Cancel + Start Jailbreak. This loop automates that:
            // it monitors the host, auto-continues the moment the device is really in DFU
            // (openra1n does its own checkm8 from clean DFU), and otherwise force-stops a
            // stuck helper and retries the whole DFU dance a bounded number of times.
            const int maxDfuAttempts = 4;
            var dfuReached = false;

            for (var attempt = 1; attempt <= maxDfuAttempts && !dfuReached; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // Device may already be in DFU/Pongo (a prior attempt landed it, or the
                // user entered DFU manually) — skip straight to openra1n.
                if (HostHasDfuDevice() || IsPongoPresent())
                {
                    dfuReached = true;
                    break;
                }

                var wsl = new WslService(_settings.WslDistro);
                var attachProgress = new Progress<string>(msg => ForwardLog(this, new LogLine
                {
                    Source = "usbipd",
                    Message = msg,
                }));

                Report(
                    JailbreakStage.EnsuringDfuDriver,
                    attempt == 1
                        ? "Attaching Apple USB to WSL (required for Waiting for devices)..."
                        : $"DFU retry {attempt}/{maxDfuAttempts}: re-attaching Apple USB to WSL...",
                    18);

                var attach = await _usbipdService.EnsureAppleAttachedToWslAsync(
                    _settings.WslDistro,
                    wsl,
                    attachProgress,
                    cancellationToken).ConfigureAwait(false);

                if (!attach.Succeeded)
                {
                    if (attempt == 1)
                    {
                        Fail(attach.Message);
                        if (attach.UsbDkDetected)
                        {
                            ForwardLog(this, new LogLine
                            {
                                Source = "usbipd",
                                Message =
                                    "UsbDk conflicts with usbipd. Uninstall UsbDk (Add/Remove Programs), reboot, then retry.",
                                IsError = true,
                            });
                        }

                        return false;
                    }

                    ForwardLog(this, new LogLine
                    {
                        Source = "usbipd",
                        Message = $"{attach.Message} — retrying attach...",
                        IsError = true,
                    });
                    UsbipdService.KillLeftoverUsbBridges();
                    await Task.Delay(1500, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                ForwardLog(this, new LogLine { Source = "orchestrator", Message = attach.Message });
                ForwardLog(this, new LogLine
                {
                    Source = "orchestrator",
                    Message = attempt == 1
                        ? "Starting DFU helper. When palera1n prints \"Press Enter when ready for DFU mode\", an OK/Cancel dialog will appear — not before."
                        : $"Starting DFU helper (attempt {attempt}/{maxDfuAttempts}). Get ready for the button sequence again when the dialog appears.",
                });

                var dfuMissed = false;
                var dfuPromptShown = false;
                void OnDfuLog(object? sender, LogLine line)
                {
                    if (line.Message.Contains("Whoops, device did not enter DFU mode", StringComparison.OrdinalIgnoreCase))
                    {
                        dfuMissed = true;
                    }
                    else if (line.Message.Contains("Press Enter when ready for DFU mode", StringComparison.OrdinalIgnoreCase))
                    {
                        dfuPromptShown = true;
                    }
                }

                _palera1nHostService.LogReceived += OnDfuLog;

                using var helperCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                var helperExit = 0;
                var helperEndedNaturally = false;
                try
                {
                    var attemptStart = DateTimeOffset.UtcNow;
                    var helperTask = _palera1nHostService.RunDfuHelperAsync(
                        toolchainRoot,
                        _settings.WslDistro,
                        helperCts.Token);

                    // Only give up on an attempt AFTER "Whoops" (the user is no longer
                    // holding buttons by then), so we never cut off a valid countdown.
                    DateTimeOffset? missedAt = null;
                    while (!helperTask.IsCompleted)
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        if (HostHasDfuDevice() || IsPongoPresent())
                        {
                            dfuReached = true;
                            ForwardLog(this, new LogLine
                            {
                                Source = "orchestrator",
                                Message = "Device is in DFU on Windows — stopping the DFU helper and continuing to openra1n automatically.",
                            });
                            helperCts.Cancel();
                            break;
                        }

                        if (dfuMissed)
                        {
                            missedAt ??= DateTimeOffset.UtcNow;
                            if (DateTimeOffset.UtcNow - missedAt > TimeSpan.FromSeconds(8))
                            {
                                ForwardLog(this, new LogLine
                                {
                                    Source = "orchestrator",
                                    Message = "palera1n missed DFU and is looping on \"Waiting for device to reconnect\" — stopping this attempt.",
                                    IsError = true,
                                });
                                helperCts.Cancel();
                                break;
                            }
                        }
                        else if (!dfuPromptShown && DateTimeOffset.UtcNow - attemptStart > TimeSpan.FromSeconds(30))
                        {
                            // palera1n's own "Entering recovery mode" wait has NO timeout of its
                            // own (dfuhelper.c blocks on a usbmuxd/libirecovery device-event that
                            // never fires if the bridge misses the Normal->Recovery re-enumeration).
                            // This is the exact hang you hit twice — stop and retry instead of
                            // waiting forever for an event that isn't coming.
                            ForwardLog(this, new LogLine
                            {
                                Source = "orchestrator",
                                Message = "palera1n has not reached the DFU prompt yet (likely stuck entering recovery mode — the device re-enumeration was probably missed). Stopping this attempt.",
                                IsError = true,
                            });
                            helperCts.Cancel();
                            break;
                        }

                        // Wake as soon as the helper finishes OR ~750ms elapses to re-poll.
                        await Task.WhenAny(helperTask, Task.Delay(750, cancellationToken)).ConfigureAwait(false);
                    }

                    try
                    {
                        helperExit = await helperTask.ConfigureAwait(false);
                        helperEndedNaturally = !dfuReached;
                    }
                    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                    {
                        // Expected: we intentionally stopped the helper (DFU reached or stuck loop).
                    }
                }
                finally
                {
                    _palera1nHostService.LogReceived -= OnDfuLog;
                }

                // A force-stopped helper can't run its own cleanup, so clear any orphaned
                // elevated USB bridge before the next attempt / before openra1n.
                UsbipdService.KillLeftoverUsbBridges();

                if (dfuReached || HostHasDfuDevice())
                {
                    dfuReached = true;
                    break;
                }

                // Helper ended on its own with an error that isn't a DFU-timing miss
                // (e.g. FIFO/GUI-prompt failure, runtime missing) — retrying won't help.
                if (helperEndedNaturally && helperExit != 0 && !dfuMissed)
                {
                    Fail($"DFU helper failed with exit code {helperExit} (not a DFU timing miss). See the log above.");
                    return false;
                }

                if (attempt < maxDfuAttempts)
                {
                    ForwardLog(this, new LogLine
                    {
                        Source = "orchestrator",
                        Message = $"DFU not entered (attempt {attempt}/{maxDfuAttempts}). Retrying automatically — a new DFU prompt will appear shortly.",
                    });
                    await Task.Delay(2500, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    Fail(
                        $"Device did not enter DFU after {maxDfuAttempts} automatic attempts. " +
                        "iPhone 8/X sequence: hold Side + Volume Down together ~8s, then release Side but KEEP holding Volume Down until the screen stays black, then click OK. Click Start Jailbreak to try again.");
                    return false;
                }
            }
        }

        // Require a real present DFU device (not Busy/Error ghosts).
        var deadline = DateTimeOffset.UtcNow.AddMinutes(2);
        var lastHint = DateTimeOffset.MinValue;
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _monitor.PollNow();
            var d = _monitor.ScanDevices()
                .FirstOrDefault(x =>
                    x.IsPresent &&
                    x.ProductId == 0x1227 &&
                    x.Mode is DeviceMode.Dfu or DeviceMode.YoloDfu);

            if (d is not null)
            {
                ForwardLog(this, new LogLine
                {
                    Source = "orchestrator",
                    Message = $"DFU confirmed on host ({d.Mode}).",
                });
                return true;
            }

            if (IsPongoPresent())
            {
                return true;
            }

            if ((DateTimeOffset.UtcNow - lastHint).TotalSeconds >= 10)
            {
                lastHint = DateTimeOffset.UtcNow;
                ForwardLog(this, new LogLine
                {
                    Source = "orchestrator",
                    Message = "Still waiting for DFU (05AC:1227) on Windows after helper...",
                });
            }

            await Task.Delay(750, cancellationToken).ConfigureAwait(false);
        }

        Fail("DFU device never appeared on Windows. Do not continue to openra1n/Zadig without DFU.");
        return false;
    }

    private bool IsPongoPresent()
    {
        _monitor.PollNow();
        if (_monitor.ScanDevices().Any(x => x.ProductId == 0x4141 && !string.IsNullOrWhiteSpace(x.DeviceId)))
        {
            return true;
        }

        return _monitor.IsPongoVisibleInUsbipd();
    }

    /// <summary>
    /// True when a real DFU device (05AC:1227) is present on the Windows host.
    /// Used to short-circuit palera1n's flaky "Waiting for device to reconnect"
    /// loop the moment the device is actually in DFU.
    /// </summary>
    private bool HostHasDfuDevice()
    {
        _monitor.PollNow();
        return _monitor.ScanDevices().Any(d =>
            d.IsPresent &&
            d.ProductId == 0x1227 &&
            d.Mode is DeviceMode.Dfu or DeviceMode.YoloDfu);
    }

    private static async Task StopAmdsIfPresentAsync(string toolchainRoot, CancellationToken cancellationToken)
    {
        var script = Paths.GetStopAmdsScript(toolchainRoot);
        if (!File.Exists(script))
        {
            return;
        }

        await ProcessRunner.RunAsync(
            "powershell.exe",
            new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", script },
            workingDirectory: toolchainRoot,
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private void Report(JailbreakStage stage, string message, int percent)
    {
        ProgressChanged?.Invoke(this, new ProgressEventArgs(stage.ToString(), message, percent));
        ForwardLog(this, new LogLine { Source = "orchestrator", Message = message });
    }

    private void Fail(string message)
    {
        ForwardLog(this, new LogLine { Source = "orchestrator", Message = message, IsError = true });
        ProgressChanged?.Invoke(this, new ProgressEventArgs(JailbreakStage.Failed.ToString(), message, 100));
    }

    private void ForwardLog(object? sender, LogLine line)
    {
        LogReceived?.Invoke(this, line);
    }

    public void Dispose()
    {
        _openRa1nService.LogReceived -= ForwardLog;
        _palera1nHostService.LogReceived -= ForwardLog;
        _openRa1nService.Dispose();
        _monitor.Dispose();
    }
}
