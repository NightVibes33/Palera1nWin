using System.Collections.ObjectModel;
using Microsoft.Win32;
using Palera1nWin.App.Mvvm;
using Palera1nWin.App.Services;
using Palera1nWin.Core.Drivers;
using Palera1nWin.Core.Services;
using Palera1nWin.Core.Settings;
using Palera1nWin.Core.Usb;
using Palera1nWin.Core.Util;

namespace Palera1nWin.App.ViewModels;

public sealed class DoctorCheckItem
{
    public required string Name { get; init; }
    public required string Detail { get; init; }
    public required bool Passed { get; init; }

    public string StatusText => Passed ? "OK" : "Needs attention";
}

public sealed class SetupViewModel : ObservableObject
{
    private readonly AppSettings _settings;
    private readonly AppleUsbMonitor _monitor;
    private readonly LogService _logService;
    private readonly Action<string> _setStatus;
    private readonly UsbipdService _usbipdService = new();
    private readonly WslService _wslService;
    private readonly WslProvisionService _wslProvisionService;
    private string _toolchainRoot = string.Empty;
    private string _doctorSummary = "Run environment checks to validate your setup.";
    private bool _isBusy;

    public SetupViewModel(
        AppSettings settings,
        AppleUsbMonitor monitor,
        LogService logService,
        Action<string> setStatus)
    {
        _settings = settings;
        _monitor = monitor;
        _logService = logService;
        _setStatus = setStatus;
        _wslService = new WslService(settings.WslDistro);
        _wslProvisionService = new WslProvisionService(_wslService);

        _toolchainRoot = settings.ToolchainRoot;

        BrowseToolchainCommand = new RelayCommand(BrowseToolchain);
        RunDoctorCommand = new AsyncRelayCommand(RunDoctorAsync, () => !IsBusy);
        InstallDriversCommand = new AsyncRelayCommand(InstallDriversAsync, () => !IsBusy);
        UninstallUsbDkCommand = new AsyncRelayCommand(UninstallUsbDkAsync, () => !IsBusy);
        ProvisionWslCommand = new AsyncRelayCommand(ProvisionWslAsync, () => !IsBusy);
        RefreshUsbDkCommand = new RelayCommand(RefreshUsbDkState);
    }

    public ObservableCollection<DoctorCheckItem> DoctorChecks { get; } = [];

    public string ToolchainRoot
    {
        get => _toolchainRoot;
        set
        {
            if (SetProperty(ref _toolchainRoot, value))
            {
                _settings.ToolchainRoot = value.Trim().TrimEnd('\\', '/');
            }
        }
    }

