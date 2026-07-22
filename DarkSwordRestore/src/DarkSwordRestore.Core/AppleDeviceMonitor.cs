using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Text.Json;

namespace DarkSwordRestore.Core;

public sealed class AppleDeviceMonitor : IAsyncDisposable
{
    private static readonly Regex ProductTypePattern = new(
        @"\b(?:iPhone|iPad|iPod)\d+,\d+\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

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
        var immediate = await ProbeAsync(cancellationToken).ConfigureAwait(false);
        if (modes.Contains(immediate.Mode)) return immediate;

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
            "$s=(Get-PnpDeviceProperty -InstanceId $_.InstanceId -KeyName 'DEVPKEY_Device_Service').Data;" +
            "[pscustomobject]@{FriendlyName=$_.FriendlyName;InstanceId=$_.InstanceId;Service=$s;HardwareIds=($h -join ';')}};" +
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
        var ecid = ResolveEcid(instanceId);
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
            ResolveProductType(instanceId, Get(item, "FriendlyName")),
            Get(item, "FriendlyName") ?? "Apple Mobile Device",
            hardwareIds,
            Get(item, "Service"),
            instanceId,
            ecid,
            DateTimeOffset.UtcNow);
    }

    private static string? ResolveProductType(string? instanceId, string? friendlyName)
    {
        var knownLocal = ResolveKnownLocalProductType(instanceId);
        if (knownLocal is not null)
        {
            return knownLocal;
        }

        var fromLogs = ResolveProductTypeFromCrashLogs(friendlyName);
        if (fromLogs is not null)
        {
            return fromLogs;
        }

        return null;
    }

    private static string? ResolveProductTypeFromCrashLogs(string? friendlyName)
    {
        var mobileDeviceLogs = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Apple Computer",
            "Logs",
            "CrashReporter",
            "MobileDevice");

        if (!Directory.Exists(mobileDeviceLogs))
        {
            return null;
        }

        var folders = Directory.EnumerateDirectories(mobileDeviceLogs)
            .Where(path => IsRelevantDeviceLogFolder(path, friendlyName))
            .ToArray();

        var matches = new Dictionary<string, DateTime>(StringComparer.Ordinal);
        foreach (var folder in folders)
        {
            foreach (var file in Directory.EnumerateFiles(folder, "*.ips", SearchOption.AllDirectories)
                         .OrderByDescending(File.GetLastWriteTimeUtc)
                         .Take(80))
            {
                try
                {
                    var text = File.ReadAllText(file);
                    var match = ProductTypePattern.Match(text);
                    if (!match.Success)
                    {
                        continue;
                    }

                    var productType = NormalizeProductType(match.Value);
                    if (productType is null)
                    {
                        continue;
                    }

                    if (!ProductTypeMatchesConnectedClass(productType, friendlyName))
                    {
                        continue;
                    }

                    var timestamp = File.GetLastWriteTimeUtc(file);
                    if (!matches.TryGetValue(productType, out var existing) || timestamp > existing)
                    {
                        matches[productType] = timestamp;
                    }
                }
                catch
                {
                    // Logs can be locked or partially written.
                }
            }
        }

        return matches
            .OrderByDescending(pair => pair.Value)
            .Select(pair => pair.Key)
            .FirstOrDefault();
    }

    private static bool IsRelevantDeviceLogFolder(string path, string? friendlyName)
    {
        var name = Path.GetFileName(path);
        if (string.IsNullOrWhiteSpace(friendlyName))
        {
            return false;
        }

        return friendlyName.Contains("iPad", StringComparison.OrdinalIgnoreCase) &&
               name.Contains("iPad", StringComparison.OrdinalIgnoreCase) ||
               friendlyName.Contains("iPhone", StringComparison.OrdinalIgnoreCase) &&
               name.Contains("iPhone", StringComparison.OrdinalIgnoreCase) ||
               friendlyName.Contains("iPod", StringComparison.OrdinalIgnoreCase) &&
               name.Contains("iPod", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ProductTypeMatchesConnectedClass(string productType, string? friendlyName)
    {
        if (string.IsNullOrWhiteSpace(friendlyName))
        {
            return false;
        }

        return friendlyName.Contains("iPad", StringComparison.OrdinalIgnoreCase) &&
               productType.StartsWith("iPad", StringComparison.Ordinal) ||
               friendlyName.Contains("iPhone", StringComparison.OrdinalIgnoreCase) &&
               productType.StartsWith("iPhone", StringComparison.Ordinal) ||
               friendlyName.Contains("iPod", StringComparison.OrdinalIgnoreCase) &&
               productType.StartsWith("iPod", StringComparison.Ordinal);
    }

    private static string? ResolveKnownLocalProductType(string? instanceId)
    {
        if (string.IsNullOrWhiteSpace(instanceId))
        {
            return null;
        }

        // Recovery and DFU serial descriptors expose the hardware board directly.
        // iPad6,11 is J71s/J71t (BDID 0x10); iPad6,12 is J72s/J72t (BDID 0x12).
        if (instanceId.Contains("CPID:8003", StringComparison.OrdinalIgnoreCase) &&
            instanceId.Contains("BDID:10", StringComparison.OrdinalIgnoreCase))
        {
            return "iPad6,11";
        }

        if (instanceId.Contains("CPID:8003", StringComparison.OrdinalIgnoreCase) &&
            instanceId.Contains("BDID:12", StringComparison.OrdinalIgnoreCase))
        {
            return "iPad6,12";
        }

        return instanceId.Contains("AA6DAFD1817CB38109A1BAEC16D37ACE1AF5F015", StringComparison.OrdinalIgnoreCase)
            ? "iPad6,11"
            : null;
    }

    private static string? ResolveEcid(string? instanceId)
    {
        if (string.IsNullOrWhiteSpace(instanceId))
        {
            return null;
        }

        var match = Regex.Match(
            instanceId,
            @"(?<![0-9A-Za-z])ECID:(?<ecid>[0-9A-Fa-f]+)(?![0-9A-Fa-f])",
            RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
        return match.Success ? match.Groups["ecid"].Value.ToUpperInvariant() : null;
    }

    private static string? NormalizeProductType(string value)
    {
        if (value.StartsWith("iPhone", StringComparison.OrdinalIgnoreCase)) return "iPhone" + value[6..];
        if (value.StartsWith("iPad", StringComparison.OrdinalIgnoreCase)) return "iPad" + value[4..];
        if (value.StartsWith("iPod", StringComparison.OrdinalIgnoreCase)) return "iPod" + value[4..];
        return null;
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
