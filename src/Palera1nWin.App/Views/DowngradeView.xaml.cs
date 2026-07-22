using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DarkSwordRestore.Core;
using Microsoft.Win32;
using Palera1nWin.App.Services;
using Palera1nWin.App.ViewModels;

namespace Palera1nWin.App.Views;

public partial class DowngradeView : UserControl, IDisposable
{
    private readonly ToolchainPaths _tools;
    private readonly ToolProcessRunner _runner;
    private readonly IpswInspector _inspector;
    private readonly AppleDeviceMonitor _monitor;
    private readonly RestoreSessionStore _sessions;
    private readonly DfuDriverService _driver;
    private readonly DarkSwordOrchestrator _orchestrator;
    private readonly string _dataDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DarkSword Restore");
    private readonly string _logsDirectory;
    private readonly string _hardwareValidationPath;
    private readonly string _logPath;
    private readonly object _logLock = new();
    private CancellationTokenSource? _operationCts;
    private IpswInspectionResult? _inspection;
    private bool _busy;
    private bool _started;
    private bool _disposed;

    public DowngradeView()
    {
        InitializeComponent();

        _logsDirectory = Path.Combine(_dataDirectory, "logs");
        _hardwareValidationPath = Path.Combine(_dataDirectory, "hardware", "pongo-validation.json");
        Directory.CreateDirectory(_logsDirectory);
        _logPath = Path.Combine(_logsDirectory, $"darksword-{DateTime.Now:yyyyMMdd-HHmmss}.log");

        _tools = ToolchainPaths.FromApplicationDirectory();
        _runner = new ToolProcessRunner();
        _inspector = new IpswInspector();
        _monitor = new AppleDeviceMonitor();
        _sessions = new RestoreSessionStore();
        _driver = new DfuDriverService(_runner, _tools);
        _orchestrator = new DarkSwordOrchestrator(_tools, _runner, _inspector, _monitor, _sessions, _driver);

        Loaded += DowngradeView_Loaded;
    }

    private MainViewModel? Shell => DataContext as MainViewModel;

    private async void DowngradeView_Loaded(object sender, RoutedEventArgs e)
    {
        if (_started || _disposed) return;

        _started = true;
        RefreshToolchainState();
        _monitor.DeviceChanged += Monitor_DeviceChanged;
        _monitor.Start();

        try
        {
            UpdateDeviceUi(await _monitor.ProbeAsync());
        }
        catch (Exception exception)
        {
            AppendLog($"Device probe failed: {exception.Message}");
        }

        RefreshHardwareValidationUi();
        AppendLog("DarkSword downgrade page initialized inside Palera1nWin. Idle driver mutation is disabled.");
        SetShellStatus("Downgrade ready");
    }

    private void Monitor_DeviceChanged(object? sender, AppleDeviceSnapshot snapshot) =>
        Dispatcher.BeginInvoke(() => UpdateDeviceUi(snapshot));

    private void UpdateDeviceUi(AppleDeviceSnapshot snapshot)
    {
        DeviceModeLabel.Text = snapshot.Mode.ToString();
        DeviceDetailsText.Text = snapshot.DisplayName ?? snapshot.InstanceId ?? "Connect a supported A9-A10X Apple device";

        Brush stateBrush = snapshot.Mode switch
        {
            AppleDeviceMode.Pongo => ResourceBrush("Brush.Success"),
            AppleDeviceMode.Dfu or AppleDeviceMode.PwnedDfu or AppleDeviceMode.Recovery => ResourceBrush("Brush.Accent"),
            AppleDeviceMode.Normal or AppleDeviceMode.Restore => ResourceBrush("Brush.Success"),
            _ => ResourceBrush("Brush.TextTertiary")
        };

        DeviceDot.Fill = stateBrush;
        DeviceModeLabel.Foreground = stateBrush;

        if (snapshot.Mode != AppleDeviceMode.Disconnected)
        {
            SetShellStatus($"Downgrade device: {snapshot.Mode}");
        }
        RefreshHardwareValidationUi();
    }