    public string DoctorSummary
    {
        get => _doctorSummary;
        private set => SetProperty(ref _doctorSummary, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                RunDoctorCommand.RaiseCanExecuteChanged();
                InstallDriversCommand.RaiseCanExecuteChanged();
                UninstallUsbDkCommand.RaiseCanExecuteChanged();
            }
        }
    }

    private bool _usbDkDetected;
    public bool UsbDkDetected
    {
        get => _usbDkDetected;
        private set => SetProperty(ref _usbDkDetected, value);
    }

    public RelayCommand BrowseToolchainCommand { get; }

    public AsyncRelayCommand RunDoctorCommand { get; }

    public AsyncRelayCommand InstallDriversCommand { get; }

    public AsyncRelayCommand UninstallUsbDkCommand { get; }

    public AsyncRelayCommand ProvisionWslCommand { get; }

    public RelayCommand RefreshUsbDkCommand { get; }

    private void BrowseToolchain()
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Select Palera1n-Windows toolchain root",
            InitialDirectory = Directory.Exists(ToolchainRoot) ? ToolchainRoot : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        };

        if (dialog.ShowDialog() == true && !string.IsNullOrWhiteSpace(dialog.FolderName))
        {
            ToolchainRoot = dialog.FolderName;
            _settings.Save();
            _setStatus("Toolchain root updated.");
        }
    }

    private async Task RunDoctorAsync()
    {
        IsBusy = true;
        DoctorChecks.Clear();
        _setStatus("Running environment checks...");

        try
        {
            _settings.Clamp();
            _settings.Save();

            var resolved = Paths.ResolveToolchainRoot(ToolchainRoot);
            IReadOnlyList<string> missing = Array.Empty<string>();
            var toolchainOk = resolved is not null && Paths.ValidateToolchain(resolved, out missing);
            DoctorChecks.Add(new DoctorCheckItem
            {
                Name = "Toolchain root",
                Detail = toolchainOk
                    ? resolved!
                    : missing.Count > 0
                        ? $"Missing: {string.Join(", ", missing)}"
                        : "Toolchain path is not configured or does not exist.",
                Passed = toolchainOk,
            });

            var openRa1nPath = resolved is null ? string.Empty : Paths.GetOpenRa1nExecutable(resolved);
            DoctorChecks.Add(new DoctorCheckItem
            {
                Name = "openra1n.exe",
                Detail = string.IsNullOrWhiteSpace(openRa1nPath) ? "(unknown)" : openRa1nPath,
                Passed = !string.IsNullOrWhiteSpace(openRa1nPath) && File.Exists(openRa1nPath),
            });

            var usbipdOk = _usbipdService.IsAvailable;
            DoctorChecks.Add(new DoctorCheckItem
            {
                Name = "usbipd-win",
                Detail = usbipdOk
                    ? _usbipdService.ExecutablePath ?? "Found"
                    : "usbipd.exe not found in PATH or Program Files.",
                Passed = usbipdOk,
            });

            string? distro = null;
            try
            {
                distro = await _wslService.ResolveDistroAsync().ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                distro = null;
                _logService.Append("setup", $"WSL check failed: {ex.Message}", isError: true);
            }

            DoctorChecks.Add(new DoctorCheckItem
            {
                Name = "WSL distro",
                Detail = distro ?? "No WSL distro detected. Install Ubuntu or set WSL distro in Settings.",
                Passed = !string.IsNullOrWhiteSpace(distro),
            });

            // WSL runtime provisioning: is /opt/palera1n/pln-run.sh installed?
            bool wslProvisioned = false;
            if (!string.IsNullOrWhiteSpace(distro))
            {
                try
                {
                    wslProvisioned = await _wslProvisionService
                        .IsProvisionedAsync(distro)
                        .ConfigureAwait(true);
                }
                catch (Exception ex)
                {
                    _logService.Append("setup", $"WSL provision check failed: {ex.Message}", isError: true);
                }
            }

            DoctorChecks.Add(new DoctorCheckItem
            {
                Name = "WSL palera1n runtime",
                Detail = string.IsNullOrWhiteSpace(distro)
                    ? "Install a WSL distro first."
                    : wslProvisioned
                        ? $"Provisioned in {distro} (/opt/palera1n/pln-run.sh)."
                        : $"Not provisioned in {distro}. Click 'Provision WSL' below (one-time).",
                Passed = wslProvisioned,
            });

            DoctorChecks.Add(new DoctorCheckItem
            {
                Name = "Administrator",
                Detail = Elevation.IsAdmin()
                    ? "Running elevated (driver install available)."
                    : "Not elevated. Driver install may prompt for UAC.",
                Passed = true,
            });

            _monitor.PollNow();
            var device = _monitor.CurrentDevice;
            DoctorChecks.Add(new DoctorCheckItem
            {
                Name = "Apple USB device",
                Detail = device.IsPresent
                    ? $"{device.Name} ({DeviceModeFormatting.GetLabel(device.Mode)})"
                    : "No Apple USB device detected. Connect a device to verify drivers.",
                Passed = device.IsPresent,
            });

            // UsbDk conflict check
            var usbDkInstalled = DriverInstaller.FindUsbDkUninstaller() is not null;
            UsbDkDetected = usbDkInstalled;
            DoctorChecks.Add(new DoctorCheckItem
            {
                Name = "UsbDk filter",
                Detail = usbDkInstalled
                    ? "UsbDk is installed — conflicts with usbipd. Use 'Uninstall UsbDk' below."
                    : "Not installed (good).",
                Passed = !usbDkInstalled,
            });

            var passCount = DoctorChecks.Count(c => c.Passed);
            DoctorSummary = $"{passCount}/{DoctorChecks.Count} checks passed.";
            _setStatus(DoctorSummary);
            _logService.Append("setup", DoctorSummary);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task InstallDriversAsync()
    {
        IsBusy = true;
        _setStatus("Installing libusbK drivers...");
        _logService.Append("setup", "Starting driver installation...");

        try
        {
            _settings.Clamp();
            _settings.Save();

            _monitor.PollNow();
            var device = _monitor.CurrentDevice;
            if (!device.IsPresent)
            {
                var message = "Connect an Apple device in DFU, Recovery, or Pongo mode before installing drivers.";
                _logService.Append("setup", message, isError: true);
                DoctorSummary = message;
                _setStatus("No device for driver install.");
                return;
            }

            var installer = new DriverInstaller(_settings, _monitor);
            var progress = new Progress<Core.Models.ProgressEventArgs>(e =>
            {
                System.Windows.Application.Current?.Dispatcher.Invoke(() =>
                {
                    DoctorSummary = e.Message;
                    _logService.Append("driver", e.Message, isError: false);
                });
            });

            var result = await installer.EnsureLibusbKAsync(
                device.ProductId,
                progress).ConfigureAwait(true);

            switch (result)
            {
                case DriverInstallResult.AlreadyOk:
                    DoctorSummary = "libusbK driver is already active.";
                    break;
                case DriverInstallResult.Installed:
                    DoctorSummary = "libusbK driver installed successfully.";
                    break;
                case DriverInstallResult.NeedsManualZadig:
                    DoctorSummary = "Automatic install unavailable. Use Open Zadig on the Device tab.";
                    break;
                default:
                    DoctorSummary = "Driver installation failed. See Logs for details.";
                    break;
            }

            _setStatus(DoctorSummary);
        }
        catch (Exception ex)
        {
            DoctorSummary = $"Driver install error: {ex.Message}";
            _logService.Append("setup", ex.Message, isError: true);
            _setStatus("Driver install failed.");
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void RefreshUsbDkState()
    {
        UsbDkDetected = DriverInstaller.FindUsbDkUninstaller() is not null;
    }

    private async Task UninstallUsbDkAsync()
    {
        var confirmed = System.Windows.MessageBox.Show(
            "UsbDk is a USB filter driver that conflicts with usbipd-win and prevents the jailbreak from working reliably.\n\n" +
            "This will uninstall UsbDk (a UAC prompt will appear). A reboot is recommended afterwards.\n\n" +
            "Continue?",
            "Uninstall UsbDk",
            System.Windows.MessageBoxButton.YesNo,
            System.Windows.MessageBoxImage.Question);

        if (confirmed != System.Windows.MessageBoxResult.Yes)
        {
            return;
        }

        IsBusy = true;
        _setStatus("Uninstalling UsbDk...");
        _logService.Append("setup", "Starting UsbDk uninstall...");

        try
        {
            var installer = new DriverInstaller(_settings, _monitor);
            var progress = new Progress<Core.Models.ProgressEventArgs>(e =>
            {
                System.Windows.Application.Current?.Dispatcher.Invoke(() =>
                {
                    DoctorSummary = e.Message;
                    _logService.Append("driver", e.Message, isError: e.IsError);
                });
            });

            var ok = await installer.UninstallUsbDkAsync(progress).ConfigureAwait(true);

            UsbDkDetected = DriverInstaller.FindUsbDkUninstaller() is not null;
            DoctorSummary = ok
                ? "UsbDk uninstalled. Reboot recommended."
                : "UsbDk uninstall failed. Try Settings → Apps → UsbDk → Uninstall.";
            _setStatus(DoctorSummary);
            _logService.Append("setup", DoctorSummary, isError: !ok);

            System.Windows.MessageBox.Show(
                DoctorSummary,
                "Uninstall UsbDk",
                System.Windows.MessageBoxButton.OK,
                ok ? System.Windows.MessageBoxImage.Information : System.Windows.MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            DoctorSummary = $"UsbDk uninstall error: {ex.Message}";
            _logService.Append("setup", ex.Message, isError: true);
            _setStatus("UsbDk uninstall failed.");
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task ProvisionWslAsync()
    {
        IsBusy = true;
        _setStatus("Provisioning WSL runtime...");
        _logService.Append("setup", "Starting WSL provisioning...");

        try
        {
            _settings.Clamp();
            _settings.Save();

            var resolved = Paths.ResolveToolchainRoot(ToolchainRoot);
            if (resolved is null)
            {
                var msg = "Toolchain root is not configured or does not exist. Set it in Settings first.";
                DoctorSummary = msg;
                _logService.Append("setup", msg, isError: true);
                _setStatus("Provision WSL: toolchain missing.");
                return;
            }

            DoctorSummary = "Provisioning WSL (installing runtime + palera1n binary)... this can take a few minutes on first run.";
            Action<string> progress = line =>
            {
                System.Windows.Application.Current?.Dispatcher.Invoke(() =>
                {
                    DoctorSummary = line;
                    _logService.Append("wsl-provision", line, isError: false);
                });
            };

            var result = await _wslProvisionService
                .ProvisionAsync(resolved, _settings.WslDistro, progress)
                .ConfigureAwait(true);

            var provisioned = result.Succeeded
                && await _wslProvisionService.IsProvisionedAsync(_settings.WslDistro).ConfigureAwait(true);

            DoctorSummary = provisioned
                ? "WSL provisioned. palera1n runtime installed in /opt/palera1n/."
                : $"WSL provisioning failed (exit {result.ExitCode}). See Logs for details.";
            _setStatus(provisioned ? "WSL provisioned." : "WSL provisioning failed.");
            _logService.Append("setup", DoctorSummary, isError: !provisioned);

            System.Windows.MessageBox.Show(
                DoctorSummary,
                "Provision WSL",
                System.Windows.MessageBoxButton.OK,
                provisioned ? System.Windows.MessageBoxImage.Information : System.Windows.MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            DoctorSummary = $"WSL provisioning error: {ex.Message}";
            _logService.Append("setup", ex.Message, isError: true);
            _setStatus("WSL provisioning failed.");
        }
        finally
        {
            IsBusy = false;
        }
    }
}
