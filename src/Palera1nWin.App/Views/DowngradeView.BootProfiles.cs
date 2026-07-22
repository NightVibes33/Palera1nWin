using System.IO;
using System.Windows;
using DarkSwordRestore.Core;

namespace Palera1nWin.App.Views;

public partial class DowngradeView
{
    private readonly DarkSwordBootProfileStore _bootProfileStore = new();
    private DarkSwordBootProfile? _activeBootProfile;
    private CancellationTokenSource? _bootProfileCts;
    private bool _bootProfileHooksWired;
    private bool _bootAssetValidated;
    private bool _updatingBootButton;

    private void InitializeBootProfiles()
    {
        if (_bootProfileHooksWired) return;
        _bootProfileHooksWired = true;

        TetherBootButton.Click -= TetherBoot_Click;
        TetherBootButton.Click += ValidatedTetherBoot_Click;
        TetherBootButton.IsEnabledChanged += TetherBootButton_IsEnabledChanged;
        PtePathBox.TextChanged += BootProfileInputChanged;
        _monitor.DeviceChanged += BootProfile_DeviceChanged;
        PostDowngradePanel.IsVisibleChanged += BootProfile_PostPanelChanged;

        _ = RefreshBootProfileAsync(_monitor.Current);
    }

    private async void ValidatedTetherBoot_Click(object sender, RoutedEventArgs e)
    {
        if (!await ValidateSelectedBootAssetAsync(showError: true)) return;
        TetherBoot_Click(sender, e);
    }

