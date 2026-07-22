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

public sealed class SetupViewModel : ObservableObject, IDisposable
{
    private readonly AppSettings _settings;
    private readonly AppleUsbMonitor _monitor;
    private readonly LogService _logService;
    private readonly Action<string> _setStatus;
    private readonly HardwareOperationCoordinator _hardwareOperations;
    private readonly bool _ownsCoordinator;
    private readonly UsbipdService _usbipdService = new();
    private string _toolchainRoot = string.Empty;
    private string _doctorSummary = "Run environment checks to validate your setup.";
    private bool _isBusy;
    private bool _usbDkDetected;
    private bool _disposed;

    public SetupViewModel(
        AppSettings settings,
        AppleUsbMonitor monitor,
        LogService logService,
        Action<string> setStatus,
        HardwareOperationCoordinator? hardwareOperations = null)
    {
        _settings = settings;
        _monitor = monitor;
        _logService = logService;
        _setStatus = setStatus;
        _hardwareOperations = hardwareOperations ?? new HardwareOperationCoordinator();
        _ownsCoordinator = hardwareOperations is null;
        _toolchainRoot = settings.ToolchainRoot;

        BrowseToolchainCommand = new RelayCommand(BrowseToolchain, CanEditSetup);
        RunDoctorCommand = new AsyncRelayCommand(RunDoctorAsync, CanRunSetupAction);
        InstallDriversCommand = new AsyncRelayCommand(InstallDriversAsync, CanRunSetupAction);
        UninstallUsbDkCommand = new AsyncRelayCommand(UninstallUsbDkAsync, CanRunSetupAction);
        ProvisionWslCommand = new AsyncRelayCommand(ProvisionWslAsync, CanRunSetupAction);
        RefreshUsbDkCommand = new RelayCommand(RefreshUsbDkState, CanEditSetup);
        _hardwareOperations.StateChanged += HardwareOperations_StateChanged;
    }

    public ObservableCollection<DoctorCheckItem> DoctorChecks { get; } = [];

    public string ToolchainRoot
    {
        get => _toolchainRoot;
        set
        {
            if (SetProperty(ref _toolchainRoot, value))
                _settings.ToolchainRoot = value.Trim().TrimEnd('\\', '/');
        }
    }

