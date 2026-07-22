using Palera1nWin.App.Mvvm;
using Palera1nWin.App.Services;
using Palera1nWin.Core.Settings;

namespace Palera1nWin.App.ViewModels;

public sealed class LogsViewModel : ObservableObject
{
    private readonly LogService _logService;
    private readonly Action<string> _setStatus;

    public LogsViewModel(LogService logService, Action<string> setStatus)
    {
        _logService = logService;
        _setStatus = setStatus;

        ClearCommand = new RelayCommand(Clear);
        OpenFolderCommand = new RelayCommand(OpenFolder);
    }

    public LogService LogService => _logService;

    public RelayCommand ClearCommand { get; }

    public RelayCommand OpenFolderCommand { get; }

    private void Clear()
    {
        _logService.Clear();
        _setStatus("Log cleared.");
    }

    private void OpenFolder()
    {
        Directory.CreateDirectory(AppSettings.LogsDirectory);
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(AppSettings.LogsDirectory)
        {
            UseShellExecute = true,
        });
        _setStatus("Opened logs folder.");
    }
}
