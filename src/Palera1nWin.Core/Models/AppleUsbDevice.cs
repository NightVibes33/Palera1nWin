using System.Text.RegularExpressions;

namespace Palera1nWin.Core.Models;

public sealed class AppleUsbDevice
{
    private static readonly Regex VidPidRegex = new(
        @"VID_([0-9A-Fa-f]{4}).*?PID_([0-9A-Fa-f]{4})",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public string DeviceId { get; init; } = string.Empty;

    public string Name { get; init; } = string.Empty;

    public string Status { get; init; } = string.Empty;

    public string Service { get; init; } = string.Empty;

    public string? BusId { get; init; }

    public ushort VendorId { get; init; }

    public ushort ProductId { get; init; }

    public DeviceMode Mode { get; init; }

    public bool IsPresent =>
        !string.IsNullOrWhiteSpace(DeviceId) &&
        (Mode is DeviceMode.Normal or DeviceMode.Recovery or DeviceMode.Dfu or
             DeviceMode.YoloDfu or DeviceMode.PwnedDfu or DeviceMode.Pongo ||
         (!string.Equals(Status, "Unknown", StringComparison.OrdinalIgnoreCase) &&
          !string.Equals(Status, "Error", StringComparison.OrdinalIgnoreCase)));

    public static AppleUsbDevice Empty { get; } = new();

    public static AppleUsbDevice FromPnpEntity(
        string deviceId,
        string? name,
        string? status,
        string? service,
        string? busId = null)
    {
        var normalizedId = deviceId ?? string.Empty;
        var vid = (ushort)0x05AC;
        var pid = (ushort)0;

        var match = VidPidRegex.Match(normalizedId);
        if (match.Success)
        {
            vid = Convert.ToUInt16(match.Groups[1].Value, 16);
            pid = Convert.ToUInt16(match.Groups[2].Value, 16);
        }

        var mode = MapMode(normalizedId, pid, status);

        return new AppleUsbDevice
        {
            DeviceId = normalizedId,
            Name = name ?? string.Empty,
            Status = status ?? string.Empty,
            Service = service ?? string.Empty,
            BusId = busId,
            VendorId = vid,
            ProductId = pid,
            Mode = mode,
        };
    }

    public static DeviceMode MapMode(string deviceId, ushort productId, string? status = null)
    {
        var upperId = deviceId.ToUpperInvariant();

        // Identify Pongo / YOLO / PWND before treating non-OK status as Busy.
        if (productId == 0x4141 || upperId.Contains("PID_4141", StringComparison.Ordinal))
        {
            return DeviceMode.Pongo;
        }

        if (upperId.Contains("YOLO:", StringComparison.Ordinal))
        {
            return DeviceMode.YoloDfu;
        }

        if (upperId.Contains("PWND", StringComparison.Ordinal))
        {
            return DeviceMode.PwnedDfu;
        }

        // A known Apple PID determines the mode even while Windows reports a
        // transient Error/Unknown driver state during USB re-enumeration.
        if (productId is 0x1227 or 0x1222) return DeviceMode.Dfu;
        if (productId is 0x1280 or 0x1281 or 0x1282 or 0x1283) return DeviceMode.Recovery;
        if (productId >= 0x12A0 && productId <= 0x12AF) return DeviceMode.Normal;

        if (!string.IsNullOrWhiteSpace(status) &&
            !string.Equals(status, "OK", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(status, "Present", StringComparison.OrdinalIgnoreCase))
        {
            return DeviceMode.Busy;
        }

        return DeviceMode.None;
    }

    public override string ToString()
    {
        return $"{Mode} VID_{VendorId:X4}:PID_{ProductId:X4} ({Name})";
    }
}
