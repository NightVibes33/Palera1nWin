using Microsoft.Win32;
using Palera1nWin.Core.Models;
using Palera1nWin.Core.Settings;
using Palera1nWin.Core.Usb;
using Palera1nWin.Core.Util;

namespace Palera1nWin.Core.Drivers;

/// <summary>
/// Continuously watches Apple DFU/Pongo on the Windows host and re-installs libusbK
/// whenever Windows flips the driver back to WinUSB / unknown / Apple stock.
/// This matches the manual "Replace Driver 20+ times" loop from the first successful run.
/// </summary>
public sealed class LibusbKWatchdog : IDisposable
{
    private static readonly ushort[] WatchedPids = [0x1227, 0x4141];

    private readonly AppleUsbMonitor _monitor;
    private readonly AppSettings _settings;
    private readonly TimeSpan _pollInterval;
    private readonly TimeSpan _reinstallCooldown;
    private readonly Dictionary<ushort, string?> _lastLoggedService = new();
    private readonly Dictionary<ushort, DateTimeOffset> _lastAttempt = new();
    private readonly object _startStopLock = new();
    private bool _loggedAutoOff;
    private bool _loggedNotAdmin;
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private bool _disposed;

    public LibusbKWatchdog(
        AppleUsbMonitor monitor,
        AppSettings settings,
        TimeSpan? pollInterval = null,
        TimeSpan? reinstallCooldown = null)
    {
        _monitor = monitor;
        _settings = settings;
        _pollInterval = pollInterval ?? TimeSpan.FromSeconds(1);
        _reinstallCooldown = reinstallCooldown ?? TimeSpan.FromSeconds(8);
    }

    public event EventHandler<LogLine>? LogReceived;

    public int ReinstallCount { get; private set; }

    /// <summary>
    /// When true, the watchdog skips all ticks. Set by MainViewModel during jailbreak
    /// so the orchestrator's own watchdog doesn't conflict with the global one.
    /// </summary>
    public bool IsPaused { get; set; }

    public void Start()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(LibusbKWatchdog));
        }

        lock (_startStopLock)
        {
            Stop();
            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            Emit("driver-watch", "libusbK watchdog started (will re-apply whenever DFU/Pongo leaves libusbK).");
            _loop = Task.Run(() => RunLoopAsync(token), token);
        }
    }

    public void Stop()
    {
        lock (_startStopLock)
        {
            if (_cts is null)
            {
                return;
            }

            try
            {
                _cts.Cancel();
            }
            catch
            {
                // Ignore.
            }

            try
            {
                // All loop awaits use ConfigureAwait(false), so this won't deadlock on the UI thread.
                _loop?.Wait(TimeSpan.FromSeconds(3));
            }
            catch
            {
                // Ignore cancel/fault.
            }

            _cts.Dispose();
            _cts = null;
            _loop = null;
        }

        Emit("driver-watch", $"libusbK watchdog stopped (re-applies this session: {ReinstallCount}).");
    }

    private async Task RunLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            if (!IsPaused)
            {
                try
                {
                    await TickAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Emit("driver-watch", $"watchdog tick error: {ex.Message}", isError: true);
                }
            }

            try
            {
                await Task.Delay(_pollInterval, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task TickAsync(CancellationToken cancellationToken)
    {
        _monitor.PollNow();
        var devices = _monitor.ScanDevices()
            .Where(d => d.IsPresent && WatchedPids.Contains(d.ProductId))
            .ToList();

        if (devices.Count == 0)
        {
            return;
        }

        var toolchain = Paths.ResolveToolchainRoot(_settings.ToolchainRoot);
        if (toolchain is null)
        {
            return;
        }

        var wdi = Paths.GetWdiSimpleExecutable(toolchain);
        if (!File.Exists(wdi))
        {
            return;
        }

        foreach (var device in devices)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Prefer registry service — WMI Win32_PnPEntity.Service is often empty ("unknown").
            var service = DriverInstaller.ResolveServiceName(device.DeviceId)
                          ?? DriverInstaller.DetectService(device);

            if (!_lastLoggedService.TryGetValue(device.ProductId, out var prev) ||
                !string.Equals(prev, service, StringComparison.OrdinalIgnoreCase))
            {
                _lastLoggedService[device.ProductId] = service;
                Emit(
                    "driver-watch",
                    $"PID 0x{device.ProductId:X4} driver={service ?? "(none)"} mode={device.Mode}");
            }

            if (DriverInstaller.IsLibusbKService(service))
            {
                continue;
            }

            // CRITICAL: if device is on VBoxUSB (usbipd stub), it is attached to WSL.
            // Do NOT try to replace the driver — that breaks the WSL attach and the DFU helper.
            if (DriverInstaller.IsUsbipdStubService(service))
            {
                continue;
            }

            if (!_settings.AutoInstallDrivers)
            {
                if (!_loggedAutoOff)
                {
                    _loggedAutoOff = true;
                    Emit(
                        "driver-watch",
                        $"PID 0x{device.ProductId:X4} needs libusbK but AutoInstallDrivers is off (now={service ?? "none"}).",
                        isError: true);
                }

                continue;
            }

            if (!Elevation.IsAdmin())
            {
                if (!_loggedNotAdmin)
                {
                    _loggedNotAdmin = true;
                    Emit(
                        "driver-watch",
                        "Cannot auto-install libusbK — app is not elevated. Relaunch as Administrator.",
                        isError: true);
                }

                continue;
            }

            if (_lastAttempt.TryGetValue(device.ProductId, out var last) &&
                DateTimeOffset.UtcNow - last < _reinstallCooldown)
            {
                continue;
            }

            // Retry wdi-simple up to 3 times within this tick — Windows often assigns
            // WinUSB mid-install, so the first wdi-simple may not stick.
            const int maxRetries = 3;
            for (var retry = 0; retry < maxRetries; retry++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                _lastAttempt[device.ProductId] = DateTimeOffset.UtcNow;
                ReinstallCount++;
                Emit(
                    "driver-watch",
                    $"Re-applying libusbK via wdi-simple for PID 0x{device.ProductId:X4} " +
                    $"(was '{service ?? "none"}', attempt #{ReinstallCount}, retry {retry + 1}/{maxRetries})...");

                var ok = await DriverInstaller.RunWdiSimpleOnceAsync(
                    wdi,
                    device.ProductId,
                    cancellationToken).ConfigureAwait(false);

                // Give the driver a moment to settle after install.
                await Task.Delay(1000, cancellationToken).ConfigureAwait(false);

                var after = DriverInstaller.ResolveServiceName(device.DeviceId);
                Emit(
                    "driver-watch",
                    ok
                        ? $"wdi-simple finished for 0x{device.ProductId:X4}; service now={after ?? "(re-enumerating)"}"
                        : $"wdi-simple failed for 0x{device.ProductId:X4}; will retry.",
                    isError: !ok && !DriverInstaller.IsLibusbKService(after));

                if (DriverInstaller.IsLibusbKService(after))
                {
                    break;
                }

                // Not yet libusbK — retry immediately if we have budget.
                if (retry < maxRetries - 1)
                {
                    Emit("driver-watch", $"Still not libusbK (={after ?? "none"}) — retrying immediately...");
                    // Brief re-enumeration wait before next wdi-simple.
                    await Task.Delay(500, cancellationToken).ConfigureAwait(false);
                }
            }
        }
    }

    private void Emit(string source, string message, bool isError = false)
    {
        LogReceived?.Invoke(this, new LogLine
        {
            Source = source,
            Message = message,
            IsError = isError,
        });
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Stop();
    }
}
