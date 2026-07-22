using Palera1nWin.App.Mvvm;
using Palera1nWin.App.Services;
using Palera1nWin.Core.Drivers;
using Palera1nWin.Core.Models;
using Palera1nWin.Core.Settings;
using Palera1nWin.Core.Usb;
using Palera1nWin.Core.Util;

namespace Palera1nWin.App.ViewModels;

public sealed class DeviceViewModel : ObservableObject
{
    private readonly AppleUsbMonitor _monitor;
    private readonly AppSettings _settings;
    private readonly Action<string> _setStatus;
    private readonly LogService? _logService;
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

    public DeviceViewModel(AppleUsbMonitor monitor, AppSettings settings, Action<string> setStatus)
        : this(monitor, settings, setStatus, logService: null)
    {
    }

    public DeviceViewModel(
        AppleUsbMonitor monitor,
        AppSettings settings,
        Action<string> setStatus,
        LogService? logService)
    {
        _monitor = monitor;
        _settings = settings;
        _setStatus = setStatus;
        _logService = logService;

        RefreshCommand = new RelayCommand(Refresh);
        OpenZadigCommand = new RelayCommand(OpenZadig);
        FixWindowsDriversCommand = new AsyncRelayCommand(FixWindowsDriversAsync, () => !IsBusy);

        _monitor.DeviceChanged += (_, device) =>
            System.Windows.Application.Current?.Dispatcher.Invoke(() => ApplyDevice(device));

        ApplyDevice(_monitor.CurrentDevice);
    }

    public RelayCommand RefreshCommand { get; }

    public RelayCommand OpenZadigCommand { get; }

    public AsyncRelayCommand FixWindowsDriversCommand { get; }

    public string DeviceName
    {
        get => _deviceName;
        private set => SetProperty(ref _deviceName, value);
    }

    public string UsbMode
    {
        get => _usbMode;
        private set => SetProperty(ref _usbMode, value);
    }

    public string VidPid
    {
        get => _vidPid;
        private set => SetProperty(ref _vidPid, value);
    }

    public string InstanceId
    {
        get => _instanceId;
        private set => SetProperty(ref _instanceId, value);
    }

    public string DriverService
    {
        get => _driverService;
        private set => SetProperty(ref _driverService, value);
    }

    public string BusId
    {
        get => _busId;
        private set => SetProperty(ref _busId, value);
    }

    public string DeviceStatus
    {
        get => _deviceStatus;
        private set => SetProperty(ref _deviceStatus, value);
    }

    public string ModeBadgeBackground
    {
        get => _modeBadgeBackground;
        private set => SetProperty(ref _modeBadgeBackground, value);
    }

    public string ModeBadgeForeground
    {
        get => _modeBadgeForeground;
        private set => SetProperty(ref _modeBadgeForeground, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                FixWindowsDriversCommand.RaiseCanExecuteChanged();
            }
        }
    }

    private void Refresh()
    {
        _monitor.PollNow();
        ApplyDevice(_monitor.CurrentDevice);
        _setStatus("Device list refreshed.");
    }

    private void OpenZadig()
    {
        var toolchain = Paths.ResolveToolchainRoot(_settings.ToolchainRoot);
        if (toolchain is null)
        {
            System.Windows.MessageBox.Show(
                "Toolchain root is not configured. Set it in Setup or Settings first.",
                "Palera1nWin",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Warning);
            return;
        }

        var zadig = Paths.GetZadigExecutable(toolchain);
        if (!File.Exists(zadig))
        {
            System.Windows.MessageBox.Show(
                $"Zadig was not found at:\n{zadig}",
                "Palera1nWin",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Warning);
            return;
        }

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = zadig,
                UseShellExecute = true,
            });
            _setStatus("Opened Zadig.");
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(
                $"Failed to launch Zadig: {ex.Message}",
                "Palera1nWin",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Error);
        }
    }

    private async Task FixWindowsDriversAsync()
    {
        var confirmed = System.Windows.MessageBox.Show(
            "This will remove the libusbK / WinUSB driver from all connected Apple USB devices " +
            "and restore the default Apple driver (so iTunes / Apple Devices can see the phone again).\n\n" +
            "The device may briefly disconnect and reconnect. Continue?",
            "Fix Windows Drivers",
            System.Windows.MessageBoxButton.YesNo,
            System.Windows.MessageBoxImage.Question);

        if (confirmed != System.Windows.MessageBoxResult.Yes)
        {
            return;
        }

        IsBusy = true;
        _setStatus("Fixing Windows drivers...");

        try
        {
            var installer = new DriverInstaller(_settings, _monitor);
            var progress = new Progress<ProgressEventArgs>(e =>
            {
                System.Windows.Application.Current?.Dispatcher.Invoke(() =>
                {
                    _setStatus(e.Message);
                    _logService?.Append("driver", e.Message, isError: e.IsError);
                });
            });

            var result = await installer.RestoreAppleDriversAsync(progress)
                .ConfigureAwait(true);

            _setStatus(result.Message);
            _logService?.Append("driver", $"Fix Windows Drivers: {result.Message}", isError: !result.Succeeded);

            Refresh();

            System.Windows.MessageBox.Show(
                result.Message,
                "Fix Windows Drivers",
                System.Windows.MessageBoxButton.OK,
                result.Succeeded ? System.Windows.MessageBoxImage.Information : System.Windows.MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            _logService?.Append("driver", $"Fix Windows Drivers error: {ex.Message}", isError: true);
            _setStatus("Fix Windows Drivers failed.");
            System.Windows.MessageBox.Show(
                $"Failed to restore drivers: {ex.Message}",
                "Palera1nWin",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Error);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void ApplyDevice(AppleUsbDevice device)
    {
        if (!device.IsPresent)
        {
            DeviceName = "No Apple USB device detected";
            UsbMode = "None";
            VidPid = "-";
            InstanceId = "-";
            DriverService = "-";
            BusId = "-";
            DeviceStatus = "-";
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
}
