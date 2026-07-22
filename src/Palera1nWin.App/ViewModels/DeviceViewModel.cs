using Palera1nWin.App.Mvvm;
using Palera1nWin.App.Services;
using Palera1nWin.Core.Drivers;
using Palera1nWin.Core.Models;
using Palera1nWin.Core.Settings;
using Palera1nWin.Core.Usb;
using Palera1nWin.Core.Util;

namespace Palera1nWin.App.ViewModels;

public sealed class DeviceViewModel : ObservableObject, IDisposable
{
    private readonly AppleUsbMonitor _monitor;
    private readonly AppSettings _settings;
    private readonly Action<string> _setStatus;
    private readonly LogService? _logService;
    private readonly HardwareOperationCoordinator _hardwareOperations;
    private readonly bool _ownsCoordinator;
    private string _deviceName = "No Apple USB device detected";
    private string _usbMode = "None";
    private string _vidPid = "-";
    private string _instanceId = "-";
    private string _driverService = "-";
    private string _busId = "-";
    private string _deviceStatus = "-";
    private string _modeBadgeBackground = "#26343F52";
    private string _modeBadgeForeground = "#9AA3B8";
    private bool _isBusy;
    private bool _disposed;

    public DeviceViewModel(AppleUsbMonitor monitor, AppSettings settings, Action<string> setStatus)
        : this(monitor, settings, setStatus, null, null) { }

    public DeviceViewModel(
        AppleUsbMonitor monitor,
        AppSettings settings,
        Action<string> setStatus,
        LogService? logService,
        HardwareOperationCoordinator? hardwareOperations = null)
    {
        _monitor = monitor;
        _settings = settings;
        _setStatus = setStatus;
        _logService = logService;
        _hardwareOperations = hardwareOperations ?? new HardwareOperationCoordinator();
        _ownsCoordinator = hardwareOperations is null;

        RefreshCommand = new RelayCommand(Refresh, () => !IsBusy);
        OpenZadigCommand = new AsyncRelayCommand(OpenZadigAsync, CanMutateDrivers);
        FixWindowsDriversCommand = new AsyncRelayCommand(FixWindowsDriversAsync, CanMutateDrivers);

        _monitor.DeviceChanged += Monitor_DeviceChanged;
        _hardwareOperations.StateChanged += HardwareOperations_StateChanged;
        ApplyDevice(_monitor.CurrentDevice);
    }

    public RelayCommand RefreshCommand { get; }
    public AsyncRelayCommand OpenZadigCommand { get; }
    public AsyncRelayCommand FixWindowsDriversCommand { get; }

