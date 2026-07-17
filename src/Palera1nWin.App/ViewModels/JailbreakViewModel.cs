using System.Collections.ObjectModel;
using Palera1nWin.App.Mvvm;
using Palera1nWin.App.Services;
using Palera1nWin.Core.Models;
using Palera1nWin.Core.Orchestration;
using Palera1nWin.Core.Settings;
using Palera1nWin.Core.Usb;

namespace Palera1nWin.App.ViewModels;

public sealed class JailbreakViewModel : ObservableObject
{
    private readonly AppSettings _settings;
    private readonly AppleUsbMonitor _monitor;
    private readonly LogService _logService;
    private readonly Action<string> _setStatus;
    private CancellationTokenSource? _runCts;
    private bool _isRunning;
    private double _progress;
    private string _progressText = "Ready to start.";
    private string _deviceModeLabel = "No device";
    private string _deviceModeBadgeBackground = "#26343F52";
    private string _deviceModeBadgeForeground = "#9AA3B8";
    private bool _passcodeAcknowledged;
    private string _selectedJailbreakMode = "rootless";
    private string _logPreview = string.Empty;

    public JailbreakViewModel(
        AppSettings settings,
        AppleUsbMonitor monitor,
        LogService logService,
        Action<string> setStatus)
    {
        _settings = settings;
        _monitor = monitor;
        _logService = logService;
        _setStatus = setStatus;

        _selectedJailbreakMode = settings.JailbreakMode;
        _passcodeAcknowledged = settings.PasscodeAcknowledged;

        SafeMode = settings.SafeMode;
        VerboseBoot = settings.VerboseBoot;
        AutoInstallDrivers = settings.AutoInstallDrivers;

        StartJailbreakCommand = new AsyncRelayCommand(StartJailbreakAsync, () => !IsRunning && PasscodeAcknowledged);
        CancelJailbreakCommand = new RelayCommand(CancelJailbreak, () => IsRunning);

        _monitor.DeviceChanged += OnDeviceChanged;
        UpdateDeviceBadge(_monitor.CurrentDevice);
        RefreshLogPreview();
        _logService.LineAdded += (_, _) => RefreshLogPreview();
    }

    public AsyncRelayCommand StartJailbreakCommand { get; }

    public RelayCommand CancelJailbreakCommand { get; }

    public IReadOnlyList<string> JailbreakModes { get; } = ["rootless", "rootful"];

    public string SelectedJailbreakMode
    {
        get => _selectedJailbreakMode;
        set
        {
            if (SetProperty(ref _selectedJailbreakMode, value))
            {
                _settings.JailbreakMode = AppSettings.NormalizeJailbreakMode(value);
            }
        }
    }

    public bool SafeMode
    {
        get => _settings.SafeMode;
        set
        {
            if (_settings.SafeMode != value)
            {
                _settings.SafeMode = value;
                OnPropertyChanged();
            }
        }
    }

    public bool VerboseBoot
    {
        get => _settings.VerboseBoot;
        set
        {
            if (_settings.VerboseBoot != value)
            {
                _settings.VerboseBoot = value;
                OnPropertyChanged();
            }
        }
    }

    public bool AutoInstallDrivers
    {
        get => _settings.AutoInstallDrivers;
        set
        {
            if (_settings.AutoInstallDrivers != value)
            {
                _settings.AutoInstallDrivers = value;
                OnPropertyChanged();
            }
        }
    }

