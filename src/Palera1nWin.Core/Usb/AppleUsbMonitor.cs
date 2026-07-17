using System.Management;
using System.Text.RegularExpressions;
using Palera1nWin.Core.Models;
using Palera1nWin.Core.Services;
using Palera1nWin.Core.Util;

namespace Palera1nWin.Core.Usb;

public sealed class AppleUsbMonitor : IDisposable
{
    private static readonly Regex BusIdRegex = new(
        @"\b(\d+-\d+)\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly Timer _pollTimer;
    private readonly UsbipdService _usbipdService;
    private readonly object _sync = new();
    private AppleUsbDevice _currentDevice = AppleUsbDevice.Empty;
    private bool _disposed;

    public AppleUsbMonitor(UsbipdService? usbipdService = null)
    {
        _usbipdService = usbipdService ?? new UsbipdService();
        _pollTimer = new Timer(_ => PollSafe(), null, TimeSpan.Zero, TimeSpan.FromSeconds(1.5));
    }

    public event EventHandler<AppleUsbDevice>? DeviceChanged;

    public AppleUsbDevice CurrentDevice
    {
        get
        {
            lock (_sync)
            {
                return _currentDevice;
            }
        }
    }

    public void PollNow()
    {
        PollSafe();
    }

    /// <summary>
    /// boot-pongo.ps1 fallback: Pongo often Status=Error in PnP but still listed by usbipd.
    /// </summary>
    public bool IsPongoVisibleInUsbipd()
    {
        try
        {
            var listOutput = _usbipdService.ListDevices();
            return listOutput.Contains("05ac:4141", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    public IReadOnlyList<AppleUsbDevice> ScanDevices()
    {
        var devices = new List<AppleUsbDevice>();
        devices.AddRange(ScanPnPDevices());
        MergeUsbipdBusIds(devices);
        return devices
            .GroupBy(d => d.DeviceId, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .OrderByDescending(d => ScoreDevice(d))
            .ToList();
    }

    private void PollSafe()
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            var devices = ScanDevices();
            var best = devices.FirstOrDefault() ?? AppleUsbDevice.Empty;
            AppleUsbDevice previous;

            lock (_sync)
            {
                previous = _currentDevice;
                _currentDevice = best;
            }

            if (!DeviceEquals(previous, best))
            {
                DeviceChanged?.Invoke(this, best);
            }
        }
        catch
        {
            // Polling must never crash the host process.
        }
    }

    private static IEnumerable<AppleUsbDevice> ScanPnPDevices()
    {
        var results = new List<AppleUsbDevice>();

        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT DeviceID, Name, Status, Service FROM Win32_PnPEntity WHERE DeviceID LIKE 'USB\\\\VID_05AC%'");

            foreach (var obj in searcher.Get())
            {
                try
                {
                    var deviceId = obj["DeviceID"]?.ToString() ?? string.Empty;
                    var name = obj["Name"]?.ToString();
                    var status = obj["Status"]?.ToString();
                    var service = obj["Service"]?.ToString();
                    if (string.IsNullOrWhiteSpace(service))
                    {
                        service = Drivers.DriverInstaller.ResolveServiceName(deviceId);
                    }

                    results.Add(AppleUsbDevice.FromPnpEntity(deviceId, name, status, service));
                }
                finally
                {
                    obj.Dispose();
                }
            }
        }
        catch (ManagementException)
        {
            // WMI may be unavailable in restricted environments.
        }

        return results;
    }

    private void MergeUsbipdBusIds(List<AppleUsbDevice> devices)
    {
        try
        {
            var listOutput = _usbipdService.ListDevices();
            if (string.IsNullOrWhiteSpace(listOutput))
            {
                return;
            }

            var busIdsByPid = ParseUsbipdList(listOutput);
            for (var i = 0; i < devices.Count; i++)
            {
                var device = devices[i];
                var pidKey = device.ProductId.ToString("X4");
                if (busIdsByPid.TryGetValue(pidKey, out var busId))
                {
                    devices[i] = AppleUsbDevice.FromPnpEntity(
                        device.DeviceId,
                        device.Name,
                        device.Status,
                        device.Service,
                        busId);
                }
            }
        }
        catch
        {
            // usbipd is optional for basic PnP detection.
        }
    }

    internal static Dictionary<string, string> ParseUsbipdList(string output)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in output.Split('\n', '\r'))
        {
            if (!line.Contains("05ac:", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var busMatch = BusIdRegex.Match(line);
            if (!busMatch.Success)
            {
                continue;
            }

            var pidMatch = Regex.Match(line, @"05ac:([0-9a-f]{4})", RegexOptions.IgnoreCase);
            if (!pidMatch.Success)
            {
                continue;
            }

            map[pidMatch.Groups[1].Value.ToUpperInvariant()] = busMatch.Groups[1].Value;
        }

        return map;
    }

    private static int ScoreDevice(AppleUsbDevice device)
    {
        if (!device.IsPresent)
        {
            return 0;
        }

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

    private static bool DeviceEquals(AppleUsbDevice left, AppleUsbDevice right)
    {
        return string.Equals(left.DeviceId, right.DeviceId, StringComparison.OrdinalIgnoreCase) &&
               left.Mode == right.Mode &&
               string.Equals(left.Status, right.Status, StringComparison.OrdinalIgnoreCase) &&
               string.Equals(left.Service, right.Service, StringComparison.OrdinalIgnoreCase) &&
               string.Equals(left.BusId, right.BusId, StringComparison.OrdinalIgnoreCase);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _pollTimer.Dispose();
    }
}
