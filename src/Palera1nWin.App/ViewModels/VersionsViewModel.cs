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
    public bool HasDownload => AssetSize > 0 && !AssetName.StartsWith("(", StringComparison.Ordinal);
    public string SizeText => AssetSize > 0 ? $"{AssetSize / (1024.0 * 1024.0):F1} MB" : "Unavailable";

    private PongoCompatibilityResult _pongo = new(PongoCompatibilityLevel.Unknown, null, null);
    public PongoCompatibilityResult Pongo
    {
        get => _pongo;
        set
        {
            if (!SetProperty(ref _pongo, value)) return;
            OnPropertyChanged(nameof(PongoBadgeText));
            OnPropertyChanged(nameof(PongoIsWarning));
            OnPropertyChanged(nameof(PongoIsUnknown));
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
    private readonly HardwareOperationCoordinator _hardwareOperations;
    private readonly GitHubReleasesClient _releasesClient = new();
    private readonly string? _bundledPongoVersion;
    private ReleaseItemViewModel? _selectedRelease;
    private string _downloadStatus = "No download in progress.";
    private bool _isBusy;
    private bool _disposed;

    public VersionsViewModel(
        AppSettings settings,
        LogService logService,
        Action<string> setStatus,
        HardwareOperationCoordinator? hardwareOperations = null)
    {
        _settings = settings;
        _logService = logService;
        _setStatus = setStatus;
        _hardwareOperations = hardwareOperations ?? new HardwareOperationCoordinator();
        OwnsCoordinator = hardwareOperations is null;

        var toolchain = Paths.ResolveToolchainRoot(settings.ToolchainRoot);
        _bundledPongoVersion = toolchain is not null
            ? PongoCompatibility.ExtractEmbeddedPongoVersion(Paths.GetOpenRa1nExecutable(toolchain))
            : null;

        RefreshCommand = new AsyncRelayCommand(RefreshAsync, () => !IsBusy);
        DownloadCommand = new AsyncRelayCommand(DownloadSelectedAsync, CanDownload);
        _hardwareOperations.StateChanged += HardwareOperations_StateChanged;

        BundledVersionNote = _bundledPongoVersion is not null
            ? $"The packaged openra1n embeds PongoOS {_bundledPongoVersion}. Downloaded palera1n assets are SHA-256 verified before they can replace the active WSL runtime."
            : "Downloaded palera1n assets are SHA-256 verified before they can replace the active WSL runtime.";
    }

    private bool OwnsCoordinator { get; }
    public string BundledVersionNote { get; }
    public ObservableCollection<ReleaseItemViewModel> Releases { get; } = [];

    public ReleaseItemViewModel? SelectedRelease
    {
        get => _selectedRelease;
        set
        {
            if (!SetProperty(ref _selectedRelease, value)) return;
            if (value is not null) _settings.SelectedReleaseTag = value.TagName;
            DownloadCommand.RaiseCanExecuteChanged();
            if (value is not null && Releases.Count > 0)
                DownloadStatus = value.HasDownload
                    ? $"Loaded {Releases.Count} releases. Selected: {value.TagName}."
                    : $"{value.TagName} does not contain a Linux x86_64 executable.";
        }
    }

    public string DownloadStatus { get => _downloadStatus; private set => SetProperty(ref _downloadStatus, value); }
    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (!SetProperty(ref _isBusy, value)) return;
            RefreshCommand.RaiseCanExecuteChanged();
            DownloadCommand.RaiseCanExecuteChanged();
        }
    }

    public AsyncRelayCommand RefreshCommand { get; }
    public AsyncRelayCommand DownloadCommand { get; }

    private bool CanDownload() => !IsBusy && !_hardwareOperations.Current.IsBusy && SelectedRelease?.HasDownload == true;

    private void HardwareOperations_StateChanged(object? sender, HardwareOperationState e) =>
        System.Windows.Application.Current?.Dispatcher.Invoke(DownloadCommand.RaiseCanExecuteChanged);

    public async Task RefreshAsync()
    {
        if (_disposed) return;
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
                    AssetName = asset?.Name ?? "(no Linux x86_64 binary)",
                    AssetSize = asset?.Size ?? 0,
                    Pongo = PongoCompatibility.CheckTag(release.TagName, _bundledPongoVersion),
                });
            }

            SelectedRelease = Releases.FirstOrDefault(r => string.Equals(r.TagName, _settings.SelectedReleaseTag, StringComparison.OrdinalIgnoreCase))
                              ?? Releases.FirstOrDefault(r => r.HasDownload);
            DownloadStatus = Releases.Count == 0
                ? "No releases returned from GitHub."
                : $"Loaded {Releases.Count} releases. Selected: {SelectedRelease?.TagName ?? "none"}.";
            _setStatus($"Loaded {Releases.Count} palera1n releases.");
            _logService.Append("versions", DownloadStatus);
        }
        catch (Exception ex)
        {
            DownloadStatus = $"Failed to fetch releases: {ex.Message}";
            _setStatus("Release fetch failed.");
            _logService.Append("versions", ex.ToString(), isError: true);
        }
        finally { IsBusy = false; }
    }

    private async Task DownloadSelectedAsync()
    {
        var selected = SelectedRelease;
        if (selected?.HasDownload != true) return;

        HardwareOperationLease? lease = null;
        VerifiedDownloadReceipt? receipt = null;
        IsBusy = true;
        try
        {
            lease = await _hardwareOperations.AcquireAsync(
                HardwareOperationKind.RuntimeUpdate,
                $"Downloading and activating palera1n {selected.TagName}").ConfigureAwait(true);

            var destination = Path.Combine(AppSettings.RuntimeDirectory, "palera1n-linux-x86_64");
            DownloadStatus = $"Downloading and verifying {selected.TagName}...";
            _setStatus(DownloadStatus);
            var progress = new Progress<Core.Models.ProgressEventArgs>(e => DownloadStatus = e.Message);
            receipt = await _releasesClient.DownloadReleaseBinaryAsync(selected.TagName, destination, progress).ConfigureAwait(true);

            var pongoCheck = PongoCompatibility.CheckBinary(destination, _bundledPongoVersion);
            selected.Pongo = pongoCheck;
            if (pongoCheck.Level == PongoCompatibilityLevel.Incompatible)
                throw new InvalidDataException($"Verified binary is incompatible with packaged Pongo: {pongoCheck.Summary}");

            DownloadStatus = $"Installing verified {selected.TagName} into WSL...";
            _hardwareOperations.UpdateDetail(HardwareOperationKind.RuntimeUpdate, DownloadStatus);
            var wsl = new WslService(_settings.WslDistro);
            var distro = await wsl.ResolveDistroAsync().ConfigureAwait(true)
                         ?? throw new InvalidOperationException("No WSL distro is installed.");
            var provision = new WslProvisionService(wsl);
            var installResult = await provision.InstallPalera1nBinaryAsync(
                destination,
                distro,
                line =>
                {
                    DownloadStatus = line;
                    _logService.Append("versions", line);
                }).ConfigureAwait(true);
            if (!installResult.Succeeded)
                throw new InvalidOperationException($"WSL install exited with code {installResult.ExitCode}: {installResult.StandardError}");

            var active = await provision.GetInstalledVersionAsync(distro).ConfigureAwait(true);
            _settings.SelectedReleaseTag = selected.TagName;
            _settings.Save();
            receipt.Commit();
            DownloadStatus = $"Active in {distro}: {active ?? selected.TagName}. SHA-256 {receipt.Sha256}.";
            _setStatus($"palera1n {selected.TagName} is active.");
            _logService.Append("versions", DownloadStatus);
        }
        catch (HardwareOperationBusyException ex)
        {
            DownloadStatus = ex.Message;
            _setStatus("Runtime update blocked by active hardware operation.");
        }
        catch (Exception ex)
        {
            DownloadStatus = $"Runtime update failed: {ex.Message}";
            _setStatus("Release update failed; previous active binary was restored.");
            _logService.Append("versions", ex.ToString(), isError: true);
        }
        finally
        {
            receipt?.Dispose();
            if (lease is not null) await lease.DisposeAsync();
            IsBusy = false;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _hardwareOperations.StateChanged -= HardwareOperations_StateChanged;
        _releasesClient.Dispose();
        if (OwnsCoordinator) _hardwareOperations.Dispose();
    }
}
