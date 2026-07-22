using System.IO;
using System.Windows;
using DarkSwordRestore.Core;
using Palera1nWin.App.Services;

namespace Palera1nWin.App.Views;

public partial class DowngradeView
{
    private bool _experienceHooksWired;
    private bool _refreshingEnhancedActions;

    private void WireDowngradeExperienceHooks()
    {
        if (_experienceHooksWired) return;
        _experienceHooksWired = true;
        InitializeDowngradeExperience();
        InitializeOperationalExperience();
        InitializeBootProfiles();
        WireOperationalDeferredHooks();

        StartDowngradeButton.Click -= StartDowngrade_Click;
        StartDowngradeButton.Click += StartEnhancedDowngrade_Click;
        StartDowngradeButton.IsEnabledChanged += EnhancedAction_IsEnabledChanged;

        _monitor.DeviceChanged += Experience_DeviceChanged;
        IpswPathBox.TextChanged += ExperienceFirmwareChanged;
        FirmwareList.SelectionChanged += ExperienceFirmwareSelectionChanged;
        BackupEncryptedCheck.Checked += ExperienceSafetyChanged;
        BackupEncryptedCheck.Unchecked += ExperienceSafetyChanged;
        BackupPhotosCheck.Checked += ExperienceSafetyChanged;
        BackupPhotosCheck.Unchecked += ExperienceSafetyChanged;
        BackupAuthCheck.Checked += ExperienceSafetyChanged;
        BackupAuthCheck.Unchecked += ExperienceSafetyChanged;
        ActivationLockCheck.Checked += ExperienceSafetyChanged;
        ActivationLockCheck.Unchecked += ExperienceSafetyChanged;
        BackupCompatibilityCheck.Checked += ExperienceSafetyChanged;
        BackupCompatibilityCheck.Unchecked += ExperienceSafetyChanged;
        EraseCheck.Checked += ExperienceSafetyChanged;
        EraseCheck.Unchecked += ExperienceSafetyChanged;
        TetherCheck.Checked += ExperienceSafetyChanged;
        TetherCheck.Unchecked += ExperienceSafetyChanged;
        OwnershipCheck.Checked += ExperienceSafetyChanged;
        OwnershipCheck.Unchecked += ExperienceSafetyChanged;
        ProductTypeConfirmationBox.TextChanged += ExperienceProductTypeChanged;

        Loaded += Experience_Loaded;
        RefreshEnhancedActionState();
    }

    private async void Experience_Loaded(object sender, RoutedEventArgs e)
    {
        InitializeOperationalExperience();
        InitializeBootProfiles();
        WireOperationalDeferredHooks();
        try
        {
            var snapshot = await _monitor.ProbeAsync();
            UpdateExperienceDeviceState(snapshot);
            await RefreshBootProfileAsync(snapshot);
        }
        catch (Exception exception)
        {
            AppendLog($"Enhanced experience device probe failed: {exception.Message}");
        }
        await RefreshRecoveryStateAsync();
        RefreshHardwareValidationUi();
        RefreshEnhancedActionState();
    }

    private void Experience_DeviceChanged(object? sender, AppleDeviceSnapshot snapshot) =>
        Dispatcher.BeginInvoke(() =>
        {
            UpdateExperienceDeviceState(snapshot);
            RefreshHardwareValidationUi();
            RefreshEnhancedActionState();
        });

