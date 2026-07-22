using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using DarkSwordRestore.Core;
using Microsoft.Win32;
using Palera1nWin.App.Services;

namespace Palera1nWin.App.Views;

public partial class DowngradeView
{
    private readonly DarkSwordBootProfileStore _bootProfileStore = new();
    private DarkSwordBootProfile? _activeBootProfile;
    private CancellationTokenSource? _bootProfileCts;
    private DispatcherTimer? _bootProfileUiTimer;
    private Button? _bootProfileBrowseButton;
    private Button? _postBootButton;
    private bool _bootProfileHooksWired;
    private bool _bootAssetValidated;
    private bool _updatingBootButton;
    private string? _lastKnownBootEcid;
    private string? _lastKnownBootProductType;
    private string? _lastSavedBootProfileSession;

    private void InitializeBootProfiles()
    {
        if (_bootProfileHooksWired) return;
        _bootProfileHooksWired = true;
        RememberBootIdentity(_monitor.Current);

        TetherBootButton.Click -= TetherBoot_Click;
        TetherBootButton.Click += ValidatedTetherBoot_Click;
        TetherBootButton.IsEnabledChanged += TetherBootButton_IsEnabledChanged;
        PtePathBox.TextChanged += BootProfileInputChanged;
        _monitor.DeviceChanged += BootProfile_DeviceChanged;
        PostDowngradePanel.IsVisibleChanged += BootProfile_PostPanelChanged;

        if (PtePathBox.Parent is Grid pteGrid)
        {
            _bootProfileBrowseButton = pteGrid.Children
                .OfType<Button>()
                .FirstOrDefault(button => string.Equals(button.Content?.ToString(), "Browse", StringComparison.OrdinalIgnoreCase));
            if (_bootProfileBrowseButton is not null)
            {
                _bootProfileBrowseButton.Click -= BrowsePte_Click;
                _bootProfileBrowseButton.Click += ImportBootProfile_Click;
                _bootProfileBrowseButton.Content = "Import Profile";
            }
        }

        _postBootButton = Descendants<Button>(PostDowngradePanel)
            .FirstOrDefault(button => string.Equals(button.Content?.ToString(), "Boot Device", StringComparison.OrdinalIgnoreCase));
        if (_postBootButton is not null)
        {
            _postBootButton.Click -= PostBootDevice_Click;
            _postBootButton.Click += ValidatedTetherBoot_Click;
        }

        _nextActionButton.Click -= NextAction_Click;
        _nextActionButton.Click += BootAwareNextAction_Click;

        _bootProfileUiTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(2200)
        };
        _bootProfileUiTimer.Tick += BootProfileUiTimer_Tick;
        _bootProfileUiTimer.Start();

