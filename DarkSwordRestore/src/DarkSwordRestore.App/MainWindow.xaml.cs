using System.Diagnostics;
using System.Text;
using System.Windows;
using System.Windows.Media;
using DarkSwordRestore.Core;
using Microsoft.Win32;

namespace DarkSwordRestore.App;

public partial class MainWindow : Window
{
    private readonly string _logsDirectory = Path.Combine(AppContext.BaseDirectory, "logs");
    private readonly string _sessionsDirectory = Path.Combine(AppContext.BaseDirectory, "sessions");
    private readonly SessionLogger _logger;
    private readonly ProcessRunner _runner;
    private readonly ToolchainLocator _tools;
    private readonly AppleUsbMonitor _monitor;
    private readonly DfuDriverService _driver;
    private readonly IpswInspector _inspector;
    private readonly RestoreOrchestrator _orchestrator;
    private CancellationTokenSource? _operationCts;
    private IpswInspectionResult? _inspection;
    private bool _busy;

    public MainWindow()
    {
        InitializeComponent();
        Directory.CreateDirectory(_logsDirectory);
        Directory.CreateDirectory(_sessionsDirectory);

        _logger = new SessionLogger(_logsDirectory);
        _runner = new ProcessRunner(_logger);
        _tools = new ToolchainLocator();
        _monitor = new AppleUsbMonitor(_runner, _logger);
        _driver = new DfuDriverService(_tools, _runner, _logger);
        _inspector = new IpswInspector();
        _orchestrator = new RestoreOrchestrator(_tools, _runner, _logger, _monitor, _driver, _inspector);

        _logger.LineWritten += Logger_LineWritten;
        _monitor.DeviceChanged += Monitor_DeviceChanged;
        Loaded += MainWindow_Loaded;
        Closed += MainWindow_Closed;
    }

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        RefreshToolchainState();
        _monitor.Start();
        _ = RefreshDeviceAsync();
        _logger.Info("DarkSword Restore started.");
    }

    private async void MainWindow_Closed(object? sender, EventArgs e)
    {
        _operationCts?.Cancel();
        await _monitor.DisposeAsync();
        _logger.Dispose();
    }

    private void Logger_LineWritten(object? sender, string line)
    {
        Dispatcher.BeginInvoke(() =>
        {
            LogBox.AppendText(line + Environment.NewLine);
            LogBox.ScrollToEnd();
        });
    }

    private void Monitor_DeviceChanged(object? sender, AppleDeviceSnapshot snapshot) =>
        Dispatcher.BeginInvoke(() => UpdateDeviceUi(snapshot));

    private async Task RefreshDeviceAsync()
    {
        var snapshot = await _monitor.ProbeAsync();
        await Dispatcher.InvokeAsync(() => UpdateDeviceUi(snapshot));
    }

    private void UpdateDeviceUi(AppleDeviceSnapshot snapshot)
    {
        DeviceModeText.Text = snapshot.Mode.ToString();
        DeviceDetailsText.Text = snapshot.DisplayName ?? snapshot.InstanceId ?? "Connect iPad6,11 or iPad6,12";
        HeaderDeviceText.Text = snapshot.Mode == AppleDeviceMode.Disconnected ? "No iPad connected" : $"Apple device: {snapshot.Mode}";
        DeviceDot.Fill = snapshot.Mode switch
        {
            AppleDeviceMode.Pongo => (Brush)FindResource("SuccessBrush"),
            AppleDeviceMode.Dfu or AppleDeviceMode.Recovery => (Brush)FindResource("AccentBrush"),
            AppleDeviceMode.Normal or AppleDeviceMode.Restore => (Brush)FindResource("SuccessBrush"),
            _ => (Brush)FindResource("MutedBrush")
        };
    }

    private void RefreshToolchainState()
    {
        var missing = _tools.MissingRequiredTools();
        var resources = new[] { "sep_racer.bin", "kpf.bin" }
            .Where(x => !File.Exists(Path.Combine(_tools.ResourcesDirectory, x)))
            .ToArray();
        if (missing.Count == 0 && resources.Length == 0)
        {
            ToolchainStateText.Text = "Ready";
            ToolchainStateText.Foreground = (Brush)FindResource("SuccessBrush");
            ToolchainDetailsText.Text = _tools.Root;
        }
        else
        {
            ToolchainStateText.Text = "Incomplete";
            ToolchainStateText.Foreground = (Brush)FindResource("DangerBrush");
            ToolchainDetailsText.Text = string.Join(", ", missing.Concat(resources));
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
        if (dialog.ShowDialog(this) == true)
        {
            IpswPathBox.Text = dialog.FileName;
            _inspection = null;
            IpswSummaryText.Text = "Firmware selected. Click Inspect before starting.";
            UpdateActionState();
        }
    }

    private async void InspectIpsw_Click(object sender, RoutedEventArgs e)
    {
        if (!File.Exists(IpswPathBox.Text))
        {
            MessageBox.Show(this, "Select an IPSW first.", "DarkSword Restore", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        SetBusy(true, "Inspecting IPSW", "Calculating SHA-256 and reading BuildManifest.plist");
        try
        {
            _inspection = await _inspector.InspectAsync(IpswPathBox.Text);
            var builder = new StringBuilder();
            builder.AppendLine(_inspection.IsValid ? "VALID OFFICIAL-FORMAT IPSW" : "IPSW VALIDATION FAILED");
            builder.AppendLine($"Version: {_inspection.ProductVersion ?? "Unknown"} ({_inspection.BuildVersion ?? "unknown build"})");
            builder.AppendLine($"Models: {string.Join(", ", _inspection.SupportedProductTypes)}");
            builder.AppendLine($"Size: {_inspection.FileSize / 1024d / 1024d / 1024d:F2} GB");
            builder.AppendLine($"SHA-256: {_inspection.Sha256}");
            foreach (var warning in _inspection.Warnings) builder.AppendLine($"Warning: {warning}");
            foreach (var error in _inspection.Errors) builder.AppendLine($"Error: {error}");
            IpswSummaryText.Text = builder.ToString().Trim();
            IpswSummaryText.Foreground = (Brush)FindResource(_inspection.IsValid ? "SuccessBrush" : "DangerBrush");
        }
        catch (Exception ex)
        {
            _inspection = null;
            IpswSummaryText.Text = ex.Message;
            IpswSummaryText.Foreground = (Brush)FindResource("DangerBrush");
        }
        finally
        {
            SetBusy(false, "Idle", "Select a task below");
            UpdateActionState();
        }
    }

    private void ConfirmationChanged(object sender, RoutedEventArgs e) => UpdateActionState();

    private async void StartDowngrade_Click(object sender, RoutedEventArgs e)
    {
        if (_inspection?.IsValid != true) return;
        _operationCts = new CancellationTokenSource();
        SetBusy(true, "Starting", "Preparing the complete downgrade workflow");
        var progress = new Progress<RestoreProgress>(UpdateProgress);
        try
        {
            var session = await _orchestrator.RunFullDowngradeAsync(
                _inspection.Path,
                destructiveOperationConfirmed: EraseCheck.IsChecked == true && TetherCheck.IsChecked == true && OwnershipCheck.IsChecked == true,
                progress,
                _operationCts.Token);
            PtePathBox.Text = session.PteBlockPath ?? string.Empty;
            MessageBox.Show(this,
                $"The downgrade workflow completed.\n\nPTE block:\n{session.PteBlockPath}\n\nKeep the complete session folder backed up.",
                "DarkSword Restore",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (OperationCanceledException)
        {
            FooterStatusText.Text = "Operation cancelled";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "DarkSword Restore stopped", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _operationCts.Dispose();
            _operationCts = null;
            SetBusy(false, CurrentStageText.Text, CurrentDetailText.Text);
            UpdateActionState();
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
        if (dialog.ShowDialog(this) == true)
        {
            PtePathBox.Text = dialog.FileName;
            UpdateActionState();
        }
    }

    private async void TetherBoot_Click(object sender, RoutedEventArgs e)
    {
        if (!File.Exists(PtePathBox.Text)) return;
        _operationCts = new CancellationTokenSource();
        SetBusy(true, "Tether boot", "Waiting for DFU mode");
        try
        {
            await _orchestrator.TetherBootAsync(PtePathBox.Text, new Progress<RestoreProgress>(UpdateProgress), _operationCts.Token);
            MessageBox.Show(this, "The tether boot sequence was sent successfully.", "DarkSword Restore", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (OperationCanceledException)
        {
            FooterStatusText.Text = "Tether boot cancelled";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Tether boot failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _operationCts.Dispose();
            _operationCts = null;
            SetBusy(false, CurrentStageText.Text, CurrentDetailText.Text);
            UpdateActionState();
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => _operationCts?.Cancel();

    private async void RunDiagnostics_Click(object sender, RoutedEventArgs e)
    {
        SetBusy(true, "Diagnostics", "Checking the Windows environment");
        try
        {
            var snapshot = await _monitor.ProbeAsync();
            var missing = _tools.MissingRequiredTools();
            var root = Path.GetPathRoot(AppContext.BaseDirectory) ?? "C:\\";
            var drive = new DriveInfo(root);
            var builder = new StringBuilder();
            builder.AppendLine($"Administrator: {_driver.IsAdministrator()}");
            builder.AppendLine($"Windows: {Environment.OSVersion}");
            builder.AppendLine($"Process: {System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}");
            builder.AppendLine($"Toolchain: {_tools.Root}");
            builder.AppendLine($"Missing tools: {(missing.Count == 0 ? "none" : string.Join(", ", missing))}");
            builder.AppendLine($"sep_racer.bin: {File.Exists(Path.Combine(_tools.ResourcesDirectory, "sep_racer.bin"))}");
            builder.AppendLine($"kpf.bin: {File.Exists(Path.Combine(_tools.ResourcesDirectory, "kpf.bin"))}");
            builder.AppendLine($"Device mode: {snapshot.Mode}");
            builder.AppendLine($"Device: {snapshot.DisplayName ?? "not detected"}");
            builder.AppendLine($"Service: {snapshot.Service ?? "n/a"}");
            builder.AppendLine($"Disk free: {drive.AvailableFreeSpace / 1024d / 1024d / 1024d:F1} GB");
            builder.AppendLine($"Log: {_logger.LogPath}");
            DiagnosticsBox.Text = builder.ToString();
            RefreshToolchainState();
        }
        finally
        {
            SetBusy(false, "Idle", "Diagnostics complete");
        }
    }

    private void OpenLogs_Click(object sender, RoutedEventArgs e) => OpenFolder(_logsDirectory);
    private void OpenSessions_Click(object sender, RoutedEventArgs e) => OpenFolder(_sessionsDirectory);

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
        FooterStatusText.Text = $"{value.Stage}: {value.Detail}";
        if (value.Stage == RestoreStage.Completed) CurrentStageText.Foreground = (Brush)FindResource("SuccessBrush");
        else if (value.Stage == RestoreStage.Failed) CurrentStageText.Foreground = (Brush)FindResource("DangerBrush");
        else CurrentStageText.Foreground = (Brush)FindResource("TextBrush");
    }

    private void SetBusy(bool busy, string title, string detail)
    {
        _busy = busy;
        CurrentStageText.Text = title;
        CurrentDetailText.Text = detail;
        CancelButton.IsEnabled = busy;
        FooterStatusText.Text = detail;
        if (!busy && OperationProgress.Value < 100) OperationProgress.Value = 0;
        UpdateActionState();
    }

    private void UpdateActionState()
    {
        var confirmations = EraseCheck.IsChecked == true && TetherCheck.IsChecked == true && OwnershipCheck.IsChecked == true;
        var toolchainReady = _tools.MissingRequiredTools().Count == 0
            && File.Exists(Path.Combine(_tools.ResourcesDirectory, "sep_racer.bin"))
            && File.Exists(Path.Combine(_tools.ResourcesDirectory, "kpf.bin"));
        StartDowngradeButton.IsEnabled = !_busy && confirmations && toolchainReady && _inspection?.IsValid == true;
        TetherBootButton.IsEnabled = !_busy && toolchainReady && File.Exists(PtePathBox.Text);
        CancelButton.IsEnabled = _busy;
    }
}
