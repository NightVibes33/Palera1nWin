using System.Diagnostics;
using System.Text.Json;

namespace DarkSwordRestore.Core;

public sealed class AppleDeviceMonitor : IAsyncDisposable
{
    private readonly TimeSpan _pollInterval;
    private CancellationTokenSource? _pollCts;
    private Task? _pollTask;
    private AppleDeviceSnapshot _current = AppleDeviceSnapshot.Disconnected;

    public AppleDeviceMonitor(TimeSpan? pollInterval = null) =>
        _pollInterval = pollInterval ?? TimeSpan.FromMilliseconds(750);

    public AppleDeviceSnapshot Current => _current;
    public event EventHandler<AppleDeviceSnapshot>? DeviceChanged;

    public void Start()
    {
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
        if (modes.Contains(Current.Mode)) return Current;

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
            throw new TimeoutException($"Timed out waiting for Apple device mode: {string.Join(", ", modes)}.");
        }
        finally
        {
            DeviceChanged -= Handler;
        }
    }

    public async Task<AppleDeviceSnapshot> ProbeAsync(CancellationToken cancellationToken = default)
    {
        var powershell = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell", "v1.0", "powershell.exe");

        const string script = "$ErrorActionPreference='SilentlyContinue';" +
            "$d=Get-PnpDevice -PresentOnly | Where-Object {$_.InstanceId -like 'USB\\VID_05AC&PID_*'} | ForEach-Object {" +
            "$h=(Get-PnpDeviceProperty -InstanceId $_.InstanceId -KeyName 'DEVPKEY_Device_HardwareIds').Data;" +
            "[pscustomobject]@{FriendlyName=$_.FriendlyName;InstanceId=$_.InstanceId;Service=$_.Service;HardwareIds=($h -join ';')}};" +
            "$d | ConvertTo-Json -Compress";

        var startInfo = new ProcessStartInfo
        {
            FileName = powershell,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add("-NoLogo");
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-ExecutionPolicy");
        startInfo.ArgumentList.Add("Bypass");
        startInfo.ArgumentList.Add("-Command");
        startInfo.ArgumentList.Add(script);

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Unable to start Windows PowerShell.");
        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        var output = (await outputTask.ConfigureAwait(false)).Trim();
        _ = await errorTask.ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(output) || output == "null") return AppleDeviceSnapshot.Disconnected;

        using var document = JsonDocument.Parse(output);
        var devices = document.RootElement.ValueKind == JsonValueKind.Array
            ? document.RootElement.EnumerateArray().ToArray()
            : new[] { document.RootElement };

        var selected = devices
            .Select(Parse)
            .OrderByDescending(snapshot => Priority(snapshot.Mode))
            .FirstOrDefault();
        return selected ?? AppleDeviceSnapshot.Disconnected;
    }

    private async Task PollLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var snapshot = await ProbeAsync(cancellationToken).ConfigureAwait(false);
                if (!Equivalent(_current, snapshot))
                {
                    _current = snapshot;
                    DeviceChanged?.Invoke(this, snapshot);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch
            {
                // A transient PnP query failure must not terminate monitoring.
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

    private static AppleDeviceSnapshot Parse(JsonElement item)
    {
        var instanceId = Get(item, "InstanceId");
        var hardwareIds = Get(item, "HardwareIds");
        var combined = $"{instanceId};{hardwareIds}".ToUpperInvariant();
        var mode = combined switch
        {
            var text when text.Contains("PID_1227") => AppleDeviceMode.Dfu,
            var text when text.Contains("PID_4141") => AppleDeviceMode.Pongo,
            var text when text.Contains("PID_1280") || text.Contains("PID_1281") || text.Contains("PID_1282") => AppleDeviceMode.Recovery,
            var text when text.Contains("PID_12A8") || text.Contains("PID_12AA") || text.Contains("PID_12AB") => AppleDeviceMode.Normal,
            _ => AppleDeviceMode.Unknown
        };

        return new AppleDeviceSnapshot(
            mode,
            null,
            Get(item, "FriendlyName") ?? "Apple Mobile Device",
            hardwareIds,
            Get(item, "Service"),
            instanceId,
            null,
            DateTimeOffset.UtcNow);
    }

    private static string? Get(JsonElement item, string name) =>
        item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static int Priority(AppleDeviceMode mode) => mode switch
    {
        AppleDeviceMode.Pongo => 5,
        AppleDeviceMode.Dfu => 4,
        AppleDeviceMode.Recovery => 3,
        AppleDeviceMode.Restore => 2,
        AppleDeviceMode.Normal => 1,
        _ => 0
    };

    private static bool Equivalent(AppleDeviceSnapshot left, AppleDeviceSnapshot right) =>
        left.Mode == right.Mode && left.InstanceId == right.InstanceId && left.Service == right.Service;

    public async ValueTask DisposeAsync()
    {
        if (_pollCts is null) return;
        _pollCts.Cancel();
        if (_pollTask is not null)
        {
            try { await _pollTask.ConfigureAwait(false); } catch { }
        }
        _pollCts.Dispose();
        _pollCts = null;
        _pollTask = null;
    }
}
