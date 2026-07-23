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

    /// <summary>
    /// Presence and driver health are different facts. Windows frequently reports an Apple
    /// composite interface as Error/Degraded while the physical device is connected and usable.
    /// Keep the device visible, then let the workflow validate the mode and required driver.
    /// </summary>
    public bool IsPresent =>
        !string.IsNullOrWhiteSpace(DeviceId) &&
        VendorId == 0x05AC &&
        !string.Equals(Status, "Unknown", StringComparison.OrdinalIgnoreCase);

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

        // Identity markers and known Apple PIDs are authoritative. Do not turn a valid
        // Normal/Recovery/DFU device into Busy merely because one composite interface has a
        // transient non-OK PnP status.
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

        var knownMode = productId switch
        {
            0x12A0 or 0x12A8 or 0x12AA or 0x12AB => DeviceMode.Normal,
            0x1280 or 0x1281 or 0x1282 or 0x1283 => DeviceMode.Recovery,
            0x1227 or 0x1222 => DeviceMode.Dfu,
            0x4141 => DeviceMode.Pongo,
            _ => DeviceMode.None,
        };
        if (knownMode != DeviceMode.None)
        {
            return knownMode;
        }

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
