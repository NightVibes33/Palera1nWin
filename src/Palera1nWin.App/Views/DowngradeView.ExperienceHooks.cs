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

        // Replace every destructive/recovery entry point with the schema-2 exact
        // ProductType+ECID gate. The original handlers remain private implementation
        // details and are called only after the exact guard has been armed.
        StartDowngradeButton.Click -= StartDowngrade_Click;
        StartDowngradeButton.Click -= StartEnhancedDowngrade_Click;
        StartDowngradeButton.Click += StartIdentityBoundDowngrade_Click;
        StartDowngradeButton.IsEnabledChanged += EnhancedAction_IsEnabledChanged;

        ValidateHardwareButton.Click -= ValidateHardware_Click;
        ValidateHardwareButton.Click += ValidateExactHardware_Click;
        ResumeSessionButton.Click -= ResumeSession_Click;
        ResumeSessionButton.Click += ResumeIdentityBoundSession_Click;
        RetryStageButton.Click -= RetryStage_Click;
        RetryStageButton.Click += ResumeIdentityBoundSession_Click;

        _monitor.DeviceChanged += Experience_DeviceChanged;
        _monitor.DeviceChanged += ExactValidation_DeviceChanged;
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
        RefreshExactHardwareValidationUi();
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
        RefreshExactHardwareValidationUi();
        RefreshEnhancedActionState();
    }

    private void Experience_DeviceChanged(object? sender, AppleDeviceSnapshot snapshot) =>
        Dispatcher.BeginInvoke(() =>
        {
            UpdateExperienceDeviceState(snapshot);
            RefreshHardwareValidationUi();
            RefreshExactHardwareValidationUi();
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
            var exactGateReady = LoadCurrentExactHardwareValidation() is not null;
            StartDowngradeButton.IsEnabled = !hardwareBusy &&
                                              !_busy &&
                                              confirmations &&
                                              toolchainReady &&
                                              exactGateReady &&
                                              IsActiveRestoreTargetReady() &&
                                              IsEnhancedSafetyReady();
            RunPreflightButton.IsEnabled = !hardwareBusy && !_busy && _preflightCts is null;
            ResumeSessionButton.IsEnabled = !hardwareBusy && !_busy && _recoveryCandidate?.CanResume == true &&
                                            (_recoveryCandidate.Session.HasBoundIdentity || exactGateReady);
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
        if (LoadCurrentExactHardwareValidation() is null)
        {
            ShowMessage(
                "Run the exact-device Test DFU → PongoOS successfully before starting a destructive restore.",
                "Exact hardware gate required",
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
            "Full tethered downgrade using exact ECID-bound DFU/Pongo validation",
            _operationCts.Token);
        if (lease is null)
        {
            _operationCts.Dispose();
            _operationCts = null;
            return;
        }

        SetBusy(true, "Starting downgrade", "Every later exact-identity device transition is guarded against target replacement.");
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
            AppendLog("Downgrade operation paused or cancelled by the exact-device guard.");
        }
        catch (Exception exception)
        {
            var stage = exception is DarkSwordException darkSword ? darkSword.Stage : RestoreStage.Failed;
            HandleEnhancedProgress(new RestoreProgress(RestoreStage.Failed, OperationProgress.Value, "Downgrade stopped", exception.Message));
            AppendLog(exception.ToString());
            var guidance = DowngradeFailureTranslator.Translate(exception.Message, stage);
            ShowMessage(
                guidance.DisplayText + "\n\nRecovery will offer only hash-validated, identity-bound SHC/PTE artifacts.",
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
