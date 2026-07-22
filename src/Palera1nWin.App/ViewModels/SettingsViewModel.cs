using Palera1nWin.App.Mvvm;
using Palera1nWin.App.Services;
using Palera1nWin.Core.Settings;

namespace Palera1nWin.App.ViewModels;

public sealed class SettingsViewModel : ObservableObject, IDisposable
{
    private readonly AppSettings _settings;
    private readonly Action<string> _setStatus;
    private readonly HardwareOperationCoordinator _hardwareOperations;
    private readonly bool _ownsCoordinator;
    private string _toolchainRoot = string.Empty;
    private string _wslDistro = "Ubuntu";
    private string _selectedReleaseTag = "v2.3";
    private string _jailbreakMode = "rootless";
    private bool _safeMode;
    private bool _verboseBoot = true;
    private bool _debugLogging = true;
    private bool _autoInstallDrivers = true;
    private bool _checkUpdates = true;
    private bool _preferUsbA = true;
    private bool _disposed;

    public SettingsViewModel(
        AppSettings settings,
        Action<string> setStatus,
        HardwareOperationCoordinator? hardwareOperations = null)
    {
        _settings = settings;
        _setStatus = setStatus;
        _hardwareOperations = hardwareOperations ?? new HardwareOperationCoordinator();
        _ownsCoordinator = hardwareOperations is null;
        LoadFromSettings();
        SaveCommand = new RelayCommand(Save, () => !_hardwareOperations.Current.IsBusy);
        _hardwareOperations.StateChanged += HardwareOperations_StateChanged;
    }

    public IReadOnlyList<string> JailbreakModes { get; } = ["rootless", "rootful"];
    public string ToolchainRoot { get => _toolchainRoot; set => SetProperty(ref _toolchainRoot, value); }
    public string WslDistro { get => _wslDistro; set => SetProperty(ref _wslDistro, value); }
    public string SelectedReleaseTag { get => _selectedReleaseTag; set => SetProperty(ref _selectedReleaseTag, value); }
    public string JailbreakMode { get => _jailbreakMode; set => SetProperty(ref _jailbreakMode, AppSettings.NormalizeJailbreakMode(value)); }
    public bool SafeMode { get => _safeMode; set => SetProperty(ref _safeMode, value); }
    public bool VerboseBoot { get => _verboseBoot; set => SetProperty(ref _verboseBoot, value); }
    public bool DebugLogging { get => _debugLogging; set => SetProperty(ref _debugLogging, value); }
    public bool AutoInstallDrivers { get => _autoInstallDrivers; set => SetProperty(ref _autoInstallDrivers, value); }
    public bool CheckUpdates { get => _checkUpdates; set => SetProperty(ref _checkUpdates, value); }
    public bool PreferUsbA { get => _preferUsbA; set => SetProperty(ref _preferUsbA, value); }
    public RelayCommand SaveCommand { get; }

    public void ReloadFromSettings() => LoadFromSettings();

    private void HardwareOperations_StateChanged(object? sender, HardwareOperationState e) =>
        System.Windows.Application.Current?.Dispatcher.Invoke(SaveCommand.RaiseCanExecuteChanged);

    private void LoadFromSettings()
    {
        _settings.Clamp();
        ToolchainRoot = _settings.ToolchainRoot;
        WslDistro = _settings.WslDistro;
        SelectedReleaseTag = _settings.SelectedReleaseTag;
        JailbreakMode = _settings.JailbreakMode;
        SafeMode = _settings.SafeMode;
        VerboseBoot = _settings.VerboseBoot;
        DebugLogging = _settings.DebugLogging;
        AutoInstallDrivers = _settings.AutoInstallDrivers;
        CheckUpdates = _settings.CheckUpdates;
        PreferUsbA = _settings.PreferUsbA;
    }

    private void Save()
    {
        if (_hardwareOperations.Current.IsBusy)
        {
            _setStatus("Finish the active hardware operation before changing runtime settings.");
            return;
        }

        var distro = WslDistro.Trim();
        if (distro.Any(char.IsControl))
            throw new InvalidOperationException("The WSL distro name contains invalid control characters.");
        var tag = SelectedReleaseTag.Trim();
        if (tag.Any(char.IsControl))
            throw new InvalidOperationException("The release tag contains invalid control characters.");

        _settings.ToolchainRoot = ToolchainRoot.Trim().TrimEnd('\\', '/');
        _settings.WslDistro = distro;
        _settings.SelectedReleaseTag = tag;
        _settings.JailbreakMode = JailbreakMode;
        _settings.SafeMode = SafeMode;
        _settings.VerboseBoot = VerboseBoot;
        _settings.DebugLogging = DebugLogging;
        _settings.AutoInstallDrivers = AutoInstallDrivers;
        _settings.CheckUpdates = CheckUpdates;
        _settings.PreferUsbA = PreferUsbA;
        _settings.Save();
        _setStatus("Settings saved. Setup and Versions will use the new WSL distro immediately.");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _hardwareOperations.StateChanged -= HardwareOperations_StateChanged;
        if (_ownsCoordinator) _hardwareOperations.Dispose();
    }
}
