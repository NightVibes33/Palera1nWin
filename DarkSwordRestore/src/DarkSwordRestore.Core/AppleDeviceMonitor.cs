using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DarkSwordRestore.Core;

public sealed class AppleDeviceMonitor : IAsyncDisposable
{
    private static readonly Regex ProductTypePattern = new(
        @"\b(?:iPhone|iPad|iPod)\d+,\d+\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Regex EcidPattern = new(
        @"(?im)^\s*(?:ECID|UniqueChipID)\s*:\s*(?<value>(?:0x)?[0-9a-f]+)\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly TimeSpan _pollInterval;
    private readonly string _irecoveryPath;
    private readonly string _ideviceInfoPath;
    private readonly SemaphoreSlim _probeGate = new(1, 1);
    private readonly object _stateGate = new();
    private CancellationTokenSource? _pollCts;
    private Task? _pollTask;
    private AppleDeviceSnapshot _current = AppleDeviceSnapshot.Disconnected;
    private bool _disposed;

    public AppleDeviceMonitor(
        TimeSpan? pollInterval = null,
        string? irecoveryPath = null,
        string? ideviceInfoPath = null)
    {
        _pollInterval = pollInterval ?? TimeSpan.FromMilliseconds(1200);
        _irecoveryPath = irecoveryPath ?? Path.Combine(AppContext.BaseDirectory, "toolchain", "irecovery.exe");
        _ideviceInfoPath = ideviceInfoPath ?? Path.Combine(AppContext.BaseDirectory, "toolchain", "ideviceinfo.exe");
    }

    public AppleDeviceSnapshot Current
    {
        get { lock (_stateGate) return _current; }
    }

    public event EventHandler<AppleDeviceSnapshot>? DeviceChanged;

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_pollTask is not null) return;
        _pollCts = new CancellationTokenSource();
        _pollTask = Task.Run(() => PollLoopAsync(_pollCts.Token));
    }

    public async Task<AppleDeviceSnapshot> WaitForModeAsync(
        IReadOnlyCollection<AppleDeviceMode> modes,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        Start();
        var current = Current;
        if (modes.Contains(current.Mode)) return current;

        var completion = new TaskCompletionSource<AppleDeviceSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Handler(object? _, AppleDeviceSnapshot snapshot)
        {
            if (modes.Contains(snapshot.Mode)) completion.TrySetResult(snapshot);
        }

        DeviceChanged += Handler;
        try
        {
            using var timeoutCts = new CancellationTokenSource(timeout);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
            using var registration = linked.Token.Register(() => completion.TrySetCanceled(linked.Token));
            return await completion.Task.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            current = Current;
            throw new TimeoutException(
                $"Timed out waiting for Apple device mode: {string.Join(", ", modes)}. Last mode: {current.Mode}, driver: {current.Service ?? "unknown"}.");
        }
        finally
        {
            DeviceChanged -= Handler;
        }
    }

    /// <summary>
    /// Returns one exact target only. Multiple connected Apple devices are a hard error
    /// rather than silently selecting the highest-priority one.
    /// </summary>
    public async Task<AppleDeviceSnapshot> ProbeAsync(CancellationToken cancellationToken = default)
    {
        var devices = await ProbeAllAsync(cancellationToken).ConfigureAwait(false);
        return devices.Count switch
        {
            0 => AppleDeviceSnapshot.Disconnected,
            1 => devices[0],
            _ => throw new InvalidOperationException(
                $"Exactly one Apple device must be connected for DarkSword operations; detected {devices.Count}.")
        };
    }

    public async Task<IReadOnlyList<AppleDeviceSnapshot>> ProbeAllAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _probeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var powershell = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System),
                "WindowsPowerShell", "v1.0", "powershell.exe");
            const string script = "$ErrorActionPreference='Stop';" +
                "$d=Get-PnpDevice -PresentOnly | Where-Object {$_.InstanceId -like 'USB\\VID_05AC&PID_*'} | ForEach-Object {" +
                "$h=(Get-PnpDeviceProperty -InstanceId $_.InstanceId -KeyName 'DEVPKEY_Device_HardwareIds' -ErrorAction SilentlyContinue).Data;" +
                "$c=(Get-PnpDeviceProperty -InstanceId $_.InstanceId -KeyName 'DEVPKEY_Device_ContainerId' -ErrorAction SilentlyContinue).Data;" +
                "[pscustomobject]@{FriendlyName=$_.FriendlyName;InstanceId=$_.InstanceId;Service=$_.Service;HardwareIds=($h -join ';');ContainerId=([string]$c)}};" +
                "$d | Group-Object {if ($_.ContainerId) {$_.ContainerId} else {$_.InstanceId}} | ForEach-Object {" +
                "$_.Group | Sort-Object @{Expression={if ($_.InstanceId -match '&MI_[0-9A-Fa-f]{2}') {1} else {0}}},@{Expression={if ($_.Service) {0} else {1}}} | Select-Object -First 1" +
                "} | ConvertTo-Json -Compress";

            var output = await RunCaptureAsync(
                powershell,
                ["-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command", script],
                TimeSpan.FromSeconds(10),
                cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(output) || output.Trim() == "null") return [];

            using var document = JsonDocument.Parse(output);
            var items = document.RootElement.ValueKind == JsonValueKind.Array
                ? document.RootElement.EnumerateArray().ToArray()
                : [document.RootElement];
            var devices = items.Select(Parse)
                .Where(snapshot => snapshot.Mode != AppleDeviceMode.Unknown)
                .OrderByDescending(snapshot => Priority(snapshot.Mode))
                .ThenBy(snapshot => snapshot.InstanceId, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            // Identity tools cannot disambiguate two same-mode devices. Enrichment is
            // therefore allowed only after Windows reports one physical Apple target.
            if (devices.Length == 1)
            {
                var selected = devices[0];
                if (selected.Mode is AppleDeviceMode.Dfu or AppleDeviceMode.Recovery)
                    selected = await EnrichFromIRecoveryAsync(selected, cancellationToken).ConfigureAwait(false);
                else if (selected.Mode == AppleDeviceMode.Normal)
                    selected = await EnrichFromIDeviceInfoAsync(selected, cancellationToken).ConfigureAwait(false);
                devices[0] = selected;
            }
            return devices;
        }
        finally
        {
            _probeGate.Release();
        }
    }

    private async Task<AppleDeviceSnapshot> EnrichFromIRecoveryAsync(
        AppleDeviceSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(_irecoveryPath)) return snapshot;
        try
        {
            var output = await RunCaptureAsync(
                _irecoveryPath,
                ["-q"],
                TimeSpan.FromSeconds(5),
                cancellationToken,
                Path.GetDirectoryName(_irecoveryPath)).ConfigureAwait(false);
            var mode = output.Contains("YOLO", StringComparison.OrdinalIgnoreCase) ||
                       output.Contains("PWND", StringComparison.OrdinalIgnoreCase)
                ? AppleDeviceMode.PwnedDfu
                : snapshot.Mode;
            var productMatch = ProductTypePattern.Match(output);
            var ecidMatch = EcidPattern.Match(output);
            return snapshot with
            {
                Mode = mode,
                ProductType = productMatch.Success ? NormalizeProductType(productMatch.Value) : snapshot.ProductType,
                Ecid = ecidMatch.Success ? AppleDeviceSnapshot.NormalizeEcid(ecidMatch.Groups["value"].Value) : snapshot.Ecid,
            };
        }
        catch (OperationCanceledException) { throw; }
        catch { return snapshot; }
    }

    private async Task<AppleDeviceSnapshot> EnrichFromIDeviceInfoAsync(
        AppleDeviceSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(_ideviceInfoPath)) return snapshot;
        try
        {
            var product = (await RunCaptureAsync(
                _ideviceInfoPath, ["-k", "ProductType"], TimeSpan.FromSeconds(8), cancellationToken,
                Path.GetDirectoryName(_ideviceInfoPath)).ConfigureAwait(false)).Trim();
            var ecidOutput = await RunCaptureAsync(
                _ideviceInfoPath, ["-k", "UniqueChipID"], TimeSpan.FromSeconds(8), cancellationToken,
                Path.GetDirectoryName(_ideviceInfoPath)).ConfigureAwait(false);
            var productMatch = ProductTypePattern.Match(product);
            var ecid = Regex.Match(ecidOutput, @"(?i)(?:0x)?[0-9a-f]+", RegexOptions.CultureInvariant);
            return snapshot with
            {
                ProductType = productMatch.Success ? NormalizeProductType(productMatch.Value) : snapshot.ProductType,
                Ecid = ecid.Success ? AppleDeviceSnapshot.NormalizeEcid(ecid.Value) : snapshot.Ecid,
            };
        }
        catch (OperationCanceledException) { throw; }
        catch { return snapshot; }
    }

    private async Task PollLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            AppleDeviceSnapshot next;
            try
            {
                next = await ProbeAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch
            {
                // Discovery failure or multiple devices must clear any stale DFU/Pongo
                // state. The UI can then show a safe disconnected/ambiguous condition.
                next = AppleDeviceSnapshot.Disconnected;
            }
            Publish(next);

            try { await Task.Delay(_pollInterval, cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }

    private void Publish(AppleDeviceSnapshot snapshot)
    {
        AppleDeviceSnapshot previous;
        lock (_stateGate)
        {
            previous = _current;
            _current = snapshot;
        }
        if (Equivalent(previous, snapshot)) return;
        var handlers = DeviceChanged;
        if (handlers is null) return;
        foreach (EventHandler<AppleDeviceSnapshot> handler in handlers.GetInvocationList())
        {
            try { handler(this, snapshot); }
            catch { }
        }
    }

    private static async Task<string> RunCaptureAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        string? workingDirectory = null)
    {
        var start = new ProcessStartInfo
        {
            FileName = fileName,
            WorkingDirectory = workingDirectory ?? AppContext.BaseDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException($"Unable to start {fileName}.");
        var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
        try
        {
            await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
            throw new TimeoutException($"Identity probe timed out after {timeout}: {Path.GetFileName(fileName)}");
        }
        var output = (await stdout.ConfigureAwait(false)) + Environment.NewLine + (await stderr.ConfigureAwait(false));
        if (process.ExitCode != 0 && string.IsNullOrWhiteSpace(output))
            throw new InvalidOperationException($"{Path.GetFileName(fileName)} exited with code {process.ExitCode}.");
        return output.Trim();
    }

    private static AppleDeviceSnapshot Parse(JsonElement item)
    {
        var instanceId = Get(item, "InstanceId");
        var hardwareIds = Get(item, "HardwareIds");
        var combined = $"{instanceId};{hardwareIds}".ToUpperInvariant();
        var mode = combined switch
        {
            var text when text.Contains("YOLO:") || text.Contains("PWND") => AppleDeviceMode.PwnedDfu,
            var text when text.Contains("PID_1227") => AppleDeviceMode.Dfu,
            var text when text.Contains("PID_4141") => AppleDeviceMode.Pongo,
            var text when text.Contains("PID_1280") || text.Contains("PID_1281") || text.Contains("PID_1282") || text.Contains("PID_1283") => AppleDeviceMode.Recovery,
            var text when text.Contains("PID_12A8") || text.Contains("PID_12AA") || text.Contains("PID_12AB") || text.Contains("PID_12A0") => AppleDeviceMode.Normal,
            _ => AppleDeviceMode.Unknown,
        };
        return new AppleDeviceSnapshot(
            mode, null, Get(item, "FriendlyName") ?? "Apple Mobile Device", hardwareIds,
            Get(item, "Service"), instanceId, null, DateTimeOffset.UtcNow);
    }

    private static string NormalizeProductType(string value)
    {
        if (value.StartsWith("iPhone", StringComparison.OrdinalIgnoreCase)) return "iPhone" + value[6..];
        if (value.StartsWith("iPad", StringComparison.OrdinalIgnoreCase)) return "iPad" + value[4..];
        if (value.StartsWith("iPod", StringComparison.OrdinalIgnoreCase)) return "iPod" + value[4..];
        return value;
    }

    private static string? Get(JsonElement item, string name) =>
        item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static int Priority(AppleDeviceMode mode) => mode switch
    {
        AppleDeviceMode.Pongo => 6,
        AppleDeviceMode.PwnedDfu => 5,
        AppleDeviceMode.Dfu => 4,
        AppleDeviceMode.Recovery => 3,
        AppleDeviceMode.Restore => 2,
        AppleDeviceMode.Normal => 1,
        _ => 0,
    };

    private static bool Equivalent(AppleDeviceSnapshot left, AppleDeviceSnapshot right) =>
        left.Mode == right.Mode &&
        string.Equals(left.InstanceId, right.InstanceId, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(left.Service, right.Service, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(left.ProductType, right.ProductType, StringComparison.Ordinal) &&
        string.Equals(left.NormalizedEcid, right.NormalizedEcid, StringComparison.OrdinalIgnoreCase);

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        if (_pollCts is not null)
        {
            _pollCts.Cancel();
            if (_pollTask is not null)
            {
                try { await _pollTask.ConfigureAwait(false); } catch { }
            }
            _pollCts.Dispose();
        }
        _probeGate.Dispose();
    }
}