    public string DoctorSummary { get => _doctorSummary; private set => SetProperty(ref _doctorSummary, value); }
    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (!SetProperty(ref _isBusy, value)) return;
            RaiseCommandStates();
        }
    }
    public bool UsbDkDetected { get => _usbDkDetected; private set => SetProperty(ref _usbDkDetected, value); }

    public RelayCommand BrowseToolchainCommand { get; }
    public AsyncRelayCommand RunDoctorCommand { get; }
    public AsyncRelayCommand InstallDriversCommand { get; }
    public AsyncRelayCommand UninstallUsbDkCommand { get; }
    public AsyncRelayCommand ProvisionWslCommand { get; }
    public RelayCommand RefreshUsbDkCommand { get; }

    private bool CanRunSetupAction() => !IsBusy && !_hardwareOperations.Current.IsBusy;
    private bool CanEditSetup() => !IsBusy && !_hardwareOperations.Current.IsBusy;

    private void HardwareOperations_StateChanged(object? sender, HardwareOperationState e) =>
        System.Windows.Application.Current?.Dispatcher.Invoke(RaiseCommandStates);

    private void RaiseCommandStates()
    {
        BrowseToolchainCommand.RaiseCanExecuteChanged();
        RunDoctorCommand.RaiseCanExecuteChanged();
        InstallDriversCommand.RaiseCanExecuteChanged();
        UninstallUsbDkCommand.RaiseCanExecuteChanged();
        ProvisionWslCommand.RaiseCanExecuteChanged();
        RefreshUsbDkCommand.RaiseCanExecuteChanged();
    }

    private void BrowseToolchain()
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Select Palera1n-Windows toolchain root",
            InitialDirectory = Directory.Exists(ToolchainRoot)
                ? ToolchainRoot
                : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        };
        if (dialog.ShowDialog() != true || string.IsNullOrWhiteSpace(dialog.FolderName)) return;
        ToolchainRoot = dialog.FolderName;
        _settings.Save();
        _setStatus("Toolchain root updated.");
    }

    private async Task RunDoctorAsync()
    {
        HardwareOperationLease? lease = null;
        IsBusy = true;
        DoctorChecks.Clear();
        try
        {
            lease = await _hardwareOperations.AcquireAsync(
                HardwareOperationKind.Diagnostics,
                "Checking toolchain, WSL and Apple USB state").ConfigureAwait(true);
            _settings.Clamp();
            _settings.Save();

            var resolved = Paths.ResolveToolchainRoot(ToolchainRoot);
            IReadOnlyList<string> missing = [];
            var toolchainOk = resolved is not null && Paths.ValidateToolchain(resolved, out missing);
            Add("Toolchain root", toolchainOk ? resolved! : missing.Count > 0
                ? $"Missing: {string.Join(", ", missing.Select(Path.GetFileName))}"
                : "Toolchain path is missing.", toolchainOk);

            Add("openra1n.exe",
                resolved is null ? "Toolchain unresolved." : Paths.GetOpenRa1nExecutable(resolved),
                resolved is not null && File.Exists(Paths.GetOpenRa1nExecutable(resolved)));
            Add("usbipd-win", _usbipdService.IsAvailable
                ? _usbipdService.ExecutablePath ?? "Found"
                : "usbipd.exe not found.", _usbipdService.IsAvailable);

            var wsl = new WslService(_settings.WslDistro);
            var distro = await wsl.ResolveDistroAsync().ConfigureAwait(true);
            Add("WSL distro", distro ?? "No WSL distro detected.", distro is not null);

            var provisioned = false;
            string? activeVersion = null;
            if (distro is not null)
            {
                var provision = new WslProvisionService(wsl);
                provisioned = await provision.IsProvisionedAsync(distro).ConfigureAwait(true);
                if (provisioned) activeVersion = await provision.GetInstalledVersionAsync(distro).ConfigureAwait(true);
            }
            Add("WSL palera1n runtime",
                provisioned ? $"{activeVersion ?? "palera1n"} active in {distro}." : "Click Provision WSL.",
                provisioned);
            Add("Administrator", Elevation.IsAdmin() ? "Running elevated." : "Restart as administrator.", Elevation.IsAdmin());

            _monitor.PollNow();
            var devices = SafeScanDevices();
            Add("Apple USB device",
                devices.Count == 0 ? "No Apple USB device detected."
                : devices.Count == 1 ? devices[0].ToString()
                : $"{devices.Count} Apple devices detected. Disconnect all but the target before hardware operations.",
                devices.Count == 1);

            UsbDkDetected = DriverInstaller.FindUsbDkUninstaller() is not null;
            Add("UsbDk filter", UsbDkDetected ? "Installed and conflicts with usbipd." : "Not installed.", !UsbDkDetected);

            var passed = DoctorChecks.Count(check => check.Passed);
            DoctorSummary = $"{passed}/{DoctorChecks.Count} checks passed.";
            _setStatus(DoctorSummary);
            _logService.Append("setup", DoctorSummary);
        }
        catch (Exception ex)
        {
            DoctorSummary = $"Doctor failed: {ex.Message}";
            _logService.Append("setup", ex.ToString(), isError: true);
        }
        finally
        {
            if (lease is not null) await lease.DisposeAsync();
            IsBusy = false;
        }

        void Add(string name, string detail, bool passed) => DoctorChecks.Add(new DoctorCheckItem
        {
            Name = name,
            Detail = detail,
            Passed = passed,
        });
    }

    private async Task InstallDriversAsync()
    {
        HardwareOperationLease? lease = null;
        IsBusy = true;
        try
        {
            lease = await _hardwareOperations.AcquireAsync(
                HardwareOperationKind.DriverRepair,
                "Installing the exact DFU/Pongo USB driver").ConfigureAwait(true);
            _monitor.PollNow();
            var devices = SafeScanDevices();
            if (devices.Count != 1)
                throw new InvalidOperationException("Connect exactly one Apple device before changing a USB driver.");
            var device = devices[0];
            if (device.ProductId is not (0x1227 or 0x4141))
                throw new InvalidOperationException("Automatic libusbK installation is allowed only for Apple DFU (1227) or PongoOS (4141), never normal/recovery mode.");

            var installer = new DriverInstaller(_settings, _monitor);
            var progress = new Progress<Core.Models.ProgressEventArgs>(e =>
            {
                DoctorSummary = e.Message;
                _logService.Append("driver", e.Message, e.IsError);
            });
            var result = await installer.EnsureLibusbKAsync(device.ProductId, progress).ConfigureAwait(true);
            DoctorSummary = result switch
            {
                DriverInstallResult.AlreadyOk => "The required driver is already active.",
                DriverInstallResult.Installed => "The required driver installed and re-enumerated successfully.",
                DriverInstallResult.NeedsManualZadig => "Automatic repair did not verify. Use the locked Zadig action on Device.",
                _ => "Driver installation failed. See Logs.",
            };
            _setStatus(DoctorSummary);
        }
        catch (Exception ex)
        {
            DoctorSummary = $"Driver install error: {ex.Message}";
            _logService.Append("setup", ex.ToString(), isError: true);
            _setStatus("Driver install failed.");
        }
        finally
        {
            if (lease is not null) await lease.DisposeAsync();
            IsBusy = false;
        }
    }

    private void RefreshUsbDkState() => UsbDkDetected = DriverInstaller.FindUsbDkUninstaller() is not null;

    private async Task UninstallUsbDkAsync()
    {
        if (System.Windows.MessageBox.Show(
                "This removes the UsbDk filter driver. Other hardware operations will remain locked until the uninstaller exits. Continue?",
                "Uninstall UsbDk",
                System.Windows.MessageBoxButton.YesNo,
                System.Windows.MessageBoxImage.Question) != System.Windows.MessageBoxResult.Yes) return;

        HardwareOperationLease? lease = null;
        IsBusy = true;
        try
        {
            lease = await _hardwareOperations.AcquireAsync(
                HardwareOperationKind.DriverRepair,
                "Uninstalling UsbDk").ConfigureAwait(true);
            var installer = new DriverInstaller(_settings, _monitor);
            var progress = new Progress<Core.Models.ProgressEventArgs>(e =>
            {
                DoctorSummary = e.Message;
                _logService.Append("driver", e.Message, e.IsError);
            });
            var ok = await installer.UninstallUsbDkAsync(progress).ConfigureAwait(true);
            RefreshUsbDkState();
            DoctorSummary = ok ? "UsbDk uninstalled. Reboot Windows before the next jailbreak." : "UsbDk uninstall failed.";
            _setStatus(DoctorSummary);
            _logService.Append("setup", DoctorSummary, !ok);
        }
        catch (Exception ex)
        {
            DoctorSummary = $"UsbDk uninstall error: {ex.Message}";
            _logService.Append("setup", ex.ToString(), true);
        }
        finally
        {
            if (lease is not null) await lease.DisposeAsync();
            IsBusy = false;
        }
    }

    private async Task ProvisionWslAsync()
    {
        HardwareOperationLease? lease = null;
        IsBusy = true;
        try
        {
            lease = await _hardwareOperations.AcquireAsync(
                HardwareOperationKind.WslProvision,
                "Provisioning the packaged palera1n WSL runtime").ConfigureAwait(true);
            _settings.Clamp();
            _settings.Save();
            var resolved = Paths.ResolveToolchainRoot(ToolchainRoot)
                           ?? throw new InvalidOperationException("Toolchain root does not exist.");
            var wsl = new WslService(_settings.WslDistro);
            var distro = await wsl.ResolveDistroAsync().ConfigureAwait(true)
                         ?? throw new InvalidOperationException("No WSL distro is installed.");
            var service = new WslProvisionService(wsl);
            var downloaded = Path.Combine(AppSettings.RuntimeDirectory, "palera1n-linux-x86_64");
            var preferred = File.Exists(downloaded) && File.Exists(downloaded + ".verified.json") ? downloaded : null;
            var result = await service.ProvisionAsync(
                resolved,
                distro,
                line =>
                {
                    DoctorSummary = line;
                    _logService.Append("wsl-provision", line);
                },
                preferBinaryPath: preferred).ConfigureAwait(true);
            var provisioned = result.Succeeded && await service.IsProvisionedAsync(distro).ConfigureAwait(true);
            DoctorSummary = provisioned
                ? $"WSL provisioned in {distro}: {await service.GetInstalledVersionAsync(distro).ConfigureAwait(true) ?? "palera1n"}."
                : $"WSL provisioning failed with exit {result.ExitCode}.";
            _setStatus(DoctorSummary);
            _logService.Append("setup", DoctorSummary, !provisioned);
        }
        catch (Exception ex)
        {
            DoctorSummary = $"WSL provisioning error: {ex.Message}";
            _logService.Append("setup", ex.ToString(), true);
            _setStatus("WSL provisioning failed.");
        }
        finally
        {
            if (lease is not null) await lease.DisposeAsync();
            IsBusy = false;
        }
    }

    private IReadOnlyList<Core.Models.AppleUsbDevice> SafeScanDevices()
    {
        try { return _monitor.ScanDevices().Where(device => device.IsPresent).ToArray(); }
        catch { return []; }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _hardwareOperations.StateChanged -= HardwareOperations_StateChanged;
        if (_ownsCoordinator) _hardwareOperations.Dispose();
    }
}
