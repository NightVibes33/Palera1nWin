using Palera1nWin.App.Mvvm;
using Palera1nWin.Core.Settings;

namespace Palera1nWin.App.ViewModels;

public sealed class SettingsViewModel : ObservableObject
{
    private readonly AppSettings _settings;
    private readonly Action<string> _setStatus;
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

    public SettingsViewModel(AppSettings settings, Action<string> setStatus)
    {
        _settings = settings;
        _setStatus = setStatus;

        LoadFromSettings();
        SaveCommand = new RelayCommand(Save);
    }

    public IReadOnlyList<string> JailbreakModes { get; } = ["rootless", "rootful"];

    public string ToolchainRoot
    {
        get => _toolchainRoot;
        set => SetProperty(ref _toolchainRoot, value);
    }

    public string WslDistro
    {
        get => _wslDistro;
        set => SetProperty(ref _wslDistro, value);
    }

    public string SelectedReleaseTag
    {
        get => _selectedReleaseTag;
        set => SetProperty(ref _selectedReleaseTag, value);
    }

    public string JailbreakMode
    {
        get => _jailbreakMode;
        set => SetProperty(ref _jailbreakMode, AppSettings.NormalizeJailbreakMode(value));
    }

    public bool SafeMode
    {
        get => _safeMode;
        set => SetProperty(ref _safeMode, value);
    }

    public bool VerboseBoot
    {
        get => _verboseBoot;
        set => SetProperty(ref _verboseBoot, value);
    }

    public bool DebugLogging
    {
        get => _debugLogging;
        set => SetProperty(ref _debugLogging, value);
    }

    public bool AutoInstallDrivers
    {
        get => _autoInstallDrivers;
        set => SetProperty(ref _autoInstallDrivers, value);
    }

    public bool CheckUpdates
    {
        get => _checkUpdates;
        set => SetProperty(ref _checkUpdates, value);
    }

    public bool PreferUsbA
    {
        get => _preferUsbA;
        set => SetProperty(ref _preferUsbA, value);
    }

    public RelayCommand SaveCommand { get; }

    public void ReloadFromSettings() => LoadFromSettings();

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
        _settings.ToolchainRoot = ToolchainRoot.Trim().TrimEnd('\\', '/');
        _settings.WslDistro = WslDistro.Trim();
        _settings.SelectedReleaseTag = SelectedReleaseTag.Trim();
        _settings.JailbreakMode = JailbreakMode;
        _settings.SafeMode = SafeMode;
        _settings.VerboseBoot = VerboseBoot;
        _settings.DebugLogging = DebugLogging;
        _settings.AutoInstallDrivers = AutoInstallDrivers;
        _settings.CheckUpdates = CheckUpdates;
        _settings.PreferUsbA = PreferUsbA;
        _settings.Save();
        _setStatus("Settings saved.");
    }
}