    private void TetherBootButton_IsEnabledChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_updatingBootButton || TetherBootButton.IsEnabled == false || _bootAssetValidated) return;
        SetBootButtonEnabled(false);
        _ = ValidateSelectedBootAssetAsync(showError: false);
    }

    private void BootProfileInputChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        _bootAssetValidated = false;
        SetBootButtonEnabled(false);
        _ = ValidateSelectedBootAssetAsync(showError: false);
    }

    private void BootProfile_DeviceChanged(object? sender, AppleDeviceSnapshot snapshot) =>
        Dispatcher.BeginInvoke(async () =>
        {
            await Task.Delay(600);
            await RefreshBootProfileAsync(snapshot);
        });

    private async void BootProfile_PostPanelChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (PostDowngradePanel.Visibility != Visibility.Visible || _completedSession is null) return;
        await SaveCompletedBootProfileAsync(_completedSession);
    }

    private async Task SaveCompletedBootProfileAsync(RestoreSession session)
    {
        if (string.IsNullOrWhiteSpace(session.PteBlockPath) ||
            string.IsNullOrWhiteSpace(DetectedProductType))
        {
            return;
        }

        var resources = Path.Combine(_tools.Root, "resources");
        var sepRacer = Path.Combine(resources, "sep_racer.bin");
        var kpf = Path.Combine(resources, "kpf.bin");
        try
        {
            _activeBootProfile = await _bootProfileStore.CreateAsync(
                session,
                DetectedProductType,
                _monitor.Current.Ecid,
                sepRacer,
                kpf);
            PtePathBox.Text = _activeBootProfile.PtePath;
            AppendLog($"Saved exact-device cold-boot profile: {_activeBootProfile.Key} PTE={_activeBootProfile.PteSha256}");
            await ValidateSelectedBootAssetAsync(showError: false);
        }
        catch (Exception exception)
        {
            _activeBootProfile = null;
            _bootAssetValidated = false;
            SetBootButtonEnabled(false);
            AppendLog($"Cold-boot profile creation failed: {exception}");
            ShowMessage(
                $"The downgrade session completed, but its cold-boot profile could not be validated: {exception.Message}",
                "Cold-boot profile not ready",
                MessageBoxImage.Warning);
        }
    }

    private async Task RefreshBootProfileAsync(AppleDeviceSnapshot snapshot)
    {
        if (!_bootProfileHooksWired) return;
        _bootProfileCts?.Cancel();
        _bootProfileCts?.Dispose();
        _bootProfileCts = new CancellationTokenSource();
        var token = _bootProfileCts.Token;

        try
        {
            var profile = await _bootProfileStore.FindAsync(
                DetectedProductType,
                snapshot.Ecid,
                token);
            if (profile is null) return;

            var resources = Path.Combine(_tools.Root, "resources");
            var result = await _bootProfileStore.ValidateAsync(
                profile,
                DetectedProductType,
                snapshot.Ecid,
                Path.Combine(resources, "sep_racer.bin"),
                Path.Combine(resources, "kpf.bin"),
                token);
            if (!result.IsValid)
            {
                _activeBootProfile = null;
                _bootAssetValidated = false;
                SetBootButtonEnabled(false);
                AppendLog($"Saved cold-boot profile rejected: {result.Summary}");
                return;
            }

            _activeBootProfile = profile;
            if (!string.Equals(PtePathBox.Text, profile.PtePath, StringComparison.OrdinalIgnoreCase))
            {
                PtePathBox.Text = profile.PtePath;
            }
            _bootAssetValidated = true;
            SetBootButtonEnabled(CanUseBootButton());
            AppendLog($"Loaded exact-device cold-boot profile for {profile.ProductType} {profile.TargetVersion} ({profile.TargetBuild}).");
        }
        catch (OperationCanceledException)
        {
            // A newer device/profile state replaced this check.
        }
        catch (Exception exception)
        {
            AppendLog($"Cold-boot profile scan failed: {exception.Message}");
        }
    }

    private async Task<bool> ValidateSelectedBootAssetAsync(bool showError)
    {
        _bootProfileCts?.Cancel();
        _bootProfileCts?.Dispose();
        _bootProfileCts = new CancellationTokenSource();
        var token = _bootProfileCts.Token;
        _bootAssetValidated = false;
        SetBootButtonEnabled(false);

        if (!File.Exists(PtePathBox.Text)) return false;

        var resources = Path.Combine(_tools.Root, "resources");
        var sepRacer = Path.Combine(resources, "sep_racer.bin");
        var kpf = Path.Combine(resources, "kpf.bin");
        BootProfileValidationResult result;
        try
        {
            if (_activeBootProfile is not null &&
                string.Equals(_activeBootProfile.PtePath, Path.GetFullPath(PtePathBox.Text), StringComparison.OrdinalIgnoreCase))
            {
                result = await _bootProfileStore.ValidateAsync(
                    _activeBootProfile,
                    DetectedProductType,
                    _monitor.Current.Ecid,
                    sepRacer,
                    kpf,
                    token);
            }
            else
            {
                result = await _bootProfileStore.ValidatePteImportAsync(
                    PtePathBox.Text,
                    DetectedProductType,
                    _monitor.Current.Ecid,
                    sepRacer,
                    kpf,
                    token);
                if (result.IsValid && result.Profile is not null)
                {
                    _activeBootProfile = result.Profile;
                    await _bootProfileStore.SaveAsync(result.Profile, token);
                }
            }
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception exception)
        {
            result = new BootProfileValidationResult(false, null, [exception.Message]);
        }

        _bootAssetValidated = result.IsValid;
        SetBootButtonEnabled(result.IsValid && CanUseBootButton());
        if (!result.IsValid)
        {
            AppendLog($"Cold-boot asset rejected: {result.Summary}");
            if (showError)
            {
                ShowMessage(
                    result.Summary,
                    "Boot profile validation failed",
                    MessageBoxImage.Error);
            }
            return false;
        }

        AppendLog($"Cold-boot asset verified: {result.Profile!.PteSha256}");
        return true;
    }

    private bool CanUseBootButton()
    {
        var resources = Path.Combine(_tools.Root, "resources");
        var toolchainReady = _tools.MissingFiles().Count == 0 &&
                             File.Exists(Path.Combine(resources, "sep_racer.bin")) &&
                             File.Exists(Path.Combine(resources, "kpf.bin"));
        return !_busy &&
               Shell?.HardwareOperations.Current.IsBusy != true &&
               toolchainReady &&
               IsActiveA9TetherBootTarget() &&
               File.Exists(PtePathBox.Text);
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
    }

    private void DisposeBootProfiles()
    {
        if (!_bootProfileHooksWired) return;
        _bootProfileHooksWired = false;
        _bootProfileCts?.Cancel();
        _bootProfileCts?.Dispose();
        _bootProfileCts = null;
        TetherBootButton.Click -= ValidatedTetherBoot_Click;
        TetherBootButton.IsEnabledChanged -= TetherBootButton_IsEnabledChanged;
        PtePathBox.TextChanged -= BootProfileInputChanged;
        _monitor.DeviceChanged -= BootProfile_DeviceChanged;
        PostDowngradePanel.IsVisibleChanged -= BootProfile_PostPanelChanged;
    }
}
