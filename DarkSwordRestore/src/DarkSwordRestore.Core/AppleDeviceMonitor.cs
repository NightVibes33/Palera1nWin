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
        @"(?im)^\s*ECID\s*:\s*(?<value>(?:0x)?[0-9a-f]+)\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly TimeSpan _pollInterval;
    private readonly string _irecoveryPath;
    private CancellationTokenSource? _pollCts;
    private Task? _pollTask;
    private AppleDeviceSnapshot _current = AppleDeviceSnapshot.Disconnected;

    public AppleDeviceMonitor(TimeSpan? pollInterval = null, string? irecoveryPath = null)
    {
        _pollInterval = pollInterval ?? TimeSpan.FromMilliseconds(750);
        _irecoveryPath = irecoveryPath ?? Path.Combine(AppContext.BaseDirectory, "toolchain", "irecovery.exe");
    }

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
            throw new TimeoutException($"Timed out waiting for Apple device mode: {string.Join(", ", modes)}. Last mode: {Current.Mode}, driver: {Current.Service ?? "unknown"}.");
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
        if (selected is null) return AppleDeviceSnapshot.Disconnected;

        if (selected.Mode is AppleDeviceMode.Dfu or AppleDeviceMode.Recovery)
        {
            selected = await EnrichFromIRecoveryAsync(selected, cancellationToken).ConfigureAwait(false);
        }
        return selected;
    }

    private async Task<AppleDeviceSnapshot> EnrichFromIRecoveryAsync(
        AppleDeviceSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(_irecoveryPath)) return snapshot;

        var startInfo = new ProcessStartInfo
        {
            FileName = _irecoveryPath,
            WorkingDirectory = Path.GetDirectoryName(_irecoveryPath) ?? AppContext.BaseDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add("-q");

        using var process = Process.Start(startInfo);
        if (process is null) return snapshot;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(4));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        try
        {
            var stdout = process.StandardOutput.ReadToEndAsync(linked.Token);
            var stderr = process.StandardError.ReadToEndAsync(linked.Token);
            await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
            var output = (await stdout.ConfigureAwait(false)) + Environment.NewLine + (await stderr.ConfigureAwait(false));

            var mode = output.Contains("YOLO", StringComparison.OrdinalIgnoreCase) ||
                       output.Contains("PWND", StringComparison.OrdinalIgnoreCase)
                ? AppleDeviceMode.PwnedDfu
                : snapshot.Mode;
            var productMatch = ProductTypePattern.Match(output);
            var productType = productMatch.Success ? NormalizeProductType(productMatch.Value) : snapshot.ProductType;
            var ecidMatch = EcidPattern.Match(output);
            var ecid = ecidMatch.Success
                ? ecidMatch.Groups["value"].Value.Replace("0x", string.Empty, StringComparison.OrdinalIgnoreCase).ToUpperInvariant()
                : snapshot.Ecid;

            return snapshot with { Mode = mode, ProductType = productType, Ecid = ecid };
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
            }
            catch
            {
                // Probe cleanup is best-effort.
            }
            return snapshot;
        }
        catch
        {
            return snapshot;
        }
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
                // A transient PnP/irecovery query failure must not terminate monitoring.
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
            var text when text.Contains("YOLO:") || text.Contains("PWND") => AppleDeviceMode.PwnedDfu,
            var text when text.Contains("PID_1227") => AppleDeviceMode.Dfu,
            var text when text.Contains("PID_4141") => AppleDeviceMode.Pongo,
            var text when text.Contains("PID_1280") || text.Contains("PID_1281") || text.Contains("PID_1282") || text.Contains("PID_1283") => AppleDeviceMode.Recovery,
            var text when text.Contains("PID_12A8") || text.Contains("PID_12AA") || text.Contains("PID_12AB") || text.Contains("PID_12A0") => AppleDeviceMode.Normal,
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

    private static string? NormalizeProductType(string value)
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
        _ => 0
    };

    private static bool Equivalent(AppleDeviceSnapshot left, AppleDeviceSnapshot right) =>
        left.Mode == right.Mode &&
        left.InstanceId == right.InstanceId &&
        left.Service == right.Service &&
        left.ProductType == right.ProductType &&
        left.Ecid == right.Ecid;

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
