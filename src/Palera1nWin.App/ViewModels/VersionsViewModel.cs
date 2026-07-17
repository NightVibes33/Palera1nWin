using System.Collections.ObjectModel;
using Palera1nWin.App.Mvvm;
using Palera1nWin.App.Services;
using Palera1nWin.Core.Releases;
using Palera1nWin.Core.Services;
using Palera1nWin.Core.Settings;

namespace Palera1nWin.App.ViewModels;

public sealed class ReleaseItemViewModel
{
    public required string TagName { get; init; }
    public required string DisplayName { get; init; }
    public required string PublishedText { get; init; }
    public required string AssetName { get; init; }
    public required long AssetSize { get; init; }

    public string SizeText => AssetSize > 0
        ? $"{AssetSize / (1024.0 * 1024.0):F1} MB"
        : "Unknown size";
}

public sealed class VersionsViewModel : ObservableObject, IDisposable
{
    private readonly AppSettings _settings;
    private readonly LogService _logService;
    private readonly Action<string> _setStatus;
    private readonly GitHubReleasesClient _releasesClient = new();
    private readonly WslProvisionService _wslProvisionService;
    private ReleaseItemViewModel? _selectedRelease;
    private string _downloadStatus = "No download in progress.";
    private bool _isBusy;

    public VersionsViewModel(AppSettings settings, LogService logService, Action<string> setStatus)
    {
        _settings = settings;
        _logService = logService;
        _setStatus = setStatus;
        _wslProvisionService = new WslProvisionService(settings.WslDistro);

        RefreshCommand = new AsyncRelayCommand(RefreshAsync, () => !IsBusy);
        DownloadCommand = new AsyncRelayCommand(DownloadSelectedAsync, () => !IsBusy && SelectedRelease is not null);

        BundledVersionNote = "The bundled toolchain ships with palera1n v2.3. Selecting a release and clicking "
            + "Download installs that version into WSL (/opt/palera1n/palera1n) so the jailbreak uses it.";
    }

    public string BundledVersionNote { get; }

    public ObservableCollection<ReleaseItemViewModel> Releases { get; } = [];

    public ReleaseItemViewModel? SelectedRelease
    {
        get => _selectedRelease;
        set
        {
            if (SetProperty(ref _selectedRelease, value) && value is not null)
            {
                _settings.SelectedReleaseTag = value.TagName;
                DownloadCommand.RaiseCanExecuteChanged();
                if (Releases.Count > 0)
                {
                    DownloadStatus = $"Loaded {Releases.Count} releases. Selected: {value.TagName}.";
                }
            }
        }
    }

