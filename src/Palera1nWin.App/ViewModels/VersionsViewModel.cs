using System.Collections.ObjectModel;
using Palera1nWin.App.Mvvm;
using Palera1nWin.App.Services;
using Palera1nWin.Core.Releases;
using Palera1nWin.Core.Services;
using Palera1nWin.Core.Settings;
using Palera1nWin.Core.Util;

namespace Palera1nWin.App.ViewModels;

public sealed class ReleaseItemViewModel : ObservableObject
{
    public required string TagName { get; init; }
    public required string DisplayName { get; init; }
    public required string PublishedText { get; init; }
    public required string AssetName { get; init; }
    public required long AssetSize { get; init; }

    public string SizeText => AssetSize > 0
        ? $"{AssetSize / (1024.0 * 1024.0):F1} MB"
        : "Unknown size";

    private PongoCompatibilityResult _pongo = new(PongoCompatibilityLevel.Unknown, null, null);

    /// <summary>
    /// Pongo/PongoOS compatibility with the bundled openra1n. Set from the static,
    /// pre-tested tag map when the list is refreshed, and refined to a definitive
    /// answer (from the real binary) once this release is downloaded.
    /// </summary>
    public PongoCompatibilityResult Pongo
    {
        get => _pongo;
        set
        {
            if (SetProperty(ref _pongo, value))
            {
                OnPropertyChanged(nameof(PongoBadgeText));
                OnPropertyChanged(nameof(PongoIsWarning));
            }
        }
    }

    public string PongoBadgeText => Pongo.Level switch
    {
        PongoCompatibilityLevel.Compatible => "Pongo OK",
        PongoCompatibilityLevel.Incompatible => "Pongo mismatch",
        _ => "Pongo unverified",
    };

    public bool PongoIsWarning => Pongo.Level == PongoCompatibilityLevel.Incompatible;

    public bool PongoIsUnknown => Pongo.Level == PongoCompatibilityLevel.Unknown;
}

public sealed class VersionsViewModel : ObservableObject, IDisposable
{
    private readonly AppSettings _settings;
    private readonly LogService _logService;
    private readonly Action<string> _setStatus;
    private readonly GitHubReleasesClient _releasesClient = new();
    private readonly WslProvisionService _wslProvisionService;
    private readonly string? _bundledPongoVersion;
    private ReleaseItemViewModel? _selectedRelease;
    private string _downloadStatus = "No download in progress.";
    private bool _isBusy;

    public VersionsViewModel(AppSettings settings, LogService logService, Action<string> setStatus)
    {
        _settings = settings;
        _logService = logService;
        _setStatus = setStatus;
        _wslProvisionService = new WslProvisionService(settings.WslDistro);

        // Determine our bundled openra1n's embedded PongoOS once, from the actual
        // toolchain binary (not hardcoded) so this self-corrects if the toolchain
        // is ever rebuilt with a newer PongoOS. See PongoCompatibility for how/why.
        var toolchain = Paths.ResolveToolchainRoot(settings.ToolchainRoot);
        _bundledPongoVersion = toolchain is not null
            ? PongoCompatibility.ExtractEmbeddedPongoVersion(Paths.GetOpenRa1nExecutable(toolchain))
            : null;

        RefreshCommand = new AsyncRelayCommand(RefreshAsync, () => !IsBusy);
        DownloadCommand = new AsyncRelayCommand(DownloadSelectedAsync, () => !IsBusy && SelectedRelease is not null);

        BundledVersionNote = _bundledPongoVersion is not null
            ? $"The bundled openra1n embeds PongoOS {_bundledPongoVersion}. Selecting a release and clicking Download " +
              "installs that palera1n version into WSL (/opt/palera1n/palera1n). Releases whose own PongoOS build " +
              "differs are flagged below — the device-side Pongo upload always uses openra1n's fixed image."
            : "The bundled toolchain ships with palera1n v2.3. Selecting a release and clicking "
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
                    Pongo = PongoCompatibility.CheckTag(release.TagName, _bundledPongoVersion),
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

            // Definitive Pongo compatibility check against the actual downloaded
            // binary (supersedes the static tag-map guess from RefreshAsync — this
            // is what makes newly-released, not-yet-mapped versions self-classify).
            var pongoCheck = PongoCompatibility.CheckBinary(destination, _bundledPongoVersion);
            SelectedRelease.Pongo = pongoCheck;
            if (pongoCheck.Level == PongoCompatibilityLevel.Incompatible)
            {
                _logService.Append("versions", $"Pongo compatibility warning: {pongoCheck.Summary}", isError: true);
            }

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
                var pongoWarning = pongoCheck.Level == PongoCompatibilityLevel.Incompatible
                    ? $" ⚠ {pongoCheck.Summary}"
                    : string.Empty;

                if (installResult.Succeeded)
                {
                    DownloadStatus = (active is not null
                        ? $"Active in WSL: {active}  (selected {SelectedRelease.TagName})"
                        : $"{SelectedRelease.TagName} installed into WSL (/opt/palera1n/palera1n).") + pongoWarning;
                    _setStatus($"palera1n {SelectedRelease.TagName} is now active.");
                }
                else
                {
                    DownloadStatus = $"Downloaded {SelectedRelease.TagName}, but WSL install returned exit "
                        + $"{installResult.ExitCode}. Provision WSL from the Setup tab, then re-download.{pongoWarning}";
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
