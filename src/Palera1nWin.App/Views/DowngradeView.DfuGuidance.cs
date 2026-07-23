using System.Windows;
using DarkSwordRestore.Core;

namespace Palera1nWin.App.Views;

public partial class DowngradeView
{
    private async Task<AppleDeviceSnapshot> EnsureCleanDfuWithGuidanceAsync(
        string operation,
        CancellationToken cancellationToken)
    {
        var current = await _monitor.ProbeAsync(cancellationToken);
        if (current.Mode == AppleDeviceMode.Dfu) return current;

        var identity = string.IsNullOrWhiteSpace(current.ProductType)
            ? current.DisplayName ?? "Apple device"
            : current.ProductType;

        string instructions = current.Mode switch
        {
            AppleDeviceMode.Normal =>
                $"{identity} was detected in normal iOS/iPadOS mode.\n\n" +
                "Enter DFU now:\n" +
                "1. Keep the USB cable connected.\n" +
                "2. Hold Top + Home until the screen turns black.\n" +
                "3. As soon as it turns black, keep holding both for 4 seconds.\n" +
                "4. Release Top but keep holding Home for about 10 seconds.\n\n" +
                "The screen must remain completely black. The app will detect DFU automatically.",
            AppleDeviceMode.Recovery =>
                $"{identity} is in Recovery, not DFU.\n\n" +
                "Hold Top + Home until the screen turns black. Keep holding both for 4 seconds, " +
                "then release Top and keep holding Home for about 10 seconds. The screen must stay black.",
            AppleDeviceMode.Pongo or AppleDeviceMode.PwnedDfu =>
                $"{identity} is still in {current.Mode}. Force-restart it, then immediately enter clean DFU:\n\n" +
                "Hold Top + Home until black, keep both held for 4 seconds, release Top, and keep Home held for about 10 seconds.",
            AppleDeviceMode.Disconnected or AppleDeviceMode.Unknown =>
                "No usable Apple device is detected. Connect and unlock the iPad, tap Trust if prompted, and keep only that one Apple device connected.",
            _ =>
                $"The device is in {current.Mode}. Enter clean DFU with a completely black screen before continuing."
        };

        AppendLog($"{operation}: current mode={current.Mode}; opening guided DFU entry for {identity}.");
        ShowMessage(instructions, $"{operation} — enter DFU", MessageBoxImage.Information);

        SetBusy(true, "Waiting for clean DFU", "Follow the Home-button DFU steps. Detection continues automatically for five minutes.");
        var dfu = await _monitor.WaitForModeAsync(
            [AppleDeviceMode.Dfu],
            TimeSpan.FromMinutes(5),
            cancellationToken);
        AppendLog($"{operation}: clean DFU detected. ProductType={dfu.ProductType ?? "pending"}; service={dfu.Service ?? "unknown"}.");
        return dfu;
    }
}