    public bool PasscodeAcknowledged
    {
        get => _passcodeAcknowledged;
        set
        {
            if (SetProperty(ref _passcodeAcknowledged, value))
            {
                _settings.PasscodeAcknowledged = value;
                _settings.Save();
                OnPropertyChanged(nameof(CanStartJailbreak));
                StartJailbreakCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool IsRunning
    {
        get => _isRunning;
        private set
        {
            if (SetProperty(ref _isRunning, value))
            {
                StartJailbreakCommand.RaiseCanExecuteChanged();
                CancelJailbreakCommand.RaiseCanExecuteChanged();
                OnPropertyChanged(nameof(CanStartJailbreak));
                IsRunningChanged?.Invoke(this, value);
            }
        }
    }

    /// <summary>
    /// Raised when IsRunning changes. MainViewModel uses this to pause/resume the global driver watchdog.
    /// </summary>
    public event EventHandler<bool>? IsRunningChanged;

    public bool CanStartJailbreak => !IsRunning && PasscodeAcknowledged;

    public double Progress
    {
        get => _progress;
        private set => SetProperty(ref _progress, value);
    }

    public string ProgressText
    {
        get => _progressText;
        private set => SetProperty(ref _progressText, value);
    }

    public string DeviceModeLabel
    {
        get => _deviceModeLabel;
        private set => SetProperty(ref _deviceModeLabel, value);
    }

    public string DeviceModeBadgeBackground
    {
        get => _deviceModeBadgeBackground;
        private set => SetProperty(ref _deviceModeBadgeBackground, value);
    }

    public string DeviceModeBadgeForeground
    {
        get => _deviceModeBadgeForeground;
        private set => SetProperty(ref _deviceModeBadgeForeground, value);
    }

    public string LogPreview
    {
        get => _logPreview;
        private set => SetProperty(ref _logPreview, value);
    }

    public ObservableCollection<string> ChecklistItems { get; } =
    [
        "Remove your device passcode before jailbreaking.",
        "Use a USB-A cable or a quality USB-C data cable.",
        "Put the device in DFU mode when prompted.",
        "Do not unplug the device during the jailbreak flow.",
    ];

    private async Task StartJailbreakAsync()
    {
        _settings.Clamp();
        _settings.Save();

        _runCts = new CancellationTokenSource();
        IsRunning = true;
        Progress = 0;
        ProgressText = "Starting jailbreak...";
        _setStatus("Jailbreak in progress...");

        try
        {
            using var orchestrator = new JailbreakOrchestrator(_settings, new WpfUserPromptService());
            orchestrator.LogReceived += OnOrchestratorLog;
            orchestrator.ProgressChanged += OnOrchestratorProgress;

            var stage = await orchestrator.RunAsync(_runCts.Token).ConfigureAwait(true);

            orchestrator.LogReceived -= OnOrchestratorLog;
            orchestrator.ProgressChanged -= OnOrchestratorProgress;

            switch (stage)
            {
                case JailbreakStage.Completed:
                    ProgressText = "Jailbreak completed successfully.";
                    _setStatus("Jailbreak completed.");
                    break;
                case JailbreakStage.Cancelled:
                    ProgressText = "Jailbreak cancelled.";
                    _setStatus("Jailbreak cancelled.");
                    break;
                case JailbreakStage.PasscodeAcknowledgement:
                    ProgressText = "Passcode acknowledgement is required.";
                    _setStatus("Acknowledge passcode removal to continue.");
                    PasscodeAcknowledged = false;
                    break;
                default:
                    ProgressText = "Jailbreak failed. See Logs for details.";
                    _setStatus("Jailbreak failed.");
                    break;
            }
        }
        catch (OperationCanceledException)
        {
            ProgressText = "Jailbreak cancelled.";
            _setStatus("Jailbreak cancelled.");
        }
        catch (Exception ex)
        {
            _logService.Append("jailbreak", ex.Message, isError: true);
            ProgressText = $"Jailbreak error: {ex.Message}";
            _setStatus("Jailbreak error.");
        }
        finally
        {
            IsRunning = false;
            _runCts?.Dispose();
            _runCts = null;
            RefreshLogPreview();
        }
    }

    private void CancelJailbreak()
    {
        _runCts?.Cancel();
        ProgressText = "Cancelling...";
    }

    private void OnOrchestratorLog(object? sender, LogLine line)
    {
        System.Windows.Application.Current?.Dispatcher.Invoke(() => _logService.Append(line));
    }

    private void OnOrchestratorProgress(object? sender, ProgressEventArgs e)
    {
        System.Windows.Application.Current?.Dispatcher.Invoke(() =>
        {
            Progress = e.Percent ?? 0;
            ProgressText = e.Message;
        });
    }

    private void OnDeviceChanged(object? sender, AppleUsbDevice device)
    {
        System.Windows.Application.Current?.Dispatcher.Invoke(() => UpdateDeviceBadge(device));
    }

    private void UpdateDeviceBadge(AppleUsbDevice device)
    {
        DeviceModeLabel = DeviceModeFormatting.GetLabel(device.Mode);
        DeviceModeBadgeBackground = DeviceModeFormatting.GetBadgeBackground(device.Mode);
        DeviceModeBadgeForeground = DeviceModeFormatting.GetBadgeForeground(device.Mode);
    }

    private void RefreshLogPreview()
    {
        var recent = _logService.GetRecent(12);
        LogPreview = recent.Count == 0
            ? "Log output will appear here during jailbreak."
            : string.Join(Environment.NewLine, recent);
    }
}
