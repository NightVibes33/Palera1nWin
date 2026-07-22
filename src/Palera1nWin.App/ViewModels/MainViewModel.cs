using System.Reflection;
using Palera1nWin.App.Mvvm;
using Palera1nWin.App.Services;
using Palera1nWin.Core.Settings;
using Palera1nWin.Core.Usb;
using Palera1nWin.Core.Util;

namespace Palera1nWin.App.ViewModels;

public sealed class MainViewModel : ObservableObject, IDisposable
{
    private readonly AppSettings _settings;
    private readonly AppleUsbMonitor _monitor;
    private readonly LogService _logService;
    private readonly HardwareOperationCoordinator _hardwareOperations;
    private string _statusText = "Ready";

    public MainViewModel()
    {
        _settings = AppSettings.Load();
        _logService = new LogService();
        _monitor = new AppleUsbMonitor();
        _hardwareOperations = new HardwareOperationCoordinator();

        _logService.Append("app", "Palera1nWin started.");
        _logService.Append("driver", "Idle driver mutation is disabled. Drivers are changed only inside an exclusive hardware operation.");

        Jailbreak = new JailbreakViewModel(_settings, _monitor, _logService, _hardwareOperations, SetStatus);
        Device = new DeviceViewModel(_monitor, _settings, SetStatus, _logService);
        Versions = new VersionsViewModel(_settings, _logService, SetStatus);
        Setup = new SetupViewModel(_settings, _monitor, _logService, SetStatus);
        Logs = new LogsViewModel(_logService, SetStatus);
        SettingsVm = new SettingsViewModel(_settings, SetStatus);
        About = new AboutViewModel();

        RestartAsAdminCommand = new RelayCommand(RestartAsAdmin);
        OpenLogsFolderCommand = new RelayCommand(OpenLogsFolder);

        _monitor.DeviceChanged += OnDeviceChanged;
        _hardwareOperations.StateChanged += OnHardwareOperationChanged;
        UpdateDeviceStatus(_monitor.CurrentDevice);

        if (_settings.CheckUpdates)
        {
            _ = Versions.RefreshAsync();
        }
    }

    public JailbreakViewModel Jailbreak { get; }

    public DeviceViewModel Device { get; }

    public VersionsViewModel Versions { get; }

    public SetupViewModel Setup { get; }

    public LogsViewModel Logs { get; }

    public SettingsViewModel SettingsVm { get; }

    public AboutViewModel About { get; }

    public HardwareOperationCoordinator HardwareOperations => _hardwareOperations;

    public HardwareOperationState ActiveHardwareOperation => _hardwareOperations.Current;

    public bool IsHardwareBusy => ActiveHardwareOperation.IsBusy;

    public RelayCommand RestartAsAdminCommand { get; }

    public RelayCommand OpenLogsFolderCommand { get; }

    public event Action<int>? NavigateRequested;

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public bool IsElevated => Elevation.IsAdmin();

    public string VersionText
    {
        get
        {
            var version = Assembly.GetExecutingAssembly().GetName().Version;
            return version is null ? "v1.0.0" : $"v{version.ToString(3)}";
        }
    }

    public void NavigateTo(int tabIndex) => NavigateRequested?.Invoke(tabIndex);

    public void SetStatusText(string text) => SetStatus(text);

    public void AppendLog(string source, string message, bool isError = false) =>
        _logService.Append(source, message, isError);

    private void SetStatus(string text) =>
        System.Windows.Application.Current?.Dispatcher.Invoke(() => StatusText = text);

    private void RestartAsAdmin()
    {
        if (Elevation.RelaunchAsAdmin(string.Empty))
        {
            System.Windows.Application.Current.Shutdown();
        }
    }

    private void OpenLogsFolder()
    {
        Directory.CreateDirectory(AppSettings.LogsDirectory);
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(AppSettings.LogsDirectory)
        {
            UseShellExecute = true,
        });
    }

    private void OnDeviceChanged(object? sender, Core.Models.AppleUsbDevice device)
    {
        System.Windows.Application.Current?.Dispatcher.Invoke(() => UpdateDeviceStatus(device));
    }

    private void UpdateDeviceStatus(Core.Models.AppleUsbDevice device)
    {
        if (_hardwareOperations.Current.IsBusy)
        {
            return;
        }

        if (device.IsPresent)
        {
            StatusText = $"Device: {DeviceModeFormatting.GetLabel(device.Mode)} ({device.Name})";
        }
        else
        {
            StatusText = "Ready";
        }
    }

    private void OnHardwareOperationChanged(object? sender, HardwareOperationState state)
    {
        System.Windows.Application.Current?.Dispatcher.Invoke(() =>
        {
            OnPropertyChanged(nameof(ActiveHardwareOperation));
            OnPropertyChanged(nameof(IsHardwareBusy));
            StatusText = state.IsBusy
                ? $"{state.Operation}: {state.Detail ?? "hardware operation active"}"
                : "Ready";
        });
    }

    public void Dispose()
    {
        _hardwareOperations.StateChanged -= OnHardwareOperationChanged;
        Versions.Dispose();
        _logService.Dispose();
        _monitor.DeviceChanged -= OnDeviceChanged;
        _monitor.Dispose();
    }
}
