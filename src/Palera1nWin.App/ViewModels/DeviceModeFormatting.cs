using Palera1nWin.Core.Models;

namespace Palera1nWin.App.ViewModels;

internal static class DeviceModeFormatting
{
    public static string GetLabel(DeviceMode mode) => mode switch
    {
        DeviceMode.None => "No device",
        DeviceMode.Normal => "Normal",
        DeviceMode.Recovery => "Recovery",
        DeviceMode.Dfu => "DFU",
        DeviceMode.YoloDfu => "YOLO DFU",
        DeviceMode.PwnedDfu => "Pwned DFU",
        DeviceMode.Pongo => "PongoOS",
        DeviceMode.Busy => "Busy",
        _ => mode.ToString(),
    };

    public static string GetBadgeBackground(DeviceMode mode) => mode switch
    {
        DeviceMode.Pongo => "#2634D399",
        DeviceMode.PwnedDfu or DeviceMode.YoloDfu => "#262DD4BF",
        DeviceMode.Dfu => "#26FBBF24",
        DeviceMode.Recovery => "#2638BDF8",
        DeviceMode.Normal => "#26343F52",
        DeviceMode.Busy => "#26F87171",
        _ => "#26343F52",
    };

    public static string GetBadgeForeground(DeviceMode mode) => mode switch
    {
        DeviceMode.Pongo => "#34D399",
        DeviceMode.PwnedDfu or DeviceMode.YoloDfu => "#2DD4BF",
        DeviceMode.Dfu => "#FBBF24",
        DeviceMode.Recovery => "#38BDF8",
        DeviceMode.Busy => "#F87171",
        _ => "#9AA3B8",
    };
}
