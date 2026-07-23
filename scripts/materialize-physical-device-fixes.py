from pathlib import Path

root = Path(__file__).resolve().parents[1]
view = root / "src/Palera1nWin.App/Views/DowngradeView.xaml.cs"
text = view.read_text(encoding="utf-8-sig")

old = "    private MainViewModel? Shell => DataContext as MainViewModel;"
new = """    private MainViewModel? Shell =>
        DataContext as MainViewModel ??
        Application.Current?.MainWindow?.DataContext as MainViewModel;"""
if text.count(old) != 1:
    raise SystemExit(f"Expected one Shell property, found {text.count(old)}")
text = text.replace(old, new, 1)

old = """        if (Shell is null) return null;
        try
        {
            return await Shell.HardwareOperations.AcquireAsync(operation, detail, cancellationToken);"""
new = """        var shell = Shell;
        if (shell is null)
        {
            AppendLog($"Cannot start {operation}: the shared application shell is unavailable.");
            ShowMessage("The shared application state was not initialized. Close Palera1nWin, reopen it as Administrator, and retry.", "Workflow initialization failed", MessageBoxImage.Error);
            return null;
        }
        try
        {
            return await shell.HardwareOperations.AcquireAsync(operation, detail, cancellationToken);"""
if text.count(old) != 1:
    raise SystemExit("Hardware lease block changed")
text = text.replace(old, new, 1)

old = """            Shell?.AppendLog("darksword", line, line.Contains("error", StringComparison.OrdinalIgnoreCase));"""
new = """            var shell = Shell;
            shell?.AppendLog(
                "darksword",
                line,
                line.Contains("error", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("fail", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("exception", StringComparison.OrdinalIgnoreCase));"""
if text.count(old) != 1:
    raise SystemExit("Shared downgrade log forwarding line changed")
text = text.replace(old, new, 1)
view.write_text(text, encoding="utf-8", newline="\n")

monitor = root / "DarkSwordRestore/src/DarkSwordRestore.Core/AppleDeviceMonitor.cs"
source = monitor.read_text(encoding="utf-8-sig")
old = """        var selected = devices
            .Select(Parse)
            .OrderByDescending(snapshot => Priority(snapshot.Mode))
            .FirstOrDefault();
        return await EnrichIdentityAsync(selected ?? AppleDeviceSnapshot.Disconnected, cancellationToken).ConfigureAwait(false);"""
new = """        var selected = devices
            .Select(Parse)
            .Where(snapshot => snapshot.Mode != AppleDeviceMode.Unknown)
            .OrderByDescending(snapshot => Priority(snapshot.Mode))
            .FirstOrDefault();
        return await EnrichIdentityAsync(selected ?? AppleDeviceSnapshot.Disconnected, cancellationToken).ConfigureAwait(false);"""
if text.count(old) == 0 and source.count(old) != 1:
    raise SystemExit(f"DarkSword monitor selection block changed: {source.count(old)}")
source = source.replace(old, new, 1)

# Accept every normal-mode PID used by Apple mobile USB drivers in this app.
old = 'var text when text.Contains("PID_12A0") || text.Contains("PID_12A8") || text.Contains("PID_12AA") || text.Contains("PID_12AB") => AppleDeviceMode.Normal,'
if old not in source:
    # Older ordering without 12A0/12AA.
    old = 'var text when text.Contains("PID_12A8") || text.Contains("PID_12AA") || text.Contains("PID_12AB") => AppleDeviceMode.Normal,'
new = 'var text when text.Contains("PID_12A0") || text.Contains("PID_12A8") || text.Contains("PID_12AA") || text.Contains("PID_12AB") => AppleDeviceMode.Normal,'
if old not in source:
    raise SystemExit("DarkSword normal PID mapping changed")
source = source.replace(old, new, 1)
monitor.write_text(source, encoding="utf-8", newline="\n")
print("Materialized robust Apple mode detection and shared downgrade logging.")