    private void RefreshToolchainState()
    {
        var missing = _tools.MissingFiles().ToList();
        var resources = Path.Combine(_tools.Root, "resources");
        foreach (var name in new[] { "sep_racer.bin", "kpf.bin" })
        {
            var path = Path.Combine(resources, name);
            if (!File.Exists(path)) missing.Add(path);
        }

        if (missing.Count == 0)
        {
            ToolchainStateText.Text = "Ready";
            ToolchainStateText.Foreground = ResourceBrush("Brush.Success");
            ToolchainDetailsText.Text = "Restore, ProductType detection, DFU, Pongo, SEP, and boot components are packaged";
        }
        else
        {
            ToolchainStateText.Text = "Incomplete";
            ToolchainStateText.Foreground = ResourceBrush("Brush.Danger");
            ToolchainDetailsText.Text = string.Join(", ", missing.Select(Path.GetFileName));
        }

        UpdateActionState();
    }

    private void BrowseIpsw_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Select an official Apple iOS or iPadOS IPSW",
            Filter = "Apple firmware (*.ipsw)|*.ipsw|All files (*.*)|*.*",
            CheckFileExists = true
        };

        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;

        IpswPathBox.Text = dialog.FileName;
        _inspection = null;
        IpswSummaryText.Text = "Firmware selected. Inspect it before starting the downgrade.";
        IpswSummaryText.Foreground = ResourceBrush("Brush.TextTertiary");
        UpdateActionState();
    }

    private async void InspectIpsw_Click(object sender, RoutedEventArgs e)
    {
        if (!File.Exists(IpswPathBox.Text))
        {
            ShowMessage("Select an IPSW first.", "Palera1nWin Downgrade", MessageBoxImage.Information);
            return;
        }

        SetBusy(true, "Inspecting IPSW", "Calculating SHA-256 and reading BuildManifest.plist");
        try
        {
            _inspection = await _inspector.InspectAsync(IpswPathBox.Text);
            if (_inspection.IsValid &&
                !string.IsNullOrWhiteSpace(DetectedProductType) &&
                !_inspection.MatchesProductType(DetectedProductType))
            {
                var errors = _inspection.Errors
                    .Concat(new[]
                    {
                        $"The connected device is {DetectedProductType}, but this IPSW targets {string.Join(", ", _inspection.SupportedProductTypes)}."
                    })
                    .ToArray();
                _inspection = _inspection with { IsValid = false, Errors = errors };
            }

            var builder = new StringBuilder();
            builder.AppendLine(_inspection.IsValid ? "VALID SUPPORTED APPLE IPSW" : "IPSW VALIDATION FAILED");
            builder.AppendLine($"Version: {_inspection.ProductVersion ?? "Unknown"} ({_inspection.BuildVersion ?? "unknown build"})");
            builder.AppendLine($"ProductTypes: {string.Join(", ", _inspection.SupportedProductTypes)}");
            if (!string.IsNullOrWhiteSpace(DetectedProductType))
            {
                builder.AppendLine($"Connected ProductType: {DetectedProductType}");
                builder.AppendLine($"Exact match: {_inspection.MatchesProductType(DetectedProductType)}");
            }
            builder.AppendLine($"Size: {_inspection.FileSize / 1024d / 1024d / 1024d:F2} GB");
            builder.AppendLine($"SHA-256: {_inspection.Sha256}");
            foreach (var warning in _inspection.Warnings) builder.AppendLine($"Warning: {warning}");
            foreach (var error in _inspection.Errors) builder.AppendLine($"Error: {error}");

            IpswSummaryText.Text = builder.ToString().Trim();
            IpswSummaryText.Foreground = ResourceBrush(_inspection.IsValid ? "Brush.Success" : "Brush.Danger");
            AppendLog($"Inspected IPSW: {_inspection.Path} SHA256={_inspection.Sha256}");
        }
        catch (Exception exception)
        {
            _inspection = null;
            IpswSummaryText.Text = exception.Message;
            IpswSummaryText.Foreground = ResourceBrush("Brush.Danger");
            AppendLog($"IPSW inspection failed: {exception}");
        }
        finally
        {
            SetBusy(false, "Ready", "Review the exact-device firmware result and confirmations");
        }
    }

    private void ConfirmationChanged(object sender, RoutedEventArgs e) => UpdateActionState();

    private async void ValidateHardware_Click(object sender, RoutedEventArgs e)
    {
        _operationCts?.Cancel();
        _operationCts?.Dispose();
        _operationCts = new CancellationTokenSource();
        var lease = await TryAcquireHardwareLeaseAsync(
            HardwareOperationKind.DriverRepair,
            "Testing DFU, checkm8, PongoOS, driver re-enumeration, and bridge access",
            _operationCts.Token);
        if (lease is null)
        {
            _operationCts.Dispose();
            _operationCts = null;
            return;
        }

        SetBusy(true, "Test DFU → PongoOS", "This non-destructive test stops after the Pongo bridge is verified");
        try
        {
            await _orchestrator.ValidateDfuToPongoAsync(
                new Progress<RestoreProgress>(UpdateProgress),
                AppendLog,
                _operationCts.Token);
            await SaveHardwareValidationAsync();
            RefreshHardwareValidationUi();
            ShowMessage(
                "DFU → checkm8 → PongoOS → driver verification → bridge probe passed. No firmware was erased.",
                "Hardware gate passed",
                MessageBoxImage.Information);
        }
        catch (OperationCanceledException)
        {
            AppendLog("Hardware validation cancelled.");
        }
        catch (Exception exception)
        {
            AppendLog(exception.ToString());
            ShowMessage(exception.Message, "Hardware gate failed", MessageBoxImage.Error);
        }
        finally
        {
            await lease.DisposeAsync();
            _operationCts?.Dispose();
            _operationCts = null;
            SetBusy(false, CurrentStageText.Text, CurrentDetailText.Text);
        }
    }

    private async void StartDowngrade_Click(object sender, RoutedEventArgs e)
    {
        if (!IsActiveRestoreTargetReady())
        {
            ShowMessage(
                "The active Windows restore path requires a detected A9/A9X device and an inspected iOS/iPadOS 15 IPSW that contains that exact ProductType.",
                "Restore target not ready",
                MessageBoxImage.Information);
            return;
        }
        if (!HasCurrentHardwareValidation())
        {
            ShowMessage("Run Test DFU → PongoOS successfully before enabling a destructive restore.", "Hardware gate required", MessageBoxImage.Warning);
            return;
        }

        _operationCts = new CancellationTokenSource();
        var lease = await TryAcquireHardwareLeaseAsync(
            HardwareOperationKind.Downgrade,
            "Full tethered downgrade",
            _operationCts.Token);
        if (lease is null)
        {
            _operationCts.Dispose();
            _operationCts = null;
            return;
        }

        SetBusy(true, "Starting downgrade", "Preparing the complete chip-specific DarkSword restore workflow");
        try
        {
            var session = await _orchestrator.RunFullDowngradeAsync(
                _inspection!.Path,
                EraseCheck.IsChecked == true && TetherCheck.IsChecked == true && OwnershipCheck.IsChecked == true,
                new Progress<RestoreProgress>(UpdateProgress),
                AppendLog,
                _operationCts.Token);

            PtePathBox.Text = session.PteBlockPath ?? string.Empty;
            ShowMessage(
                $"The downgrade workflow completed.\n\nBoot asset:\n{session.PteBlockPath}\n\nKeep the complete session folder backed up.",
                "Downgrade complete",
                MessageBoxImage.Information);
        }
        catch (OperationCanceledException)
        {
            CurrentStageText.Text = "Cancelled";
            CurrentDetailText.Text = "The downgrade operation was cancelled";
            AppendLog("Downgrade operation cancelled.");
        }
        catch (Exception exception)
        {
            AppendLog(exception.ToString());
            ShowMessage(exception.Message, "Downgrade stopped", MessageBoxImage.Error);
        }
        finally
        {
            await lease.DisposeAsync();
            _operationCts?.Dispose();
            _operationCts = null;
            SetBusy(false, CurrentStageText.Text, CurrentDetailText.Text);
        }
    }

    private void BrowsePte_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Select a DarkSword tether-boot asset",
            Filter = "DarkSword block (*.bin)|*.bin|All files (*.*)|*.*",
            CheckFileExists = true
        };

        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;

        PtePathBox.Text = dialog.FileName;
        UpdateActionState();
    }

    private async void TetherBoot_Click(object sender, RoutedEventArgs e)
    {
        if (!File.Exists(PtePathBox.Text) || !IsActiveA9TetherBootTarget()) return;

        _operationCts = new CancellationTokenSource();
        var lease = await TryAcquireHardwareLeaseAsync(
            HardwareOperationKind.TetherBoot,
            "Cold boot using the saved PTE profile",
            _operationCts.Token);
        if (lease is null)
        {
            _operationCts.Dispose();
            _operationCts = null;
            return;
        }

        SetBusy(true, "Tether boot", "Waiting for DFU mode");
        try
        {
            await _orchestrator.TetherBootAsync(
                PtePathBox.Text,
                new Progress<RestoreProgress>(UpdateProgress),
                AppendLog,
                _operationCts.Token);

            ShowMessage("The tether boot sequence was sent successfully.", "Tether boot complete", MessageBoxImage.Information);
        }
        catch (OperationCanceledException)
        {
            CurrentStageText.Text = "Cancelled";
            CurrentDetailText.Text = "Tether boot was cancelled";
            AppendLog("Tether boot cancelled.");
        }
        catch (Exception exception)
        {
            AppendLog(exception.ToString());
            ShowMessage(exception.Message, "Tether boot failed", MessageBoxImage.Error);
        }
        finally
        {
            await lease.DisposeAsync();
            _operationCts?.Dispose();
            _operationCts = null;
            SetBusy(false, CurrentStageText.Text, CurrentDetailText.Text);
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => _operationCts?.Cancel();

    private async void RunDiagnostics_Click(object sender, RoutedEventArgs e)
    {
        using var diagnosticsCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var lease = await TryAcquireHardwareLeaseAsync(
            HardwareOperationKind.Diagnostics,
            "Reading DarkSword device and toolchain state",
            diagnosticsCts.Token);
        if (lease is null) return;

        SetBusy(true, "Diagnostics", "Checking Windows, exact device identity, storage, and native components");
        try
        {
            var snapshot = await _monitor.ProbeAsync();
            var root = Path.GetPathRoot(_dataDirectory) ?? "C:\\";
            var drive = new DriveInfo(root);
            var resources = Path.Combine(_tools.Root, "resources");
            var detected = DarkSwordDeviceCatalog.Find(DetectedProductType);
            DiagnosticsBox.Text = string.Join(Environment.NewLine, new[]
            {
                $"Administrator: {IsAdministrator()}",
                $"Windows: {Environment.OSVersion}",
                $"Process: {System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}",
                $"Active hardware operation: {Shell?.HardwareOperations.Current.Operation}",
                $"Toolchain: {_tools.Root}",
                $"Missing tools: {(_tools.MissingFiles().Count == 0 ? "none" : string.Join(", ", _tools.MissingFiles().Select(Path.GetFileName)))}",
                $"ideviceinfo.exe: {File.Exists(Path.Combine(_tools.Root, "ideviceinfo.exe"))}",
                $"irecovery.exe: {File.Exists(Path.Combine(_tools.Root, "irecovery.exe"))}",
                $"sep_racer.bin: {File.Exists(Path.Combine(resources, "sep_racer.bin"))}",
                $"kpf.bin: {File.Exists(Path.Combine(resources, "kpf.bin"))}",
                $"Device mode: {snapshot.Mode}",
                $"Device: {snapshot.DisplayName ?? "not detected"}",
                $"ProductType: {DetectedProductType ?? "unresolved"}",
                $"Supported device: {detected is not null}",
                $"Chip: {detected?.Chip.ToString() ?? "unknown"}",
                $"Active native restore path: {(detected?.UsesA9SepBlocks == true ? "A9/A9X SHC/PTE" : "not available for this chip")}",
                $"USB service: {snapshot.Service ?? "n/a"}",
                $"Hardware gate current: {HasCurrentHardwareValidation()}",
                $"Disk free: {drive.AvailableFreeSpace / 1024d / 1024d / 1024d:F1} GB",
                $"DarkSword log: {_logPath}",
                $"Sessions: {_sessions.RootDirectory}"
            });
            RefreshToolchainState();
        }
        catch (Exception exception)
        {
            DiagnosticsBox.Text = exception.ToString();
            AppendLog($"Diagnostics failed: {exception}");
        }
        finally
        {
            await lease.DisposeAsync();
            SetBusy(false, "Ready", "Diagnostics complete");
        }
    }

    private void OpenLogs_Click(object sender, RoutedEventArgs e) => OpenFolder(_logsDirectory);

    private void OpenSessions_Click(object sender, RoutedEventArgs e) => OpenFolder(_sessions.RootDirectory);

    private static void OpenFolder(string path)
    {
        Directory.CreateDirectory(path);
        Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
    }

    private void UpdateProgress(RestoreProgress value)
    {
        OperationProgress.Value = Math.Clamp(value.Percent, 0, 100);
        CurrentStageText.Text = value.Title;
        CurrentDetailText.Text = value.Detail;
        CurrentStageText.Foreground = value.Stage switch
        {
            RestoreStage.Completed => ResourceBrush("Brush.Success"),
            RestoreStage.Failed => ResourceBrush("Brush.Danger"),
            _ => ResourceBrush("Brush.Text")
        };
        if (Shell?.HardwareOperations.Current.IsBusy == true)
        {
            Shell.HardwareOperations.UpdateDetail(Shell.HardwareOperations.Current.Operation, value.Detail);
        }
        SetShellStatus($"{value.Stage}: {value.Detail}");
    }

    private void SetBusy(bool busy, string title, string detail)
    {
        _busy = busy;
        CurrentStageText.Text = title;
        CurrentDetailText.Text = detail;
        if (!busy && OperationProgress.Value < 100) OperationProgress.Value = 0;
        SetShellStatus(detail);
        UpdateActionState();
        RefreshEnhancedActionState();
    }

    private void UpdateActionState()
    {
        var confirmations = EraseCheck.IsChecked == true && TetherCheck.IsChecked == true && OwnershipCheck.IsChecked == true;
        var resources = Path.Combine(_tools.Root, "resources");
        var toolchainReady = _tools.MissingFiles().Count == 0
            && File.Exists(Path.Combine(resources, "sep_racer.bin"))
            && File.Exists(Path.Combine(resources, "kpf.bin"));
        var hardwareBusy = Shell?.HardwareOperations.Current.IsBusy == true;

        StartDowngradeButton.IsEnabled = !hardwareBusy && !_busy && confirmations && toolchainReady && IsActiveRestoreTargetReady() && HasCurrentHardwareValidation();
        TetherBootButton.IsEnabled = !hardwareBusy && !_busy && toolchainReady && IsActiveA9TetherBootTarget() && File.Exists(PtePathBox.Text);
        ValidateHardwareButton.IsEnabled = !hardwareBusy && !_busy && toolchainReady;
        CancelButton.IsEnabled = _busy;
        InspectIpswButton.IsEnabled = !_busy;
    }

    private async Task<HardwareOperationLease?> TryAcquireHardwareLeaseAsync(
        HardwareOperationKind operation,
        string detail,
        CancellationToken cancellationToken)
    {
        if (Shell is null) return null;
        try
        {
            return await Shell.HardwareOperations.AcquireAsync(operation, detail, cancellationToken);
        }
        catch (HardwareOperationBusyException exception)
        {
            AppendLog(exception.Message);
            ShowMessage(exception.Message, "Apple hardware is busy", MessageBoxImage.Warning);
            return null;
        }
    }

    private async Task SaveHardwareValidationAsync()
    {
        var snapshot = await _monitor.ProbeAsync();
        Directory.CreateDirectory(Path.GetDirectoryName(_hardwareValidationPath)!);
        var payload = new
        {
            schema = 1,
            productType = DetectedProductType,
            mode = snapshot.Mode.ToString(),
            service = snapshot.Service,
            instanceId = snapshot.InstanceId,
            validatedAt = DateTimeOffset.UtcNow
        };
        await File.WriteAllTextAsync(
            _hardwareValidationPath,
            JsonSerializer.Serialize(payload, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));
    }

    private bool HasCurrentHardwareValidation()
    {
        if (!File.Exists(_hardwareValidationPath)) return false;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(_hardwareValidationPath));
            var root = document.RootElement;
            var productType = root.TryGetProperty("productType", out var product) ? product.GetString() : null;
            var validatedAt = root.TryGetProperty("validatedAt", out var time) && time.TryGetDateTimeOffset(out var parsed)
                ? parsed
                : DateTimeOffset.MinValue;
            return !string.IsNullOrWhiteSpace(DetectedProductType) &&
                   string.Equals(productType, DetectedProductType, StringComparison.Ordinal) &&
                   DateTimeOffset.UtcNow - validatedAt <= TimeSpan.FromDays(7);
        }
        catch
        {
            return false;
        }
    }

    private void RefreshHardwareValidationUi()
    {
        if (!IsLoaded) return;
        var valid = HasCurrentHardwareValidation();
        HardwareValidationStatusText.Text = valid
            ? "PASSED — DFU → checkm8 → PongoOS → bridge was verified for this ProductType within the last 7 days."
            : "REQUIRED — run the non-destructive hardware gate before a full downgrade. Cold Boot remains available with a saved PTE.";
        HardwareValidationStatusText.Foreground = ResourceBrush(valid ? "Brush.Success" : "Brush.Accent");
        UpdateActionState();
    }

    private void AppendLog(string line)
    {
        var formatted = $"[{DateTimeOffset.Now:O}] {line}";
        lock (_logLock)
        {
            File.AppendAllText(_logPath, formatted + Environment.NewLine);
        }

        Dispatcher.BeginInvoke(() =>
        {
            LogBox.AppendText(formatted + Environment.NewLine);
            LogBox.ScrollToEnd();
            Shell?.AppendLog("darksword", line, line.Contains("error", StringComparison.OrdinalIgnoreCase));
        });
    }

    private void SetShellStatus(string text) => Shell?.SetStatusText(text);

    private Brush ResourceBrush(string key) => TryFindResource(key) as Brush ?? Brushes.Gray;

    private void ShowMessage(string message, string title, MessageBoxImage image)
    {
        var owner = Window.GetWindow(this);
        if (owner is null)
            MessageBox.Show(message, title, MessageBoxButton.OK, image);
        else
            MessageBox.Show(owner, message, title, MessageBoxButton.OK, image);
    }

    private static bool IsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    public void Dispose()
    {
        if (_disposed) return;

        _disposed = true;
        DisposeFirmwareFeatures();
        _operationCts?.Cancel();
        _operationCts?.Dispose();
        _operationCts = null;
        _monitor.DeviceChanged -= Monitor_DeviceChanged;
        try
        {
            _monitor.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
        catch
        {
            // App shutdown cleanup is best-effort.
        }
    }
}
