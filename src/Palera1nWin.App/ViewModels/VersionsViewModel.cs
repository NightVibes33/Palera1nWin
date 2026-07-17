using System.Collections.ObjectModel;
using Palera1nWin.App.Mvvm;
using Palera1nWin.App.Services;
using Palera1nWin.Core.Releases;
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
    private ReleaseItemViewModel? _selectedRelease;
    private string _downloadStatus = "No download in progress.";
    private bool _isBusy;

    public VersionsViewModel(AppSettings settings, LogService logService, Action<string> setStatus)
    {
        _settings = settings;
        _logService = logService;
        _setStatus = setStatus;

        RefreshCommand = new AsyncRelayCommand(RefreshAsync, () => !IsBusy);
        DownloadCommand = new AsyncRelayCommand(DownloadSelectedAsync, () => !IsBusy && SelectedRelease is not null);

        BundledVersionNote = "The bundled hybrid toolchain currently ships with palera1n v2.3. "
            + "Download a release here to update the WSL runtime binary.";
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
                : $"Loaded {Releases.Count} releases.";
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

            DownloadStatus = $"Saved to {destination}";
            _setStatus("Release download completed.");
            _logService.Append("versions", DownloadStatus);
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