    private void ExperienceFirmwareChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        InvalidatePreflight("Firmware path changed. Inspect the IPSW and run preflight again.");
        RefreshEnhancedActionState();
    }

    private void ExperienceFirmwareSelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_lastPreflight is not null)
        {
            InvalidatePreflight("Firmware selection changed. Download or inspect it, then run preflight again.");
        }
        RefreshEnhancedActionState();
    }

    private void ExperienceSafetyChanged(object sender, RoutedEventArgs e) => RefreshEnhancedActionState();

    private void ExperienceProductTypeChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) =>
        RefreshEnhancedActionState();

    private void EnhancedAction_IsEnabledChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (!_refreshingEnhancedActions) RefreshEnhancedActionState();
    }

    private void RefreshEnhancedActionState()
    {
        if (!_experienceInitialized || _refreshingEnhancedActions) return;
        _refreshingEnhancedActions = true;
        try
        {
            var confirmations = EraseCheck.IsChecked == true &&
                                TetherCheck.IsChecked == true &&
                                OwnershipCheck.IsChecked == true;
            var resources = Path.Combine(_tools.Root, "resources");
            var toolchainReady = _tools.MissingFiles().Count == 0 &&
                                 File.Exists(Path.Combine(resources, "sep_racer.bin")) &&
                                 File.Exists(Path.Combine(resources, "kpf.bin"));
            var hardwareBusy = Shell?.HardwareOperations.Current.IsBusy == true;
            StartDowngradeButton.IsEnabled = !hardwareBusy &&
                                              !_busy &&
                                              confirmations &&
                                              toolchainReady &&
                                              HasCurrentHardwareValidation() &&
                                              IsActiveRestoreTargetReady() &&
                                              IsEnhancedSafetyReady();
            RunPreflightButton.IsEnabled = !hardwareBusy && !_busy && _preflightCts is null;
            ResumeSessionButton.IsEnabled = !hardwareBusy && !_busy && _recoveryCandidate?.CanResume == true;
            RetryStageButton.IsEnabled = ResumeSessionButton.IsEnabled;
            ValidateHardwareButton.IsEnabled = !hardwareBusy && !_busy && toolchainReady;
            if (_bootProfileHooksWired)
            {
                SetBootButtonEnabled(_bootAssetValidated && CanUseBootButton());
            }
        }
        finally
        {
            _refreshingEnhancedActions = false;
        }
    }

    private async void StartEnhancedDowngrade_Click(object sender, RoutedEventArgs e)
    {
        if (!IsActiveRestoreTargetReady())
        {
            ShowMessage(
                "The active Windows restore path requires a detected A9/A9X device and an inspected iOS/iPadOS 15 IPSW containing that exact ProductType.",
                "Restore target not ready",
                MessageBoxImage.Information);
            return;
        }
        if (!HasCurrentHardwareValidation())
        {
            ShowMessage(
                "Run Test DFU → PongoOS successfully before starting a destructive restore.",
                "Hardware gate required",
                MessageBoxImage.Warning);
            return;
        }

        if (!IsPreflightCurrent() && !await RunPreflightAsync(showResultDialog: false)) return;
        if (!IsEnhancedSafetyReady())
        {
            ShowMessage(
                "Complete all five backup/account items, all three erase acknowledgements, and type the exact ProductType shown by the app.",
                "Safety confirmations required",
                MessageBoxImage.Information);
            return;
        }
        if (!ConfirmFinalDowngradeSummary()) return;

        _operationCts?.Cancel();
        _operationCts?.Dispose();
        _operationCts = new CancellationTokenSource();
        var lease = await TryAcquireHardwareLeaseAsync(
            HardwareOperationKind.Downgrade,
            "Full tethered downgrade using validated DFU/Pongo hardware",
            _operationCts.Token);
        if (lease is null)
        {
            _operationCts.Dispose();
            _operationCts = null;
            return;
        }

        SetBusy(true, "Starting downgrade", "Using the verified hardware gate and exact-device preflight report.");
        RefreshEnhancedActionState();

        try
        {
            var session = await _orchestrator.RunFullDowngradeAsync(
                _inspection!.Path,
                destructiveOperationConfirmed: true,
                new Progress<RestoreProgress>(HandleEnhancedProgress),
                AppendLog,
                _operationCts.Token);

            PtePathBox.Text = session.PteBlockPath ?? string.Empty;
            ShowPostDowngradeDashboard(session);
            await SaveCompletedBootProfileAsync(session);
            await RefreshRecoveryStateAsync();
            ShowMessage(
                $"Downgrade completed.\n\nSession: {session.SessionId}\nBoot asset: {session.PteBlockPath}\n\nThe exact-device cold-boot profile was validated and saved.",
                "Downgrade complete",
                MessageBoxImage.Information);
        }
        catch (OperationCanceledException)
        {
            var progress = new RestoreProgress(RestoreStage.Cancelled, OperationProgress.Value, "Paused", "The operation was paused. Hash-validated artifacts remain in the session folder.");
            HandleEnhancedProgress(progress);
            AppendLog("Downgrade operation paused by the user.");
        }
        catch (Exception exception)
        {
            var stage = exception is DarkSwordException darkSword ? darkSword.Stage : RestoreStage.Failed;
            HandleEnhancedProgress(new RestoreProgress(RestoreStage.Failed, OperationProgress.Value, "Downgrade stopped", exception.Message));
            AppendLog(exception.ToString());
            var guidance = DowngradeFailureTranslator.Translate(exception.Message, stage);
            ShowMessage(
                guidance.DisplayText + "\n\nRecovery will offer only hash-validated SHC/PTE artifacts.",
                guidance.Title,
                MessageBoxImage.Error);
        }
        finally
        {
            await lease.DisposeAsync();
            _operationCts?.Dispose();
            _operationCts = null;
            SetBusy(false, CurrentStageText.Text, CurrentDetailText.Text);
            await RefreshRecoveryStateAsync();
            RefreshEnhancedActionState();
        }
    }
}
