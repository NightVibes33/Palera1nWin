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
        _monitor = monitor ?? new AppleUsbMonitor();
        _ownsMonitor = monitor is null;
        _pongoTimeout = pongoTimeout ?? TimeSpan.FromMinutes(3);
        _postUploadGrace = postUploadGrace ?? TimeSpan.FromSeconds(45);
        _stuckTimeout = stuckTimeout ?? TimeSpan.FromSeconds(60);
    }

    public event EventHandler<LogLine>? LogReceived;

    public async Task<bool> RunUntilPongoAsync(string toolchainRoot, CancellationToken cancellationToken = default)
    {
        var executable = Paths.GetOpenRa1nExecutable(toolchainRoot);
        if (!File.Exists(executable))
        {
            Emit("openra1n", $"Missing executable: {executable}", true);
            return false;
        }

        var preDevices = SafeScan().Where(device => device.IsPresent).ToArray();
        if (preDevices.Length != 1)
        {
            Emit("openra1n", $"Exactly one Apple USB device is required; detected {preDevices.Length}.", true);
            return false;
        }
        if (preDevices[0].Mode == DeviceMode.PwnedDfu)
        {
            Emit("openra1n", "Stale generic PWND state is not accepted. Force-reboot and enter clean DFU.", true);
            return false;
        }

        Emit("openra1n", $"Starting {executable}; hard timeout {(int)_pongoTimeout.TotalSeconds}s.");
        using var processCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        processCts.CancelAfter(_pongoTimeout);
        var sync = new object();
        var uploadFinished = false;
        var lastOutput = DateTimeOffset.UtcNow;

        void Observe(string line, bool error)
        {
            lock (sync)
            {
                lastOutput = DateTimeOffset.UtcNow;
                if (LooksLikePongoUploadFinished(line)) uploadFinished = true;
            }
            Emit("openra1n", line, error);
        }

        var runTask = ProcessRunner.RunAsync(
            executable,
            [],
            workingDirectory: Path.GetDirectoryName(executable),
            cancellationToken: processCts.Token,
            onStdoutLine: line => Observe(line, false),
            onStderrLine: line => Observe(line, true),
            timeout: _pongoTimeout + TimeSpan.FromSeconds(5));

        var pongoDetected = false;
        var stuck = false;
        while (!runTask.IsCompleted && !processCts.IsCancellationRequested)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (IsPongoPresent())
            {
                pongoDetected = true;
                Emit("openra1n", "PongoOS USB 05AC:4141 detected; stopping and observing the openra1n process before continuing.");
                processCts.Cancel();
                break;
            }

            DateTimeOffset last;
            lock (sync) last = lastOutput;
            if (DateTimeOffset.UtcNow - last > _stuckTimeout)
            {
                stuck = true;
                Emit("openra1n", $"No output for {(int)_stuckTimeout.TotalSeconds}s; stopping the hung process.", true);
                processCts.Cancel();
                break;
            }
            await Task.Delay(400, cancellationToken).ConfigureAwait(false);
        }

        ProcessResult? result = null;
        try
        {
            result = await runTask.ConfigureAwait(false);
            Emit("openra1n", $"openra1n exited with code {result.ExitCode}.", !result.Succeeded);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            Emit("openra1n", pongoDetected
                ? "openra1n process stopped after Pongo enumeration."
                : stuck ? "openra1n process killed after the no-output timeout." : "openra1n timed out.", !pongoDetected);
        }

        if (pongoDetected || IsPongoPresent()) return true;

        bool uploaded;
        lock (sync) uploaded = uploadFinished;
        if ((uploaded || result?.Succeeded == true) && !cancellationToken.IsCancellationRequested)
        {
            Emit("openra1n", $"Upload finished; waiting up to {(int)_postUploadGrace.TotalSeconds}s for Pongo re-enumeration.");
            var deadline = DateTimeOffset.UtcNow + _postUploadGrace;
            while (DateTimeOffset.UtcNow < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (IsPongoPresent()) return true;
                await Task.Delay(500, cancellationToken).ConfigureAwait(false);
            }
        }

        Emit("openra1n", "PongoOS USB device was not detected after openra1n.", true);
        return false;
    }

    private IReadOnlyList<AppleUsbDevice> SafeScan()
    {
        try
        {
            _monitor.PollNow();
            return _monitor.ScanDevices();
        }
        catch { return []; }
    }

    private bool IsPongoPresent() =>
        SafeScan().Any(device => device.ProductId == 0x4141 && device.IsPresent) ||
        _monitor.IsPongoVisibleInUsbipd();

    private static bool LooksLikePongoUploadFinished(string text) =>
        !string.IsNullOrWhiteSpace(text) &&
        (text.Contains("pongoOS sent", StringComparison.OrdinalIgnoreCase) ||
         text.Contains("Pongo upload finished", StringComparison.OrdinalIgnoreCase) ||
         text.Contains("look for 05ac:4141", StringComparison.OrdinalIgnoreCase));

    private void Emit(string source, string message, bool isError = false) =>
        LogReceived?.Invoke(this, new LogLine { Source = source, Message = message, IsError = isError });

    public void Dispose()
    {
        if (_ownsMonitor) _monitor.Dispose();
    }
}