        _ = RefreshBootProfileAsync(_monitor.Current);
    }

    private async void ValidatedTetherBoot_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        var profile = await EnsureActiveBootProfileAsync(showError: true);
        if (profile is null) return;

        _operationCts?.Cancel();
        _operationCts?.Dispose();
        _operationCts = new CancellationTokenSource();
        var lease = await TryAcquireHardwareLeaseAsync(
            HardwareOperationKind.TetherBoot,
            "Exact-device cold boot: waiting for DFU identity verification",
            _operationCts.Token);
        if (lease is null)
        {
            _operationCts.Dispose();
            _operationCts = null;
            return;
        }

        SetBusy(true, "Tether boot", "Enter DFU mode. ProductType, ECID, PTE, SEP, and KPF will be verified before boot.");
        try
        {
            UpdateProgress(new RestoreProgress(
                RestoreStage.WaitingForDfu,
                5,
                "Enter DFU mode",
                "Connect the downgraded device and enter DFU. The app will not send a payload until exact identity verification passes."));

            await _monitor.WaitForModeAsync(
                new[] { AppleDeviceMode.Dfu },
                TimeSpan.FromMinutes(5),
                _operationCts.Token);
            await _driver.EnsureDfuReadyAsync(_monitor, AppendLog, _operationCts.Token);
            var exactDevice = await _monitor.ProbeAsync(_operationCts.Token);
            RememberBootIdentity(exactDevice);

            var resources = Path.Combine(_tools.Root, "resources");
            var validation = await _bootProfileStore.ValidateAsync(
                profile,
                exactDevice.ProductType,
                exactDevice.Ecid,
                Path.Combine(resources, "sep_racer.bin"),
                Path.Combine(resources, "kpf.bin"),
                _operationCts.Token);
            if (!validation.IsValid)
            {
                throw new DarkSwordException(
                    RestoreStage.Preflight,
                    "Cold boot was blocked before payload transfer:" + Environment.NewLine + validation.Summary);
            }

            _activeBootProfile = validation.Profile;
            _bootAssetValidated = true;
            PtePathBox.Text = profile.PtePath;
            AppendLog(
                $"Exact cold-boot profile verified: ProductType={exactDevice.ProductType}, ECID={exactDevice.Ecid}, " +
                $"session={profile.SessionId}, PTE={profile.PteSha256}.");

            await _orchestrator.TetherBootAsync(
                profile.PtePath,
                new Progress<RestoreProgress>(UpdateProgress),
                AppendLog,
                _operationCts.Token);

            ShowMessage(
                $"The exact-device tether boot sequence was sent successfully.\n\n" +
                $"Device: {profile.ProductType}\nTarget: {profile.TargetVersion} ({profile.TargetBuild})\nSession: {profile.SessionId}",
                "Tether boot complete",
                MessageBoxImage.Information);
        }
        catch (OperationCanceledException)
        {
            CurrentStageText.Text = "Cancelled";
            CurrentDetailText.Text = "Tether boot was cancelled";
            AppendLog("Exact-device tether boot cancelled.");
        }
        catch (Exception exception)
        {
            _bootAssetValidated = false;
            AppendLog(exception.ToString());
            ShowMessage(exception.Message, "Tether boot blocked", MessageBoxImage.Error);
        }
        finally
        {
            await lease.DisposeAsync();
            _operationCts?.Dispose();
            _operationCts = null;
            SetBusy(false, CurrentStageText.Text, CurrentDetailText.Text);
            RefreshBootProfileStatus();
            RefreshEnhancedActionState();
        }
    }

    private async void ImportBootProfile_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Import a DarkSword exact-device boot profile",
            Filter = "DarkSword boot profile (boot-profile.json;*.json)|boot-profile.json;*.json|JSON files (*.json)|*.json",
            CheckFileExists = true
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;

        try
        {
            var profile = await _bootProfileStore.LoadAsync(dialog.FileName);
            if (profile is null) throw new InvalidDataException("The selected JSON is not a DarkSword boot profile.");
            if (!string.IsNullOrWhiteSpace(DetectedProductType) &&
                !string.Equals(profile.ProductType, DetectedProductType, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"The profile targets {profile.ProductType}, but the currently detected device is {DetectedProductType}.");
            }

            var resources = Path.Combine(_tools.Root, "resources");
            var validation = await _bootProfileStore.ValidateAssetsAsync(
                profile,
                Path.Combine(resources, "sep_racer.bin"),
                Path.Combine(resources, "kpf.bin"));
            if (!validation.IsValid) throw new InvalidDataException(validation.Summary);

            _activeBootProfile = validation.Profile;
            _bootAssetValidated = true;
            PtePathBox.Text = profile.PtePath;
            await _bootProfileStore.SaveAsync(profile);
            AppendLog($"Imported exact-device cold-boot profile {profile.Key} from {dialog.FileName}.");
            RefreshBootProfileStatus();
            RefreshEnhancedActionState();
        }
        catch (Exception exception)
        {
            _activeBootProfile = null;
            _bootAssetValidated = false;
            SetBootButtonEnabled(false);
            ShowMessage(exception.Message, "Boot profile import failed", MessageBoxImage.Error);
        }
    }

    private void TetherBootButton_IsEnabledChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_updatingBootButton || !TetherBootButton.IsEnabled || (_bootAssetValidated && _activeBootProfile is not null)) return;
        SetBootButtonEnabled(false);
    }

    private void BootProfileInputChanged(object sender, TextChangedEventArgs e)
    {
        if (_activeBootProfile is not null &&
            string.Equals(_activeBootProfile.PtePath, PtePathBox.Text, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _bootAssetValidated = false;
        _activeBootProfile = null;
        SetBootButtonEnabled(false);
        _ = ResolveProfileFromPteAsync(PtePathBox.Text);
    }

    private async Task ResolveProfileFromPteAsync(string? ptePath)
    {
        if (string.IsNullOrWhiteSpace(ptePath) || !File.Exists(ptePath)) return;
        try
        {
            var profile = await _bootProfileStore.FindByPteAsync(ptePath);
            if (profile is null) return;
            await LoadAndValidateProfileAssetsAsync(profile);
        }
        catch (Exception exception)
        {
            AppendLog($"Could not resolve a trusted profile for the selected PTE: {exception.Message}");
        }
    }

    private void BootProfile_DeviceChanged(object? sender, AppleDeviceSnapshot snapshot) =>
        Dispatcher.BeginInvoke(async () =>
        {
            RememberBootIdentity(snapshot);
            await Task.Delay(500);
            await RefreshBootProfileAsync(snapshot);
        });

    private async void BootProfile_PostPanelChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (PostDowngradePanel.Visibility != Visibility.Visible || _completedSession is null) return;
        await SaveCompletedBootProfileAsync(_completedSession);
    }

    private async Task SaveCompletedBootProfileAsync(RestoreSession session)
    {
        if (string.Equals(_lastSavedBootProfileSession, session.SessionId, StringComparison.Ordinal)) return;
        if (string.IsNullOrWhiteSpace(session.PteBlockPath)) return;

        var productType = DetectedProductType ?? _lastKnownBootProductType ?? session.Ipsw.SupportedProductTypes.FirstOrDefault();
        var ecid = _monitor.Current.Ecid ?? _lastKnownBootEcid;
        if (string.IsNullOrWhiteSpace(productType)) return;

        var resources = Path.Combine(_tools.Root, "resources");
        try
        {
            _activeBootProfile = await _bootProfileStore.CreateAsync(
                session,
                productType,
                ecid,
                Path.Combine(resources, "sep_racer.bin"),
                Path.Combine(resources, "kpf.bin"));
            _lastSavedBootProfileSession = session.SessionId;
            PtePathBox.Text = _activeBootProfile.PtePath;
            _bootAssetValidated = true;
            AppendLog(
                $"Saved exact-device cold-boot profile: {_activeBootProfile.Key} " +
                $"ECID={_activeBootProfile.Ecid} PTE={_activeBootProfile.PteSha256}.");
            RefreshBootProfileStatus();
            RefreshEnhancedActionState();
        }
        catch (Exception exception)
        {
            _activeBootProfile = null;
            _bootAssetValidated = false;
            SetBootButtonEnabled(false);
            AppendLog($"Cold-boot profile creation failed: {exception}");
            ShowMessage(
                $"The downgrade session completed, but its exact-device cold-boot profile could not be created: {exception.Message}\n\n" +
                "Re-enter DFU so ProductType and ECID can be captured, then resume the profile/final-boot stage.",
                "Cold-boot profile not ready",
                MessageBoxImage.Warning);
        }
    }

    private async Task RefreshBootProfileAsync(AppleDeviceSnapshot snapshot)
    {
        if (!_bootProfileHooksWired) return;
        RememberBootIdentity(snapshot);
        _bootProfileCts?.Cancel();
        _bootProfileCts?.Dispose();
        _bootProfileCts = new CancellationTokenSource();
        var token = _bootProfileCts.Token;

        try
        {
            var profile = await _bootProfileStore.FindAsync(
                snapshot.ProductType ?? DetectedProductType ?? _lastKnownBootProductType,
                snapshot.Ecid ?? _lastKnownBootEcid,
                token);
            profile ??= await _bootProfileStore.FindMostRecentAsync(token);
            if (profile is null)
            {
                _activeBootProfile = null;
                _bootAssetValidated = false;
                SetBootButtonEnabled(false);
                RefreshBootProfileStatus();
                return;
            }

            await LoadAndValidateProfileAssetsAsync(profile, token);
        }
        catch (OperationCanceledException)
        {
            // A newer device/profile state replaced this check.
        }
        catch (Exception exception)
        {
            _activeBootProfile = null;
            _bootAssetValidated = false;
            SetBootButtonEnabled(false);
            AppendLog($"Cold-boot profile scan failed: {exception.Message}");
        }
    }

    private async Task LoadAndValidateProfileAssetsAsync(
        DarkSwordBootProfile profile,
        CancellationToken cancellationToken = default)
    {
        var resources = Path.Combine(_tools.Root, "resources");
        var result = await _bootProfileStore.ValidateAssetsAsync(
            profile,
            Path.Combine(resources, "sep_racer.bin"),
            Path.Combine(resources, "kpf.bin"),
            cancellationToken);
        if (!result.IsValid)
        {
            _activeBootProfile = null;
            _bootAssetValidated = false;
            SetBootButtonEnabled(false);
            AppendLog($"Saved cold-boot profile rejected: {result.Summary}");
            RefreshBootProfileStatus();
            return;
        }

        _activeBootProfile = result.Profile;
        _bootAssetValidated = true;
        if (!string.Equals(PtePathBox.Text, profile.PtePath, StringComparison.OrdinalIgnoreCase))
        {
            PtePathBox.Text = profile.PtePath;
        }
        SetBootButtonEnabled(CanUseBootButton());
        AppendLog(
            $"Loaded cold-boot profile for {profile.ProductType} {profile.TargetVersion} ({profile.TargetBuild}); " +
            "exact ECID will be rechecked in DFU before payload transfer.");
        RefreshBootProfileStatus();
    }

    private async Task<DarkSwordBootProfile?> EnsureActiveBootProfileAsync(bool showError)
    {
        if (_activeBootProfile is null && File.Exists(PtePathBox.Text))
        {
            _activeBootProfile = await _bootProfileStore.FindByPteAsync(PtePathBox.Text);
        }
        if (_activeBootProfile is null && _completedSession is not null)
        {
            await SaveCompletedBootProfileAsync(_completedSession);
        }
        if (_activeBootProfile is null)
        {
            if (showError)
            {
                ShowMessage(
                    "No exact-device boot profile is loaded. Import the boot-profile.json from the completed DarkSword session; raw .bin PTE booting is blocked.",
                    "Boot profile required",
                    MessageBoxImage.Warning);
            }
            return null;
        }

        var resources = Path.Combine(_tools.Root, "resources");
        var validation = await _bootProfileStore.ValidateAssetsAsync(
            _activeBootProfile,
            Path.Combine(resources, "sep_racer.bin"),
            Path.Combine(resources, "kpf.bin"));
        if (!validation.IsValid)
        {
            _bootAssetValidated = false;
            if (showError) ShowMessage(validation.Summary, "Boot profile validation failed", MessageBoxImage.Error);
            return null;
        }

        _bootAssetValidated = true;
        return validation.Profile;
    }

    private bool CanUseBootButton()
    {
        var resources = Path.Combine(_tools.Root, "resources");
        var toolchainReady = _tools.MissingFiles().Count == 0 &&
                             File.Exists(Path.Combine(resources, "sep_racer.bin")) &&
                             File.Exists(Path.Combine(resources, "kpf.bin"));
        var profileSupported = _activeBootProfile is not null &&
                               DarkSwordDeviceCatalog.Find(_activeBootProfile.ProductType)?.UsesA9SepBlocks == true;
        var productCompatible = string.IsNullOrWhiteSpace(DetectedProductType) ||
                                _activeBootProfile is null ||
                                string.Equals(_activeBootProfile.ProductType, DetectedProductType, StringComparison.Ordinal);
        return !_busy &&
               Shell?.HardwareOperations.Current.IsBusy != true &&
               toolchainReady &&
               profileSupported &&
               productCompatible &&
               File.Exists(_activeBootProfile?.PtePath);
    }

    private void SetBootButtonEnabled(bool enabled)
    {
        _updatingBootButton = true;
        try
        {
            TetherBootButton.IsEnabled = enabled;
        }
        finally
        {
            _updatingBootButton = false;
        }
        RefreshBootProfileStatus();
    }

    private void RememberBootIdentity(AppleDeviceSnapshot snapshot)
    {
        if (!string.IsNullOrWhiteSpace(snapshot.Ecid)) _lastKnownBootEcid = snapshot.Ecid;
        if (!string.IsNullOrWhiteSpace(snapshot.ProductType)) _lastKnownBootProductType = snapshot.ProductType;
    }

    private void BootProfileUiTimer_Tick(object? sender, EventArgs e) => RefreshBootProfileStatus();

    private void RefreshBootProfileStatus()
    {
        if (!_bootProfileHooksWired || !_operationalExperienceInitialized) return;
        if (_activeBootProfile is null)
        {
            _profile.Text = "No exact cold-boot profile loaded";
            _profileDetail.Text = "Complete a downgrade or import boot-profile.json. Raw PTE files cannot be booted.";
            return;
        }

        var ecidSuffix = _activeBootProfile.Ecid is { Length: > 6 } ecid ? ecid[^6..] : _activeBootProfile.Ecid ?? "missing";
        _profile.Text = _bootAssetValidated
            ? $"READY — {_activeBootProfile.ProductType} exact boot profile"
            : $"BLOCKED — {_activeBootProfile.ProductType} profile needs validation";
        _profile.Foreground = ResourceBrush(_bootAssetValidated ? "Brush.Success" : "Brush.Danger");
        _profileDetail.Text =
            $"Target {_activeBootProfile.TargetVersion} ({_activeBootProfile.TargetBuild}); ECID …{ecidSuffix}; session {_activeBootProfile.SessionId}. " +
            "ProductType and ECID are rechecked in DFU before every boot.";

        if (_busy) return;
        _nextAction.Text = _monitor.Current.Mode switch
        {
            AppleDeviceMode.Normal => "Device is already running. Use Boot Device only after a full shutdown, restart, or dead battery.",
            AppleDeviceMode.Recovery => "Recovery mode detected. Use the timed DFU guide, then boot the saved exact-device profile.",
            AppleDeviceMode.Dfu => "DFU detected. Boot the saved exact-device profile after ECID verification.",
            _ => "Connect the downgraded device, enter DFU, and boot the saved exact-device profile."
        };
        _nextActionButton.Content = _monitor.Current.Mode switch
        {
            AppleDeviceMode.Normal => "Device Running",
            AppleDeviceMode.Recovery => "Start DFU Guide",
            _ => "Boot Device"
        };
        _nextActionButton.IsEnabled = _monitor.Current.Mode != AppleDeviceMode.Normal;
    }

    private async void BootAwareNextAction_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        if (_activeBootProfile is null)
        {
            NextAction_Click(sender, e);
            return;
        }

        if (_monitor.Current.Mode == AppleDeviceMode.Recovery)
        {
            StartDfuGuide_Click(StartDfuGuideButton, new RoutedEventArgs());
            return;
        }
        if (_monitor.Current.Mode == AppleDeviceMode.Normal)
        {
            ShowMessage("The device is already running. Cold boot is needed only after shutdown, restart, or a dead battery.", "Device already running", MessageBoxImage.Information);
            return;
        }

        ValidatedTetherBoot_Click(TetherBootButton, new RoutedEventArgs());
        await Task.CompletedTask;
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T typed) yield return typed;
            foreach (var descendant in Descendants<T>(child)) yield return descendant;
        }
    }

    private void DisposeBootProfiles()
    {
        if (!_bootProfileHooksWired) return;
        _bootProfileHooksWired = false;
        _bootProfileCts?.Cancel();
        _bootProfileCts?.Dispose();
        _bootProfileCts = null;
        if (_bootProfileUiTimer is not null)
        {
            _bootProfileUiTimer.Stop();
            _bootProfileUiTimer.Tick -= BootProfileUiTimer_Tick;
            _bootProfileUiTimer = null;
        }
        TetherBootButton.Click -= ValidatedTetherBoot_Click;
        TetherBootButton.IsEnabledChanged -= TetherBootButton_IsEnabledChanged;
        PtePathBox.TextChanged -= BootProfileInputChanged;
        _monitor.DeviceChanged -= BootProfile_DeviceChanged;
        PostDowngradePanel.IsVisibleChanged -= BootProfile_PostPanelChanged;
        _nextActionButton.Click -= BootAwareNextAction_Click;
        if (_bootProfileBrowseButton is not null)
        {
            _bootProfileBrowseButton.Click -= ImportBootProfile_Click;
            _bootProfileBrowseButton = null;
        }
        if (_postBootButton is not null)
        {
            _postBootButton.Click -= ValidatedTetherBoot_Click;
            _postBootButton = null;
        }
    }
}

internal static class DarkSwordBootProfileStoreExtensions
{
    public static async Task<DarkSwordBootProfile?> FindMostRecentAsync(
        this DarkSwordBootProfileStore store,
        CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(store.RootDirectory)) return null;
        foreach (var path in Directory.EnumerateFiles(store.RootDirectory, "*.json")
                     .OrderByDescending(File.GetLastWriteTimeUtc))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var profile = await store.LoadAsync(path, cancellationToken);
                if (profile is not null) return profile;
            }
            catch
            {
                // Continue to the next saved profile.
            }
        }
        return null;
    }
}
