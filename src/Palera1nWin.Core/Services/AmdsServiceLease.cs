using Palera1nWin.Core.Util;

namespace Palera1nWin.Core.Services;

/// <summary>
/// Stops Apple Mobile Device Service only for the active USB transaction and restores
/// its original running state on every success, failure and cancellation path.
/// </summary>
public sealed class AmdsServiceLease : IAsyncDisposable
{
    private static readonly string[] CandidateNames =
    [
        "Apple Mobile Device Service",
        "AppleMobileDeviceService",
    ];

    private readonly string? _serviceName;
    private readonly bool _wasRunning;
    private readonly Action<string>? _log;
    private int _disposed;

    private AmdsServiceLease(string? serviceName, bool wasRunning, Action<string>? log)
    {
        _serviceName = serviceName;
        _wasRunning = wasRunning;
        _log = log;
    }

    public static async Task<AmdsServiceLease> AcquireAsync(
        Action<string>? log = null,
        CancellationToken cancellationToken = default)
    {
        foreach (var name in CandidateNames)
        {
            var query = await RunScAsync("query", name, cancellationToken).ConfigureAwait(false);
            if (query.ExitCode == 1060 || query.CombinedOutput.Contains("does not exist", StringComparison.OrdinalIgnoreCase))
                continue;
            if (!query.Succeeded && !query.CombinedOutput.Contains("STATE", StringComparison.OrdinalIgnoreCase))
                continue;

            var running = query.CombinedOutput.Contains("RUNNING", StringComparison.OrdinalIgnoreCase);
            if (!running)
            {
                log?.Invoke($"AMDS service '{name}' is already stopped; its state will not be changed on cleanup.");
                return new AmdsServiceLease(name, false, log);
            }

            log?.Invoke($"Stopping AMDS service '{name}' for the active jailbreak transaction...");
            var stop = await RunScAsync("stop", name, cancellationToken).ConfigureAwait(false);
            if (!stop.Succeeded && !stop.CombinedOutput.Contains("STOP_PENDING", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Could not stop {name}: {stop.CombinedOutput}");

            var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(30);
            while (DateTimeOffset.UtcNow < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var state = await RunScAsync("query", name, cancellationToken).ConfigureAwait(false);
                if (state.CombinedOutput.Contains("STOPPED", StringComparison.OrdinalIgnoreCase))
                {
                    log?.Invoke("AMDS stopped. It will be restarted automatically when the transaction ends.");
                    return new AmdsServiceLease(name, true, log);
                }
                await Task.Delay(500, cancellationToken).ConfigureAwait(false);
            }
            throw new TimeoutException($"Timed out stopping {name}.");
        }

        log?.Invoke("Apple Mobile Device Service is not installed; no AMDS state change is required.");
        return new AmdsServiceLease(null, false, log);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0 || !_wasRunning || string.IsNullOrWhiteSpace(_serviceName))
            return;

        try
        {
            _log?.Invoke($"Restarting AMDS service '{_serviceName}'...");
            var start = await RunScAsync("start", _serviceName, CancellationToken.None).ConfigureAwait(false);
            if (!start.Succeeded && !start.CombinedOutput.Contains("START_PENDING", StringComparison.OrdinalIgnoreCase) &&
                !start.CombinedOutput.Contains("already running", StringComparison.OrdinalIgnoreCase))
            {
                _log?.Invoke($"AMDS restart failed: {start.CombinedOutput}");
                return;
            }

            var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(30);
            while (DateTimeOffset.UtcNow < deadline)
            {
                var state = await RunScAsync("query", _serviceName, CancellationToken.None).ConfigureAwait(false);
                if (state.CombinedOutput.Contains("RUNNING", StringComparison.OrdinalIgnoreCase))
                {
                    _log?.Invoke("AMDS restored to its original running state.");
                    return;
                }
                await Task.Delay(500).ConfigureAwait(false);
            }
            _log?.Invoke("AMDS restart was requested, but RUNNING state was not confirmed within 30 seconds.");
        }
        catch (Exception exception)
        {
            _log?.Invoke($"AMDS cleanup error: {exception.Message}");
        }
    }

    private static Task<ProcessResult> RunScAsync(
        string operation,
        string serviceName,
        CancellationToken cancellationToken) =>
        ProcessRunner.RunAsync(
            "sc.exe",
            new[] { operation, serviceName },
            cancellationToken: cancellationToken,
            timeout: TimeSpan.FromSeconds(15));
}
