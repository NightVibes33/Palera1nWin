using System.Management;
using System.Text.RegularExpressions;
using Palera1nWin.Core.Models;
using Palera1nWin.Core.Services;

namespace Palera1nWin.Core.Usb;

public sealed class AppleUsbMonitor : IDisposable
{
    private static readonly Regex BusIdRegex = new(
        @"^\s*(\d+-\d+(?:\.\d+)*)\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex VidPidRegex = new(
        @"05ac:([0-9a-f]{4})",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private readonly Timer _pollTimer;
    private readonly UsbipdService _usbipdService;
    private readonly object _sync = new();
    private readonly object _pollGate = new();
    private AppleUsbDevice _currentDevice = AppleUsbDevice.Empty;
    private bool _disposed;

    public AppleUsbMonitor(UsbipdService? usbipdService = null)
    {
        _usbipdService = usbipdService ?? new UsbipdService();
        _pollTimer = new Timer(_ => PollSafe(waitForTurn: false), null, TimeSpan.Zero, TimeSpan.FromSeconds(1.5));
    }

    public event EventHandler<AppleUsbDevice>? DeviceChanged;

    public AppleUsbDevice CurrentDevice
    {
        get
        {
            lock (_sync) return _currentDevice;
        }
    }

    /// <summary>
    /// Performs a serialized immediate poll. It can never overlap the timer callback.
    /// </summary>
    public void PollNow() => PollSafe(waitForTurn: true);

    public bool IsPongoVisibleInUsbipd()
    {
        try
        {
            return ParseUsbipdEntries(_usbipdService.ListDevices())
                .Any(entry => entry.ProductId == 0x4141);
        }
        catch
        {
            return false;
        }
    }

    public IReadOnlyList<AppleUsbDevice> ScanDevices()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        // Normal iOS exposes several MI_* composite interfaces for one physical
        // device. Collapse those interfaces before the exact-device count and before
        // associating the single usbipd bus ID.
        var devices = CollapsePhysicalInterfaces(ScanPnPDevices());
        MergeUsbipdBusIds(devices);
        return devices
            .OrderByDescending(ScoreDevice)
            .ThenBy(d => d.DeviceId, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    internal static List<AppleUsbDevice> CollapsePhysicalInterfaces(IEnumerable<AppleUsbDevice> source) =>
        source
            .GroupBy(device => (device.VendorId, device.ProductId, device.Mode))
            .Select(group => group
                .OrderByDescending(device => device.IsPresent)
                .ThenByDescending(device => !string.IsNullOrWhiteSpace(device.Service))
                .ThenBy(device => device.DeviceId, StringComparer.OrdinalIgnoreCase)
                .First())
            .ToList();

    private void PollSafe(bool waitForTurn)
    {
        if (_disposed) return;

        var entered = false;
        try
        {
            if (waitForTurn)
            {
                Monitor.Enter(_pollGate, ref entered);
            }
            else
            {
                entered = Monitor.TryEnter(_pollGate);
                if (!entered) return;
            }

            AppleUsbDevice best;
            try
            {
                best = ScanDevices().FirstOrDefault() ?? AppleUsbDevice.Empty;
            }
            catch
            {
                // Never keep a previous device active after discovery failed. A stale DFU/Pongo
                // state is more dangerous than temporarily reporting disconnected.
                best = AppleUsbDevice.Empty;
            }

            AppleUsbDevice previous;
            lock (_sync)
            {
                previous = _currentDevice;
                _currentDevice = best;
            }

            if (!DeviceEquals(previous, best)) RaiseDeviceChanged(best);
        }
        finally
        {
            if (entered) Monitor.Exit(_pollGate);
        }
    }

    private void RaiseDeviceChanged(AppleUsbDevice device)
    {
        var handlers = DeviceChanged;
        if (handlers is null) return;
        foreach (EventHandler<AppleUsbDevice> handler in handlers.GetInvocationList())
        {
            try { handler(this, device); }
            catch { /* A UI subscriber must not stop hardware monitoring. */ }
        }
    }

    private static IEnumerable<AppleUsbDevice> ScanPnPDevices()
    {
        var results = new List<AppleUsbDevice>();
        using var searcher = new ManagementObjectSearcher(
            "SELECT DeviceID, Name, Status, Service FROM Win32_PnPEntity WHERE DeviceID LIKE 'USB\\\\VID_05AC%'");
        using var collection = searcher.Get();
        foreach (ManagementObject obj in collection)
        {
            using (obj)
            {
                var deviceId = obj["DeviceID"]?.ToString() ?? string.Empty;
                var name = obj["Name"]?.ToString();
                var status = obj["Status"]?.ToString();
                var service = obj["Service"]?.ToString();
                if (string.IsNullOrWhiteSpace(service))
                    service = Drivers.DriverInstaller.ResolveServiceName(deviceId);

                results.Add(AppleUsbDevice.FromPnpEntity(deviceId, name, status, service));
            }
        }
        return results;
    }

    private void MergeUsbipdBusIds(List<AppleUsbDevice> devices)
    {
        if (!_usbipdService.IsAvailable || devices.Count == 0) return;

        var entries = ParseUsbipdEntries(_usbipdService.ListDevices());
        foreach (var pidGroup in devices.GroupBy(device => device.ProductId))
        {
            var matchingEntries = entries.Where(entry => entry.ProductId == pidGroup.Key).ToArray();
            var matchingDevices = pidGroup.ToArray();

            // usbipd's table does not expose the Windows PnP instance ID. A PID-only
            // association is safe only when both sides are unambiguous.
            if (matchingEntries.Length != 1 || matchingDevices.Length != 1) continue;

            var target = matchingDevices[0];
            var index = devices.FindIndex(device =>
                string.Equals(device.DeviceId, target.DeviceId, StringComparison.OrdinalIgnoreCase));
            if (index < 0) continue;

            devices[index] = AppleUsbDevice.FromPnpEntity(
                target.DeviceId,
                target.Name,
                target.Status,
                target.Service,
                matchingEntries[0].BusId);
        }
    }

    internal static IReadOnlyList<UsbipdEntry> ParseUsbipdEntries(string output)
    {
        var entries = new List<UsbipdEntry>();
        foreach (var line in output.Split('\n', '\r'))
        {
            if (!line.Contains("05ac:", StringComparison.OrdinalIgnoreCase)) continue;
            var busMatch = BusIdRegex.Match(line);
            var pidMatch = VidPidRegex.Match(line);
            if (!busMatch.Success || !pidMatch.Success) continue;
            if (!ushort.TryParse(pidMatch.Groups[1].Value,
                    System.Globalization.NumberStyles.HexNumber,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out var pid)) continue;
            entries.Add(new UsbipdEntry(busMatch.Groups[1].Value, pid, line.Trim()));
        }
        return entries;
    }

    // Kept for existing tests/callers; ambiguous duplicate PIDs are intentionally omitted.
    internal static Dictionary<string, string> ParseUsbipdList(string output) =>
        ParseUsbipdEntries(output)
            .GroupBy(entry => entry.ProductId)
            .Where(group => group.Count() == 1)
            .ToDictionary(
                group => group.Key.ToString("X4"),
                group => group.Single().BusId,
                StringComparer.OrdinalIgnoreCase);

    private static int ScoreDevice(AppleUsbDevice device)
    {
        if (!device.IsPresent) return 0;
        return device.Mode switch
        {
            DeviceMode.Pongo => 100,
            DeviceMode.PwnedDfu => 95,
            DeviceMode.YoloDfu => 90,
            DeviceMode.Dfu => 80,
            DeviceMode.Recovery => 70,
            DeviceMode.Normal => 60,
            DeviceMode.Busy => 10,
            _ => 1,
        };
    }

    private static bool DeviceEquals(AppleUsbDevice left, AppleUsbDevice right) =>
        string.Equals(left.DeviceId, right.DeviceId, StringComparison.OrdinalIgnoreCase) &&
        left.Mode == right.Mode &&
        string.Equals(left.Status, right.Status, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(left.Service, right.Service, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(left.BusId, right.BusId, StringComparison.OrdinalIgnoreCase);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _pollTimer.Dispose();
        lock (_pollGate) { }
    }

    internal sealed record UsbipdEntry(string BusId, ushort ProductId, string RawLine);
}