    public string DeviceName { get => _deviceName; private set => SetProperty(ref _deviceName, value); }
    public string UsbMode { get => _usbMode; private set => SetProperty(ref _usbMode, value); }
    public string VidPid { get => _vidPid; private set => SetProperty(ref _vidPid, value); }
    public string InstanceId { get => _instanceId; private set => SetProperty(ref _instanceId, value); }
    public string DriverService { get => _driverService; private set => SetProperty(ref _driverService, value); }
    public string BusId { get => _busId; private set => SetProperty(ref _busId, value); }
    public string DeviceStatus { get => _deviceStatus; private set => SetProperty(ref _deviceStatus, value); }
    public string ModeBadgeBackground { get => _modeBadgeBackground; private set => SetProperty(ref _modeBadgeBackground, value); }
    public string ModeBadgeForeground { get => _modeBadgeForeground; private set => SetProperty(ref _modeBadgeForeground, value); }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (!SetProperty(ref _isBusy, value)) return;
            RefreshCommand.RaiseCanExecuteChanged();
            OpenZadigCommand.RaiseCanExecuteChanged();
            FixWindowsDriversCommand.RaiseCanExecuteChanged();
        }
    }

    private bool CanMutateDrivers() => !IsBusy && !_hardwareOperations.Current.IsBusy;

    private void HardwareOperations_StateChanged(object? sender, HardwareOperationState e)
    {
        System.Windows.Application.Current?.Dispatcher.Invoke(() =>
        {
            OpenZadigCommand.RaiseCanExecuteChanged();
            FixWindowsDriversCommand.RaiseCanExecuteChanged();
        });
    }

    private void Monitor_DeviceChanged(object? sender, AppleUsbDevice device) =>
        System.Windows.Application.Current?.Dispatcher.Invoke(() => ApplyDevice(device));

    private void Refresh()
    {
        _monitor.PollNow();
        ApplyDevice(_monitor.CurrentDevice);
        _setStatus("Device list refreshed.");
    }

    private async Task OpenZadigAsync()
    {
        HardwareOperationLease? lease = null;
        try
        {
            lease = await _hardwareOperations.AcquireAsync(
                HardwareOperationKind.DriverRepair,
                "Manual Zadig driver repair").ConfigureAwait(true);

            var toolchain = Paths.ResolveToolchainRoot(_settings.ToolchainRoot);
            if (toolchain is null) throw new InvalidOperationException("Toolchain root is not configured.");
            var zadig = Paths.GetZadigExecutable(toolchain);
            if (!File.Exists(zadig)) throw new FileNotFoundException("Zadig was not found.", zadig);

            using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = zadig,
                UseShellExecute = true,
            }) ?? throw new InvalidOperationException("Zadig did not start.");
            _setStatus("Zadig is open; other hardware operations remain locked until it closes.");
            await process.WaitForExitAsync().ConfigureAwait(true);
            _monitor.PollNow();
            _setStatus("Zadig closed; driver state refreshed.");
        }
        catch (HardwareOperationBusyException ex)
        {
            ShowError(ex.Message, warning: true);
        }
        catch (Exception ex)
        {
            _logService?.Append("driver", $"Zadig launch failed: {ex}", isError: true);
            ShowError($"Failed to launch Zadig: {ex.Message}");
        }
        finally
        {
            if (lease is not null) await lease.DisposeAsync();
        }
    }

    private async Task FixWindowsDriversAsync()
    {
        var confirmed = System.Windows.MessageBox.Show(
            "This restores the default Apple driver for the connected Apple USB devices. " +
            "The device may briefly disconnect and reconnect. Continue?",
            "Fix Windows Drivers",
            System.Windows.MessageBoxButton.YesNo,
            System.Windows.MessageBoxImage.Question);
        if (confirmed != System.Windows.MessageBoxResult.Yes) return;

        HardwareOperationLease? lease = null;
        IsBusy = true;
        try
        {
            lease = await _hardwareOperations.AcquireAsync(
                HardwareOperationKind.DriverRepair,
                "Restoring default Apple USB drivers").ConfigureAwait(true);
            _setStatus("Fixing Windows drivers...");

            var installer = new DriverInstaller(_settings, _monitor);
            var progress = new Progress<ProgressEventArgs>(e =>
            {
                _setStatus(e.Message);
                _logService?.Append("driver", e.Message, isError: e.IsError);
            });
            var result = await installer.RestoreAppleDriversAsync(progress).ConfigureAwait(true);
            _setStatus(result.Message);
            _logService?.Append("driver", $"Fix Windows Drivers: {result.Message}", isError: !result.Succeeded);
            Refresh();
            System.Windows.MessageBox.Show(
                result.Message,
                "Fix Windows Drivers",
                System.Windows.MessageBoxButton.OK,
                result.Succeeded ? System.Windows.MessageBoxImage.Information : System.Windows.MessageBoxImage.Warning);
        }
        catch (HardwareOperationBusyException ex)
        {
            ShowError(ex.Message, warning: true);
        }
        catch (Exception ex)
        {
            _logService?.Append("driver", $"Fix Windows Drivers error: {ex}", isError: true);
            _setStatus("Fix Windows Drivers failed.");
            ShowError($"Failed to restore drivers: {ex.Message}");
        }
        finally
        {
            if (lease is not null) await lease.DisposeAsync();
            IsBusy = false;
        }
    }

    private static void ShowError(string message, bool warning = false) =>
        System.Windows.MessageBox.Show(
            message,
            "Palera1nWin",
            System.Windows.MessageBoxButton.OK,
            warning ? System.Windows.MessageBoxImage.Warning : System.Windows.MessageBoxImage.Error);

    private void ApplyDevice(AppleUsbDevice device)
    {
        if (!device.IsPresent)
        {
            DeviceName = "No Apple USB device detected";
            UsbMode = "None";
            VidPid = InstanceId = DriverService = BusId = DeviceStatus = "-";
            ModeBadgeBackground = DeviceModeFormatting.GetBadgeBackground(DeviceMode.None);
            ModeBadgeForeground = DeviceModeFormatting.GetBadgeForeground(DeviceMode.None);
            return;
        }

        DeviceName = string.IsNullOrWhiteSpace(device.Name) ? "Apple USB device" : device.Name;
        UsbMode = DeviceModeFormatting.GetLabel(device.Mode);
        VidPid = $"VID_{device.VendorId:X4}:PID_{device.ProductId:X4}";
        InstanceId = device.DeviceId;
        DriverService = string.IsNullOrWhiteSpace(device.Service) ? "(unknown)" : device.Service;
        BusId = string.IsNullOrWhiteSpace(device.BusId) ? "(not shared via usbipd)" : device.BusId;
        DeviceStatus = string.IsNullOrWhiteSpace(device.Status) ? "(unknown)" : device.Status;
        ModeBadgeBackground = DeviceModeFormatting.GetBadgeBackground(device.Mode);
        ModeBadgeForeground = DeviceModeFormatting.GetBadgeForeground(device.Mode);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _monitor.DeviceChanged -= Monitor_DeviceChanged;
        _hardwareOperations.StateChanged -= HardwareOperations_StateChanged;
        if (_ownsCoordinator) _hardwareOperations.Dispose();
    }
}
