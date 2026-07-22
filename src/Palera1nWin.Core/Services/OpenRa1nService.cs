using Palera1nWin.Core.Models;
using Palera1nWin.Core.Usb;
using Palera1nWin.Core.Util;

namespace Palera1nWin.Core.Services;

public sealed class OpenRa1nService : IDisposable
{
    private readonly AppleUsbMonitor _monitor;
    private readonly bool _ownsMonitor;
    private readonly TimeSpan _pongoTimeout;
    private readonly TimeSpan _postUploadGrace;
    private readonly TimeSpan _stuckTimeout;

    public OpenRa1nService(
        AppleUsbMonitor? monitor = null,
        TimeSpan? pongoTimeout = null,
        TimeSpan? postUploadGrace = null,
        TimeSpan? stuckTimeout = null)
    {
        if (monitor is null)
        {
            _monitor = new AppleUsbMonitor();
            _ownsMonitor = true;
        }
        else
        {
            _monitor = monitor;
            _ownsMonitor = false;
        }

        _pongoTimeout = pongoTimeout ?? TimeSpan.FromMinutes(10);
        _postUploadGrace = postUploadGrace ?? TimeSpan.FromSeconds(90);
        // If openra1n produces no stdout for this long, it's hung (usually stale YOLO).
        _stuckTimeout = stuckTimeout ?? TimeSpan.FromSeconds(60);
    }

    public event EventHandler<LogLine>? LogReceived;

