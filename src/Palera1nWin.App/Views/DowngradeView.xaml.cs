using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DarkSwordRestore.Core;
using Microsoft.Win32;
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
    private readonly string _logsDirectory = Path.Combine(AppContext.BaseDirectory, "logs");
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

        Directory.CreateDirectory(_logsDirectory);
        _logPath = Path.Combine(_logsDirectory, $"darksword-{DateTime.Now:yyyyMMdd-HHmmss}.log");

        _tools = ToolchainPaths.FromApplicationDirectory();
        _runner = new ToolProcessRunner();
        _inspector = new IpswInspector();
        _monitor = new AppleDeviceMonitor();
        _sessions = new RestoreSessionStore(Path.Combine(AppContext.BaseDirectory, "sessions"));
        _driver = new DfuDriverService(_runner, _tools);
        _orchestrator = new DarkSwordOrchestrator(_tools, _runner, _inspector, _monitor, _sessions, _driver);

        Loaded += DowngradeView_Loaded;
    }

    private MainViewModel? Shell => DataContext as MainViewModel;

    private async void DowngradeView_Loaded(object sender, RoutedEventArgs e)
    {
        if (_started || _disposed)
        {
            return;
        }

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

        AppendLog("DarkSword downgrade page initialized inside Palera1nWin.");
        SetShellStatus("Downgrade ready");
    }

    private void Monitor_DeviceChanged(object? sender, AppleDeviceSnapshot snapshot) =>
        Dispatcher.BeginInvoke(() => UpdateDeviceUi(snapshot));

    private void UpdateDeviceUi(AppleDeviceSnapshot snapshot)
    {
        DeviceModeLabel.Text = snapshot.Mode.ToString();
        DeviceDetailsText.Text = snapshot.DisplayName ?? snapshot.InstanceId ?? "Connect iPad6,11 or iPad6,12";

        Brush stateBrush = snapshot.Mode switch
        {
            AppleDeviceMode.Pongo => ResourceBrush("Brush.Success"),
            AppleDeviceMode.Dfu or AppleDeviceMode.Recovery => ResourceBrush("Brush.Accent"),
            AppleDeviceMode.Normal or AppleDeviceMode.Restore => ResourceBrush("Brush.Success"),
            _ => ResourceBrush("Brush.TextTertiary")
        };

        DeviceDot.Fill = stateBrush;
        DeviceModeLabel.Foreground = stateBrush;

        if (snapshot.Mode != AppleDeviceMode.Disconnected)
        {
            SetShellStatus($"Downgrade device: {snapshot.Mode}");
        }
    }

    private void RefreshToolchainState()
    {
        var missing = _tools.MissingFiles().ToList();
        var resources = Path.Combine(_tools.Root, "resources");
        foreach (var name in new[] { "sep_racer.bin", "kpf.bin" })
        {
            var path = Path.Combine(resources, name);
            if (!File.Exists(path))
            {
                missing.Add(path);
            }
        }

        if (missing.Count == 0)
        {
            ToolchainStateText.Text = "Ready";
            ToolchainStateText.Foreground = ResourceBrush("Brush.Success");
            ToolchainDetailsText.Text = "All native restore components are packaged";
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
            Title = "Select official iPad 5 IPSW",
            Filter = "Apple firmware (*.ipsw)|*.ipsw|All files (*.*)|*.*",
            CheckFileExists = true
        };

        if (dialog.ShowDialog(Window.GetWindow(this)) != true)
        {
            return;
        }

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
            var builder = new StringBuilder();
            builder.AppendLine(_inspection.IsValid ? "VALID IPAD 5 IPSW" : "IPSW VALIDATION FAILED");
            builder.AppendLine($"Version: {_inspection.ProductVersion ?? "Unknown"} ({_inspection.BuildVersion ?? "unknown build"})");
            builder.AppendLine($"Models: {string.Join(", ", _inspection.SupportedProductTypes)}");
            builder.AppendLine($"Size: {_inspection.FileSize / 1024d / 1024d / 1024d:F2} GB");
            builder.AppendLine($"SHA-256: {_inspection.Sha256}");
            foreach (var warning in _inspection.Warnings)
            {
                builder.AppendLine($"Warning: {warning}");
            }
            foreach (var error in _inspection.Errors)
            {
                builder.AppendLine($"Error: {error}");
            }

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
            SetBusy(false, "Ready", "Review the firmware result and confirmations");
        }
    }

    private void ConfirmationChanged(object sender, RoutedEventArgs e) => UpdateActionState();

    private async void StartDowngrade_Click(object sender, RoutedEventArgs e)
    {
        if (_inspection?.IsValid != true)
        {
            return;
        }

        _operationCts = new CancellationTokenSource();
        SetBusy(true, "Starting downgrade", "Preparing the complete DarkSword restore workflow");
        try
        {
            var session = await _orchestrator.RunFullDowngradeAsync(
                _inspection.Path,
                EraseCheck.IsChecked == true && TetherCheck.IsChecked == true && OwnershipCheck.IsChecked == true,
                new Progress<RestoreProgress>(UpdateProgress),
                AppendLog,
                _operationCts.Token);

            PtePathBox.Text = session.PteBlockPath ?? string.Empty;
            ShowMessage(
                $"The downgrade workflow completed.\n\nPTE block:\n{session.PteBlockPath}\n\nKeep the complete session folder backed up.",
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
            _operationCts.Dispose();
            _operationCts = null;
            SetBusy(false, CurrentStageText.Text, CurrentDetailText.Text);
        }
    }

    private void BrowsePte_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Select DarkSword PTE block",
            Filter = "PTE block (*.bin)|*.bin|All files (*.*)|*.*",
            CheckFileExists = true
        };

        if (dialog.ShowDialog(Window.GetWindow(this)) != true)
        {
            return;
        }

        PtePathBox.Text = dialog.FileName;
        UpdateActionState();
    }

    private async void TetherBoot_Click(object sender, RoutedEventArgs e)
    {
        if (!File.Exists(PtePathBox.Text))
        {
            return;
        }

        _operationCts = new CancellationTokenSource();
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
            _operationCts.Dispose();
            _operationCts = null;
            SetBusy(false, CurrentStageText.Text, CurrentDetailText.Text);
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => _operationCts?.Cancel();

    private async void RunDiagnostics_Click(object sender, RoutedEventArgs e)
    {
        SetBusy(true, "Diagnostics", "Checking Windows, device, storage, and native components");
        try
        {
            var snapshot = await _monitor.ProbeAsync();
            var root = Path.GetPathRoot(AppContext.BaseDirectory) ?? "C:\\";
            var drive = new DriveInfo(root);
            var resources = Path.Combine(_tools.Root, "resources");
            DiagnosticsBox.Text = string.Join(Environment.NewLine, new[]
            {
                $"Administrator: {IsAdministrator()}",
                $"Windows: {Environment.OSVersion}",
                $"Process: {System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}",
                $"Toolchain: {_tools.Root}",
                $"Missing tools: {(_tools.MissingFiles().Count == 0 ? "none" : string.Join(", ", _tools.MissingFiles().Select(Path.GetFileName)))}",
                $"sep_racer.bin: {File.Exists(Path.Combine(resources, "sep_racer.bin"))}",
                $"kpf.bin: {File.Exists(Path.Combine(resources, "kpf.bin"))}",
                $"Device mode: {snapshot.Mode}",
                $"Device: {snapshot.DisplayName ?? "not detected"}",
                $"USB service: {snapshot.Service ?? "n/a"}",
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
        SetShellStatus($"{value.Stage}: {value.Detail}");
    }

    private void SetBusy(bool busy, string title, string detail)
    {
        _busy = busy;
        CurrentStageText.Text = title;
        CurrentDetailText.Text = detail;
        if (!busy && OperationProgress.Value < 100)
        {
            OperationProgress.Value = 0;
        }
        SetShellStatus(detail);
        UpdateActionState();
    }

    private void UpdateActionState()
    {
        var confirmations = EraseCheck.IsChecked == true && TetherCheck.IsChecked == true && OwnershipCheck.IsChecked == true;
        var resources = Path.Combine(_tools.Root, "resources");
        var toolchainReady = _tools.MissingFiles().Count == 0
            && File.Exists(Path.Combine(resources, "sep_racer.bin"))
            && File.Exists(Path.Combine(resources, "kpf.bin"));

        StartDowngradeButton.IsEnabled = !_busy && confirmations && toolchainReady && _inspection?.IsValid == true;
        TetherBootButton.IsEnabled = !_busy && toolchainReady && File.Exists(PtePathBox.Text);
        CancelButton.IsEnabled = _busy;
        InspectIpswButton.IsEnabled = !_busy;
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
        {
            MessageBox.Show(message, title, MessageBoxButton.OK, image);
        }
        else
        {
            MessageBox.Show(owner, message, title, MessageBoxButton.OK, image);
        }
    }

    private static bool IsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
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