    public string DownloadStatus
    {
        get => _downloadStatus;
        private set => SetProperty(ref _downloadStatus, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                RefreshCommand.RaiseCanExecuteChanged();
                DownloadCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public AsyncRelayCommand RefreshCommand { get; }

    public AsyncRelayCommand DownloadCommand { get; }

    public async Task RefreshAsync()
    {
        IsBusy = true;
        DownloadStatus = "Fetching releases from GitHub...";
        _setStatus("Fetching palera1n releases...");

        try
        {
            var releases = await _releasesClient.GetReleasesAsync(forceRefresh: true).ConfigureAwait(true);
            Releases.Clear();

            foreach (var release in releases.OrderByDescending(r => r.PublishedAt))
            {
                var asset = release.PreferredLinuxBinary;
                Releases.Add(new ReleaseItemViewModel
                {
                    TagName = release.TagName,
                    DisplayName = string.IsNullOrWhiteSpace(release.Name) ? release.TagName : release.Name,
                    PublishedText = release.PublishedAt.ToString("yyyy-MM-dd"),
                    AssetName = asset?.Name ?? "(no linux binary)",
                    AssetSize = asset?.Size ?? 0,
                });
            }

            var preferred = Releases.FirstOrDefault(r =>
                string.Equals(r.TagName, _settings.SelectedReleaseTag, StringComparison.OrdinalIgnoreCase))
                ?? Releases.FirstOrDefault();

            SelectedRelease = preferred;
            DownloadStatus = Releases.Count == 0
                ? "No releases returned from GitHub."
                : $"Loaded {Releases.Count} releases. Selected: {SelectedRelease?.TagName}.";
            _setStatus($"Loaded {Releases.Count} palera1n releases.");
            _logService.Append("versions", DownloadStatus);
        }
        catch (Exception ex)
        {
            DownloadStatus = $"Failed to fetch releases: {ex.Message}";
            _setStatus("Release fetch failed.");
            _logService.Append("versions", ex.Message, isError: true);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task DownloadSelectedAsync()
    {
        if (SelectedRelease is null)
        {
            return;
        }

        IsBusy = true;
        var destination = Path.Combine(AppSettings.RuntimeDirectory, SelectedRelease.AssetName);
        DownloadStatus = $"Downloading {SelectedRelease.TagName}...";
        _setStatus($"Downloading {SelectedRelease.TagName}...");

        try
        {
            var progress = new Progress<Core.Models.ProgressEventArgs>(e =>
            {
                System.Windows.Application.Current?.Dispatcher.Invoke(() =>
                {
                    DownloadStatus = e.Message;
                });
            });

            await _releasesClient.DownloadReleaseBinaryAsync(
                SelectedRelease.TagName,
                destination,
                progress).ConfigureAwait(true);

            _settings.SelectedReleaseTag = SelectedRelease.TagName;
            _settings.Save();

            _logService.Append("versions", $"Downloaded {SelectedRelease.TagName} to {destination}");

            // Downloading is not enough — the jailbreak runs /opt/palera1n/palera1n
            // inside WSL. Install the downloaded binary there so the selected version
            // is the one that actually runs (otherwise it stays on the bundled 2.3).
            DownloadStatus = $"Installing {SelectedRelease.TagName} into WSL...";
            _setStatus($"Activating {SelectedRelease.TagName} in WSL...");
            try
            {
                var installResult = await _wslProvisionService.InstallPalera1nBinaryAsync(
                    destination,
                    distro: null,
                    onOutput: line => System.Windows.Application.Current?.Dispatcher.Invoke(() =>
                    {
                        DownloadStatus = line;
                        _logService.Append("versions", line);
                    })).ConfigureAwait(true);

                var active = await _wslProvisionService.GetInstalledVersionAsync().ConfigureAwait(true);
                if (installResult.Succeeded)
                {
                    DownloadStatus = active is not null
                        ? $"Active in WSL: {active}  (selected {SelectedRelease.TagName})"
                        : $"{SelectedRelease.TagName} installed into WSL (/opt/palera1n/palera1n).";
                    _setStatus($"palera1n {SelectedRelease.TagName} is now active.");
                }
                else
                {
                    DownloadStatus = $"Downloaded {SelectedRelease.TagName}, but WSL install returned exit "
                        + $"{installResult.ExitCode}. Provision WSL from the Setup tab, then re-download.";
                    _setStatus("Downloaded, WSL activation incomplete.");
                }

                _logService.Append("versions", DownloadStatus);
            }
            catch (Exception wslEx)
            {
                DownloadStatus = $"Downloaded {SelectedRelease.TagName} to {destination}. "
                    + $"Could not activate in WSL ({wslEx.Message}). Install a WSL distro / Provision WSL, then re-download.";
                _setStatus("Downloaded; WSL not available to activate.");
                _logService.Append("versions", DownloadStatus, isError: true);
            }
        }
        catch (Exception ex)
        {
            DownloadStatus = $"Download failed: {ex.Message}";
            _setStatus("Release download failed.");
            _logService.Append("versions", ex.Message, isError: true);
        }
        finally
        {
            IsBusy = false;
        }
    }

    public void Dispose()
    {
        _releasesClient.Dispose();
    }
}