    public async Task<bool> RunUntilPongoAsync(
        string toolchainRoot,
        CancellationToken cancellationToken = default)
    {
        var executable = Paths.GetOpenRa1nExecutable(toolchainRoot);
        if (!File.Exists(executable))
        {
            Emit("openra1n", $"Missing executable: {executable}", isError: true);
            return false;
        }

        // Check for stale YOLO before starting — openra1n hangs on invalid YOLO state.
        _monitor.PollNow();
        var preDevice = _monitor.ScanDevices()
            .FirstOrDefault(d => d.ProductId == 0x1227 && d.IsPresent);
        if (preDevice is not null && preDevice.Mode == DeviceMode.YoloDfu)
        {
            Emit(
                "openra1n",
                "WARNING: Device has YOLO from a previous session. openra1n may hang on stale YOLO. " +
                "If it hangs for 60s, force-reboot the device (Side + Volume Down 10s) and re-enter clean DFU.",
                isError: true);
        }

        Emit("openra1n", $"Starting {executable} (wait up to {(int)_pongoTimeout.TotalSeconds}s for Pongo)");

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linkedCts.CancelAfter(_pongoTimeout);

        var uploadFinished = false;
        var lastOutputTime = DateTimeOffset.UtcNow;
        var lastStageLog = DateTimeOffset.MinValue;

        var runTask = ProcessRunner.RunAsync(
            executable,
            Array.Empty<string>(),
            workingDirectory: Path.GetDirectoryName(executable),
            cancellationToken: linkedCts.Token,
            onStdoutLine: line =>
            {
                lastOutputTime = DateTimeOffset.UtcNow;
                Emit("openra1n", line);
                if (LooksLikePongoUploadFinished(line))
                {
                    uploadFinished = true;
                }
            },
            onStderrLine: line =>
            {
                lastOutputTime = DateTimeOffset.UtcNow;
                Emit("openra1n", line, isError: true);
                if (LooksLikePongoUploadFinished(line))
                {
                    uploadFinished = true;
                }
            });

        while (!linkedCts.Token.IsCancellationRequested)
        {
            if (await TryDetectPongoAsync(linkedCts.Token).ConfigureAwait(false))
            {
                return true;
            }

            var device = _monitor.ScanDevices()
                .FirstOrDefault(d => d.ProductId is 0x1227 or 0x4141);

            if (device is not null &&
                (device.Mode == DeviceMode.PwnedDfu || device.Mode == DeviceMode.YoloDfu) &&
                (DateTimeOffset.UtcNow - lastStageLog).TotalSeconds >= 5)
            {
                lastStageLog = DateTimeOffset.UtcNow;
                Emit("openra1n", $"checkm8 stage: {device.Mode} service={device.Service}");
            }

            // Stuck detection: no stdout from openra1n for _stuckTimeout seconds.
            if (!runTask.IsCompleted &&
                DateTimeOffset.UtcNow - lastOutputTime > _stuckTimeout)
            {
                Emit(
                    "openra1n",
                    $"openra1n has produced no output for {(int)_stuckTimeout.TotalSeconds}s — likely hung on stale YOLO. " +
                    "Killing openra1n. Force-reboot the device (Side + Volume Down 10s), re-enter clean DFU, then retry.",
                    isError: true);

                linkedCts.Cancel();
                break;
            }

            if (runTask.IsCompleted)
            {
                break;
            }

            try
            {
                await Task.Delay(500, linkedCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        ProcessResult? result = null;
        try
        {
            result = await runTask.ConfigureAwait(false);
            if (result.ExitCode == unchecked((int)0xC0000005) || result.ExitCode == -1073741819)
            {
                Emit(
                    "openra1n",
                    "openra1n crashed (ACCESS_VIOLATION). Usually DFU was on VBoxUSB/usbipd stub instead of libusbK.",
                    isError: true);
            }
            else
            {
                Emit("openra1n", $"openra1n exited with code {result.ExitCode}");
            }

            if (LooksLikePongoUploadFinished(result.CombinedOutput))
            {
                uploadFinished = true;
            }
        }
        catch (OperationCanceledException)
        {
            if (DateTimeOffset.UtcNow - lastOutputTime > _stuckTimeout)
            {
                Emit("openra1n", "openra1n killed (stuck/hung — likely stale YOLO).", isError: true);
            }
            else
            {
                Emit("openra1n", "openra1n wait cancelled/timed out.", isError: true);
            }
        }

        if (await TryDetectPongoAsync(cancellationToken).ConfigureAwait(false))
        {
            return true;
        }

        var shouldGraceWait = uploadFinished || result?.ExitCode == 0;
        if (shouldGraceWait && !cancellationToken.IsCancellationRequested)
        {
            Emit(
                "openra1n",
                $"Pongo upload done — waiting up to {(int)_postUploadGrace.TotalSeconds}s for 05AC:4141 " +
                "(PnP may show Error until libusbK binds)...");

            var graceDeadline = DateTimeOffset.UtcNow + _postUploadGrace;
            while (DateTimeOffset.UtcNow < graceDeadline && !cancellationToken.IsCancellationRequested)
            {
                if (await TryDetectPongoAsync(cancellationToken).ConfigureAwait(false))
                {
                    return true;
                }

                try
                {
                    await Task.Delay(500, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }

        Emit("openra1n", "PongoOS USB device (05AC:4141) not seen after openra1n.", isError: true);
        return false;
    }

    private async Task<bool> TryDetectPongoAsync(CancellationToken cancellationToken)
    {
        _monitor.PollNow();
        if (IsPongoPresent())
        {
            Emit("openra1n", "PongoOS USB device detected (05AC:4141).");
            return true;
        }

        await Task.CompletedTask.ConfigureAwait(false);
        return false;
    }

    private bool IsPongoPresent()
    {
        if (_monitor.ScanDevices().Any(d => d.ProductId == 0x4141 && !string.IsNullOrWhiteSpace(d.DeviceId)))
        {
            return true;
        }

        return _monitor.IsPongoVisibleInUsbipd();
    }

    private static bool LooksLikePongoUploadFinished(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        return text.Contains("pongoOS sent", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("Pongo upload finished", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("look for 05ac:4141", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("look for 05AC:4141", StringComparison.OrdinalIgnoreCase);
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
        if (_ownsMonitor)
        {
            _monitor.Dispose();
        }
    }
}
