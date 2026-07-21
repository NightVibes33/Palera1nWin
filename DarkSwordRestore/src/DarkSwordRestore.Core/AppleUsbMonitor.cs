using System.Text.Json;

namespace DarkSwordRestore.Core;

public sealed class AppleUsbMonitor : IAsyncDisposable
{
    private readonly ProcessRunner _runner;
    private readonly SessionLogger _log;
    private readonly CancellationTokenSource _stop = new();
    private Task? _loop;
    private AppleDeviceSnapshot _current = AppleDeviceSnapshot.Disconnected;

    public AppleDeviceSnapshot Current => _current;
    public event EventHandler<AppleDeviceSnapshot>? DeviceChanged;

    public AppleUsbMonitor(ProcessRunner runner, SessionLogger log)
    {
        _runner = runner;
        _log = log;
    }

    public void Start() => _loop ??= Task.Run(() => MonitorLoopAsync(_stop.Token));

    public async Task<AppleDeviceSnapshot> ProbeAsync(CancellationToken cancellationToken = default)
    {
        const string script = "$ErrorActionPreference='SilentlyContinue'; " +
            "$items=Get-PnpDevice -PresentOnly | Where-Object { $_.InstanceId -like 'USB\\VID_05AC*' }; " +
            "$items | ForEach-Object { $hw=(Get-PnpDeviceProperty -InstanceId $_.InstanceId -KeyName 'DEVPKEY_Device_HardwareIds').Data; " +
            "[pscustomobject]@{FriendlyName=$_.FriendlyName;InstanceId=$_.InstanceId;Service=$_.Service;HardwareIds=@($hw)} } | ConvertTo-Json -Compress";

        try
        {
            var result = await _runner.RunAsync(
                "powershell.exe",
                new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command", script },
                timeout: TimeSpan.FromSeconds(12),
                cancellationToken: cancellationToken).ConfigureAwait(false);

            if (!result.Success || string.IsNullOrWhiteSpace(result.StandardOutput))
            {
                return AppleDeviceSnapshot.Disconnected with { ObservedAt = DateTimeOffset.UtcNow };
            }

            using var document = JsonDocument.Parse(result.StandardOutput.Trim());
            var devices = document.RootElement.ValueKind == JsonValueKind.Array
                ? document.RootElement.EnumerateArray().ToArray()
                : new[] { document.RootElement };

            return devices.Select(ParseDevice).OrderByDescending(x => Priority(x.Mode)).FirstOrDefault()
                ?? AppleDeviceSnapshot.Disconnected with { ObservedAt = DateTimeOffset.UtcNow };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.Warn($"Apple USB probe failed: {ex.Message}");
            return AppleDeviceSnapshot.Disconnected with { ObservedAt = DateTimeOffset.UtcNow };
        }
    }

    public async Task<bool> WaitForModeAsync(AppleDeviceMode mode, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if ((await ProbeAsync(cancellationToken).ConfigureAwait(false)).Mode == mode) return true;
            await Task.Delay(500, cancellationToken).ConfigureAwait(false);
        }
        return false;
    }

    private async Task MonitorLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var next = await ProbeAsync(cancellationToken).ConfigureAwait(false);
            if (next.Mode != _current.Mode || !string.Equals(next.InstanceId, _current.InstanceId, StringComparison.OrdinalIgnoreCase))
            {
                _current = next;
                _log.Info($"Apple USB mode: {next.Mode} ({next.DisplayName ?? "no device"})");
                DeviceChanged?.Invoke(this, next);
            }
            await Task.Delay(1000, cancellationToken).ConfigureAwait(false);
        }
    }

    private static AppleDeviceSnapshot ParseDevice(JsonElement device)
    {
        var name = ReadString(device, "FriendlyName");
        var instanceId = ReadString(device, "InstanceId");
        var service = ReadString(device, "Service");
        var hardwareIds = new List<string>();
        if (device.TryGetProperty("HardwareIds", out var ids))
        {
            if (ids.ValueKind == JsonValueKind.Array)
                hardwareIds.AddRange(ids.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!));
            else if (ids.ValueKind == JsonValueKind.String)
                hardwareIds.Add(ids.GetString()!);
        }
        if (!string.IsNullOrWhiteSpace(instanceId)) hardwareIds.Add(instanceId);
        var joined = string.Join(";", hardwareIds);
        return new AppleDeviceSnapshot(Classify(joined, name), null, name, joined, service, instanceId, null, DateTimeOffset.UtcNow);
    }

    private static AppleDeviceMode Classify(string ids, string? name)
    {
        if (ids.Contains("PID_1227", StringComparison.OrdinalIgnoreCase)) return AppleDeviceMode.Dfu;
        if (ids.Contains("PID_4141", StringComparison.OrdinalIgnoreCase)) return AppleDeviceMode.Pongo;
        if (ids.Contains("PID_1281", StringComparison.OrdinalIgnoreCase) || name?.Contains("Recovery", StringComparison.OrdinalIgnoreCase) == true) return AppleDeviceMode.Recovery;
        if (name?.Contains("Restore", StringComparison.OrdinalIgnoreCase) == true) return AppleDeviceMode.Restore;
        if (ids.Contains("VID_05AC", StringComparison.OrdinalIgnoreCase)) return AppleDeviceMode.Normal;
        return AppleDeviceMode.Unknown;
    }

    private static int Priority(AppleDeviceMode mode) => mode switch
    {
        AppleDeviceMode.Pongo => 6,
        AppleDeviceMode.Dfu => 5,
        AppleDeviceMode.Recovery => 4,
        AppleDeviceMode.Restore => 3,
        AppleDeviceMode.Normal => 2,
        _ => 1
    };

    private static string? ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        if (_loop is not null)
        {
            try { await _loop.ConfigureAwait(false); } catch (OperationCanceledException) { }
        }
        _stop.Dispose();
    }
}
